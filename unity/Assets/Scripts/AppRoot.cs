using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

public class AppRoot : MonoBehaviour
{
    private void Awake()
    {
        // Allow execution in the background.
        Application.runInBackground = true;        
        // Control the frame-rate limit through targetFrameRate, rather than vSync.
        // Unity ignores targetFrameRate when vSyncCount is 1 or greater.
        // The Unity window is covered by Tauri's UI window in this game,
        // so while occluded, Present does not wait for vblank and vSync no longer throttles rendering.
        // Explicitly disable vSync to ensure the targetFrameRate limit remains active
        // and prevents excessive GPU use even in that state.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
#if UNITY_WEBGL && !UNITY_EDITOR
        // The Web build runs in the page canvas. Accept keys only while the canvas has focus, allowing typing in UI input fields;
        // let the browser control frame timing (a fixed targetFrameRate would use setTimeout).
        WebGLInput.captureAllKeyboardInput = false;
        Application.targetFrameRate = -1;
#endif
        DontDestroyOnLoad(gameObject); 
    }

    private IEnumerator Start()
    {
        UnityEngine.Debug.Log("AppRoot: Starting app...");

        // Read the backend API base URL from command-line arguments.
        // Launches from city-genesis/tauri include --api-base-url.
        // If omitted, retain ApiClient.BASE_URL's initial value (127.0.0.1:5000).
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--api-base-url")
            {
                ApiClient.BASE_URL = args[i + 1];
                // Log only the selected URL. Full launch arguments may contain credentials.
                UnityEngine.Debug.Log($"AppRoot: API ベース URL を {ApiClient.BASE_URL} に設定しました");
                break;
            }
        }

        // Initialize SteamAPI for the Steam overlay. Tauri handles authentication.
        // When Steam is unavailable, SteamService only warns and the game continues.
        if (SteamService.Instance == null)
        {
            var steamGO = new GameObject("Steam");
            DontDestroyOnLoad(steamGO);
            steamGO.AddComponent<SteamService>();
        }

        // Only one IPC pair is needed for the entire application.
        // Adding a new GameObject when an instance already exists
        // can cause initialization to race with a component awaiting destruction,
        // so AppRoot explicitly checks for an existing instance before creation.
        if (IpcClient.Instance == null)
        {
            var ipcGO = new GameObject("IpcClient");
            DontDestroyOnLoad(ipcGO);

            var ipcClient = ipcGO.AddComponent<IpcClient>();
            ipcClient.Initialize();

            ipcGO.AddComponent<IpcStateCoordinator>();
            ipcGO.AddComponent<IpcViewCoordinator>();
            ipcGO.AddComponent<IpcEventHandler>();
            ipcGO.AddComponent<AssetManagerRenderer>();
        }

        // Production builds exit on their own even if the UI's shutdown notification does not arrive.
        // Editor and Development Build allow restarting the backend and continuing development.
        if (!Application.isEditor && !Debug.isDebugBuild) StartCoroutine(BackendHealthPolling());

        // Notify Tauri that startup is complete, both on first launch and
        // whenever Tauri automatically restarts Unity after a crash.
        // Tauri clears popups only on the second and subsequent notifications
        // (after a crash restart). The first notification leaves loading screens and other ongoing user interactions open.
        do {
            yield return BackendCompatibility.Verify();
            IpcClient.Instance?.EmitGraphicReady();
            if (!BackendCompatibility.VersionReceived) yield return new WaitForSeconds(2f);
        } while (!BackendCompatibility.VersionReceived);
        if (!BackendCompatibility.Compatible) yield break;
        yield return new WaitUntil(() => IpcClient.Instance != null && IpcClient.Instance.ProtocolCompatible);

        if (WeatherController.Instance == null)
            gameObject.AddComponent<WeatherController>();
        if (GetComponent<CameraAmbience>() == null)
            gameObject.AddComponent<CameraAmbience>();
        if (GetComponent<ConstructionAmbience>() == null)
            gameObject.AddComponent<ConstructionAmbience>();
        if (GetComponent<VehicleAmbience>() == null)
            gameObject.AddComponent<VehicleAmbience>();
        if (VisualEffectManager.Instance == null)
            gameObject.AddComponent<VisualEffectManager>();

        // Initialize master data by calling Initialize on the instance manually placed in the scene.
        if (MasterDataStore.Instance != null)
        {
            StartCoroutine(MasterDataStore.Instance.Initialize());
        }

        // When launched after the UI (Tauri), Unity may miss the IPC game:start event,
        // so query the backend directly to check whether a game is running.
        StartCoroutine(PollBackendSessionState());
    }

    /// <summary>
    /// Periodically query the backend's /api/v1/sessions endpoint
    /// and enter the game state once current_save_id is set.
    ///
    /// Main purposes:
    /// - Recover when launched after Tauri and the IPC game:start event was missed.
    /// - Detect the ongoing session and return to the game when
    ///   Tauri automatically restarts Unity after a crash.
    ///
    /// Stop the loop once the game has started, including starts via IPC.
    /// Retry startup connection failures on the next polling cycle.
    /// Tauri polling and game:end notifications return development builds to the main menu after persistent disconnection.
    /// </summary>
    private IEnumerator PollBackendSessionState()
    {
        while (true)
        {
            if (GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted())
            {
                yield break;
            }

            if (GameStateManager.Instance != null && BackendCompatibility.Compatible && IpcClient.Instance != null && IpcClient.Instance.ProtocolCompatible)
            {
                using (ApiRequest call = ApiClient.Request("GET", "/sessions", timeoutSeconds: 5))
                {
                    yield return call.Send();
                    UnityWebRequest req = call.Web;
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            var data = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(req.downloadHandler.text);
                            if (data != null && data.TryGetValue("current_save_id", out var saveId) && saveId != null)
                            {
                                GameStateManager.Instance.SetGameStarted(true, null);
                                yield break;
                            }
                        }
                        catch (Exception e)
                        {
                            UnityEngine.Debug.LogWarning($"AppRoot: セッション状態のパースに失敗: {e.Message}");
                        }
                    }
                }
            }

            yield return new WaitForSeconds(2f);
        }
    }

    private IEnumerator BackendHealthPolling()
    {
        int failures = 0;
        while (true)
        {
            yield return new WaitForSecondsRealtime(5f);
            bool success = false;
            using (ApiRequest call = ApiClient.Request("GET", "/sessions", timeoutSeconds: 10))
            {
                yield return call.Send();
                if (call.Web.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var session = Newtonsoft.Json.Linq.JObject.Parse(call.Web.downloadHandler.text);
                        success = session.ContainsKey("current_save_id");
                    }
                    catch (Exception error) { Debug.LogWarning($"AppRoot: セッション確認の応答が不正です: {error.Message}"); }
                }
            }
            ApiClient.HealthBackendConnected = success;
            failures = success ? 0 : failures + 1;
            if (failures >= 2)
            {
                Debug.LogWarning("AppRoot: バックエンドの通信確認が2回連続で失敗したため終了します");
                Application.Quit();
                yield break;
            }
        }
    }

    private void OnApplicationQuit()
    {
        // Disconnect IPC.
        IpcClient.Instance?.Disconnect();
    }

}

public static class SceneNames
{
    public const string Game    = "GameScene";
}
