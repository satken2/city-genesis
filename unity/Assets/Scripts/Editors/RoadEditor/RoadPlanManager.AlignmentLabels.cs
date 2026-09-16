using System.Collections.Generic;
using UnityEngine;

public partial class RoadPlanManager
{
    private sealed class AlignmentLabel
    {
        public Vector3 position;
        public bool curve;
        public float value;
        public float limit;
        public string text;
    }

    private readonly List<AlignmentLabel> alignmentLabels = new List<AlignmentLabel>();
    private readonly List<Rect> alignmentLabelRects = new List<Rect>();
    private GUIStyle alignmentLabelStyle;

    /// <summary>原因ごとの連続違反区間をまとめ、その区間の最悪値と位置を保持する。</summary>
    private static void CollectAlignmentLabels(List<Vector3> samples, Vector3 origin, float[] values,
        float limit, bool curve, List<AlignmentLabel> labels)
    {
        if (limit <= 0f) return;
        AlignmentLabel current = null;
        for (int i = 0; i < values.Length; i++)
        {
            bool bad = curve ? values[i] < limit : values[i] > limit + AlignmentGradeTolerancePct;
            if (!bad) { current = null; continue; }
            if (current == null)
            {
                current = new AlignmentLabel { curve = curve, limit = limit, value = values[i], position = samples[i] + origin };
                labels.Add(current);
            }
            if (curve ? values[i] < current.value : values[i] > current.value)
            {
                current.value = values[i];
                current.position = samples[i] + origin;
            }
        }
        foreach (var label in labels)
        {
            if (label.text != null) continue;
            label.text = label.curve
                ? $"カーブ  R {label.value:0.00}m\n下限 {label.limit:0.##}m 未満"
                : $"勾配  G {label.value:0.00}%\n上限 {label.limit:0.##}% 超過";
        }
    }

    private void OnGUI()
    {
        if (!isActiveAndEnabled || !IsSessionActive || RadialMenuController.IsOpen) return;
        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null || Event.current.type != EventType.Repaint) return;
        CheckAlignmentViolations();
        if (alignmentLabels.Count == 0) return;
        if (alignmentLabelStyle == null)
            alignmentLabelStyle = new GUIStyle(GUI.skin.label) {
                font = UiFontProvider.GetFont(13), fontSize = 13, alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };
        alignmentLabelRects.Clear();
        Color oldColor = GUI.color;
        foreach (var label in alignmentLabels)
        {
            Vector3 projected = cam.WorldToScreenPoint(label.position + Vector3.up * 0.3f);
            if (projected.z <= 0 || projected.x < 0 || projected.x > Screen.width || projected.y < 0 || projected.y > Screen.height) continue;
            Vector2 anchor = new Vector2(projected.x, Screen.height - projected.y);
            float width = alignmentLabelStyle.CalcSize(new GUIContent(label.text)).x + 44f;
            Rect box = new Rect(Mathf.Clamp(anchor.x + 18, 4, Mathf.Max(4, Screen.width - width - 4)),
                Mathf.Clamp(anchor.y - 52, 4, Mathf.Max(4, Screen.height - 48)), width, 44);
            // 投影位置が重なる区間は左右・上下へずらし、引出線で元の違反位置を示す。
            Rect initial = box;
            for (int attempt = 0; attempt < alignmentLabels.Count * 4; attempt++)
            {
                bool overlaps = alignmentLabelRects.Exists(r => r.Overlaps(box));
                if (!overlaps) break;
                int row = attempt / 2 + 1;
                float y = initial.y + (attempt % 2 == 0 ? row * 48 : -row * 48);
                if (y < 4 || y + 44 > Screen.height - 4)
                {
                    box.x = Mathf.Clamp(initial.x - width - 36, 4, Mathf.Max(4, Screen.width - width - 4));
                    y = Mathf.Clamp(y, 4, Mathf.Max(4, Screen.height - 48));
                }
                box.y = y;
            }
            alignmentLabelRects.Add(box);
            Color accent = label.curve ? new Color(1f, 0.55f, 0.25f) : new Color(1f, 0.8f, 0.2f);
            GUI.color = accent;
            DrawAlignmentGuiLine(anchor, new Vector2(Mathf.Clamp(anchor.x, box.xMin, box.xMax), Mathf.Clamp(anchor.y, box.yMin, box.yMax)), 1.5f);
            GUI.DrawTexture(new Rect(anchor.x - 3, anchor.y - 3, 6, 6), Texture2D.whiteTexture);
            GUI.color = new Color(0.08f, 0.08f, 0.08f, 0.92f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = accent;
            GUI.DrawTexture(new Rect(box.x, box.y, 3, box.height), Texture2D.whiteTexture);
            Vector2 icon = new Vector2(box.x + 11, box.y + 12);
            if (label.curve)
            {
                Vector2 previous = icon + new Vector2(0, 20);
                for (int i = 1; i <= 10; i++)
                {
                    float angle = i * Mathf.PI / 20;
                    Vector2 next = icon + new Vector2(20 * (1 - Mathf.Cos(angle)), 20 * (1 - Mathf.Sin(angle)));
                    DrawAlignmentGuiLine(previous, next, 2.5f);
                    previous = next;
                }
            }
            else
            {
                DrawAlignmentGuiLine(icon + new Vector2(0, 20), icon + new Vector2(20, 0), 2.5f);
                DrawAlignmentGuiLine(icon + new Vector2(0, 20), icon + new Vector2(20, 20), 1.5f);
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 38, box.y, box.width - 40, box.height), label.text, alignmentLabelStyle);
        }
        GUI.color = oldColor;
    }

    private static void DrawAlignmentGuiLine(Vector2 a, Vector2 b, float width)
    {
        Matrix4x4 matrix = GUI.matrix;
        Vector2 delta = b - a;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, delta.magnitude, width), Texture2D.whiteTexture);
        GUI.matrix = matrix;
    }
}
