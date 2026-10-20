using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedurally generate a compass needle, a red square pyramid pointing north, and an "N" mesh.
/// Used by CompassPanel.
///
/// Coordinates: local +Z is north, +Y is up, and the origin is the rotation pivot.
/// Bake shading into vertex colors without lights; Custom/CompassUnlit used by CompassPanel
/// is fully unlit. A fixed light direction determines face brightness, so the four faces' shading changes
/// as the needle rotates with the camera bearing, making its 3D shape apparent.
/// </summary>
public static class CompassNeedleMesh
{
    /// <summary>Z coordinate of the needle tip (north).</summary>
    public const float TipZ = 0.85f;
    /// <summary>Z coordinate of the pyramid base, opposite the tip across the origin (rotation pivot).</summary>
    private const float BaseZ = -0.32f;
    /// <summary>Radius of the pyramid base, from center to vertex.</summary>
    private const float BaseRadius = 0.20f;

    /// <summary>Height of the "N" glyph in world units.</summary>
    private const float LetterSize = 0.30f;
    /// <summary>Stroke width of "N" in glyph-local coordinates, with height 1.0.</summary>
    private const float LetterStrokeWidth = 0.2f;

    private static readonly Color NeedleColor = new Color(0.88f, 0.29f, 0.24f);
    private static readonly Color LetterColor = new Color(0.95f, 0.96f, 0.98f);

    /// <summary>Fixed shading-light direction, from the upper left and toward the viewer.</summary>
    private static readonly Vector3 LightDir = new Vector3(-0.45f, 0.75f, -0.5f).normalized;

    /// <summary>
    /// Stroke definitions for "N": pairs of segment endpoints in glyph-local coordinates
    /// (a height-1.0 square centered at the origin, +X right, +Y up).
    /// </summary>
    private static readonly Vector2[] LetterStrokes =
    {
        new Vector2(-0.32f, -0.5f), new Vector2(-0.32f, 0.5f),
        new Vector2(-0.32f, 0.5f), new Vector2(0.32f, -0.5f),
        new Vector2(0.32f, -0.5f), new Vector2(0.32f, 0.5f),
    };

    /// <summary>
    /// Generate a red square pyramid pointing north. Rotate the square base 45 degrees, with vertices at top/bottom/left/right,
    /// so its four side normals spread in those directions and edges reveal the shape from any angle.
    /// </summary>
    public static Mesh BuildNeedle()
    {
        var b = new MeshBuffer();

        Vector3 apex = new Vector3(0f, 0f, TipZ);
        Vector3 up = new Vector3(0f, BaseRadius, BaseZ);
        Vector3 down = new Vector3(0f, -BaseRadius, BaseZ);
        Vector3 left = new Vector3(-BaseRadius, 0f, BaseZ);
        Vector3 right = new Vector3(BaseRadius, 0f, BaseZ);

        b.AddShadedTri(apex, up, left, NeedleColor);
        b.AddShadedTri(apex, left, down, NeedleColor);
        b.AddShadedTri(apex, down, right, NeedleColor);
        b.AddShadedTri(apex, right, up, NeedleColor);
        // Base face, closing the opening when the needle faces away from north.
        b.AddShadedTri(up, down, left, NeedleColor);
        b.AddShadedTri(up, right, down, NeedleColor);

        return b.ToMesh("CompassNeedle");
    }

    /// <summary>
    /// Generate "N" as flat strips on the local XY plane, centered at the origin.
    /// CompassPanel rotates it to face the camera, so do not assign an orientation here.
    /// </summary>
    public static Mesh BuildNorthLabel()
    {
        var b = new MeshBuffer();
        float half = LetterStrokeWidth * 0.5f;

        for (int s = 0; s + 1 < LetterStrokes.Length; s += 2)
        {
            Vector2 dir = (LetterStrokes[s + 1] - LetterStrokes[s]).normalized;
            Vector2 perp = new Vector2(-dir.y, dir.x) * half;
            // Extend segment ends by half the stroke width to avoid gaps at corners.
            Vector2 p0 = LetterStrokes[s] - dir * half;
            Vector2 p1 = LetterStrokes[s + 1] + dir * half;
            b.AddQuad(
                ToLocal(p0 + perp), ToLocal(p1 + perp),
                ToLocal(p1 - perp), ToLocal(p0 - perp), LetterColor);
        }

        return b.ToMesh("CompassNorthLabel");
    }

    private static Vector3 ToLocal(Vector2 letterSpace)
    {
        return new Vector3(letterSpace.x * LetterSize, letterSpace.y * LetterSize, 0f);
    }

    /// <summary>Working buffer for building a mesh with vertex colors.</summary>
    private class MeshBuffer
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<int> triangles = new List<int>();

        public void AddTri(Vector3 a, Vector3 b, Vector3 c, Color color)
        {
            int baseIndex = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            colors.Add(color); colors.Add(color); colors.Add(color);
            triangles.Add(baseIndex); triangles.Add(baseIndex + 1); triangles.Add(baseIndex + 2);
        }

        /// <summary>
        /// Calculate brightness from the face normal and fixed light direction, then add a shaded triangle.
        /// Normals must point outward according to Unity's winding convention. Add an ambient-light floor
        /// so unlit faces do not become completely black.
        /// </summary>
        public void AddShadedTri(Vector3 a, Vector3 b, Vector3 c, Color color)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            float lambert = Mathf.Max(0f, Vector3.Dot(normal, LightDir));
            AddTri(a, b, c, Shade(color, 0.35f + 0.65f * lambert));
        }

        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            AddTri(a, b, c, color);
            AddTri(a, c, d, color);
        }

        public Mesh ToMesh(string name)
        {
            Mesh mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Return a brightness-adjusted color, always keeping alpha at 1.
        /// Render-texture alpha becomes the panel opacity directly,
        /// so ordinary Color multiplication, which also scales alpha, would make shaded areas transparent.
        /// </summary>
        private static Color Shade(Color color, float factor)
        {
            return new Color(color.r * factor, color.g * factor, color.b * factor, 1f);
        }
    }
}
