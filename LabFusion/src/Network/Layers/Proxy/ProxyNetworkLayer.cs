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

    private IMatchmaker _matchmaker = null;
    public override IMatchmaker Matchmaker => _matchmaker;

    protected bool _isServerActive = false;
    protected bool _isConnectionActive = false;

    protected ulong _targetServerId;

    protected string _targetJoinId;

    protected bool _isInitialized = false;

    private NetManager client;
    private NetPeer serverConnection;
    private ProxyLobbyManager _lobbyManager;

    private const float ProxyLoginTimeoutSeconds = 20f;
    private const float ProxyDiscoveryIntervalSeconds = 1f;
    private bool _loginInProgress;
    private int _loginGeneration;

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

            if (serverConnection == null && sinceBroadcast >= ProxyDiscoveryIntervalSeconds)
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

    private static bool TryStartLocalFusionHelper(int port)
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
                }

                Process.Start(new ProcessStartInfo()
                {
                    FileName = candidate,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true,
                });

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
        ulong id = dataReader.GetByte();
        switch (id)
        {
            case (ulong)MessageTypes.Ping:
                {
                    double theTime = dataReader.GetDouble();
                    double curTime = DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;
                    FusionLogger.Log("Server -> Client = " + (curTime - theTime) + " ms.");
                    NetDataWriter writer = NewWriter(MessageTypes.Ping);
                    writer.Put(curTime);
                    SendToProxyServer(writer);
                    break;
                }
            case (ulong)MessageTypes.SteamID:
                {
                    SteamId = new SteamId()
                    {
                        Value = dataReader.GetULong()
                    };

                    if (SteamId.Value == 0)
                    {
                        FailProxyLogin(_loginGeneration, "Fusion Helper connected, but Steamworks failed to initialize. Make sure Steam is running and SteamVR is in your library.");
                        break;
                    }

                    PlayerIDManager.SetLongID(SteamId.Value);
                    _isInitialized = true;

                    if (_loginInProgress)
                    {
                        _loginInProgress = false;
                        InvokeLoggedInEvent();
                    }

                    NetDataWriter writer = NewWriter(MessageTypes.GetUsername);
                    writer.Put(SteamId.Value);
                    SendToProxyServer(writer);

                    FusionLogger.Log($"Steamworks initialized through Fusion Helper with SteamID {SteamId}.");
                    break;
                }
            case (ulong)MessageTypes.GetUsername:
                {
                    string username = dataReader.GetString();
                    LocalPlayer.Username = username;
                }
                break;
            case (ulong)MessageTypes.OnDisconnected:
                ulong longId = dataReader.GetULong();
                if (PlayerIDManager.HasPlayerID(longId))
                {
                    // Update the mod so it knows this user has left
                    InternalServerHelpers.OnPlayerLeft(longId);

                    // Send disconnect notif to everyone
                    ConnectionSender.SendDisconnect(longId);
                }
                break;
            case (ulong)MessageTypes.OnMessage:
                {
                    byte[] data = dataReader.GetBytesWithLength();
                    ulong platformID = dataReader.GetULong();

                    ProxySocketHandler.OnSocketMessageReceived(data, true, platformID);
                    break;
                }
            case (ulong)MessageTypes.OnConnectionDisconnected:
                NetworkHelper.Disconnect();
                break;
            case (ulong)MessageTypes.OnConnectionMessage:
                {
                    byte[] data = dataReader.GetBytesWithLength();
                    ulong platformID = dataReader.GetULong();

                    ProxySocketHandler.OnSocketMessageReceived(data, false, platformID);
                    break;
                }
            case (ulong)MessageTypes.JoinServer:
                {
                    ulong serverId = dataReader.GetULong();
                    JoinServer(new SteamId()
                    {
                        Value = serverId
                    });
                }
                break;
            case (ulong)MessageTypes.StartServer:
                {
                    _isServerActive = true;
                    _isConnectionActive = true;

                    // Call server setup
                    InternalServerHelpers.OnStartServer();

                    RefreshServerCode();
                    break;
                }
            case (ulong)MessageTypes.LobbyIDs:
            case (ulong)MessageTypes.LobbyMetadata:
                {
                    _lobbyManager.HandleLobbyMessage((MessageTypes)id, dataReader);
                    break;
                }
            case (ulong)MessageTypes.SteamFriends:
                {
                    FriendIds = dataReader.GetULongArray().ToList();
                    break;
                }
        }

        dataReader.Recycle();
    }

    public override void OnDeinitializeLayer()
    {
        Disconnect();

        UnHookSteamEvents();

        _voiceManager.Disable();
        _voiceManager = null;
    }

    public override void LogIn()
    {
        if (_loginInProgress)
        {
            FusionLogger.Warn("Proxy login is already in progress.");
            return;
        }

        _loginGeneration++;
        var generation = _loginGeneration;
        _loginInProgress = true;

        // On desktop, the crash-safe SteamVR path is the out-of-process Fusion Helper.
        // Require it up front instead of silently waiting forever for a process that
        // is not installed. Android/Quest still discovers the Helper over the LAN.
        var proxyPort = GetProxyPort();
        if (!PlatformHelper.IsAndroid && !TryStartLocalFusionHelper(proxyPort))
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
        listener.NetworkReceiveEvent += EvaluateMessage;
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

            FusionLogger.Error("Proxy has disconnected, logging out!");
            InvokeLoggedOutEvent();
        };

        listener.NetworkReceiveUnconnectedEvent += (endPoint, reader, messageType) =>
        {
            if (generation == _loginGeneration && _loginInProgress && reader.TryGetString(out string data) && data == "YOU_FOUND_ME")
            {
                FusionLogger.Log("Found the proxy server!");
                client?.Connect(endPoint, "ProxyConnection");
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
        _loginGeneration++;
        _loginInProgress = false;

        try
        {
            client?.Stop();
        }
        catch (Exception e)
        {
            FusionLogger.LogException("stopping proxy client during logout", e);
        }

        client = null;
        serverConnection = null;

        InvokeLoggedOutEvent();
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
