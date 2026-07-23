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
        // VSync破損時のGPU使用率暴走を防ぐため、ターゲットフレームレートを60に設定
        Application.targetFrameRate = 60;
        DontDestroyOnLoad(gameObject); 
    }

    private void Start()
    {
        UnityEngine.Debug.Log("AppRoot: Starting app...");

        // コマンドライン引数からバックエンドの API ベース URL を読み取る。
        // genesis-ui から起動された場合、--api-base-url が付与される。
        // 指定がない場合は ApiClient.BASE_URL の初期値（localhost:5000）がそのまま使用される。
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

        // Tauri へ起動完了を通知する。初回起動と、クラッシュ後にスーパーバイザ
        // (genesis.py)が Unity を自動再起動した場合の両方で毎回送信する。
        // Tauri 側はこれを受けてポップアップ一掃などの表示リセットを行う(冪等)。
        IpcClient.Instance?.EmitGraphicReady();

        // マスターデータの初期化。シーン内に手動配置されたインスタンスの Initialize を呼び出す。
        if (MasterDataStore.Instance != null)
        {
            StartCoroutine(MasterDataStore.Instance.Initialize());
        }

        // UI（Tauri）より後から起動した場合、IPC 経由の game:start イベントを
        // 受け損ねる可能性があるため、自らバックエンドに問い合わせてゲーム開始状態を確認する。
        StartCoroutine(PollBackendSessionState());

        // 本番ビルド（exe）でのみ、定期的にバックエンドの生存を確認する。
        // 開発ビルド（Development Build）やエディタ実行時は、一時的な通信切れでも終了しない。
        if (!Debug.isDebugBuild)
        {
            StartCoroutine(BackendHealthPolling());
        }
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
    /// 通信失敗(起動直後のバックエンド未準備・再起動レース等)は次周期で再試行する。
    /// バックエンドの死活監視は BackendHealthPolling が担う。
    /// </summary>
    private IEnumerator PollBackendSessionState()
    {
        while (true)
        {
            if (GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted())
            {
                yield break;
            }

            if (GameStateManager.Instance != null)
            {
                using (var req = UnityWebRequest.Get($"{ApiClient.BASE_URL}/sessions"))
                {
                    req.timeout = 5;
                    yield return req.SendWebRequest();
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

    private void OnApplicationQuit()
    {
        // IPC切断
        IpcClient.Instance?.Disconnect();
    }

    /// <summary>
    /// 一定間隔でバックエンドのセッションエンドポイントに問い合わせ、生存確認を行う。
    /// 2回連続で通信に失敗した場合はアプリケーションを終了する。
    /// 本番ビルド（非 Development Build）でのみ有効。
    /// </summary>
    private IEnumerator BackendHealthPolling()
    {
        int consecutiveFailures = 0;
        while (true)
        {
            yield return new WaitForSeconds(5f);

            using (var req = UnityWebRequest.Get($"{ApiClient.BASE_URL}/sessions"))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    consecutiveFailures = 0;
                }
                else
                {
                    consecutiveFailures++;
                    Debug.LogWarning($"AppRoot: バックエンド生存確認に失敗（連続{consecutiveFailures}回）: {req.error}");
                    if (consecutiveFailures >= 2)
                    {
                        Debug.LogError("AppRoot: バックエンドへの通信が2回連続で失敗しました。アプリケーションを終了します。");
                        Application.Quit();
                        yield break;
                    }
                }
            }
        }
    }

}

public static class SceneNames
{
    public const string Game    = "GameScene";
}
