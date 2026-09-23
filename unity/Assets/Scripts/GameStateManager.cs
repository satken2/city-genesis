using UnityEngine;

/// <summary>
/// ゲーム全体の状態（トップメニュー/ゲームプレイ）を管理するシングルトン。
///
/// 本ゲームでは Unity のシーン遷移を使わず、単一シーン内で UI（Tauri）からの IPC イベント
///（game:start / game:end）を受信して状態を切り替える。
/// バックエンドのセッション有無（current_save_id が null かどうか）がゲーム開始/終了の判定基準であり、
/// UI 側のポーリングで検知された変化が IPC 経由でここに届く。
/// </summary>
public class GameStateManager : MonoBehaviour
{
    public enum GameState
    {
        Init,
        Menu,
        Game
    }

    public static GameStateManager Instance { get; private set; }

    private GameState currentState = GameState.Init;
    private string savedataId = null;
    public bool IsWorldVisible { get; private set; }
    private int loadingId;
    private bool waitingForInitialLoad;
    private bool loadedNotified;
    private int readyFrames;
    private float nextReadyNotification;

    public void BeginLoading()
    {
        IsWorldVisible = false;
        waitingForInitialLoad = false;
        loadedNotified = false;
        readyFrames = 0;
    }

    public void PrepareInitialLoad(int id)
    {
        BeginLoading();
        loadingId = id;
        waitingForInitialLoad = true;
        nextReadyNotification = 0f;
    }

    /// <summary>
    /// UIの開始ボタンは game:loaded を受けてから押せるので、通知済みの世代なら表示を解禁する。
    /// 押した瞬間の準備状態は再判定しない。周期的な描画処理で readyFrames が一時的に0へ戻ると、
    /// UIは既にTipsを閉じているため開始操作が捨てられ、黒画面のまま戻れなくなる。
    /// </summary>
    public void RevealWorld(int id)
    {
        if (id != loadingId || !loadedNotified) return;
        IsWorldVisible = true;
        waitingForInitialLoad = false;
    }

    private void LateUpdate()
    {
        if (!waitingForInitialLoad) return;
        var loader = CellDataLoader.Instance;
        bool ready = loader != null && loader.IsInitialSceneReady
            && CameraController.Instance != null && CameraController.Instance.IsSessionCameraReady;
        readyFrames = ready ? readyFrames + 1 : 0;
        // 生成器が次フレームに積む子処理も待つ。再送によりMMFの取りこぼしから復帰する。
        if (readyFrames >= 3 && Time.unscaledTime >= nextReadyNotification)
        {
            IpcClient.Instance?.EmitGameLoaded(loadingId);
            loadedNotified = true;
            nextReadyNotification = Time.unscaledTime + 1f;
        }
    }


    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // IpcClient が先に生成されている場合、Awake 時点で購読を試行する。
        // 生成順序が不定なため、双方向に購読を試行する。
        SubscribeIpcEvents();
    }

    private void OnEnable()
    {
        SubscribeIpcEvents();
    }

    private void Start()
    {
        // IpcClient が GameStateManager より後に生成された場合、OnEnable 時には
        // Instance が未設定で購読できていない可能性があるため、Start でも再試行する。
        SubscribeIpcEvents();
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnGameStart -= HandleGameStart;
            IpcClient.Instance.OnGameEnd -= HandleGameEnd;
        }
    }

    /// <summary>
    /// IpcClient のゲーム開始/終了イベントを購読する。
    /// 重複登録を防ぐため、一度解除してから再登録する。
    /// IpcClient からも Awake 時に呼び出されるため public にしている。
    /// </summary>
    public void SubscribeIpcEvents()
    {
        if (IpcClient.Instance == null) return;
        IpcClient.Instance.OnGameStart -= HandleGameStart;
        IpcClient.Instance.OnGameStart += HandleGameStart;
        IpcClient.Instance.OnGameEnd -= HandleGameEnd;
        IpcClient.Instance.OnGameEnd += HandleGameEnd;
    }

    public GameState CurrentState => currentState;

    /// <summary>
    /// ゲーム状態が変化したときに発火するイベント。
    /// LoadingOverlay などの UI 要素が購読して表示/非表示を切り替える。
    /// </summary>
    public event System.Action<GameState> OnStateChanged;

    public bool IsGameStarted()
    {
        return currentState == GameState.Game;
    }

    public void SetGameStarted(bool started, string savedataId)
    {
        var newState = started ? GameState.Game : GameState.Menu;
        if (currentState == newState) return;
        currentState = newState;
        this.savedataId = savedataId;
        OnStateChanged?.Invoke(currentState);
    }



    /// <summary>
    /// UI 側から game:start イベントを受信したときのハンドラ。
    /// バックエンドにセッションが作成され、current_save_id が非 null になった際に呼ばれる。
    /// カメラの視点はバックエンドが保持するセッションのカメラ状態(GET /api/v1/city の camera)から
    /// CameraController が復元する。Menu→Game の遷移時は OnStateChanged 経由で適用されるが、
    /// 既にゲーム中(既存セッションからの切り替え)は状態が変わらずイベントが出ないため、
    /// ここで明示的に適用する。
    /// </summary>
    private void HandleGameStart()
    {
        bool alreadyStarted = IsGameStarted();
        SetGameStarted(true, null);
        if (alreadyStarted && CameraController.Instance != null)
        {
            CameraController.Instance.ApplySessionCamera();
        }
    }

    /// <summary>
    /// UI 側から game:end イベントを受信したときのハンドラ。
    /// セッションが破棄され current_save_id が null に戻った際に呼ばれる。
    /// 3Dの全オブジェクト、地形、キャッシュ等をクリアして初期状態に戻す。
    /// </summary>
    private void HandleGameEnd()
    {
        BeginLoading();
        SetGameStarted(false, null);
        if (CellDataLoader.Instance != null)
        {
            CellDataLoader.Instance.ClearAllManagerData();
        }
    }
} 