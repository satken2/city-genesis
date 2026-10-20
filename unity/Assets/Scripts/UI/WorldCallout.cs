using System.Collections.Generic;
using UnityEngine;

/// <summary>Shared rendering for leader lines pointing to 3D locations and name labels arranged to avoid screen-space overlap.</summary>
public static class WorldCallout
{
    public static bool TryPlace(Vector2 anchor, Vector2 size, List<Rect> occupied, out Rect box)
    {
        box = default;
        for (int row = 0; row < 16; row++)
        {
            float dy = (row % 2 == 0 ? -1 : 1) * (row / 2 + 1) * (size.y + 5);
            var candidate = new Rect(Mathf.Clamp(anchor.x + 18, 4, Mathf.Max(4, Screen.width - size.x - 4)), anchor.y + dy, size.x, size.y);
            if (candidate.y < 4 || candidate.yMax > Screen.height - 4 || occupied.Exists(r => r.Overlaps(candidate))) continue;
            occupied.Add(candidate); box = candidate; return true;
        }
        return false;
    }

    public static void Draw(Vector2 anchor, Rect box, string text, Color color, GUIStyle style)
    {
        Color old = GUI.color;
        GUI.color = color;
        DrawLine(anchor, new Vector2(Mathf.Clamp(anchor.x, box.xMin, box.xMax), Mathf.Clamp(anchor.y, box.yMin, box.yMax)), 1.5f);
        GUI.DrawTexture(new Rect(anchor.x - 3, anchor.y - 3, 6, 6), Texture2D.whiteTexture);
        GUI.color = new Color(.06f, .07f, .09f, .96f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = color;
        GUI.DrawTexture(new Rect(box.x, box.y, 3, box.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(box.x + 9, box.y, box.width - 12, box.height), text, style);
        GUI.color = old;
    }

    public static void DrawLine(Vector2 a, Vector2 b, float width)
    {
        Matrix4x4 matrix = GUI.matrix;
        Vector2 delta = b - a;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * .5f, delta.magnitude, width), Texture2D.whiteTexture);
        GUI.matrix = matrix;
    }
}
