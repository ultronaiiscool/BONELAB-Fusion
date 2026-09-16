using Il2CppSLZ.Marrow.Forklift.Model;

using LabFusion.Utilities;

using MelonLoader;

using Newtonsoft.Json.Linq;

using System.Collections;

namespace LabFusion.Downloading.ModIO;

public static class ModIOManager
{
    private const int RequestAttempts = 3;

    public static ModIOModTarget GetTargetFromListing(ModListing listing)
    {
        if (listing == null)
        {
            return null;
        }

        foreach (var target in listing.Targets.Values)
        {
            var modIOTarget = target.TryCast<ModIOModTarget>();

            if (modIOTarget != null)
            {
                return modIOTarget;
            }
        }

        return null;
    }

    public static void GetMod(int modID, ModCallback modCallback)
    {
        var url = $"{ModIOSettings.GameApiPath}{modID}";

        ModIOSettings.LoadToken(OnTokenLoaded);

        void OnTokenLoaded(string token)
        {
            // If the token is null, it likely didn't load
            if (string.IsNullOrWhiteSpace(token))
            {
                modCallback?.Invoke(ModCallbackInfo.FailedCallback);

                return;
            }

            MelonCoroutines.Start(CoGetMod(url, token, modCallback));
        }
    }

    private static IEnumerator CoGetMod(string url, string token, ModCallback modCallback)
    {
        var handler = new HttpClientHandler()
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        using HttpClient client = new(handler);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

        HttpResponseMessage response = null;

        for (var attempt = 1; attempt <= RequestAttempts; attempt++)
        {
            Task<HttpResponseMessage> responseTask;
            try
            {
                responseTask = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (Exception e)
            {
                FusionLogger.LogException($"starting mod.io metadata request for mod {new Uri(url).Segments[^1]} (attempt {attempt}/{RequestAttempts})", e);
                continue;
            }

            while (!responseTask.IsCompleted)
            {
                yield return null;
            }

            if (!responseTask.IsCompletedSuccessfully)
            {
                FusionLogger.LogException($"requesting mod.io metadata (attempt {attempt}/{RequestAttempts})", responseTask.Exception);
                continue;
            }

            var candidate = responseTask.Result;
            if (candidate.IsSuccessStatusCode)
            {
                response = candidate;
                break;
            }

            FusionLogger.Warn($"mod.io metadata request returned HTTP {(int)candidate.StatusCode} (attempt {attempt}/{RequestAttempts}).");
            candidate.Dispose();

            if ((int)candidate.StatusCode < 500)
            {
                break;
            }
        }

        if (response == null)
        {
            modCallback?.Invoke(ModCallbackInfo.FailedCallback);
            yield break;
        }

        using (response)
        {
            var streamTask = response.Content.ReadAsStreamAsync();

            while (!streamTask.IsCompleted)
            {
                yield return null;
            }

            if (!streamTask.IsCompletedSuccessfully)
            {
                FusionLogger.LogException("reading mod.io metadata response", streamTask.Exception);
                modCallback?.Invoke(ModCallbackInfo.FailedCallback);
                yield break;
            }

            Task<string> jsonTask;

            try
            {
                jsonTask = new StreamReader(streamTask.Result).ReadToEndAsync();
            }
            catch (Exception e)
            {
                FusionLogger.LogException("reading mod.io mod stream", e);

                modCallback?.Invoke(ModCallbackInfo.FailedCallback);
                yield break;
            }

            while (!jsonTask.IsCompleted)
            {
                yield return null;
            }

            if (!jsonTask.IsCompletedSuccessfully)
            {
                FusionLogger.LogException("reading mod.io metadata JSON", jsonTask.Exception);
                modCallback?.Invoke(ModCallbackInfo.FailedCallback);
                yield break;
            }

            try
            {
                var jObject = JObject.Parse(jsonTask.Result);

                var modData = new ModData(jObject);
                var modCallbackInfo = new ModCallbackInfo()
                {
                    Data = modData,
                    Result = ModResult.SUCCEEDED,
                };

                modCallback?.Invoke(modCallbackInfo);
            }
            catch (Exception e)
            {
                FusionLogger.LogException("parsing mod.io metadata JSON", e);
                modCallback?.Invoke(ModCallbackInfo.FailedCallback);
            }
        }
    }

    public static string GetActivePlatform()
    {
        if (PlatformHelper.IsAndroid)
        {
            return "android";
        }
        else
        {
            return "windows";
        }
    }

    public static ModPlatformData? GetValidPlatform(ModData mod)
    {
        string activePlatform = GetActivePlatform();

        foreach (var platform in mod.Platforms)
        {
            if (platform.Platform != activePlatform)
            {
                continue;
            }

            return platform;
        }

        return null;
    }
}
