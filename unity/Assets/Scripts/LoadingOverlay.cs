using UnityEngine;

/// <summary>
/// 初期ロードとTips表示中は黒いCanvasを維持し、game:revealでワールドを見せる。
/// ロード中のCamera.mainはLOD生成が参照するため有効に保つ。未開始時だけ描画を停止する。
/// CanvasGroupを使い、自身を無効化せず次のセッションの通知も受け取る。
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class LoadingOverlay : MonoBehaviour
{
    private CanvasGroup _canvasGroup;
    private Camera _cachedCamera;

    private void Awake()
    {
        _canvasGroup = GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
        {
            _canvasGroup = gameObject.AddComponent<CanvasGroup>();
        }
        _cachedCamera = Camera.main;
    }

    private void OnEnable()
    {
        SubscribeStateEvent();
        UpdateVisibility();
    }

    private void Start()
    {
        SubscribeStateEvent();
        UpdateVisibility();
    }

    private void OnDestroy()
    {
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnStateChanged -= HandleStateChanged;
        }
    }

    /// <summary>
    /// GameStateManager の状態変化イベントを購読する。
    /// 重複登録を防ぐため、一度解除してから再登録する。
    /// </summary>
    private void SubscribeStateEvent()
    {
        if (GameStateManager.Instance == null) return;
        GameStateManager.Instance.OnStateChanged -= HandleStateChanged;
        GameStateManager.Instance.OnStateChanged += HandleStateChanged;
    }

    /// <summary>
    /// 毎フレーム表示状態を同期する。
    /// イベント購読が外れた場合の保険として LateUpdate で常に確認する。
    /// </summary>
    private void LateUpdate()
    {
        if (GameStateManager.Instance != null)
        {
            bool shouldShow = !GameStateManager.Instance.IsWorldVisible;
            if (shouldShow && _canvasGroup.alpha < 0.01f)
            {
                Debug.LogWarning($"LoadingOverlay: alpha should be 1 but is {_canvasGroup.alpha}. GameState={GameStateManager.Instance.CurrentState}");
            }
        }
        SyncVisibility();
    }

    /// <summary>
    /// ゲーム状態が変化したときのコールバック。
    /// </summary>
    private void HandleStateChanged(GameStateManager.GameState state)
    {
        SyncVisibility();
    }

    /// <summary>
    /// 現在のゲーム状態に応じて、オーバーレイの表示/非表示を即座に反映する。
    /// </summary>
    private void UpdateVisibility()
    {
        SyncVisibility();
    }

    /// <summary>
    /// 黒いオーバーレイとカメラの有効状態を独立に同期する。
    /// </summary>
    private void SyncVisibility()
    {
        bool shouldShow = GameStateManager.Instance == null || !GameStateManager.Instance.IsWorldVisible;
        float targetAlpha = shouldShow ? 1f : 0f;
        if (_canvasGroup.alpha != targetAlpha)
        {
            _canvasGroup.alpha = targetAlpha;
        }
        _canvasGroup.blocksRaycasts = shouldShow;

        bool renderWorld = GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted();
        if (_cachedCamera != null) _cachedCamera.enabled = renderWorld;
    }
}
