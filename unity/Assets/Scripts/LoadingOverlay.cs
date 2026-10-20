using UnityEngine;

/// <summary>
/// Keep a black Canvas during initial loading and Tips display; reveal the world on game:reveal.
/// Keep Camera.main enabled during loading because LOD generation uses it. Disable rendering only before the game starts.
/// Use CanvasGroup so this component stays enabled and can receive notifications for the next session.
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
    /// Subscribe to GameStateManager state-change events.
    /// Unsubscribe before registering again to prevent duplicate subscriptions.
    /// </summary>
    private void SubscribeStateEvent()
    {
        if (GameStateManager.Instance == null) return;
        GameStateManager.Instance.OnStateChanged -= HandleStateChanged;
        GameStateManager.Instance.OnStateChanged += HandleStateChanged;
    }

    /// <summary>
    /// Synchronize visibility every frame.
    /// Always check in LateUpdate as a fallback if the event subscription is lost.
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
    /// Callback for game-state changes.
    /// </summary>
    private void HandleStateChanged(GameStateManager.GameState state)
    {
        SyncVisibility();
    }

    /// <summary>
    /// Immediately apply overlay visibility for the current game state.
    /// </summary>
    private void UpdateVisibility()
    {
        SyncVisibility();
    }

    /// <summary>
    /// Synchronize the black overlay and camera enabled state independently.
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
