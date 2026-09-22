using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// UI から input:query_modes が来たときに、
/// InputHandler と VisualModeHandler の現在のモード設定をまとめて返す。
/// </summary>
public class IpcInputDebugCoordinator : MonoBehaviour
{
    private static readonly int[] OBJECT_TYPES = new int[]
    {
        InputObjectType.BUILDING,
        InputObjectType.PARCEL,
        InputObjectType.PATH,
        InputObjectType.NODE,
        InputObjectType.TERRAIN,
        InputObjectType.ZONE
    };

    public void QueryCurrentModes()
    {
        Debug.Log("IpcInputDebugCoordinator: 現在の入力/表示モードを送信します");

        var objectTypes = new Dictionary<string, object>();

        foreach (int ot in OBJECT_TYPES)
        {
            int inputMode = InputHandler.Instance != null
                ? InputHandler.Instance.GetMode(ot)
                : -1;
            string inputModeName = InputHandler.Instance != null
                ? InputHandler.Instance.GetModeName(inputMode)
                : "N/A";

            int visualMode = VisualModeHandler.Instance != null
                ? VisualModeHandler.Instance.GetMode(ot)
                : -1;
            string visualModeName = VisualModeHandler.Instance != null
                ? VisualModeHandler.Instance.GetModeName(visualMode)
                : "N/A";

            string objectTypeName = InputObjectType.GetName(ot);

            objectTypes[ot.ToString()] = new Dictionary<string, object>
            {
                { "name", objectTypeName },
                { "input_mode", inputMode },
                { "input_mode_name", inputModeName },
                { "visual_mode", visualMode },
                { "visual_mode_name", visualModeName }
            };
        }

        IpcClient.Instance?.EmitInputCurrentModes(objectTypes);
    }
}
