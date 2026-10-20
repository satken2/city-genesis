using UnityEngine;

/// <summary>
/// Singleton managing application-wide state (main menu / gameplay).
///
/// The game uses a single Unity scene rather than scene transitions, receiving IPC events from the UI (Tauri)
///(game:start / game:end) to switch states.
/// The backend session's existence (whether current_save_id is null) determines game start/end;
/// changes detected by UI polling arrive here over IPC.
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
    /// The UI enables its start button only after game:loaded, so reveal rendering for an already notified generation.
    /// Do not reevaluate readiness at the moment of the click. If periodic rendering briefly resets readyFrames to 0,
    /// the UI has already closed Tips, so dropping the start action would leave an unrecoverable black screen.
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
        // Also wait for child work queued by generators on the next frame. Resending recovers from missed MMF messages.
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

        // Try subscribing in Awake if IpcClient already exists.
        // Creation order is undefined, so attempt subscription from both sides.
        SubscribeIpcEvents();
    }

    private void OnEnable()
    {
        SubscribeIpcEvents();
    }

    private void Start()
    {
        // If IpcClient is created after GameStateManager, its Instance may be unset during OnEnable,
        // preventing subscription; retry in Start as well.
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
    /// Subscribe to IpcClient's game start/end events.
    /// Unsubscribe before registering again to prevent duplicate subscriptions.
    /// Public because IpcClient also calls this in Awake.
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
    /// Event raised when the game state changes.
    /// UI elements such as LoadingOverlay subscribe to toggle their visibility.
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
    /// Handler for the UI's game:start event.
    /// Called when a backend session is created and current_save_id becomes non-null.
    /// CameraController restores the view from the backend session's camera state
    /// (camera in GET /api/v1/city). Menu -> Game applies it via OnStateChanged;
    /// when already in Game, switching existing sessions causes no state change or event,
    /// so apply it explicitly here.
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
    /// Handler for the UI's game:end event.
    /// Called when the session is destroyed and current_save_id returns to null.
    /// Clear all 3D objects, terrain, caches, and related state to restore the initial state.
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