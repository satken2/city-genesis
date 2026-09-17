using UnityEngine;

/// <summary>
/// 敷設中パスの各ノードに番号(1始まり)を出す IMGUI ラベル。
/// Tauriの2Dパス形状編集パネル(地図・縦断面ストリップ)も同じ番号を表示するため、
/// どのノードがどれかを3Dと2Dで対応付けられる。選択中ノードは強調色で示す。
/// 描画は AlignmentLabels の OnGUI から呼ばれる(同一クラスに OnGUI は1つしか持てないため)。
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
            // ノードの右上に置き、アンカー本体やカーソルHUDと重ならないようにする
            Rect box = new Rect(projected.x + 10f, Screen.height - projected.y - 26f, width, 18f);
            GUI.color = i == selectedPointIndex ? NodeNumberSelectedBg : NodeNumberBg;
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(box, text, nodeNumberStyle);
        }
        GUI.color = oldColor;
    }
}
