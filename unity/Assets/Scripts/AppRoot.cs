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
        // バックグラウンド実行を許可する
        Application.runInBackground = true;        
        // フレームレート上限は vSync ではなく targetFrameRate で制御する。
        // Unity の仕様で vSyncCount が 1 以上だと targetFrameRate は無視される。
        // 本ゲームは Unity ウィンドウが Tauri の UI ウィンドウに覆われる構成のため、
        // occluded 状態では Present が vblank 待ちをせず vSync による抑制が消える。
        // その状態でも GPU 使用率が暴走しないよう、vSync を明示的に無効化して
        // targetFrameRate による上限を必ず有効にする。
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        DontDestroyOnLoad(gameObject); 
    }

    private IEnumerator Start()
    {
        UnityEngine.Debug.Log("AppRoot: Starting app...");

        // コマンドライン引数からバックエンドの API ベース URL を読み取る。
        // city-genesis/tauri から起動された場合、--api-base-url が付与される。
        // 指定がない場合は ApiClient.BASE_URL の初期値（127.0.0.1:5000）がそのまま使用される。
        string[] args = Environment.GetCommandLineArgs();
        UnityEngine.Debug.Log($"AppRoot: Raw arguments: {string.Join(" ", args)}");
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--api-base-url")
            {
                ApiClient.BASE_URL = args[i + 1];
                UnityEngine.Debug.Log($"AppRoot: API ベース URL を {ApiClient.BASE_URL} に設定しました");
                break;
            }
        }

        // Steam 連携(Steamworks.NET)。SteamAuthClient がバックエンドへチケットを
        // POST するため、BASE_URL 確定後に生成する必要がある。
        // Steam 不在時も SteamService が警告を出すだけでゲームは続行する。
        if (SteamService.Instance == null)
        {
            var steamGO = new GameObject("Steam");
            DontDestroyOnLoad(steamGO);
            steamGO.AddComponent<SteamService>();
            steamGO.AddComponent<SteamAuthClient>();
        }

        // IPC はアプリケーション全体で1組だけ存在すればよい。
        // 既存インスタンスがある場合に新しい GameObject を追加すると、
        // 破棄待ちのコンポーネントと初期化処理が競合しやすくなるため、
        // AppRoot 側で生成前に明示的に存在確認を行う。
        if (IpcClient.Instance == null)
        {
            var ipcGO = new GameObject("IpcClient");
            DontDestroyOnLoad(ipcGO);

            var ipcClient = ipcGO.AddComponent<IpcClient>();
            ipcClient.Initialize();

            ipcGO.AddComponent<IpcStateCoordinator>();
            ipcGO.AddComponent<IpcViewCoordinator>();
            ipcGO.AddComponent<IpcEventHandler>();
        }

        // 製品版はUI側の終了通知が届かない場合にも自分で終了する。
        // EditorとDevelopment Buildはバックエンドを再起動して開発を続けられる。
        if (!Application.isEditor && !Debug.isDebugBuild) StartCoroutine(BackendHealthPolling());

        // Tauri へ起動完了を通知する。初回起動と、クラッシュ後にスーパーバイザ
        // (genesis.py)が Unity を自動再起動した場合の両方で毎回送信する。
        // Tauri 側は2回目以降の受信(=クラッシュ後の再起動)でのみポップアップを
        // 一掃する。初回はユーザーが操作中のロード画面などを閉じないため何もしない。
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

        // マスターデータの初期化。シーン内に手動配置されたインスタンスの Initialize を呼び出す。
        if (MasterDataStore.Instance != null)
        {
            StartCoroutine(MasterDataStore.Instance.Initialize());
        }

        // UI（Tauri）より後から起動した場合、IPC 経由の game:start イベントを
        // 受け損ねる可能性があるため、自らバックエンドに問い合わせてゲーム開始状態を確認する。
        StartCoroutine(PollBackendSessionState());
    }

    /// <summary>
    /// バックエンドの /api/v1/sessions を定期的に問い合わせ、
    /// current_save_id が設定されたらゲーム開始状態に遷移するループ。
    ///
    /// 主な役割:
    /// - Tauri より後から起動して IPC の game:start を取りこぼした場合の保険
    /// - Unity がクラッシュしてスーパーバイザ(genesis.py)に自動再起動された場合に、
    ///   進行中のセッションを検知してゲームへ復帰する経路
    ///
    /// ゲーム開始済みになった時点でループを終了する(IPC 経由で開始された場合も含む)。
    /// 起動直後の通信失敗は次周期で再試行する。
    /// 開発版の持続的な切断時のトップメニュー復帰はTauriのポーリングと game:end 通知が担う。
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
                        if (session.Value<bool>("shutting_down"))
                        {
                            Application.Quit();
                            yield break;
                        }
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
        // IPC切断
        IpcClient.Instance?.Disconnect();
    }

}

public static class SceneNames
{
    public const string Game    = "GameScene";
}
