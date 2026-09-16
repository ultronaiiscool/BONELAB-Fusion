using LabFusion.Utilities;
using LabFusion.Support;

using MelonLoader;

using Steamworks;
using Steamworks.Data;

using System.Collections;

namespace LabFusion.Network;

public sealed class SteamMatchmaker : IMatchmaker
{
    private const double BrowseTimeoutSeconds = 15d;

    private delegate Task<Lobby[]> LobbySearchDelegate(MatchmakerFilters filters);

    private readonly SteamNetworkLayer _owner;
    private readonly int _layerGeneration;

    private int _requestEpoch;
    private int _latestRequestId;

    public SteamMatchmaker(SteamNetworkLayer owner, int layerGeneration)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _layerGeneration = layerGeneration;
    }

    public void CancelAll(string reason)
    {
        Interlocked.Increment(ref _requestEpoch);
        Interlocked.Increment(ref _latestRequestId);
        FusionLogger.Log($"Steam Browse requests cancelled for generation {_layerGeneration}: {reason}.");
    }

    public void RequestLobbies(Action<IMatchmaker.MatchmakerCallbackInfo> callback) => RequestLobbies(MatchmakerFilters.Empty, callback);

    public void RequestLobbies(MatchmakerFilters filters, Action<IMatchmaker.MatchmakerCallbackInfo> callback)
    {
        StartRequest(FetchLobbies, filters, callback);
    }

    public void RequestLobbiesByCode(string code, Action<IMatchmaker.MatchmakerCallbackInfo> callback)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            callback?.Invoke(IMatchmaker.MatchmakerCallbackInfo.Empty);
            return;
        }

        StartRequest((_) => FetchLobbiesByCode(code), MatchmakerFilters.Empty, callback);
    }

    private void StartRequest(LobbySearchDelegate searchDelegate, MatchmakerFilters filters, Action<IMatchmaker.MatchmakerCallbackInfo> callback)
    {
        var requestId = Interlocked.Increment(ref _latestRequestId);
        var epoch = Volatile.Read(ref _requestEpoch);

        FusionLogger.Log($"Steam Browse started: generation {_layerGeneration}, request {requestId}.");
        MelonCoroutines.Start(FindLobbies(searchDelegate, filters, callback, requestId, epoch));
    }

    private IEnumerator FindLobbies(LobbySearchDelegate searchDelegate, MatchmakerFilters filters, Action<IMatchmaker.MatchmakerCallbackInfo> callback, int requestId, int epoch)
    {
        bool completed = false;
        Task<Lobby[]> task;

        if (!CanUseRequest(requestId, epoch))
        {
            yield break;
        }

        try
        {
            task = searchDelegate(filters);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("starting Steam lobby search", e);
            Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "start failure");
            yield break;
        }

        var started = DateTime.UtcNow;

        while (!task.IsCompleted)
        {
            if (!CanUseRequest(requestId, epoch))
            {
                FusionLogger.Log($"Steam Browse cancelled/stale: generation {_layerGeneration}, request {requestId}.");
                MelonCoroutines.Start(RetainTaskUntilNativeCompletion(task, requestId));
                yield break;
            }

            if ((DateTime.UtcNow - started).TotalSeconds >= BrowseTimeoutSeconds)
            {
                FusionLogger.Warn($"Steam Browse timed out: generation {_layerGeneration}, request {requestId}.");
                Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "timeout");

                // Facepunch's LobbyQuery does not expose supported cancellation. Keep
                // the Task rooted until Steam finishes it; the Steam client itself is
                // intentionally retained for process lifetime by SteamNetworkLayer.
                MelonCoroutines.Start(RetainTaskUntilNativeCompletion(task, requestId));
                yield break;
            }

            yield return null;
        }

        if (!CanUseRequest(requestId, epoch))
        {
            FusionLogger.Log($"Steam Browse late completion ignored: generation {_layerGeneration}, request {requestId}.");
            yield break;
        }

        if (!task.IsCompletedSuccessfully)
        {
            FusionLogger.LogException("searching for Steam lobbies", task.Exception);
            Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "failure");
            yield break;
        }

        var lobbies = task.Result;
        if (lobbies == null || lobbies.Length == 0)
        {
            Complete(callback, IMatchmaker.MatchmakerCallbackInfo.Empty, ref completed, requestId, "zero lobbies");
            yield break;
        }

        List<IMatchmaker.LobbyInfo> netLobbies = new(lobbies.Length);

        foreach (var lobby in lobbies)
        {
            if (!CanUseRequest(requestId, epoch))
            {
                FusionLogger.Log($"Steam Browse metadata processing cancelled: generation {_layerGeneration}, request {requestId}.");
                yield break;
            }

            try
            {
                if (lobby.Owner.IsMe)
                {
                    continue;
                }

                var networkLobby = new SteamLobby(lobby);
                var metadata = LobbyMetadataSerializer.ReadInfo(networkLobby);

                if (!metadata.HasLobbyOpen)
                {
                    continue;
                }

                netLobbies.Add(new IMatchmaker.LobbyInfo()
                {
                    Lobby = networkLobby,
                    Metadata = metadata,
                });
            }
            catch (Exception e)
            {
                // A malformed lobby should not fail the whole Browse operation.
                FusionLogger.LogException("validating Steam lobby metadata", e);
            }
        }

        var info = new IMatchmaker.MatchmakerCallbackInfo()
        {
            Lobbies = netLobbies.ToArray(),
        };

        Complete(callback, info, ref completed, requestId, "success");
    }

    private IEnumerator RetainTaskUntilNativeCompletion(Task<Lobby[]> task, int requestId)
    {
        while (!task.IsCompleted)
        {
            yield return null;
        }

        // Observe faults so an abandoned native request does not become an
        // unobserved Task exception. Never call the old UI callback from here.
        if (task.IsFaulted && task.Exception != null)
        {
            FusionLogger.LogException($"late Steam Browse request {requestId}", task.Exception);
        }
    }

    private bool CanUseRequest(int requestId, int epoch)
    {
        return SteamClient.IsValid
            && _owner.IsGenerationCurrent(_layerGeneration)
            && requestId == Volatile.Read(ref _latestRequestId)
            && epoch == Volatile.Read(ref _requestEpoch);
    }

    private static void Complete(Action<IMatchmaker.MatchmakerCallbackInfo> callback, IMatchmaker.MatchmakerCallbackInfo info, ref bool completed, int requestId, string reason)
    {
        if (completed)
        {
            FusionLogger.Warn($"Steam Browse duplicate completion suppressed for request {requestId}.");
            return;
        }

        completed = true;
        FusionLogger.Log($"Steam Browse completed: request {requestId}, reason {reason}.");

        try
        {
            // This method is only called by a Melon coroutine, so menu/UI completion
            // stays on Unity's main thread rather than a native/Task callback thread.
            callback?.Invoke(info);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("completing Steam Browse UI callback", e);
        }
    }

    private static Task<Lobby[]> FetchLobbies(MatchmakerFilters filters)
    {
        var query = SteamMatchmaking.LobbyList;
        query = AddPersistentFilters(query);
        query = AddMatchmakingFilters(query, filters);

        return query
            .WithNotEqual(LobbyKeys.PrivacyKey, (int)ServerPrivacy.PRIVATE)
            .WithNotEqual(LobbyKeys.PrivacyKey, (int)ServerPrivacy.LOCKED)
            .RequestAsync();
    }

    private static Task<Lobby[]> FetchLobbiesByCode(string code)
    {
        var query = SteamMatchmaking.LobbyList;
        query = AddPersistentFilters(query);

        return query
            .WithKeyValue(LobbyKeys.LobbyCodeKey, code.ToUpperInvariant())
            .RequestAsync();
    }

    private static LobbyQuery AddPersistentFilters(LobbyQuery query)
    {
        return query
            .FilterDistanceWorldwide()
            .WithKeyValue(LobbyKeys.IdentifierKey, bool.TrueString)
            .WithKeyValue(LobbyKeys.HasLobbyOpenKey, bool.TrueString)
            .WithKeyValue(LobbyKeys.GameKey, GameInfo.GameName);
    }

    private static LobbyQuery AddMatchmakingFilters(LobbyQuery query, MatchmakerFilters filters)
    {
        if (filters.FilterFull)
        {
            query = query.WithKeyValue(LobbyKeys.FullKey, bool.FalseString);
        }

        if (filters.FilterMismatchingVersions)
        {
            var version = FusionMod.Version;
            query = query
                .WithEqual(LobbyKeys.VersionMajorKey, version.Major)
                .WithEqual(LobbyKeys.VersionMinorKey, version.Minor);
        }

        return query;
    }
}
