using UnityEngine;

/// <summary>
/// IMGUI labels numbering path construction nodes from 1.
/// The Tauri 2D path shape editor (map/profile strip) shows the same numbers,
/// linking corresponding nodes between 3D and 2D. Highlights the selected node.
/// Called from AlignmentLabels.OnGUI (a class can have only one OnGUI).
/// </summary>
public partial class RoadPlanManager
{
    private GUIStyle nodeNumberStyle;
    private static readonly Color NodeNumberBg = new Color(0.08f, 0.08f, 0.08f, 0.85f);
    private static readonly Color NodeNumberSelectedBg = new Color(0.31f, 0.63f, 1f, 0.95f);

    private void DrawNodeNumberLabels(Camera cam)
    {
        if (points.Count == 0) return;
        if (nodeNumberStyle == null)
            nodeNumberStyle = new GUIStyle(GUI.skin.label) {
                font = UiFontProvider.GetFont(12), fontSize = 12, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
        Color oldColor = GUI.color;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 projected = cam.WorldToScreenPoint(points[i].position + Vector3.up * 0.5f);
            if (projected.z <= 0 || projected.x < 0 || projected.x > Screen.width || projected.y < 0 || projected.y > Screen.height) continue;
            string text = (i + 1).ToString();
            float width = Mathf.Max(18f, nodeNumberStyle.CalcSize(new GUIContent(text)).x + 8f);
            // Place above/right of the node to avoid overlapping its anchor or cursor HUD.
            Rect box = new Rect(projected.x + 10f, Screen.height - projected.y - 26f, width, 18f);
            GUI.color = i == selectedPointIndex ? NodeNumberSelectedBg : NodeNumberBg;
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(box, text, nodeNumberStyle);
        }
        GUI.color = oldColor;
    }
}
