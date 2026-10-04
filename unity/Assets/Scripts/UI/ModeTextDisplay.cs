using TMPro;
using UnityEngine;

public class ModeTextDisplay : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI modeText;

    private static readonly (string state, string label)[] StateLabels = new[]
    {
        ("STATE_NORMAL", "Normal"),
        ("STATE_EDIT_PATH", "Road Construction"),
        ("STATE_EDIT_BUILDING", "Building Construction"),
        ("STATE_EDIT_PROP", "Prop Placement"),
        ("STATE_EDIT_ROAD_MEMBERS", "Road Segment Edit"),
        ("STATE_EDIT_GRADING", "Land Grading"),
        // フライモードはUI(Tauri)を経由しないUnityローカルのステート。
        // モード中はTauriのUIが隠れるため、抜け方（Esc）をキャプションに含めて案内する。
        ("STATE_FLY_MODE", "STATE_FLY_MODE (Esc to exit)"),
    };

    // 直近に適用されたステート。カメラ専用モード終了時に戻すために保持する。
    private string lastState = "STATE_NORMAL";
    private RectTransform cinematicCaptionPanel;
    private Vector2 savedCaptionSize;
    private Vector2 savedCaptionPosition;

    // IPC の world:change_state ではなく、Unity が実際に適用したステートを購読する。
    // パネルを閉じたときの編集モードの打ち切りや Esc など、Unity 内部で通常モードへ戻る経路にも追従するため。
    private void OnEnable()
    {
        IpcStateCoordinator.OnStateApplied -= OnStateChanged;
        IpcStateCoordinator.OnStateApplied += OnStateChanged;
    }

    private void OnDisable()
    {
        IpcStateCoordinator.OnStateApplied -= OnStateChanged;
    }

    private void OnStateChanged(string state)
    {
        // カメラ専用モード中も直近のステートを記録し、終了時に正しい表示へ復帰させる。
        lastState = state;
        if (CameraController.Instance != null &&
            (CameraController.Instance.IsFlyMode || CameraController.Instance.IsCinematicMode)) return;
        ApplyStateText(state);
    }

    /// <summary>
    /// ステート文字列に対応するキャプションを表示する。既知のステートはラベルへ、未知のステートは
    /// 文字列そのものを表示する。
    /// </summary>
    private void ApplyStateText(string state)
    {
        if (modeText == null) return;

        foreach (var (s, label) in StateLabels)
        {
            if (s == state)
            {
                modeText.text = label;
                return;
            }
        }

        modeText.text = state;
    }

    /// <summary>
    /// フライモード進入時に呼ぶ。キャプションを STATE_FLY_MODE の案内に切り替える。
    /// </summary>
    public void ShowFlyMode()
    {
        RestoreCaptionLayout();
        ApplyStateText("STATE_FLY_MODE");
    }

    /// <summary>
    /// カメラ専用モード終了時に呼ぶ。直前のIPCステートのキャプションへ戻す。
    /// </summary>
    public void RestoreState()
    {
        RestoreCaptionLayout();
        ApplyStateText(lastState);
    }

    public void ShowCinematicMode(bool orbit, bool paused, bool hasPivot, bool reversed)
    {
        if (modeText == null) return;
        if (cinematicCaptionPanel == null && modeText.transform.parent is RectTransform panel)
        {
            cinematicCaptionPanel = panel;
            savedCaptionSize = panel.sizeDelta;
            savedCaptionPosition = panel.anchoredPosition;
            float height = Mathf.Max(72f, panel.rect.height);
            float increase = height - panel.rect.height;
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            panel.anchoredPosition -= Vector2.up * (increase * (1f - panel.pivot.y));
        }
        string status = orbit && !hasPivot ? "No target (Space: retry)" : paused ? "Paused" : "Playing";
        string controls = orbit ? "" : "\nA/D: heading +/-5° | Arrows: move | R/F: height";
        modeText.text = $"Camera {(orbit ? "1: Orbit" : "2: Travel")} | {status}{(reversed ? " | Reversed" : "")}\n"
            + "Mouse: look | Shift: mode | Space: pause | Backspace: reverse | Esc: exit" + controls;
    }

    private void RestoreCaptionLayout()
    {
        if (cinematicCaptionPanel == null) return;
        cinematicCaptionPanel.sizeDelta = savedCaptionSize;
        cinematicCaptionPanel.anchoredPosition = savedCaptionPosition;
        cinematicCaptionPanel = null;
    }
}
