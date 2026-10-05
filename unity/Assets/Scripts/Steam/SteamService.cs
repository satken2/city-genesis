using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// SteamAPI のライフサイクルを管理するシングルトン。
///
/// Unity が SteamAPI を初期化するのは、3D を描くこのプロセスで Steam オーバーレイ
/// (Shift+Tab)やスクリーンショットを効かせるためだけである。認証チケットの取得と
/// 検証の依頼は、Steam の起動対象である Tauri が行う。
///
/// 初期化に失敗した場合(Steam クライアント未起動・非 Steam 環境・DLL 欠落)でも
/// ゲームは続行する。オーバーレイが出ないだけで、致命的ではない。
///
/// 注意: SteamAPI.RestartAppIfNecessary は意図的に呼ばない。
/// Steam の起動対象は Tauri(親プロセス)であり、子プロセスである Unity が
/// これを呼ぶと「Steam 経由での再起動」= Tauri の二重起動と既存プロセスツリーの
/// 破壊を招く。Init は親から継承した SteamAppId 環境変数(または開発時の
/// steam_appid.txt)で通る。同梱の標準 SteamManager.cs は
/// RestartAppIfNecessary を呼ぶため使用せず、本クラスで代替する。
/// </summary>
public class SteamService : MonoBehaviour
{
    public static SteamService Instance { get; private set; }

    /// <summary>SteamAPI.Init が成功したかどうか</summary>
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
