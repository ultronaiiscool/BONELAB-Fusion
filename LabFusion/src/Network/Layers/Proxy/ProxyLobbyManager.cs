using LabFusion.Utilities;

using LiteNetLib;
using LiteNetLib.Utils;

namespace LabFusion.Network.Proxy;

public class ProxyLobbyManager
{
    internal const int MaxLobbyCount = 256;
    private const int MaxMetadataKeyCount = 128;
    private const int MaxMetadataKeyLength = 128;
    private const int MaxMetadataValueLength = 4096;

    private TaskCompletionSource<ulong[]> _lobbyIdSource = null;
    private readonly ProxyNetworkLayer _networkLayer;
    private readonly Dictionary<ulong, TaskCompletionSource<LobbyMetadataInfo>> _metadataInfoRequests = new();

    internal ProxyLobbyManager(ProxyNetworkLayer networkLayer)
    {
        _networkLayer = networkLayer;
    }

    internal void CancelPending()
    {
        _lobbyIdSource?.TrySetResult(Array.Empty<ulong>());
        _lobbyIdSource = null;

        foreach (var request in _metadataInfoRequests.Values)
        {
            request.TrySetCanceled();
        }

        _metadataInfoRequests.Clear();
    }

    internal void HandleLobbyMessage(MessageTypes messageType, NetPacketReader packetReader)
    {
        if (messageType == MessageTypes.LobbyIDs)
        {
            var source = _lobbyIdSource;
            if (source == null)
            {
                FusionLogger.Warn("Ignoring an unexpected proxy lobby-list response.");
                return;
            }

            if (!packetReader.TryGetUInt(out uint numLobbyIds)
                || numLobbyIds > MaxLobbyCount
                || packetReader.AvailableBytes < checked((int)numLobbyIds * sizeof(ulong)))
            {
                _lobbyIdSource = null;
                source.TrySetException(new InvalidDataException("Proxy lobby list was malformed or exceeded the accepted limit."));
                return;
            }

            ulong[] ids = new ulong[numLobbyIds];

            for (uint i = 0; i < numLobbyIds; i++)
            {
                if (!packetReader.TryGetULong(out ids[i]))
                {
                    _lobbyIdSource = null;
                    source.TrySetException(new InvalidDataException("Proxy lobby list ended unexpectedly."));
                    return;
                }
            }

            _lobbyIdSource = null;
            source.TrySetResult(ids);
        }

        if (messageType == MessageTypes.LobbyMetadata)
        {
            if (!packetReader.TryGetULong(out ulong lobbyId))
            {
                FusionLogger.Warn("Ignoring malformed proxy lobby metadata without a lobby ID.");
                return;
            }

            if (!_metadataInfoRequests.TryGetValue(lobbyId, out var source))
            {
                FusionLogger.Warn("Ignoring unexpected proxy lobby metadata.");
                return;
            }

            _metadataInfoRequests.Remove(lobbyId);

            try
            {
                ProxyNetworkLobby lobby = new();
                if (!packetReader.TryGetInt(out int keyCount))
                {
                    throw new InvalidDataException("Proxy lobby metadata did not contain a key count.");
                }

                if (keyCount < 0 || keyCount > MaxMetadataKeyCount)
                {
                    throw new InvalidDataException("Proxy lobby metadata key count was outside the accepted range.");
                }

                for (var i = 0; i < keyCount; i++)
                {
                    if (!packetReader.TryGetString(out string key)
                        || !packetReader.TryGetString(out string value)
                        || key.Length > MaxMetadataKeyLength
                        || value.Length > MaxMetadataValueLength)
                    {
                        throw new InvalidDataException("Proxy lobby metadata contained an invalid key or value.");
                    }

                    lobby.CacheMetadata(key, value);
                }

                LobbyMetadataInfo info = LobbyMetadataSerializer.ReadInfo(lobby);
                source.TrySetResult(info);
            }
            catch (Exception e)
            {
                FusionLogger.LogException("parsing proxy lobby metadata", e);
                source.TrySetException(e);
            }
        }
    }

    public Task<ulong[]> RequestLobbyIDs(ProxyLobbyRequestParameters parameters)
    {
        // Only one list request is meaningful at a time. Finish the older request
        // deterministically instead of overwriting its TaskCompletionSource forever.
        _lobbyIdSource?.TrySetResult(Array.Empty<ulong>());
        _lobbyIdSource = new TaskCompletionSource<ulong[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        NetDataWriter writer = ProxyNetworkLayer.NewWriter(MessageTypes.LobbyIDs);
        parameters.Put(writer);
        _networkLayer.SendToProxyServer(writer);

        return _lobbyIdSource.Task;
    }

    public Task<LobbyMetadataInfo> RequestLobbyMetadataInfo(ulong lobbyId)
    {
        if (_metadataInfoRequests.TryGetValue(lobbyId, out var existing))
        {
            return existing.Task;
        }

        if (_metadataInfoRequests.Count >= MaxLobbyCount)
        {
            return Task.FromException<LobbyMetadataInfo>(new InvalidOperationException("Too many proxy lobby metadata requests are pending."));
        }

        var source = new TaskCompletionSource<LobbyMetadataInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataInfoRequests.Add(lobbyId, source);

        NetDataWriter writer = ProxyNetworkLayer.NewWriter(MessageTypes.LobbyMetadata);
        writer.Put(lobbyId);
        _networkLayer.SendToProxyServer(writer);

        return source.Task;
    }
}
