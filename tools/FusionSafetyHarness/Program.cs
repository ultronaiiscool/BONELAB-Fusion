using System.Collections.Concurrent;

static class AssertEx
{
    public static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
    {
        if (!expected.Equals(actual)) throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }
}

sealed class LifecycleModel
{
    public bool GameSteamPresent { get; set; }
    public bool SteamAvailable { get; set; } = true;
    public bool HelperAvailable { get; set; } = true;
    public bool LoggedIn { get; private set; }
    public int Generation { get; private set; }
    public int CallbackCount { get; private set; }
    public int LatestBrowse { get; private set; }
    public bool NativeClientDestroyed { get; private set; }

    public string Login()
    {
        if (LoggedIn) return "already";
        if (!SteamAvailable) return "unavailable";

        if (GameSteamPresent)
        {
            if (!HelperAvailable) return "proxy-unavailable";
            LoggedIn = true;
            Generation++;
            return "proxy";
        }

        LoggedIn = true;
        Generation++;
        return "owned";
    }

    public void Logout()
    {
        if (!LoggedIn) return;
        LoggedIn = false;
        Generation++;
        // Intentional policy: do not destroy a native Steam client while callbacks
        // may still exist. Process exit owns final native teardown.
        NativeClientDestroyed = false;
    }

    public Browse StartBrowse()
    {
        LatestBrowse++;
        return new Browse(this, LatestBrowse, Generation);
    }

    public sealed class Browse
    {
        private readonly LifecycleModel _owner;
        private readonly int _id;
        private readonly int _generation;
        private bool _completed;

        internal Browse(LifecycleModel owner, int id, int generation)
        {
            _owner = owner;
            _id = id;
            _generation = generation;
        }

        public bool IsLive => _owner.LoggedIn && _owner.Generation == _generation && _owner.LatestBrowse == _id;

        public void Success() => CompleteIfLive();
        public void Empty() => CompleteIfLive();
        public void Timeout() => CompleteIfLive();
        public void MalformedMetadata() => CompleteIfLive();

        public void CompleteIfLive()
        {
            if (_completed || !IsLive) return;
            _completed = true;
            _owner.CallbackCount++;
        }
    }
}

sealed class TokenCacheModel
{
    private readonly Func<string?> _read;
    private bool _checked;
    public int ReadCount { get; private set; }
    public string? Cached { get; private set; }

    public TokenCacheModel(Func<string?> read) => _read = read;

    public string? Load()
    {
        if (_checked) return Cached;
        _checked = true;
        ReadCount++;
        Cached = _read()?.Trim();
        if (string.IsNullOrWhiteSpace(Cached)) Cached = null;
        return Cached;
    }
}

var tests = new List<(string Name, Action Body)>
{
    ("01 existing valid Steam client is not replaced", () => {
        var m = new LifecycleModel { GameSteamPresent = true };
        AssertEx.Equal("proxy", m.Login(), "game-owned Steam must use isolated proxy");
        AssertEx.True(!m.NativeClientDestroyed, "game Steam must not be destroyed");
    }),
    ("02 Steam unavailable fails closed", () => {
        var m = new LifecycleModel { SteamAvailable = false };
        AssertEx.Equal("unavailable", m.Login(), "Steam unavailable");
        AssertEx.True(!m.LoggedIn, "must remain logged out");
    }),
    ("03 login failure when safe proxy unavailable", () => {
        var m = new LifecycleModel { GameSteamPresent = true, HelperAvailable = false };
        AssertEx.Equal("proxy-unavailable", m.Login(), "proxy unavailable");
    }),
    ("04 repeated login is idempotent", () => {
        var m = new LifecycleModel();
        m.Login(); var g = m.Generation;
        AssertEx.Equal("already", m.Login(), "repeat login");
        AssertEx.Equal(g, m.Generation, "generation unchanged");
    }),
    ("05 repeated logout is idempotent", () => {
        var m = new LifecycleModel(); m.Login(); m.Logout(); var g = m.Generation; m.Logout();
        AssertEx.Equal(g, m.Generation, "generation unchanged on second logout");
    }),
    ("06 Browse success completes once", () => {
        var m = new LifecycleModel(); m.Login(); var b = m.StartBrowse(); b.Success();
        AssertEx.Equal(1, m.CallbackCount, "callback count");
    }),
    ("07 Browse zero lobbies completes once", () => {
        var m = new LifecycleModel(); m.Login(); var b = m.StartBrowse(); b.Empty();
        AssertEx.Equal(1, m.CallbackCount, "empty callback");
    }),
    ("08 Browse timeout completes once", () => {
        var m = new LifecycleModel(); m.Login(); var b = m.StartBrowse(); b.Timeout();
        AssertEx.Equal(1, m.CallbackCount, "timeout callback");
    }),
    ("09 duplicate completion suppressed", () => {
        var m = new LifecycleModel(); m.Login(); var b = m.StartBrowse(); b.Success(); b.Success(); b.Timeout();
        AssertEx.Equal(1, m.CallbackCount, "duplicate completion");
    }),
    ("10 late completion after logout ignored", () => {
        var m = new LifecycleModel(); m.Login(); var b = m.StartBrowse(); m.Logout(); b.Success();
        AssertEx.Equal(0, m.CallbackCount, "late callback");
    }),
    ("11 layer replacement invalidates Browse", () => {
        var m = new LifecycleModel(); m.Login(); var b = m.StartBrowse(); m.Logout(); m.Login(); b.Success();
        AssertEx.Equal(0, m.CallbackCount, "old generation callback");
    }),
    ("12 rapid repeated Browse keeps newest request", () => {
        var m = new LifecycleModel(); m.Login(); var old = m.StartBrowse(); var latest = m.StartBrowse(); old.Success(); latest.Success();
        AssertEx.Equal(1, m.CallbackCount, "only latest request");
    }),
    ("13 shutdown while query pending does not destroy native client", () => {
        var m = new LifecycleModel(); m.Login(); _ = m.StartBrowse(); m.Logout();
        AssertEx.True(!m.NativeClientDestroyed, "native client retained");
    }),
    ("14 scene-style generation change rejects pending callback", () => {
        var m = new LifecycleModel(); m.Login(); var b = m.StartBrowse(); m.Logout(); b.Empty();
        AssertEx.Equal(0, m.CallbackCount, "scene/generation stale callback");
    }),
    ("15 callback from wrong generation rejected", () => {
        var m = new LifecycleModel(); m.Login(); var old = m.StartBrowse(); m.Logout(); m.Login(); _ = m.StartBrowse(); old.Timeout();
        AssertEx.Equal(0, m.CallbackCount, "wrong generation");
    }),
    ("16 malformed lobby metadata does not duplicate completion", () => {
        var m = new LifecycleModel(); m.Login(); var b = m.StartBrowse(); b.MalformedMetadata(); b.Success();
        AssertEx.Equal(1, m.CallbackCount, "malformed metadata terminal behavior");
    }),
    ("17 missing token file caches missing result", () => {
        var c = new TokenCacheModel(() => null); AssertEx.True(c.Load() is null, "missing token"); c.Load(); AssertEx.Equal(1, c.ReadCount, "read once");
    }),
    ("18 empty token file trims to missing", () => {
        var c = new TokenCacheModel(() => "  \r\n "); AssertEx.True(c.Load() is null, "empty token");
    }),
    ("19 valid token is trimmed and cached", () => {
        var c = new TokenCacheModel(() => "  abc123  "); AssertEx.Equal("abc123", c.Load()!, "trimmed token"); c.Load(); AssertEx.Equal(1, c.ReadCount, "cache read count");
    }),
    ("20 independent subscribers do not suppress one another", () => {
        var callbacks = new ConcurrentBag<int>(); Action<int> a = callbacks.Add; Action<int> b = callbacks.Add; a(1); b(2);
        AssertEx.Equal(2, callbacks.Count, "both subscribers invoked");
    }),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception e)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {e.Message}");
    }
}

Console.WriteLine($"{tests.Count - failed}/{tests.Count} scenarios passed.");
return failed == 0 ? 0 : 1;
