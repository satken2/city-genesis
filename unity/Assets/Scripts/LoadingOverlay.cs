using UnityEngine;

/// <summary>
/// ゲーム開始前のローディングオーバーレイを制御する。
///
/// 本コンポーネントは LoadingOverlay GameObject にアタッチすることを想定している。
/// GameStateManager の状態を監視し、ゲームが開始されていない間は表示し、
/// ゲーム開始時（game:start 受信時）に非表示にする。
/// ゲーム終了時（game:end 受信時）には再び表示に戻す。
///
/// 表示制御には CanvasGroup.alpha を使用し、GameObject の SetActive は使わない。
/// SetActive を使うと OnDisable でイベント購読が解除され、
/// 次回の game:end イベントを受け取れなくなる問題があるため。
///
/// 加えて、イベント購読が何らかの理由で外れた場合でも LateUpdate で
/// 毎フレーム表示状態を同期するため、確実に表示/非表示が追従する。
///
/// さらに、オーバーレイ表示中はメインカメラの描画を停止する。
/// オーバーレイで画面全体を覆っているにもかかわらずカメラが稼働し続けると、
/// 空シーンを数千FPSでレンダリングしてGPU使用率が異常に上昇するため。
/// Screen Space - Overlay のUIはカメラとは独立して描画されるため、
/// オーバーレイ自体は正常に表示される。
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
            bool shouldShow = !GameStateManager.Instance.IsGameStarted();
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
    /// 表示状態を実際に同期する内部メソッド。
    /// ゲーム開始前（Menu/Init）では表示し、ゲーム開始後（Game）では非表示にする。
    /// CanvasGroup.alpha を使用することで、SetActive によるイベント購読の解除を防ぐ。
    ///
    /// オーバーレイ表示中はメインカメラの描画を無効化する。
    /// 画面が完全に覆われている間にカメラが描画を続けると、
    /// 空シーンを数千FPSでレンダリングしてGPU使用率が異常に上昇するため。
    /// </summary>
    private void SyncVisibility()
    {
        bool shouldShow = GameStateManager.Instance == null || !GameStateManager.Instance.IsGameStarted();
        float targetAlpha = shouldShow ? 1f : 0f;
        if (_canvasGroup.alpha != targetAlpha)
        {
            _canvasGroup.alpha = targetAlpha;
        }
        _canvasGroup.blocksRaycasts = shouldShow;

        if (_cachedCamera != null && _cachedCamera.enabled != !shouldShow)
        {
            _cachedCamera.enabled = !shouldShow;
        }
    }
}
