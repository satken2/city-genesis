using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>前面の路線帯と車両の選択。押下時の候補を保持し、5px以上の移動はカメラ操作へ譲る。</summary>
public sealed class TransitSelection : MonoBehaviour
{
    private static List<int> pending, choices;
    private static Vector2 pressed, menuPosition;
    private static bool cancelled;
    private static int session = -1;
    private GUIStyle style;
    public static void Cancel() { pending = null; choices = null; cancelled = false; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<TransitSelection>() == null) new GameObject("TransitSelection").AddComponent<TransitSelection>();
    }
    private static Rect ChoiceRect(int index) => new Rect(menuPosition.x, menuPosition.y + index * 30, 260, 30);
    public static bool ProcessPointer(Camera cam)
    {
        var mouse = Mouse.current;
        if (mouse == null) return false;
        var store = TransitDataStore.Ensure();
        if (session != store.Session) { session = store.Session; Cancel(); }
        if (Keyboard.current?.escapeKey.wasPressedThisFrame == true || mouse.rightButton.wasPressedThisFrame) { Cancel(); return false; }
        Vector2 point = mouse.position.ReadValue();
        if (mouse.leftButton.wasPressedThisFrame)
        {
            pending = null; cancelled = false; pressed = point;
            if (choices != null)
            {
                Vector2 gui = new Vector2(point.x, Screen.height-point.y);
                for (int i = 0; i < choices.Count; i++) if (ChoiceRect(i).Contains(gui)) { pending = new List<int> { choices[i] }; break; }
                choices = null;
                // 候補メニューを閉じるクリックで背後の建物を同時に選ばない。
                pending ??= new List<int>();
            }
            else
            {
                if (TransitVehicleManager.Instance != null && TransitVehicleManager.Instance.TryPick(cam.ScreenPointToRay(point), out int id)) pending = new List<int> { id };
                else pending = TransitOverlayManager.Instance?.Pick(cam, point);
                if (pending?.Count == 0) pending = null;
            }
        }
        if (pending == null) return false;
        if ((point-pressed).sqrMagnitude >= 25) cancelled = true;
        if (mouse.leftButton.wasReleasedThisFrame)
        {
            var result = pending; pending = null;
            if (!cancelled)
            {
                result.RemoveAll(id => !store.Lines.Exists(line => line.line_agent_id == id));
                if (result.Count == 1) IpcClient.Instance?.EmitSelectObject("transit_detail", "agent", result[0].ToString());
                else if (result.Count > 1)
                {
                    choices = result;
                    menuPosition = new Vector2(Mathf.Clamp(point.x, 4, Mathf.Max(4, Screen.width-264)), Mathf.Clamp(Screen.height-point.y, 4, Mathf.Max(4, Screen.height-result.Count*30-4)));
                }
            }
        }
        else if (!mouse.leftButton.isPressed) pending = null;
        return true;
    }
    private void OnGUI()
    {
        if (choices == null) return;
        int oldDepth = GUI.depth;
        GUI.depth = -1000;
        style ??= new GUIStyle(GUI.skin.label) { font = UiFontProvider.GetFont(14), fontSize = 14, alignment = TextAnchor.MiddleLeft };
        var lines = TransitDataStore.Ensure().Lines;
        for (int i = 0; i < choices.Count; i++)
        {
            var line = lines.Find(l => l.line_agent_id == choices[i]);
            if (line == null) continue;
            Rect box = ChoiceRect(i);
            WorldCallout.Draw(new Vector2(box.x, box.center.y), box, string.IsNullOrWhiteSpace(line.name) ? "#"+line.line_agent_id : line.name, TransitOverlayManager.GetLineColor(line), style);
        }
        GUI.depth = oldDepth;
    }
}
