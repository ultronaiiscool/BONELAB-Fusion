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
    private int _requestEpoch;
    private int _requestInProgress;

    public ProxyMatchmaker(ProxyLobbyManager lobbyManager)
    {
        _lobbyManager = lobbyManager ?? throw new ArgumentNullException(nameof(lobbyManager));
    }

    public void CancelAll(string reason)
    {
        Interlocked.Increment(ref _requestEpoch);
        Interlocked.Increment(ref _latestRequestId);
        Volatile.Write(ref _requestInProgress, 0);
        _lobbyManager.CancelPending();
        FusionLogger.Log($"Proxy Browse requests cancelled: {reason}.");
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
        if (!NetworkLayerManager.LoggedIn || !ReferenceEquals(NetworkLayerManager.Layer, ProxyNetworkLayer.Instance))
        {
            bool unavailableCompleted = false;
            Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref unavailableCompleted, Volatile.Read(ref _latestRequestId), "proxy layer unavailable");
            return;
        }

        if (Interlocked.CompareExchange(ref _requestInProgress, 1, 0) != 0)
        {
            bool rejectedCompleted = false;
            Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref rejectedCompleted, Volatile.Read(ref _latestRequestId), "request already in progress");
            return;
        }

        var requestId = Interlocked.Increment(ref _latestRequestId);
        var epoch = Volatile.Read(ref _requestEpoch);
        FusionLogger.Log($"Proxy Browse started: request {requestId}.");
        MelonCoroutines.Start(FindLobbies(parameters, callback, requestId, epoch));
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

    private IEnumerator FindLobbies(ProxyLobbyRequestParameters parameters, Action<IMatchmaker.MatchmakerCallbackInfo> callback, int requestId, int epoch)
    {
        bool completed = false;
        Task<ulong[]> task;

        try
        {
            task = _lobbyManager.RequestLobbyIDs(parameters);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("starting proxy lobby request", e);
            Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "start failure");
            FinishRequest(requestId, epoch);
            yield break;
        }

        float timeTaken = 0f;
        while (!task.IsCompleted)
        {
            if (!IsLiveRequest(requestId, epoch))
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
                _lobbyManager.CancelPending();
                FinishRequest(requestId, epoch);
                yield break;
            }
        }

        if (!IsLiveRequest(requestId, epoch))
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
            FinishRequest(requestId, epoch);
            yield break;
        }

        List<(ulong LobbyId, Task<LobbyMetadataInfo> Task)> metadataTasks = new(task.Result.Length);
        HashSet<ulong> requestedLobbyIds = new();

        foreach (var lobby in task.Result)
        {
            if (!IsLiveRequest(requestId, epoch))
            {
                FusionLogger.Log($"Proxy Browse metadata processing cancelled: request {requestId}.");
                yield break;
            }

            Task<LobbyMetadataInfo> metadataTask;
            try
            {
                if (!requestedLobbyIds.Add(lobby))
                {
                    continue;
                }

                metadataTask = _lobbyManager.RequestLobbyMetadataInfo(lobby);
                metadataTasks.Add((lobby, metadataTask));
            }
            catch (Exception e)
            {
                FusionLogger.LogException("starting proxy lobby metadata request", e);
                continue;
            }

        }

        bool metadataTimedOut = false;
        while (true)
        {
            bool allCompleted = true;
            foreach (var request in metadataTasks)
            {
                if (!request.Task.IsCompleted)
                {
                    allCompleted = false;
                    break;
                }
            }

            if (allCompleted)
            {
                break;
            }

            if (!IsLiveRequest(requestId, epoch))
            {
                FusionLogger.Log($"Proxy Browse metadata request cancelled/stale: request {requestId}.");
                yield break;
            }

            yield return null;
            timeTaken += TimeReferences.DeltaTime;

            if (timeTaken >= RequestTimeoutSeconds)
            {
                metadataTimedOut = true;
                FusionLogger.Warn($"Proxy Browse timed out requesting metadata: request {requestId}; returning completed valid lobbies.");
                SendTimeOutNotification();
                _lobbyManager.CancelPending();
                break;
            }
        }

        List<IMatchmaker.LobbyInfo> netLobbies = new(metadataTasks.Count);

        foreach (var request in metadataTasks)
        {
            if (!request.Task.IsCompletedSuccessfully)
            {
                if (request.Task.IsFaulted && request.Task.Exception != null)
                {
                    FusionLogger.LogException("requesting proxy lobby metadata", request.Task.Exception);
                }
                continue;
            }

            try
            {
                var metadata = request.Task.Result;
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

        var completionReason = metadataTimedOut ? "partial metadata timeout" : (netLobbies.Count == 0 ? "zero lobbies" : "success");
        Complete(callback, info, ref completed, requestId, completionReason);
        FinishRequest(requestId, epoch);
    }

    private void FinishRequest(int requestId, int epoch)
    {
        if (requestId == Volatile.Read(ref _latestRequestId) && epoch == Volatile.Read(ref _requestEpoch))
        {
            Interlocked.Exchange(ref _requestInProgress, 0);
        }
    }

    private bool IsLiveRequest(int requestId, int epoch)
    {
        return requestId == Volatile.Read(ref _latestRequestId)
            && epoch == Volatile.Read(ref _requestEpoch)
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
