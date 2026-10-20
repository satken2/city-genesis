using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 2D overlay renderer for parcel shape editing.
/// Follows the RoadEditorOverlayRenderer pattern,
/// batching all overlay elements (vertex circles, edge lines, highlights) into one CanvasRenderer.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class ParcelShapeEditOverlayRenderer : MaskableGraphic
{
    private struct CircleEntry
    {
        public float cx, cy, radius;
        public Color color;
    }

    private struct LineEntry
    {
        public float x1, y1, x2, y2;
        public Color color;
        public float thickness;
    }

    private List<CircleEntry> frameCircles = new List<CircleEntry>(128);
    private List<LineEntry> frameLines = new List<LineEntry>(512);
    private bool meshDirty;

    public void Initialize(RectTransform canvasRect)
    {
        this.rectTransform.anchorMin = Vector2.zero;
        this.rectTransform.anchorMax = Vector2.one;
        this.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        this.rectTransform.offsetMin = Vector2.zero;
        this.rectTransform.offsetMax = Vector2.zero;
        this.rectTransform.anchoredPosition = Vector2.zero;
        this.raycastTarget = false;
        this.color = Color.white;
    }

    public void BeginFrame()
    {
        frameCircles.Clear();
        frameLines.Clear();
        meshDirty = false;
    }

    public void AddCircle(float sx, float sy, float radius, Color color)
    {
        Vector2 local;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(this.rectTransform, new Vector2(sx, sy), null, out local);
        frameCircles.Add(new CircleEntry { cx = local.x, cy = local.y, radius = radius, color = color });
        meshDirty = true;
    }

    public void AddSolidLine(float sx1, float sy1, float sx2, float sy2, Color color, float thickness)
    {
        Vector2 local1, local2;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(this.rectTransform, new Vector2(sx1, sy1), null, out local1);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(this.rectTransform, new Vector2(sx2, sy2), null, out local2);

        frameLines.Add(new LineEntry
        {
            x1 = local1.x, y1 = local1.y,
            x2 = local2.x, y2 = local2.y,
            color = color, thickness = thickness
        });
        meshDirty = true;
    }

    public void EndFrame()
    {
        if (meshDirty)
        {
            SetAllDirty();
        }
    }

    public void ClearMesh()
    {
        frameCircles.Clear();
        frameLines.Clear();
        meshDirty = true;
        SetAllDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        DrawLines(vh);
        DrawCircles(vh);
    }

    private void DrawLines(VertexHelper vh)
    {
        for (int i = 0; i < frameLines.Count; i++)
        {
            LineEntry line = frameLines[i];
            float dx = line.x2 - line.x1;
            float dy = line.y2 - line.y1;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) continue;

            float halfThick = line.thickness * 0.5f;
            float nx = (-dy / len) * halfThick;
            float ny = (dx / len) * halfThick;

            int baseIdx = vh.currentVertCount;
            vh.AddVert(new Vector3(line.x1 + nx, line.y1 + ny, 0), line.color, Vector2.zero);
            vh.AddVert(new Vector3(line.x1 - nx, line.y1 - ny, 0), line.color, Vector2.zero);
            vh.AddVert(new Vector3(line.x2 - nx, line.y2 - ny, 0), line.color, Vector2.zero);
            vh.AddVert(new Vector3(line.x2 + nx, line.y2 + ny, 0), line.color, Vector2.zero);

            vh.AddTriangle(baseIdx, baseIdx + 1, baseIdx + 2);
            vh.AddTriangle(baseIdx, baseIdx + 2, baseIdx + 3);
        }
    }

    private void DrawCircles(VertexHelper vh)
    {
        const int segments = 16;

        for (int i = 0; i < frameCircles.Count; i++)
        {
            CircleEntry circle = frameCircles[i];
            int startVert = vh.currentVertCount;

            // Center
            vh.AddVert(new Vector3(circle.cx, circle.cy, 0), circle.color, Vector2.zero);

            for (int s = 0; s < segments; s++)
            {
                float angle = (float)s / segments * Mathf.PI * 2f;
                float x = circle.cx + Mathf.Cos(angle) * circle.radius;
                float y = circle.cy + Mathf.Sin(angle) * circle.radius;
                vh.AddVert(new Vector3(x, y, 0), circle.color, Vector2.zero);
            }

            // Triangle fan
            for (int s = 0; s < segments; s++)
            {
                int next = (s + 1) % segments;
                vh.AddTriangle(startVert, startVert + 1 + s, startVert + 1 + next);
            }
        }
    }
}
