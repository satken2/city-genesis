using System.Collections.Generic;
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
        // フライモードはUI(Tauri)を経由しないUnityローカルのステート。
        // 抜け方（Shift + F）をキャプションに含めて案内する。
        ("STATE_FLY_MODE", "STATE_FLY_MODE (Shift + F to exit)"),
    };

    // 直近にIPCから受信したステート。フライモード終了時にこのキャプションへ戻すために保持する。
    private string lastState = "STATE_NORMAL";

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void Start()
    {
        // OnEnableがIpcClient.Awakeより前に実行された場合のフォールバック
        TrySubscribe();
    }

    private void OnDisable()
    {
        if (IpcClient.Instance != null)
            IpcClient.Instance.OnWorldChangeState -= OnStateChanged;
    }

    private void TrySubscribe()
    {
        if (IpcClient.Instance != null)
        {
            IpcClient.Instance.OnWorldChangeState -= OnStateChanged;
            IpcClient.Instance.OnWorldChangeState += OnStateChanged;
        }
    }

    private void OnStateChanged(string state, Dictionary<string, object> ipcParams)
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
