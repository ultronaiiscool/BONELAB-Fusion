using System.Collections;
using System.Diagnostics;

using LabFusion.Player;
using LabFusion.Utilities;
using LabFusion.Preferences.Client;
using LabFusion.Voice;
using LabFusion.Voice.Unity;
using LabFusion.UI.Popups;

using MelonLoader;
using MelonLoader.Utils;

using LabFusion.Senders;

using Steamworks;

using LiteNetLib;
using LiteNetLib.Utils;

namespace LabFusion.Network.Proxy;

public abstract class ProxyNetworkLayer : NetworkLayer
{
    public abstract uint ApplicationID { get; }

    public static ProxyNetworkLayer Instance { get; private set; }

    public override string Title => "Proxy";

    public override bool IsHost => _isServerActive;
    public override bool IsClient => _isConnectionActive;

    public SteamId SteamId;

    private INetworkLobby _currentLobby;
    public override INetworkLobby Lobby => _currentLobby;

    private IVoiceManager _voiceManager;
    public override IVoiceManager VoiceManager => _voiceManager;

    private ProxyMatchmaker _matchmaker = null;
    public override IMatchmaker Matchmaker => _matchmaker;

    protected bool _isServerActive = false;
    protected bool _isConnectionActive = false;

    protected ulong _targetServerId;

    protected string _targetJoinId;

    protected bool _isInitialized = false;
    private bool _isLayerInitialized = false;

    private NetManager client;
    private NetPeer serverConnection;
    private ProxyLobbyManager _lobbyManager;

    private const float ProxyLoginTimeoutSeconds = 20f;
    private const float ProxyDiscoveryIntervalSeconds = 1f;
    private bool _loginInProgress;
    private bool _proxyConnectRequested;
    private int _loginGeneration;
    private DateTime _lastMalformedMessageLogUtc = DateTime.MinValue;

    public override bool CheckSupported()
    {
        return PlatformHelper.IsAndroid;
    }

    public override bool CheckValidation()
    {
        return true;
    }

    public override void OnInitializeLayer()
    {
        if (_isLayerInitialized)
        {
            return;
        }

        _isLayerInitialized = true;
        Instance = this;

        _voiceManager = new UnityVoiceManager();
        _voiceManager.Enable();

        HookSteamEvents();

        _lobbyManager = new ProxyLobbyManager(this);

        _matchmaker = new ProxyMatchmaker(_lobbyManager);
    }

    public IEnumerator DiscoverServer(int generation)
    {
        int port = GetProxyPort();

        float elapsed = 0f;
        float sinceBroadcast = ProxyDiscoveryIntervalSeconds;

        NetDataWriter writer = new();
        writer.Put("FUSION_SERVER_DISCOVERY");

        while (generation == _loginGeneration && _loginInProgress)
        {
            var currentClient = client;
            if (currentClient == null)
            {
                yield break;
            }

            currentClient.PollEvents();

            if (serverConnection == null && !_proxyConnectRequested && sinceBroadcast >= ProxyDiscoveryIntervalSeconds)
            {
                currentClient.SendBroadcast(writer, port);
                sinceBroadcast = 0f;
            }

            if (elapsed >= ProxyLoginTimeoutSeconds)
            {
                FailProxyLogin(generation, "Fusion Helper was not found. Start Fusion Helper, or place it in BONELAB/Fusion Helper, then press Log In again.");
                yield break;
            }

            elapsed += TimeReferences.DeltaTime;
            sinceBroadcast += TimeReferences.DeltaTime;
            yield return null;
        }
    }

    private void FailProxyLogin(int generation, string message)
    {
        if (generation != _loginGeneration || !_loginInProgress)
        {
            return;
        }

        _loginInProgress = false;
        _proxyConnectRequested = false;
        _isInitialized = false;

        try
        {
            client?.Stop();
        }
        catch (Exception e)
        {
            FusionLogger.LogException("stopping proxy client after login failure", e);
        }

        client = null;
        serverConnection = null;

        FusionLogger.Error(message);
        Notifier.Send(new Notification()
        {
            SaveToMenu = false,
            ShowPopup = true,
            PopupLength = 8f,
            Title = "Fusion Helper Required",
            Message = message,
            Type = NotificationType.ERROR
        });

        InvokeLoggedOutEvent();
    }

    private static int GetProxyPort()
    {
        var port = ClientSettings.ProxyPort.Value;
        if (port >= 1024 && port <= 65535)
        {
            return port;
        }

        FusionLogger.Error("Custom proxy port is invalid, using default 28340.");
        return 28340;
    }

    private static bool TryStartLocalFusionHelper(int port, uint applicationId)
    {
        if (PlatformHelper.IsAndroid)
        {
            return false;
        }

        try
        {
            if (Process.GetProcessesByName("Fusion Helper").Length > 0 || Process.GetProcessesByName("FusionHelper").Length > 0)
            {
                FusionLogger.Log("Fusion Helper is already running.");
                return true;
            }
        }
        catch (Exception e)
        {
            FusionLogger.LogException("checking for Fusion Helper process", e);
        }

        var userData = MelonEnvironment.UserDataDirectory;
        var gameRoot = Directory.GetParent(userData)?.FullName;

        string[] candidates =
        {
            Path.Combine(gameRoot ?? string.Empty, "Fusion Helper", "Fusion Helper.exe"),
            Path.Combine(gameRoot ?? string.Empty, "FusionHelper", "Fusion Helper.exe"),
            Path.Combine(gameRoot ?? string.Empty, "Fusion Helper.exe"),
            Path.Combine(userData, "Fusion Helper", "Fusion Helper.exe"),
            Path.Combine(userData, "FusionHelper", "Fusion Helper.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var workingDirectory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    File.WriteAllText(Path.Combine(workingDirectory, "port.txt"), port.ToString());
                    File.WriteAllText(Path.Combine(workingDirectory, "steam_appid.txt"), applicationId.ToString());
                }

                var startInfo = new ProcessStartInfo()
                {
                    FileName = candidate,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                };

                // Steam-emulated/non-Steam BONELAB builds can set process-level Steam
                // IDs for 1592190. A child process inherits those values, and they take
                // precedence over the Helper's steam_appid.txt, causing SteamAPI_Init to
                // fail even though the Helper was explicitly asked to use SteamVR.
                // Keep the out-of-process Steam owner pinned to Fusion's requested ID.
                var helperAppId = applicationId.ToString();
                startInfo.Environment["SteamAppId"] = helperAppId;
                startInfo.Environment["SteamGameId"] = helperAppId;

                Process.Start(startInfo);

                FusionLogger.Log($"Started local Fusion Helper on proxy port {port} for isolated SteamVR networking.");
                return true;
            }
            catch (Exception e)
            {
                FusionLogger.LogException("starting local Fusion Helper", e);
            }
        }

        return false;
    }

    public void EvaluateMessage(NetPeer fromPeer, NetPacketReader dataReader, byte channel, DeliveryMethod deliveryMethod)
    {
        EvaluateMessage(_loginGeneration, fromPeer, dataReader, channel, deliveryMethod);
    }

    private void EvaluateMessage(int generation, NetPeer fromPeer, NetPacketReader dataReader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            if (generation != _loginGeneration || !ReferenceEquals(fromPeer, serverConnection))
            {
                return;
            }

            if (!dataReader.TryGetByte(out byte rawMessageType))
            {
                LogMalformedProxyMessage("missing message type");
                return;
            }

            var messageType = (MessageTypes)rawMessageType;
            switch (messageType)
            {
                case MessageTypes.Ping:
                    if (!dataReader.TryGetDouble(out double sentAt))
                    {
                        LogMalformedProxyMessage("invalid ping");
                        return;
                    }

                    double currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    NetDataWriter pingWriter = NewWriter(MessageTypes.Ping);
                    pingWriter.Put(currentTime);
                    SendToProxyServer(pingWriter);
                    break;
                case MessageTypes.SteamID:
                    if (!dataReader.TryGetULong(out ulong steamId))
                    {
                        LogMalformedProxyMessage("invalid Steam ID");
                        return;
                    }

                    if (steamId == 0)
                    {
                        FailProxyLogin(generation, "Fusion Helper connected, but Steamworks failed to initialize. Make sure Steam is running and SteamVR is in your library.");
                        return;
                    }

                    SteamId = new SteamId() { Value = steamId };
                    PlayerIDManager.SetLongID(SteamId.Value);
                    _isInitialized = true;

                    if (_loginInProgress)
                    {
                        _loginInProgress = false;
                        InvokeLoggedInEvent();
                    }

                    NetDataWriter usernameWriter = NewWriter(MessageTypes.GetUsername);
                    usernameWriter.Put(SteamId.Value);
                    SendToProxyServer(usernameWriter);
                    FusionLogger.Log($"Steamworks initialized through Fusion Helper with SteamID {SteamId}.");
                    break;
                case MessageTypes.GetUsername:
                    if (!dataReader.TryGetString(out string username))
                    {
                        LogMalformedProxyMessage("invalid username");
                        return;
                    }

                    LocalPlayer.Username = username;
                    break;
                case MessageTypes.OnDisconnected:
                    if (!dataReader.TryGetULong(out ulong disconnectedId))
                    {
                        LogMalformedProxyMessage("invalid disconnect notification");
                        return;
                    }

                    if (PlayerIDManager.HasPlayerID(disconnectedId))
                    {
                        InternalServerHelpers.OnPlayerLeft(disconnectedId);
                        ConnectionSender.SendDisconnect(disconnectedId);
                    }
                    break;
                case MessageTypes.OnMessage:
                case MessageTypes.OnConnectionMessage:
                    if (!dataReader.TryGetBytesWithLength(out byte[] payload) || !dataReader.TryGetULong(out ulong platformId))
                    {
                        LogMalformedProxyMessage("invalid network payload");
                        return;
                    }

                    ProxySocketHandler.OnSocketMessageReceived(payload, messageType == MessageTypes.OnMessage, platformId);
                    break;
                case MessageTypes.OnConnectionDisconnected:
                    NetworkHelper.Disconnect();
                    break;
                case MessageTypes.JoinServer:
                    if (!dataReader.TryGetULong(out ulong serverId))
                    {
                        LogMalformedProxyMessage("invalid server ID");
                        return;
                    }

                    JoinServer(new SteamId() { Value = serverId });
                    break;
                case MessageTypes.StartServer:
                    _isServerActive = true;
                    _isConnectionActive = true;
                    InternalServerHelpers.OnStartServer();
                    RefreshServerCode();
                    break;
                case MessageTypes.LobbyIDs:
                case MessageTypes.LobbyMetadata:
                    if (_lobbyManager == null)
                    {
                        LogMalformedProxyMessage("lobby response arrived before layer initialization");
                        return;
                    }

                    _lobbyManager.HandleLobbyMessage(messageType, dataReader);
                    break;
                case MessageTypes.SteamFriends:
                    var friendIds = dataReader.GetULongArray();
                    if (friendIds.Length > 4096)
                    {
                        LogMalformedProxyMessage("friend list exceeded the accepted limit");
                        return;
                    }

                    FriendIds = new List<ulong>(friendIds);
                    break;
                default:
                    LogMalformedProxyMessage($"unknown message type {rawMessageType}");
                    break;
            }
        }
        catch (Exception e)
        {
            LogMalformedProxyMessage("message parsing failed", e);
        }
        finally
        {
            dataReader.Recycle();
        }
    }

    private void LogMalformedProxyMessage(string reason, Exception exception = null)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastMalformedMessageLogUtc).TotalSeconds < 5d)
        {
            return;
        }

        _lastMalformedMessageLogUtc = now;
        FusionLogger.Warn($"Ignored malformed Fusion Helper message: {reason}.");
        if (exception != null)
        {
            FusionLogger.LogException("parsing Fusion Helper message", exception);
        }
    }

    public override void OnDeinitializeLayer()
    {
        if (!_isLayerInitialized)
        {
            return;
        }

        _matchmaker?.CancelAll("network layer deinitialized");
        _lobbyManager?.CancelPending();
        Disconnect();

        UnHookSteamEvents();

        _voiceManager?.Disable();
        _voiceManager = null;
        _matchmaker = null;
        _lobbyManager = null;
        _currentLobby = null;
        _isLayerInitialized = false;
        _isInitialized = false;

        StopProxyClient("network layer deinitialization");

        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    public override void LogIn()
    {
        if (_loginInProgress || _isInitialized || _isLayerInitialized)
        {
            return;
        }

        _loginGeneration++;
        var generation = _loginGeneration;
        _loginInProgress = true;
        _proxyConnectRequested = false;

        // On desktop, the crash-safe SteamVR path is the out-of-process Fusion Helper.
        // Require it up front instead of silently waiting forever for a process that
        // is not installed. Android/Quest still discovers the Helper over the LAN.
        var proxyPort = GetProxyPort();
        if (!PlatformHelper.IsAndroid && !TryStartLocalFusionHelper(proxyPort, ApplicationID))
        {
            FailProxyLogin(generation, "Fusion Helper is required for crash-safe SteamVR networking on PC. Extract the bundled 'Fusion Helper' folder into the BONELAB folder, then press Log In again.");
            return;
        }

        if (client != null)
        {
            try
            {
                client.Stop();
            }
            catch (Exception e)
            {
                FusionLogger.LogException("stopping previous proxy client", e);
            }

            client = null;
            serverConnection = null;
        }

        EventBasedNetListener listener = new();
        client = new NetManager(listener)
        {
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = true,
            DisconnectOnUnreachable = true,
            DisconnectTimeout = 10000,
            PingInterval = 5000,
        };
        listener.NetworkReceiveEvent += (peer, reader, channel, method) => EvaluateMessage(generation, peer, reader, channel, method);
        listener.PeerConnectedEvent += (peer) =>
        {
            if (generation != _loginGeneration || !_loginInProgress)
            {
                peer.Disconnect();
                return;
            }

            // A LiteNetLib connection only proves that Fusion Helper is reachable.
            // Do not mark Fusion logged in until Helper has initialized Steam for the
            // requested App ID and returned a valid SteamID.
            serverConnection = peer;

            NetDataWriter writer = NewWriter(MessageTypes.SteamID);
            writer.Put(ApplicationID);
            SendToProxyServer(writer);
        };

        listener.PeerDisconnectedEvent += (peer, disconnectInfo) =>
        {
            if (generation != _loginGeneration)
            {
                return;
            }

            serverConnection = null;

            if (_loginInProgress)
            {
                FailProxyLogin(generation, "Fusion Helper disconnected before Steam finished initializing.");
                return;
            }

            if (!_isInitialized && !_isLayerInitialized)
            {
                return;
            }

            _isInitialized = false;
            FusionLogger.Error("Proxy has disconnected, logging out!");
            InvokeLoggedOutEvent();
        };

        listener.NetworkReceiveUnconnectedEvent += (endPoint, reader, messageType) =>
        {
            if (generation == _loginGeneration
                && _loginInProgress
                && !_proxyConnectRequested
                && reader.TryGetString(out string data)
                && data == "YOU_FOUND_ME")
            {
                FusionLogger.Log("Found the proxy server!");
                _proxyConnectRequested = true;

                try
                {
                    client?.Connect(endPoint, "ProxyConnection");
                }
                catch (Exception e)
                {
                    _proxyConnectRequested = false;
                    FusionLogger.LogException("connecting to Fusion Helper", e);
                }
            }

            reader.Recycle();
        };

        try
        {
            client.Start();
        }
        catch (Exception e)
        {
            FusionLogger.LogException("starting proxy client", e);
            FailProxyLogin(generation, "Fusion could not start the local proxy client.");
            return;
        }

        FusionLogger.Log($"Beginning proxy discovery (generation {generation}, timeout {ProxyLoginTimeoutSeconds:0}s)...");
        MelonCoroutines.Start(DiscoverServer(generation));
    }

    public override void LogOut()
    {
        if (!_loginInProgress && !_isInitialized && !_isLayerInitialized && client == null)
        {
            return;
        }

        _loginGeneration++;
        _loginInProgress = false;
        _proxyConnectRequested = false;
        _isInitialized = false;

        _matchmaker?.CancelAll("logout");
        _lobbyManager?.CancelPending();

        StopProxyClient("logout");

        InvokeLoggedOutEvent();
    }

    private void StopProxyClient(string reason)
    {
        try
        {
            client?.Stop();
        }
        catch (Exception e)
        {
            FusionLogger.LogException($"stopping proxy client during {reason}", e);
        }

        client = null;
        serverConnection = null;
        _proxyConnectRequested = false;
    }

    public override void OnUpdateLayer()
    {
        client?.PollEvents();
    }

    internal static NetDataWriter NewWriter(MessageTypes type)
    {
        NetDataWriter writer = new();
        writer.Put((byte)type);
        return writer;
    }

    public void SendToProxyServer(NetDataWriter writer)
    {
        if (serverConnection == null)
        {
            FusionLogger.Warn("Attempting to send data to a null server peer! Is the proxy active?");
            Notifier.Send(new Notification()
            {
                SaveToMenu = false,
                ShowPopup = true,
                PopupLength = 4,
                Title = "Connection Failed",
                Message = "Failed to send data to the proxy, is FusionHelper running on your computer?",
                Type = NotificationType.ERROR
            });
            return;
        }

        serverConnection.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    internal void SendToProxyServer(MessageTypes type)
    {
        if (serverConnection == null)
        {
            FusionLogger.Warn("Attempting to send data to a null server peer! Is the proxy active?");
            return;
        }

        NetDataWriter writer = NewWriter(type);
        serverConnection.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    public static List<ulong> FriendIds = new();
    public override bool IsFriend(ulong userId)
    {
        if (FriendIds.Contains(userId))
            return true;
        else
            return false;
    }

    public override void BroadcastMessage(NetworkChannel channel, NetMessage message)
    {
        if (IsHost)
        {
            ProxySocketHandler.BroadcastToClients(channel, message);
        }
        else
        {
            ProxySocketHandler.BroadcastToServer(channel, message);
        }
    }

    public override void SendToServer(NetworkChannel channel, NetMessage message)
    {
        ProxySocketHandler.BroadcastToServer(channel, message);
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

        MessageTypes type = channel == NetworkChannel.Unreliable ? MessageTypes.UnreliableSendFromServer : MessageTypes.ReliableSendFromServer;
        NetDataWriter writer = NewWriter(type);
        writer.Put(userId);
        byte[] data = message.ToByteArray();
        writer.PutBytesWithLength(data);
        SendToProxyServer(writer);
    }

    public override void StartServer()
    {
        SendToProxyServer(MessageTypes.StartServer);
    }

    public void JoinServer(SteamId serverId)
    {
        // Leave existing server
        if (_isConnectionActive || _isServerActive)
            Disconnect();

        NetDataWriter writer = NewWriter(MessageTypes.JoinServer);
        writer.Put(serverId);
        SendToProxyServer(writer);

        _isServerActive = false;
        _isConnectionActive = true;

        ConnectionSender.SendConnectionRequest();
    }

    public override void Disconnect(string reason = "")
    {
        // Make sure we are currently in a server
        if (!_isServerActive && !_isConnectionActive)
        {
            return;
        }

        try
        {
            SendToProxyServer(MessageTypes.Disconnect);
        }
        catch
        {
            FusionLogger.Log("Error closing socket server / connection manager");
        }

        _isServerActive = false;
        _isConnectionActive = false;

        InternalServerHelpers.OnDisconnect(reason);
    }

    public override void DisconnectUser(ulong platformID)
    {
        // Make sure we are the host
        if (!_isServerActive)
        {
            return;
        }

        NetDataWriter writer = NewWriter(MessageTypes.DisconnectUser);

        writer.Put(platformID);

        SendToProxyServer(writer);
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
        if (Matchmaker == null)
        {
            return;
        }

#if DEBUG
        FusionLogger.Log($"Searching for servers with code {code}...");
#endif

        Matchmaker.RequestLobbiesByCode(code, (info) =>
        {
            if (info.Lobbies.Length <= 0)
            {
                return;
            }

            JoinServer(info.Lobbies[0].Metadata.LobbyInfo.LobbyID);
        });
    }

    private void HookSteamEvents()
    {
        // Add server hooks
        MultiplayerHooking.OnPlayerJoined += OnPlayerJoin;
        MultiplayerHooking.OnPlayerLeft += OnPlayerLeave;
        MultiplayerHooking.OnDisconnected += OnDisconnect;

        LobbyInfoManager.OnLobbyInfoChanged += OnUpdateLobby;

        _currentLobby = new ProxyNetworkLobby();
    }

    private void OnPlayerJoin(PlayerID id)
    {
        if (!id.IsMe)
            _voiceManager.GetSpeaker(id);

        OnUpdateLobby();
    }

    private void OnPlayerLeave(PlayerID id)
    {
        _voiceManager.RemoveSpeaker(id);
    }

    private void OnDisconnect()
    {
        _voiceManager.ClearManager();
    }

    private void UnHookSteamEvents()
    {
        // Remove server hooks
        MultiplayerHooking.OnPlayerJoined -= OnPlayerJoin;
        MultiplayerHooking.OnPlayerLeft -= OnPlayerLeave;
        MultiplayerHooking.OnDisconnected -= OnDisconnect;

        LobbyInfoManager.OnLobbyInfoChanged -= OnUpdateLobby;
    }

    public void OnUpdateLobby()
    {
        // Make sure the lobby exists
        if (Lobby == null)
        {
#if DEBUG
            FusionLogger.Warn("Tried updating the proxy lobby, but it was null!");
#endif
            return;
        }

        // Write active info about the lobby
        LobbyMetadataSerializer.WriteInfo(Lobby);

        // Request Steam Friends
        NetDataWriter writer = NewWriter(MessageTypes.SteamFriends);
        SendToProxyServer(writer);
    }
}
