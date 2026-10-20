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
        // Fly mode is a Unity-local state that does not pass through Tauri's UI.
        // Since Tauri's UI is hidden in this mode, include the exit control (Esc) in the caption.
        ("STATE_FLY_MODE", "STATE_FLY_MODE (Esc to exit)"),
    };

    // Last applied state, retained for restoration after camera-only modes end.
    private string lastState = "STATE_NORMAL";
    private RectTransform expandedCaptionPanel;
    private Vector2 savedCaptionSize;
    private Vector2 savedCaptionPosition;

    // Subscribe to the state Unity actually applies, rather than IPC world:change_state.
    // This also follows internal returns to normal mode, such as ending edits when panels close or pressing Esc.
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
        // Record the latest state even in camera-only modes to restore the correct caption afterward.
        lastState = state;
        if (DriveMode.IsRiding || (CameraController.Instance != null &&
            (CameraController.Instance.IsFlyMode || CameraController.Instance.IsCinematicMode))) return;
        ApplyStateText(state);
    }

    /// <summary>
    /// Display the caption for a state string. Known states use labels; unknown states
    /// display the raw string.
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
    /// Called on entering fly mode; switch the caption to STATE_FLY_MODE guidance.
    /// </summary>
    public void ShowFlyMode()
    {
        RestoreCaptionLayout();
        ApplyStateText("STATE_FLY_MODE");
    }

    /// <summary>
    /// Called when a camera-only mode ends; restore the previous IPC state's caption.
    /// </summary>
    public void RestoreState()
    {
        RestoreCaptionLayout();
        ApplyStateText(lastState);
    }

    public void ShowCinematicMode(bool orbit, bool paused, bool hasPivot, bool reversed)
    {
        string status = orbit && !hasPivot ? "No target (Space: retry)" : paused ? "Paused" : "Playing";
        string controls = orbit ? "" : "\nA/D: heading +/-5° | Arrows: move | R/F: height";
        ShowMultilineCaption($"Camera {(orbit ? "1: Orbit" : "2: Travel")} | {status}{(reversed ? " | Reversed" : "")}\n"
            + "Mouse: look | Shift: mode | Space: pause | Backspace: reverse | Esc: exit" + controls);
    }

    /// <summary>Status and controls during a drive. Text is built by <see cref="DriveMode.RideCaption"/>.</summary>
    public void ShowDriveMode(string caption) => ShowMultilineCaption(caption);

    /// <summary>Show several lines of capture/driving guidance. Expand the caption height only while shown; RestoreCaptionLayout restores it.</summary>
    private void ShowMultilineCaption(string text)
    {
        if (modeText == null) return;
        if (expandedCaptionPanel == null && modeText.transform.parent is RectTransform panel)
        {
            expandedCaptionPanel = panel;
            savedCaptionSize = panel.sizeDelta;
            savedCaptionPosition = panel.anchoredPosition;
            float height = Mathf.Max(72f, panel.rect.height);
            float increase = height - panel.rect.height;
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            panel.anchoredPosition -= Vector2.up * (increase * (1f - panel.pivot.y));
        }
        modeText.text = text;
    }

    private void RestoreCaptionLayout()
    {
        if (expandedCaptionPanel == null) return;
        expandedCaptionPanel.sizeDelta = savedCaptionSize;
        expandedCaptionPanel.anchoredPosition = savedCaptionPosition;
        expandedCaptionPanel = null;
    }
}
