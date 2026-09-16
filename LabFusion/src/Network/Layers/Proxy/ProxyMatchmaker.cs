using LabFusion.UI.Popups;
using LabFusion.Utilities;

using MelonLoader;

using System.Collections;

namespace LabFusion.Network.Proxy;

public sealed class ProxyMatchmaker : IMatchmaker
{
    private const float RequestTimeoutSeconds = 15f;

    private readonly ProxyLobbyManager _lobbyManager;
    private int _latestRequestId;

    public ProxyMatchmaker(ProxyLobbyManager lobbyManager)
    {
        _lobbyManager = lobbyManager ?? throw new ArgumentNullException(nameof(lobbyManager));
    }

    public void RequestLobbies(Action<IMatchmaker.MatchmakerCallbackInfo> callback) => RequestLobbies(MatchmakerFilters.Empty, callback);

    public void RequestLobbies(MatchmakerFilters filters, Action<IMatchmaker.MatchmakerCallbackInfo> callback)
    {
        var version = FusionMod.Version;
        var parameters = new ProxyLobbyRequestParameters()
        {
            Filters = filters,
            VersionMajor = version.Major,
            VersionMinor = version.Minor,
            LobbyCode = null,
        };

        StartRequest(parameters, callback);
    }

    public void RequestLobbiesByCode(string code, Action<IMatchmaker.MatchmakerCallbackInfo> callback)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            callback?.Invoke(IMatchmaker.MatchmakerCallbackInfo.Empty);
            return;
        }

        var version = FusionMod.Version;
        var parameters = new ProxyLobbyRequestParameters()
        {
            Filters = MatchmakerFilters.Empty,
            VersionMajor = version.Major,
            VersionMinor = version.Minor,
            LobbyCode = code,
        };

        StartRequest(parameters, callback);
    }

    private void StartRequest(ProxyLobbyRequestParameters parameters, Action<IMatchmaker.MatchmakerCallbackInfo> callback)
    {
        var requestId = Interlocked.Increment(ref _latestRequestId);
        FusionLogger.Log($"Proxy Browse started: request {requestId}.");
        MelonCoroutines.Start(FindLobbies(parameters, callback, requestId));
    }

    private static void SendTimeOutNotification()
    {
        Notifier.Send(new Notification()
        {
            Title = "Timed Out",
            Message = "Requesting lobbies took too long.",
            ShowPopup = true,
            SaveToMenu = false,
            Type = NotificationType.ERROR,
        });
    }

    private IEnumerator FindLobbies(ProxyLobbyRequestParameters parameters, Action<IMatchmaker.MatchmakerCallbackInfo> callback, int requestId)
    {
        bool completed = false;
        Task<List<ulong>> task;

        try
        {
            task = _lobbyManager.RequestLobbyIDs(parameters);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("starting proxy lobby request", e);
            Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "start failure");
            yield break;
        }

        float timeTaken = 0f;
        while (!task.IsCompleted)
        {
            if (!IsLiveRequest(requestId))
            {
                FusionLogger.Log($"Proxy Browse cancelled/stale: request {requestId}.");
                yield break;
            }

            yield return null;
            timeTaken += TimeReferences.DeltaTime;

            if (timeTaken >= RequestTimeoutSeconds)
            {
                FusionLogger.Warn($"Proxy Browse timed out requesting lobby IDs: request {requestId}.");
                SendTimeOutNotification();
                Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "timeout");
                yield break;
            }
        }

        if (!IsLiveRequest(requestId))
        {
            FusionLogger.Log($"Proxy Browse late lobby-ID completion ignored: request {requestId}.");
            yield break;
        }

        if (!task.IsCompletedSuccessfully || task.Result == null)
        {
            if (task.Exception != null)
            {
                FusionLogger.LogException("requesting proxy lobby IDs", task.Exception);
            }

            Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "lobby-ID failure");
            yield break;
        }

        List<IMatchmaker.LobbyInfo> netLobbies = new();

        foreach (var lobby in task.Result)
        {
            if (!IsLiveRequest(requestId))
            {
                FusionLogger.Log($"Proxy Browse metadata processing cancelled: request {requestId}.");
                yield break;
            }

            Task<LobbyMetadataInfo> metadataTask;
            try
            {
                metadataTask = _lobbyManager.RequestLobbyMetadataInfo(lobby);
            }
            catch (Exception e)
            {
                FusionLogger.LogException("starting proxy lobby metadata request", e);
                continue;
            }

            timeTaken = 0f;
            while (!metadataTask.IsCompleted)
            {
                if (!IsLiveRequest(requestId))
                {
                    FusionLogger.Log($"Proxy Browse metadata request cancelled/stale: request {requestId}.");
                    yield break;
                }

                yield return null;
                timeTaken += TimeReferences.DeltaTime;

                if (timeTaken >= RequestTimeoutSeconds)
                {
                    FusionLogger.Warn($"Proxy Browse timed out requesting metadata: request {requestId}.");
                    SendTimeOutNotification();
                    Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "metadata timeout");
                    yield break;
                }
            }

            if (!metadataTask.IsCompletedSuccessfully)
            {
                if (metadataTask.Exception != null)
                {
                    FusionLogger.LogException("requesting proxy lobby metadata", metadataTask.Exception);
                }
                continue;
            }

            try
            {
                var metadata = metadataTask.Result;
                if (!metadata.HasLobbyOpen)
                {
                    continue;
                }

                ProxyNetworkLobby networkLobby = new()
                {
                    info = metadata,
                };

                netLobbies.Add(new IMatchmaker.LobbyInfo()
                {
                    Lobby = networkLobby,
                    Metadata = metadata,
                });
            }
            catch (Exception e)
            {
                FusionLogger.LogException("validating proxy lobby metadata", e);
            }
        }

        var info = new IMatchmaker.MatchmakerCallbackInfo()
        {
            Lobbies = netLobbies.ToArray(),
        };

        Complete(callback, info, ref completed, requestId, netLobbies.Count == 0 ? "zero lobbies" : "success");
    }

    private bool IsLiveRequest(int requestId)
    {
        return requestId == Volatile.Read(ref _latestRequestId)
            && NetworkLayerManager.LoggedIn
            && ReferenceEquals(NetworkLayerManager.Layer, ProxyNetworkLayer.Instance);
    }

    private static void Complete(Action<IMatchmaker.MatchmakerCallbackInfo> callback, IMatchmaker.MatchmakerCallbackInfo info, ref bool completed, int requestId, string reason)
    {
        if (completed)
        {
            FusionLogger.Warn($"Proxy Browse duplicate completion suppressed for request {requestId}.");
            return;
        }

        completed = true;
        FusionLogger.Log($"Proxy Browse completed: request {requestId}, reason {reason}.");

        try
        {
            callback?.Invoke(info);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("completing Proxy Browse UI callback", e);
        }
    }
}
