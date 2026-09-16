using LabFusion.Utilities;

using LiteNetLib;
using LiteNetLib.Utils;

namespace LabFusion.Network.Proxy;

public class ProxyLobbyManager
{
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

            uint numLobbyIds = packetReader.GetUInt();
            ulong[] ids = new ulong[numLobbyIds];

            for (uint i = 0; i < numLobbyIds; i++)
            {
                ids[i] = packetReader.GetULong();
            }

            _lobbyIdSource = null;
            source.TrySetResult(ids);
        }

        if (messageType == MessageTypes.LobbyMetadata)
        {
            ulong lobbyId = packetReader.GetULong();

            if (!_metadataInfoRequests.TryGetValue(lobbyId, out var source))
            {
                FusionLogger.Warn("Ignoring unexpected proxy lobby metadata.");
                return;
            }

            _metadataInfoRequests.Remove(lobbyId);

            try
            {
                ProxyNetworkLobby lobby = new();
                int keyCount = packetReader.GetInt();

                if (keyCount < 0 || keyCount > 1024)
                {
                    throw new InvalidDataException("Proxy lobby metadata key count was outside the accepted range.");
                }

                for (var i = 0; i < keyCount; i++)
                {
                    string key = packetReader.GetString();
                    string value = packetReader.GetString();
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
        _lobbyIdSource = new TaskCompletionSource<ulong[]>();

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

        var source = new TaskCompletionSource<LobbyMetadataInfo>();
        _metadataInfoRequests.Add(lobbyId, source);

        NetDataWriter writer = ProxyNetworkLayer.NewWriter(MessageTypes.LobbyMetadata);
        writer.Put(lobbyId);
        _networkLayer.SendToProxyServer(writer);

        return source.Task;
    }
}
