using Il2CppSLZ.Marrow.Forklift;

using LabFusion.Utilities;

using MelonLoader;
using MelonLoader.Utils;

using Newtonsoft.Json.Linq;

using System.Collections;

namespace LabFusion.Downloading.ModIO;

public static class ModIOSettings
{
    public const string ApiPath = "https://api.mod.io/v1/games/";
    public const int GameID = 3809; // BONELAB GameID
    public const string ExternalTokenFileName = "FusionModIOToken.txt";

    public static string GameApiPath => $"{ApiPath}{GameID}/mods/";
    public static string ExternalTokenPath => Path.Combine(MelonEnvironment.UserDataDirectory, ExternalTokenFileName);

    private static readonly object _tokenLock = new();

    private static string _loadedToken = null;
    public static string LoadedToken => _loadedToken;

    private static bool _isLoadingToken = false;
    public static bool IsLoadingToken => _isLoadingToken;

    private static bool _tokenLoadCompleted = false;
    private static bool _externalTokenChecked = false;
    private static bool _loggedMissingToken = false;
    private static bool _loggedExternalTokenError = false;

    private static Action<string> _tokenLoadCallback = null;

    public static string FormatFilePath(int modId, int fileId)
    {
        return $"{GameApiPath}{modId}/files/{fileId}";
    }

    public static string FormatDownloadPath(int modId, int fileId)
    {
        return $"{FormatFilePath(modId, fileId)}/download";
    }

    public static void LoadToken(Action<string> loadCallback)
    {
        bool invokeImmediately;
        string cachedToken;

        lock (_tokenLock)
        {
            cachedToken = _loadedToken;
            invokeImmediately = _tokenLoadCompleted;

            if (!invokeImmediately)
            {
                _tokenLoadCallback += loadCallback;

                if (_isLoadingToken)
                {
                    return;
                }

                _isLoadingToken = true;
            }
        }

        if (invokeImmediately)
        {
            // Preserve the original asynchronous contract. Invoking a cached callback
            // inline allows a callback that calls LoadToken again to recurse forever.
            MelonCoroutines.Start(CoInvokeTokenCallback(loadCallback, cachedToken));
            return;
        }

        MelonCoroutines.Start(CoLoadToken());
    }

    private static IEnumerator CoInvokeTokenCallback(Action<string> callback, string token)
    {
        yield return null;
        InvokeTokenCallback(callback, token);
    }

    private static IEnumerator CoLoadToken()
    {
        // Prefer the explicit Fusion token file. This replaces the old external
        // FusionTokenBridge patch while preserving Fusion's normal settings fallback.
        if (!_externalTokenChecked)
        {
            _externalTokenChecked = true;

            var externalPath = ExternalTokenPath;

            if (File.Exists(externalPath))
            {
                Task<string> externalReadTask;

                try
                {
                    externalReadTask = File.ReadAllTextAsync(externalPath);
                }
                catch (Exception e)
                {
                    LogExternalTokenError("Could not open UserData/FusionModIOToken.txt.", e);
                    externalReadTask = null;
                }

                if (externalReadTask != null)
                {
                    while (!externalReadTask.IsCompleted)
                    {
                        yield return null;
                    }

                    if (externalReadTask.IsCompletedSuccessfully)
                    {
                        var token = externalReadTask.Result?.Trim();

                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            FusionLogger.Log("Loaded mod.io token from UserData/FusionModIOToken.txt.");
                            EndLoadToken(token);
                            yield break;
                        }

                        LogExternalTokenError("UserData/FusionModIOToken.txt is empty.");
                    }
                    else
                    {
                        LogExternalTokenError("Could not read UserData/FusionModIOToken.txt.", externalReadTask.Exception);
                    }
                }
            }
            else
            {
                LogExternalTokenError("UserData/FusionModIOToken.txt was not found; falling back to Fusion's normal mod.io settings.");
            }
        }

        // Preserve Fusion's native mod.io configuration as a fallback.
        var settingsPath = ModDownloader.ModSettingsPath;

        if (!File.Exists(settingsPath))
        {
            LogMissingToken();
            EndLoadToken(null);
            yield break;
        }

        Task<string> settingsTask;

        try
        {
            settingsTask = File.ReadAllTextAsync(settingsPath);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("opening mod.io settings", e);
            LogMissingToken();
            EndLoadToken(null);
            yield break;
        }

        while (!settingsTask.IsCompleted)
        {
            yield return null;
        }

        if (!settingsTask.IsCompletedSuccessfully)
        {
            FusionLogger.Error("Failed reading mod.io token from settings!");
            LogMissingToken();
            EndLoadToken(null);
            yield break;
        }

        try
        {
            var settingsJson = JObject.Parse(settingsTask.Result);
            var token = settingsJson["mod.io.access_token"]?.ToString()?.Trim();

            if (string.IsNullOrWhiteSpace(token))
            {
                LogMissingToken();
                EndLoadToken(null);
                yield break;
            }

            EndLoadToken(token);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("parsing mod.io settings", e);
            LogMissingToken();
            EndLoadToken(null);
        }
    }

    private static void EndLoadToken(string token)
    {
        Action<string> callbacks;

        lock (_tokenLock)
        {
            _loadedToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            _tokenLoadCompleted = true;
            _isLoadingToken = false;

            callbacks = _tokenLoadCallback;
            _tokenLoadCallback = null;
        }

        if (callbacks == null)
        {
            return;
        }

        // Invoke a stable snapshot. A callback may call LoadToken again; because the
        // load is already marked complete that call receives the cached result instead
        // of recursively starting another coroutine.
        foreach (var callback in callbacks.GetInvocationList().Cast<Action<string>>())
        {
            InvokeTokenCallback(callback, _loadedToken);
        }
    }

    private static void InvokeTokenCallback(Action<string> callback, string token)
    {
        if (callback == null)
        {
            return;
        }

        try
        {
            callback(token);
        }
        catch (Exception e)
        {
            FusionLogger.LogException("invoking token load callback", e);
        }
    }

    private static void LogMissingToken()
    {
        if (_loggedMissingToken)
        {
            return;
        }

        FusionLogger.Error("mod.io token is missing! Add UserData/FusionModIOToken.txt or set the token in the BONELAB mods menu.");
        _loggedMissingToken = true;
    }

    private static void LogExternalTokenError(string message, Exception exception = null)
    {
        if (_loggedExternalTokenError)
        {
            return;
        }

        FusionLogger.Error(message);

        if (exception != null)
        {
            FusionLogger.LogException("reading Fusion mod.io token file", exception);
        }

        _loggedExternalTokenError = true;
    }
}
