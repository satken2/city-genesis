// Disable outside standalone builds supported by Steamworks.NET, such as Web builds. Conditions match Steamworks.NET's files.
#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif
using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// Singleton managing the SteamAPI lifecycle.
///
/// Unity initializes SteamAPI only to enable the Steam overlay (Shift+Tab)
/// and screenshots in this 3D-rendering process. Tauri, the application launched by Steam,
/// obtains authentication tickets and requests their verification.
///
/// The game continues even if initialization fails because Steam is not running,
/// the environment is outside Steam, or a DLL is missing. Only the overlay is unavailable; failure is not fatal.
///
/// Note: deliberately do not call SteamAPI.RestartAppIfNecessary.
/// Steam launches Tauri, the parent process. Calling it from Unity, the child process,
/// would restart through Steam, launching Tauri twice and disrupting the existing process tree.
/// Init works with the SteamAppId environment variable inherited from the parent,
/// or steam_appid.txt during development. The bundled standard SteamManager.cs
/// calls RestartAppIfNecessary, so this class replaces it.
/// </summary>
public class SteamService : MonoBehaviour
{
    public static SteamService Instance { get; private set; }

    /// <summary>Whether SteamAPI.Init succeeded.</summary>
    public bool Initialized { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

#if !DISABLESTEAMWORKS
        try
        {
            if (!Packsize.Test())
            {
                Debug.LogError("SteamService: Packsize テストに失敗しました。プラットフォームと Steamworks.NET のビルドが一致していません");
            }
            if (!DllCheck.Test())
            {
                Debug.LogError("SteamService: DllCheck テストに失敗しました。steam_api64.dll のバージョンが不正です");
            }

            Initialized = SteamAPI.Init();
            if (Initialized)
            {
                Debug.Log($"SteamService: SteamAPI 初期化に成功しました (AppID: {SteamUtils.GetAppID().m_AppId})");
            }
            else
            {
                Debug.LogWarning(
                    "SteamService: SteamAPI.Init に失敗しました。Steam オーバーレイなしで続行します" +
                    "(Steam クライアント未起動、または SteamAppId/steam_appid.txt 未設定)");
            }
        }
        catch (System.DllNotFoundException e)
        {
            Debug.LogWarning($"SteamService: steam_api64.dll をロードできません。Steam オーバーレイなしで続行します: {e.Message}");
            Initialized = false;
        }
#else
        Debug.Log("SteamService: このプラットフォームでは Steamworks が無効です (DISABLESTEAMWORKS)");
#endif
    }

    private void Update()
    {
#if !DISABLESTEAMWORKS
        if (Initialized)
        {
            SteamAPI.RunCallbacks();
        }
#endif
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        Instance = null;

#if !DISABLESTEAMWORKS
        if (Initialized)
        {
            SteamAPI.Shutdown();
            Initialized = false;
        }
#endif
    }
}
