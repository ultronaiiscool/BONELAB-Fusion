namespace LabFusion.Network.Proxy;

public sealed class ProxySteamVRNetworkLayer : ProxyNetworkLayer
{
    public override uint ApplicationID => SteamVRNetworkLayer.SteamVRId;

    public override string Title => "Proxy SteamVR";

    public override string Platform => "Steam";

    // The proxy keeps Steam AppID 250820 in Fusion Helper instead of mutating
    // BONELAB's process-wide Steam context, so it is also the safe desktop fallback.
    public override bool CheckSupported() => true;
}
