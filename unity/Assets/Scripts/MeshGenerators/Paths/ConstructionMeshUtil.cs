using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Utility generating dirt strips covering paths under construction (STATUS_CONSTRUCTING).
/// Called by PathManager.GenerateConstructingPathMesh.
/// Also holds DirtNoise shared with building dirt mounds (ConstructingBuildingMeshBuilder).
///
/// Shape: strip along points, with trapezoidal mound cross-section: two ground-contact edge vertices and two inner vertices raised by thickness
/// plus dirt noise. Ground-contact edges eliminate the need for sides,
/// reducing terrain gaps/Z-fighting. Close both ends with caps.
/// Use noise rather than geometric density for randomness:
/// - Add multi-octave Perlin noise (fbm) to height.
/// - Vary left/right dirt-edge widths independently with low-frequency noise to break straight outlines.
/// - World-XZ UVs prevent material-pattern distortion when paths stretch.
///
/// Surface dirt follows terrain elevation + ROAD_SPLINE_OFFSET.
/// Fully buried sections use followTerrain=false, preserving design Y without rising to the surface.
/// </summary>
public static class ConstructionMeshUtil
{
    /// <summary>Offset avoiding negative Perlin input coordinates, which produce mirrored patterns.</summary>
    private const float NOISE_INPUT_OFFSET = 10000f;

    /// <summary>World frequency of low-frequency dirt-edge width noise (1/m).</summary>
    private const float WIDTH_JITTER_NOISE_SCALE = 0.05f;

    /// <summary>Width variation amplitude as fraction of half-width. 0.15 varies half-width by ±15%.</summary>
    private const float WIDTH_JITTER_RATIO = 0.15f;

    /// <summary>
    /// Dirt height noise (0–1). Three-octave fbm combines broad undulations (wavelength about 6–7m)
    /// with medium bumps (about 2.5m) and small clods (about 1m).
    /// World-coordinate inputs keep patterns stable across regeneration.
    /// Shared by building dirt mounds (ConstructingBuildingMeshBuilder) and dirt strips.
    /// </summary>
    public static float DirtNoise(float worldX, float worldZ)
    {
        float x = (worldX + NOISE_INPUT_OFFSET) * LocalConstants.CONSTRUCTION_DIRT_NOISE_SCALE;
        float z = (worldZ + NOISE_INPUT_OFFSET) * LocalConstants.CONSTRUCTION_DIRT_NOISE_SCALE;
        // Offset octave phases to avoid concentric artifacts.
        return Mathf.PerlinNoise(x, z) * 0.55f
             + Mathf.PerlinNoise(x * 2.7f + 31.4f, z * 2.7f + 47.2f) * 0.30f
             + Mathf.PerlinNoise(x * 6.1f + 113.9f, z * 6.1f + 71.3f) * 0.15f;
    }

    /// <summary>
    /// Generate a dirt-strip mesh along points (world coordinates).
    /// </summary>
    /// <param name="points">Path points (PathManager.CalculatePathPoints output or a rendering-section run). Y is ignored/replaced with terrain elevation.</param>
    /// <param name="width">Base full dirt width (m), usually master road width + CONSTRUCTION_PATH_DIRT_EXTRA_WIDTH. Edge noise varies actual width.</param>
    public static Mesh BuildDirtStripMesh(List<Vector3> points, float width, bool followTerrain = true)
    {
        if (points == null || points.Count < 2 || width <= 0f)
        {
            return null;
        }

        float baseHalfWidth = width * 0.5f;
        // Inner crest distance from center. One quarter of full width gives a natural trapezoid.
        float crestRatio = 0.75f;
        float thickness = LocalConstants.CONSTRUCTION_DIRT_THICKNESS;
        float bumpAmp = LocalConstants.CONSTRUCTION_DIRT_BUMP_AMPLITUDE;

        int pointCount = points.Count;

        // Ground Y at each point (terrain elevation + road-spline lift).
        float[] groundY = new float[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            groundY[i] = followTerrain ? Utils.GetGroundHeight(new Vector3(points[i].x, 1000f, points[i].z))
                + RoadPartHeights.ROAD_SPLINE_OFFSET : points[i].y;
        }

        var vertices = new List<Vector3>(pointCount * 4);
        var uvs = new List<Vector2>(pointCount * 4);
        var colors = new List<Color>(pointCount * 4);
        var triangles = new List<int>((pointCount - 1) * 18 + 12);

        // Cross-section: [0]=left ground edge, [1]=left raised crest, [2]=right crest, [3]=right ground edge.
        for (int i = 0; i < pointCount; i++)
        {
            Vector3 direction = PolylineUtils.SmoothedDirection(points, i);
            Vector3 binormal = Vector3.Cross(Vector3.up, direction);
            if (binormal.sqrMagnitude < 1e-6f)
            {
                binormal = Vector3.right;
            }
            binormal.Normalize();

            Vector3 basePoint = new Vector3(points[i].x, groundY[i], points[i].z);

            // Dirt-edge variation: low-frequency noise changes half-width by ±WIDTH_JITTER_RATIO, breaking straight outlines.
            // Different left/right phases vary both width and asymmetry around the centerline.
            float jitterLeft = Mathf.PerlinNoise(
                (basePoint.x + NOISE_INPUT_OFFSET) * WIDTH_JITTER_NOISE_SCALE,
                (basePoint.z + NOISE_INPUT_OFFSET) * WIDTH_JITTER_NOISE_SCALE);
            float jitterRight = Mathf.PerlinNoise(
                (basePoint.x + NOISE_INPUT_OFFSET) * WIDTH_JITTER_NOISE_SCALE + 57.7f,
                (basePoint.z + NOISE_INPUT_OFFSET) * WIDTH_JITTER_NOISE_SCALE + 91.3f);
            float halfWidthLeft = baseHalfWidth * (1f + (jitterLeft * 2f - 1f) * WIDTH_JITTER_RATIO);
            float halfWidthRight = baseHalfWidth * (1f + (jitterRight * 2f - 1f) * WIDTH_JITTER_RATIO);

            Vector3 leftEdge = basePoint - binormal * halfWidthLeft;
            Vector3 leftCrestPos = basePoint - binormal * (halfWidthLeft * crestRatio);
            Vector3 rightCrestPos = basePoint + binormal * (halfWidthRight * crestRatio);
            Vector3 rightEdge = basePoint + binormal * halfWidthRight;

            // Conform edges and roadbed independently to their ground on cross-slopes.
            if (followTerrain)
            {
                leftEdge.y = Utils.GetGroundHeight(leftEdge) + .015f;
                rightEdge.y = Utils.GetGroundHeight(rightEdge) + .015f;
                leftCrestPos.y = Utils.GetGroundHeight(leftCrestPos) + RoadPartHeights.ROAD_SPLINE_OFFSET;
                rightCrestPos.y = Utils.GetGroundHeight(rightCrestPos) + RoadPartHeights.ROAD_SPLINE_OFFSET;
            }

            // Multi-octave dirt noise (shared with building dirt mounds).
            float crestLeftY = thickness + DirtNoise(leftCrestPos.x, leftCrestPos.z) * bumpAmp;
            float crestRightY = thickness + DirtNoise(rightCrestPos.x, rightCrestPos.z) * bumpAmp;

            vertices.Add(leftEdge);
            vertices.Add(leftCrestPos + Vector3.up * crestLeftY);
            vertices.Add(rightCrestPos + Vector3.up * crestRightY);
            vertices.Add(rightEdge);
            colors.Add(new Color(1, 1, 1, 0));
            colors.Add(Color.white);
            colors.Add(Color.white);
            colors.Add(new Color(1, 1, 1, 0));

            // World-XZ UVs prevent pattern distortion from path stretching and remain continuous across adjacent sections.
            uvs.Add(new Vector2(leftEdge.x * 0.1f, leftEdge.z * 0.1f));
            uvs.Add(new Vector2(leftCrestPos.x * 0.1f, leftCrestPos.z * 0.1f));
            uvs.Add(new Vector2(rightCrestPos.x * 0.1f, rightCrestPos.z * 0.1f));
            uvs.Add(new Vector2(rightEdge.x * 0.1f, rightEdge.z * 0.1f));
        }

        // Join cross-sections with three strips (left slope/top/right slope).
        // Vertices run left (-binormal) to right (+binormal); upward winding is
        // (current left, next left, current right), because forward × right points upward.
        for (int i = 0; i < pointCount - 1; i++)
        {
            int a = i * 4;
            int b = (i + 1) * 4;
            for (int lane = 0; lane < 3; lane++)
            {
                int a0 = a + lane;
                int a1 = a + lane + 1;
                int b0 = b + lane;
                int b1 = b + lane + 1;

                triangles.Add(a0);
                triangles.Add(b0);
                triangles.Add(a1);

                triangles.Add(a1);
                triangles.Add(b0);
                triangles.Add(b1);
            }
        }

        // End caps (close dirt cross-sections at intersection cuts).
        AddEndCap(triangles, 0, flip: true);
        AddEndCap(triangles, (pointCount - 1) * 4, flip: false);

        Mesh mesh = new Mesh();
        if (vertices.Count > 65000)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Close a four-vertex trapezoidal cross-section with two triangles.
    /// flip=true for start (faces backward), false for end.
    /// </summary>
    private static void AddEndCap(List<int> triangles, int baseIndex, bool flip)
    {
        // Fan-triangulate cross-section [0]left edge [1]left crest [2]right crest [3]right edge.
        if (flip)
        {
            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);

            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3);
        }
        else
        {
            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 1);

            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 3);
            triangles.Add(baseIndex + 2);
        }
    }
}
