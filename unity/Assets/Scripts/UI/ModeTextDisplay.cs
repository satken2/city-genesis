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
        ("STATE_EDIT_ZONE", "Zone Edit"),
        ("STATE_EDIT_PROP", "Prop Placement"),
        ("STATE_EDIT_STATION", "Station Placement"),
        ("STATE_EDIT_ROAD_MEMBERS", "Road Segment Edit"),
        ("STATE_EDIT_GRADING", "Land Grading"),
        // フライモードはUI(Tauri)を経由しないUnityローカルのステート。
        // 抜け方（Shift + F）をキャプションに含めて案内する。
        ("STATE_FLY_MODE", "STATE_FLY_MODE (Shift + F to exit)"),
    };

    // 直近に適用されたステート。フライモード終了時にこのキャプションへ戻すために保持する。
    private string lastState = "STATE_NORMAL";

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
        // フライモード中にIPCステートが届いた場合でも、直近ステートは記録しておき、
        // 表示自体はフライモードのキャプションを優先する（終了時に正しく復帰させるため）。
        lastState = state;
        if (CameraController.Instance != null && CameraController.Instance.IsFlyMode) return;
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
        ApplyStateText("STATE_FLY_MODE");
    }

    /// <summary>
    /// フライモード終了時に呼ぶ。直前のIPCステートのキャプションへ戻す。
    /// </summary>
    public void HideFlyMode()
    {
        ApplyStateText(lastState);
    }
}
