using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// UI上でラインを描画するコンポーネント
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class UILineRenderer : MaskableGraphic
{
    public float lineWidth = 2f;
    private List<Vector2> points = new List<Vector2>();

    public void SetPoints(List<Vector2> newPoints)
    {
        if (newPoints == null)
        {
            points.Clear();
        }
        else
        {
            points = new List<Vector2>(newPoints);
        }
        SetAllDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (points == null || points.Count < 2)
            return;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 p1 = points[i];
            Vector2 p2 = points[i + 1];
            
            Vector2 direction = (p2 - p1).normalized;
            Vector2 perpendicular = new Vector2(-direction.y, direction.x) * lineWidth * 0.5f;
            
            Vector3 v1 = new Vector3(p1.x - perpendicular.x, p1.y - perpendicular.y, 0);
            Vector3 v2 = new Vector3(p1.x + perpendicular.x, p1.y + perpendicular.y, 0);
            Vector3 v3 = new Vector3(p2.x + perpendicular.x, p2.y + perpendicular.y, 0);
            Vector3 v4 = new Vector3(p2.x - perpendicular.x, p2.y - perpendicular.y, 0);
            
            int baseIndex = vh.currentVertCount;
            vh.AddVert(v1, color, Vector2.zero);
            vh.AddVert(v2, color, Vector2.zero);
            vh.AddVert(v3, color, Vector2.zero);
            vh.AddVert(v4, color, Vector2.zero);
            
            vh.AddTriangle(baseIndex, baseIndex + 1, baseIndex + 2);
            vh.AddTriangle(baseIndex, baseIndex + 2, baseIndex + 3);
        }
    }
}
