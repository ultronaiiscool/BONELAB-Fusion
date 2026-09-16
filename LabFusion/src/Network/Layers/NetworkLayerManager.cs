namespace LabFusion.Network;

using LabFusion.Utilities;

public static class NetworkLayerManager
{
    /// <summary>
    /// The active network transport layer.
    /// </summary>
    public static NetworkLayer Layer { get; private set; } = null;

    private static NetworkLayer _pendingLayer = null;

    /// <summary>
    /// Returns if there is an active network layer.
    /// </summary>
    public static bool HasLayer => Layer != null;

    private static bool _loggedIn = false;

    /// <summary>
    /// Returns if the user is logged into the active network layer.
    /// </summary>
    public static bool LoggedIn
    {
        get
        {
            return _loggedIn;
        }
        private set
        {
            _loggedIn = value;

            OnLoggedInChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Invoked when the user logs in or out of the active network layer.
    /// </summary>
    public static event Action<bool> OnLoggedInChanged;

    internal static void OnInitializeMelon()
    {
        NetworkLayer.OnLoggedInEvent += OnLoggedIn;
        NetworkLayer.OnLoggedOutEvent += OnLoggedOut;
    }

    public static NetworkLayer GetTargetLayer()
    {
        NetworkLayerDeterminer.LoadLayer();

        return NetworkLayerDeterminer.LoadedLayer;
    }

    public static void LogIn(NetworkLayer layer)
    {
        if (layer == null || (LoggedIn && ReferenceEquals(Layer, layer)) || ReferenceEquals(_pendingLayer, layer))
        {
            return;
        }

        var previousPending = _pendingLayer;
        _pendingLayer = layer;

        if (previousPending != null)
        {
            previousPending.LogOut();
        }

        try
        {
            layer.LogIn();
        }
        catch (Exception e)
        {
            if (ReferenceEquals(_pendingLayer, layer))
            {
                _pendingLayer = null;
            }

            FusionLogger.LogException($"logging into network layer {layer.Title}", e);
            LoggedIn = false;
        }
    }

    public static void LogOut()
    {
        if (Layer != null)
        {
            Layer.LogOut();
            return;
        }

        _pendingLayer?.LogOut();
    }

    private static void OnLoggedIn(NetworkLayer layer)
    {
        if (_pendingLayer != null && !ReferenceEquals(_pendingLayer, layer))
        {
            FusionLogger.Warn($"Ignoring a stale login completion from network layer {layer.Title}.");
            return;
        }

        _pendingLayer = null;

        var previousLayer = Layer;
        if (previousLayer != null && previousLayer != layer)
        {
            previousLayer.LogOut();
        }

        Layer = layer;

        layer.OnInitializeLayer();

        LoggedIn = true;
    }

    private static void OnLoggedOut(NetworkLayer layer)
    {
        if (ReferenceEquals(_pendingLayer, layer))
        {
            _pendingLayer = null;
            LoggedIn = false;
            return;
        }

        if (Layer == layer)
        {
            layer.OnDeinitializeLayer();
            Layer = null;
            LoggedIn = false;
        }
    }
}
