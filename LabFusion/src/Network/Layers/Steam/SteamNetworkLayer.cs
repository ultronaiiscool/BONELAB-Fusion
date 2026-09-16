using LabFusion.Data;
using LabFusion.Player;
using LabFusion.Utilities;
using LabFusion.UI.Popups;

using Steamworks;
using Steamworks.Data;

using LabFusion.Senders;
using LabFusion.Voice;
using LabFusion.Voice.Unity;

using MelonLoader;
using System.Collections;

namespace LabFusion.Network;

public abstract class SteamNetworkLayer : NetworkLayer
{
    public abstract uint ApplicationID { get; }

    public const int ReceiveBufferSize = 32;
    private const float LobbyCreationTimeoutSeconds = 15f;

    public override string Title => "Steam";
    public override string Platform => "Steam";

    public override bool IsHost => _isServerActive;
    public override bool IsClient => _isConnectionActive;

    private INetworkLobby _currentLobby;
    public override INetworkLobby Lobby => _currentLobby;

    private IVoiceManager _voiceManager = null;
    public override IVoiceManager VoiceManager => _voiceManager;

    private SteamMatchmaker _matchmaker = null;
    public override IMatchmaker Matchmaker => _matchmaker;

    public SteamId SteamId;

    public static SteamSocketManager SteamSocket;
    public static SteamConnectionManager SteamConnection;

    protected bool _isServerActive = false;
    protected bool _isConnectionActive = false;

    protected Lobby _localLobby;

    private bool _isLayerInitialized;
    private bool _isLoggedIn;
    private int _generation;

    private static int _nextGeneration;
    private static bool _fusionOwnsSteamClient;
    private static uint _fusionApplicationId;
    private static bool _loggedUnsafeGameSteamContext;
    private static Task<Lobby?> _pendingLobbyCreationTask;

    internal int Generation => _generation;

    internal bool IsGenerationCurrent(int generation)
    {
        return _isLoggedIn && _isLayerInitialized && generation == _generation && SteamClient.IsValid;
    }

    public override bool CheckSupported()
    {
        return !PlatformHelper.IsAndroid;
    }

    public override bool CheckValidation()
    {
        if (!SteamAPILoader.HasSteamAPI)
        {
            return false;
        }

        // Facepunch.Steamworks in this repository is a static, process-wide client.
        // Initializing it for SteamVR (250820) requires changing SteamAppId/SteamGameId.
        // Never do that after BONELAB has already initialized its own Steam client.
        if (GameHasSteamworks() && !SteamClient.IsValid)
        {
            if (!_loggedUnsafeGameSteamContext)
            {
                FusionLogger.Warn("BONELAB already owns the process Steam context. Refusing unsafe in-process Steam reinitialization; an isolated proxy layer will be used when available.");
                _loggedUnsafeGameSteamContext = true;
            }

            return false;
        }

        if (SteamClient.IsValid && (!_fusionOwnsSteamClient || _fusionApplicationId != ApplicationID))
        {
            FusionLogger.Error("A Steam client is already initialized with unknown or incompatible ownership. Refusing to reuse it for Fusion.");
            return false;
        }

        return true;
    }

    public override void OnInitializeLayer()
    {
        if (_isLayerInitialized)
        {
            return;
        }

        if (!SteamClient.IsValid || !_fusionOwnsSteamClient || _fusionApplicationId != ApplicationID)
        {
            FusionLogger.Error("Steamworks is not in a Fusion-owned compatible state; refusing layer initialization.");
            return;
        }

        _isLayerInitialized = true;

        SteamId = SteamClient.SteamId;
        PlayerIDManager.SetLongID(SteamId.Value);
        LocalPlayer.Username = GetUsername(SteamId.Value);

        FusionLogger.Log($"Steamworks initialized for ApplicationID {ApplicationID}; generation {_generation}.");

        SteamNetworkingUtils.InitRelayNetworkAccess();

        HookSteamEvents();

        _voiceManager = new UnityVoiceManager();
        _voiceManager.Enable();

        _matchmaker = new SteamMatchmaker(this, _generation);
    }

    public override void OnDeinitializeLayer()
    {
        // Invalidate requests before touching any managed state. We deliberately do
        // not call SteamAPI.Shutdown/SteamClient.Shutdown here: native async calls may
        // still own callbacks/handles, and BONELAB may own the process Steam lifetime.
        _isLoggedIn = false;
        _generation = Interlocked.Increment(ref _nextGeneration);

        _matchmaker?.CancelAll("network layer deinitialized");

        if (!_isLayerInitialized)
        {
            return;
        }

        Disconnect();
        UnHookSteamEvents();

        _voiceManager?.Disable();
        _voiceManager = null;
        _matchmaker = null;

        _localLobby = default;
        _currentLobby = null;
        _isLayerInitialized = false;

        FusionLogger.Log($"Steam network layer deinitialized; generation {_generation}. Native Steam client retained for process lifetime to avoid invalidating outstanding native callbacks.");
    }

    public override void LogIn()
    {
        if (_isLoggedIn)
        {
            return;
        }

        if (SteamClient.IsValid)
        {
            if (!_fusionOwnsSteamClient || _fusionApplicationId != ApplicationID)
            {
                FailLogin("Steam is already initialized by another owner or App ID. Fusion will not take over that process-wide client.");
                return;
            }

            _generation = Interlocked.Increment(ref _nextGeneration);
            _isLoggedIn = true;
            InvokeLoggedInEvent();
            return;
        }

        // Never shut down BONELAB's Steamworks instance. The previous implementation
        // did so, changed SteamAppId/SteamGameId, then reinitialized the same process
        // as App 250820. That invalidates native ownership/callback assumptions.
        if (GameHasSteamworks())
        {
            FailLogin("BONELAB already owns Steam in this process. Use the isolated Proxy SteamVR layer with Fusion Helper.");
            return;
        }

        try
        {
            SteamClient.Init(ApplicationID, false);
            _fusionOwnsSteamClient = true;
            _fusionApplicationId = ApplicationID;
        }
        catch (Exception e)
        {
            FusionLogger.LogException("initializing Fusion-owned Steamworks", e);
            FailLogin("Failed connecting to Steamworks. Make sure Steam is running and signed in.");
            return;
        }

        _generation = Interlocked.Increment(ref _nextGeneration);
        _isLoggedIn = true;
        InvokeLoggedInEvent();
    }

    public override void LogOut()
    {
        if (!_isLoggedIn && !_isLayerInitialized)
        {
            return;
        }

        _isLoggedIn = false;
        _generation = Interlocked.Increment(ref _nextGeneration);
        _matchmaker?.CancelAll("logout");

        // Do not destroy the static native Steam context here. Facepunch's async
        // lobby calls do not expose a supported cancellation primitive, so shutdown
        // while one is pending can leave native callbacks targeting freed state.
        InvokeLoggedOutEvent();
    }

    private void FailLogin(string message)
    {
        FusionLogger.Error(message);

        Notifier.Send(new Notification()
        {
            Title = "Log In Failed",
            Message = message,
            SaveToMenu = false,
            ShowPopup = true,
            Type = NotificationType.ERROR,
            PopupLength = 8f,
        });

        _isLoggedIn = false;
        InvokeLoggedOutEvent();
    }

    private const string STEAMWORKS_ASSEMBLY_NAME = "Il2CppFacepunch.Steamworks.Win64";

    internal static bool GameHasSteamworks()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.FullName?.StartsWith(STEAMWORKS_ASSEMBLY_NAME) == true)
            {
                return true;
            }
        }

        return false;
    }

    public override void OnUpdateLayer()
    {
        if (!SteamClient.IsValid || !_fusionOwnsSteamClient)
        {
            return;
        }

        SteamClient.RunCallbacks();

        try
        {
            SteamSocket?.Receive(ReceiveBufferSize);
            SteamConnection?.Receive(ReceiveBufferSize);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("receiving data on Socket and Connection", e);
        }
    }

    public override string GetUsername(ulong userId)
    {
        return new Friend(userId).Name;
    }

    public override bool IsFriend(ulong userId)
    {
        return userId == PlayerIDManager.LocalPlatformID || new Friend(userId).IsFriend;
    }

    public override void BroadcastMessage(NetworkChannel channel, NetMessage message)
    {
        if (IsHost)
        {
            SteamSocketHandler.BroadcastToClients(SteamSocket, channel, message);
        }
        else
        {
            SteamSocketHandler.BroadcastToServer(channel, message);
        }
    }

    public override void SendToServer(NetworkChannel channel, NetMessage message)
    {
        SteamSocketHandler.BroadcastToServer(channel, message);
    }

    public override void SendFromServer(byte userId, NetworkChannel channel, NetMessage message)
    {
        var id = PlayerIDManager.GetPlayerID(userId);
        if (id != null)
        {
            SendFromServer(id.PlatformID, channel, message);
        }
    }

    public override void SendFromServer(ulong userId, NetworkChannel channel, NetMessage message)
    {
        if (!IsHost)
        {
            return;
        }

        if (SteamSocket.ConnectedSteamIDs.TryGetValue(userId, out var connection))
        {
            SteamSocket.SendToClient(connection, channel, message);
        }
    }

    public override void StartServer()
    {
        SteamSocket = SteamNetworkingSockets.CreateRelaySocket<SteamSocketManager>(0);
        SteamConnection = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(SteamId);
        _isServerActive = true;
        _isConnectionActive = true;

        InternalServerHelpers.OnStartServer();
        RefreshServerCode();
    }

    public void JoinServer(SteamId serverId)
    {
        if (_isConnectionActive || _isServerActive)
        {
            Disconnect();
        }

        SteamConnection = SteamNetworkingSockets.ConnectRelay<SteamConnectionManager>(serverId, 0);

        _isServerActive = false;
        _isConnectionActive = true;

        ConnectionSender.SendConnectionRequest();
    }

    public override void Disconnect(string reason = "")
    {
        if (!_isServerActive && !_isConnectionActive)
        {
            return;
        }

        try
        {
            SteamConnection?.Close();
            SteamSocket?.Close();
        }
        catch (Exception e)
        {
            FusionLogger.LogException("closing Steam socket/connection", e);
        }
        finally
        {
            SteamConnection = null;
            SteamSocket = null;
        }

        _isServerActive = false;
        _isConnectionActive = false;

        InternalServerHelpers.OnDisconnect(reason);
    }

    public override void DisconnectUser(ulong platformID)
    {
        if (!_isServerActive)
        {
            return;
        }

        SteamSocket?.DisconnectUser(platformID);
    }

    public string ServerCode { get; private set; } = null;

    public override string GetServerCode()
    {
        return ServerCode;
    }

    public override void RefreshServerCode()
    {
        ServerCode = RandomCodeGenerator.GetString(8);
        LobbyInfoManager.PushLobbyUpdate();
    }

    public override void JoinServerByCode(string code)
    {
        if (Matchmaker == null || string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var generation = _generation;
        Matchmaker.RequestLobbiesByCode(code, (info) =>
        {
            if (!IsGenerationCurrent(generation) || info.Lobbies.Length <= 0)
            {
                return;
            }

            JoinServer(info.Lobbies[0].Metadata.LobbyInfo.LobbyID);
        });
    }

    private void HookSteamEvents()
    {
        MultiplayerHooking.OnPlayerJoined += OnPlayerJoin;
        MultiplayerHooking.OnPlayerLeft += OnPlayerLeave;
        MultiplayerHooking.OnDisconnected += OnDisconnect;
        LobbyInfoManager.OnLobbyInfoChanged += OnUpdateLobby;

        MelonCoroutines.Start(AwaitLobbyCreation(_generation));
    }

    private void OnPlayerJoin(PlayerID id)
    {
        if (VoiceManager != null && !id.IsMe)
        {
            VoiceManager.GetSpeaker(id);
        }
    }

    private void OnPlayerLeave(PlayerID id)
    {
        VoiceManager?.RemoveSpeaker(id);
    }

    private void OnDisconnect()
    {
        VoiceManager?.ClearManager();
    }

    private void UnHookSteamEvents()
    {
        MultiplayerHooking.OnPlayerJoined -= OnPlayerJoin;
        MultiplayerHooking.OnPlayerLeft -= OnPlayerLeave;
        MultiplayerHooking.OnDisconnected -= OnDisconnect;
        LobbyInfoManager.OnLobbyInfoChanged -= OnUpdateLobby;

        try
        {
            if (_localLobby.Id == SteamId)
            {
                _localLobby.Leave();
            }
        }
        catch (Exception e)
        {
            FusionLogger.LogException("leaving local Steam lobby", e);
        }
    }

    private IEnumerator AwaitLobbyCreation(int generation)
    {
        if (_pendingLobbyCreationTask != null && !_pendingLobbyCreationTask.IsCompleted)
        {
            FusionLogger.Warn("A previous Steam lobby creation is still pending; skipping a duplicate native request.");
            yield break;
        }

        _pendingLobbyCreationTask = null;
        Task<Lobby?> task;

        try
        {
            task = SteamMatchmaking.CreateLobbyAsync();
        }
        catch (Exception e)
        {
            FusionLogger.LogException("starting local Steam lobby creation", e);
            yield break;
        }

        _pendingLobbyCreationTask = task;

        var started = DateTime.UtcNow;

        while (!task.IsCompleted)
        {
            if (!IsGenerationCurrent(generation))
            {
                FusionLogger.Log($"Ignoring local Steam lobby creation from stale generation {generation}.");
                MelonCoroutines.Start(RetainLobbyCreationTask(task));
                yield break;
            }

            if ((DateTime.UtcNow - started).TotalSeconds >= LobbyCreationTimeoutSeconds)
            {
                FusionLogger.Warn($"Local Steam lobby creation timed out for generation {generation}; late completion will be ignored.");
                MelonCoroutines.Start(RetainLobbyCreationTask(task));
                yield break;
            }

            yield return null;
        }

        if (!IsGenerationCurrent(generation))
        {
            FusionLogger.Log($"Ignoring completed local Steam lobby from stale generation {generation}.");
            _pendingLobbyCreationTask = null;
            yield break;
        }

        _pendingLobbyCreationTask = null;

        if (!task.IsCompletedSuccessfully || !task.Result.HasValue)
        {
            if (task.Exception != null)
            {
                FusionLogger.LogException("creating local Steam lobby", task.Exception);
            }
            else
            {
                FusionLogger.Warn("Failed to create a Steam lobby.");
            }

            yield break;
        }

        _localLobby = task.Result.Value;
        _currentLobby = new SteamLobby(_localLobby);
    }

    private static IEnumerator RetainLobbyCreationTask(Task<Lobby?> task)
    {
        while (!task.IsCompleted)
        {
            yield return null;
        }

        if (task.IsFaulted && task.Exception != null)
        {
            FusionLogger.LogException("late Steam lobby creation", task.Exception);
        }

        if (ReferenceEquals(_pendingLobbyCreationTask, task))
        {
            _pendingLobbyCreationTask = null;
        }
    }

    public void OnUpdateLobby()
    {
        if (Lobby == null)
        {
            return;
        }

        LobbyMetadataSerializer.WriteInfo(Lobby);
    }
}
