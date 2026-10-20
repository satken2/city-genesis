// Assets/Scripts/MeshGenerators/Buildings/GableRoofBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Gable-roof mesh generator.
/// Walls, corners, and ground match MODEL_MODULAR_FLAT; only the roof is replaced.
///
/// Create the roof as two thick boards separated by the ridge. Choose slope so the board undersides
/// pass exactly through the body top at the wall outline; eave overhangs therefore extend outward and
/// downward from the walls, forming the space under the eaves.
/// Boards span the ridge but leave gable ends open, creating triangular gaps between wall tops and board undersides.
/// Close these with plain windowless wall boards (material from `generic-wall` parts).
///
/// Roof surfaces use `ceil` like flat roofs (MODEL_MODULAR_FLAT), and gable walls use
/// `generic-wall`; return separate submeshes.
/// </summary>
public static class GableRoofBuilder
{
    /// <summary>Eave overhang (m). Roof-board extension beyond walls, forming the space below the eaves.</summary>
    private const float EaveOverhang = 0.4f;

    /// <summary>Roof-board thickness (m, vertical). Height of exposed eave/verge edges.</summary>
    private const float RoofThickness = 0.25f;

    /// <summary>
    /// Ratio of floor height determining ridge rise (wall top to ridge).
    /// Fixing this to floor height rather than building width prevents oversized roofs on large buildings
    /// and keeps apparent roof height consistent within the streetscape.
    /// </summary>
    private const float RiseFloorHeightRatio = 0.5f;

    /// <summary>World-coordinate UV scale (UV units per 1m).</summary>
    private const float UvScale = 0.2f;

    /// <summary>Distance threshold from the ridge line for treating a point as on the ridge (m).</summary>
    private const float RidgeEpsilon = 1e-4f;

    /// <summary>Omit gable walls below this height (m).</summary>
    private const float MinGableWallHeight = 1e-3f;

    /// <summary>
    /// Geometric frame determining roof-surface height. Board underside height depends only on horizontal ridge distance,
    /// so boards are perfectly planar on either side of the ridge.
    /// </summary>
    private readonly struct RoofFrame
    {
        public readonly Vector2 Center;
        public readonly Vector2 RidgeDir;
        public readonly Vector2 WidthDir;
        public readonly float RidgeY;
        public readonly float Slope;
        public readonly float BaseHeight;

        public RoofFrame(Vector2 center, Vector2 ridgeDir, Vector2 widthDir,
            float ridgeY, float slope, float baseHeight)
        {
            Center = center;
            RidgeDir = ridgeDir;
            WidthDir = widthDir;
            RidgeY = ridgeY;
            Slope = slope;
            BaseHeight = baseHeight;
        }

        /// <summary>Signed distance from ridge. Sign indicates the side of the ridge.</summary>
        public float Across(Vector2 point) => Vector2.Dot(point - Center, WidthDir);

        /// <summary>Roof-board underside height.</summary>
        public float SurfaceY(Vector2 point) => RidgeY - Mathf.Abs(Across(point)) * Slope;

        /// <summary>Point on the roof-board underside (offsetY=0) or top (offsetY=thickness).</summary>
        public Vector3 SurfacePoint(Vector2 point, float offsetY)
            => new Vector3(point.x, SurfaceY(point) + offsetY, point.y);
    }

    /// <summary>
    /// Face buffer. Roof surfaces and gable walls use different materials and separate submeshes.
    /// </summary>
    private class RoofBuffers
    {
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector2> Uvs = new List<Vector2>();
        public readonly List<int> RoofTriangles = new List<int>();
        public readonly List<int> GableTriangles = new List<int>();
    }

    /// <summary>
    /// Generate a gable-roof mesh.
    /// Even without a material, retain and return the mesh so the final combining stage
    /// can assign a default material.
    /// </summary>
    /// <param name="footprint">Building footprint vertices.</param>
    /// <param name="baseHeight">Wall-top height = rendered body height.</param>
    /// <param name="floorHeight">Height per floor. Ridge rise is fixed at half this.</param>
    /// <param name="prefix">Model prefix (for ceil / generic-wall lookup).</param>
    /// <param name="resolvedSuffix">Resolved suffix (for ceil / generic-wall lookup).</param>
    /// <param name="fallbackMaterials">Fallback materials when parts are absent.</param>
    public static BuildingMeshResult GenerateRoof(
        Vector2[] footprint, float baseHeight, float floorHeight,
        string prefix, int resolvedSuffix, Material[] fallbackMaterials)
    {
        if (footprint == null || footprint.Length < 3)
        {
            Debug.LogError("GableRoofBuilder.GenerateRoof: footprint is null or less than 3");
            return null;
        }

        // Normalize because ear clipping and offset directions (outward normals) assume CCW.
        Vector2[] normalized = (Vector2[])footprint.Clone();
        if (PolygonUtils.SignedArea(normalized) < 0f)
        {
            System.Array.Reverse(normalized);
        }

        // Determine ridge direction and half-width from the wall outline. Including eave overhangs
        // would prevent the roof surface from passing through wall tops, misaligning gable-wall height.
        if (!BuildingMeshUtils.TryResolveMinAreaRect(normalized, out Vector2 center, out Vector2 ridgeDir,
                out Vector2 widthDir, out _, out float halfWidth))
        {
            // Fall back to a flat roof for degenerate polygons with no determinable direction.
            Debug.LogWarning("GableRoofBuilder.GenerateRoof: 棟線方向を決定できないため平屋根にフォールバックします。");
            return FlatRoofBuilder.GenerateRoof(footprint, baseHeight, prefix, resolvedSuffix, fallbackMaterials);
        }

        // Fix ridge rise to half the floor height. Slope varies with building width (half-width),
        // becoming gentler on wider buildings.
        float rise = (floorHeight > 0f ? floorHeight : LocalConstants.FLOOR_HEIGHT) * RiseFloorHeightRatio;
        // Derive slope from ridge height and half-width so the roof surface passes through wall tops exactly.
        float slope = rise / halfWidth;
        var frame = new RoofFrame(center, ridgeDir, widthDir, baseHeight + rise, slope, baseHeight);

        // The footprint expanded outward by the eave overhang forms the roof-board outline.
        Vector2[] eaveFootprint = BuildingMeshUtils.OffsetPolygonOutward(normalized, EaveOverhang);
        if (eaveFootprint == null || eaveFootprint.Length < 3)
        {
            eaveFootprint = normalized;
        }

        var buffers = new RoofBuffers();

        // Split roof boards at the ridge, creating one planar board on each side.
        for (int side = -1; side <= 1; side += 2)
        {
            Vector2[] half = ClipToRidgeSide(eaveFootprint, frame, side);
            if (half != null)
            {
                AddRoofBoard(half, frame, buffers);
            }
        }

        // Close gaps between wall tops and board undersides (gable walls).
        AddGableWalls(normalized, frame, buffers);

        Mesh roofMesh = new Mesh();
        roofMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        roofMesh.SetVertices(buffers.Vertices);
        roofMesh.SetNormals(buffers.Normals);
        roofMesh.SetUVs(0, buffers.Uvs);

        bool hasGableWall = buffers.GableTriangles.Count > 0;
        roofMesh.subMeshCount = hasGableWall ? 2 : 1;
        roofMesh.SetTriangles(buffers.RoofTriangles, 0);
        if (hasGableWall)
        {
            roofMesh.SetTriangles(buffers.GableTriangles, 1);
        }
        roofMesh.RecalculateBounds();

        Material roofMaterial = BuildingMeshUtils.SelectRoofMaterial(prefix, resolvedSuffix, fallbackMaterials);
        if (!hasGableWall)
        {
            return new BuildingMeshResult(roofMesh, new[] { roofMaterial });
        }

        Material gableMaterial = BuildingMeshUtils.SelectGenericWallMaterial(prefix, resolvedSuffix, fallbackMaterials);
        return new BuildingMeshResult(roofMesh, new[] { roofMaterial, gableMaterial });
    }

    /// <summary>
    /// Build one thick roof board on one side of the ridge.
    /// Consists of underside, top, and edge faces. Omit edge faces along the ridge, where boards meet.
    /// </summary>
    private static void AddRoofBoard(Vector2[] polygon, in RoofFrame frame, RoofBuffers buffers)
    {
        List<int> indices = BuildingMeshBuilder.TriangulateOrFan(polygon);
        if (indices == null || indices.Count < 3)
        {
            return;
        }

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            Vector2 p0 = polygon[indices[i]];
            Vector2 p1 = polygon[indices[i + 1]];
            Vector2 p2 = polygon[indices[i + 2]];

            // Underside (visible beneath eaves). CCW triangulation directly gives downward faces.
            AddFace(frame.SurfacePoint(p0, 0f), frame.SurfacePoint(p1, 0f), frame.SurfacePoint(p2, 0f),
                frame, buffers, buffers.RoofTriangles);
            // Top. Reverse winding to face upward.
            AddFace(frame.SurfacePoint(p0, RoofThickness), frame.SurfacePoint(p2, RoofThickness),
                frame.SurfacePoint(p1, RoofThickness), frame, buffers, buffers.RoofTriangles);
        }

        int count = polygon.Length;
        for (int i = 0; i < count; i++)
        {
            Vector2 curr = polygon[i];
            Vector2 next = polygon[(i + 1) % count];

            if (Mathf.Abs(frame.Across(curr)) < RidgeEpsilon && Mathf.Abs(frame.Across(next)) < RidgeEpsilon)
            {
                continue;
            }

            float currY = frame.SurfaceY(curr);
            float nextY = frame.SurfaceY(next);
            AddVerticalBand(curr, next, currY, nextY, currY + RoofThickness, nextY + RoofThickness,
                frame, buffers, buffers.RoofTriangles);
        }
    }

    /// <summary>
    /// Close the gap between wall tops and roof-board undersides with vertical boards (gable walls).
    /// At eave sides, the roof passes exactly through wall tops, giving zero height; at gable sides, it forms a triangle to the ridge.
    /// Split ridge-crossing edges at the roof fold.
    /// </summary>
    private static void AddGableWalls(Vector2[] wallPolygon, in RoofFrame frame, RoofBuffers buffers)
    {
        int count = wallPolygon.Length;
        for (int i = 0; i < count; i++)
        {
            Vector2 curr = wallPolygon[i];
            Vector2 next = wallPolygon[(i + 1) % count];

            float acrossCurr = frame.Across(curr);
            float acrossNext = frame.Across(next);

            if ((acrossCurr > 0f && acrossNext < 0f) || (acrossCurr < 0f && acrossNext > 0f))
            {
                Vector2 apex = Vector2.Lerp(curr, next, acrossCurr / (acrossCurr - acrossNext));
                AddGableWallSegment(curr, apex, frame, buffers);
                AddGableWallSegment(apex, next, frame, buffers);
            }
            else
            {
                AddGableWallSegment(curr, next, frame, buffers);
            }
        }
    }

    private static void AddGableWallSegment(Vector2 curr, Vector2 next, in RoofFrame frame, RoofBuffers buffers)
    {
        // Roof surfaces inside the walls are at least wall-top height; clamp numerical undershoots to avoid holes.
        float topCurrY = Mathf.Max(frame.SurfaceY(curr), frame.BaseHeight);
        float topNextY = Mathf.Max(frame.SurfaceY(next), frame.BaseHeight);

        if (topCurrY - frame.BaseHeight < MinGableWallHeight && topNextY - frame.BaseHeight < MinGableWallHeight)
        {
            return;
        }

        AddVerticalBand(curr, next, frame.BaseHeight, frame.BaseHeight, topCurrY, topNextY,
            frame, buffers, buffers.GableTriangles);
    }

    /// <summary>
    /// Add a vertical strip between two points. For CCW polygons, bottom → top → next top faces outward.
    /// AddFace discards degenerate triangles where height becomes zero, allowing either trapezoids or triangles.
    /// </summary>
    private static void AddVerticalBand(Vector2 curr, Vector2 next,
        float bottomCurrY, float bottomNextY, float topCurrY, float topNextY,
        in RoofFrame frame, RoofBuffers buffers, List<int> triangles)
    {
        Vector3 bottomCurr = new Vector3(curr.x, bottomCurrY, curr.y);
        Vector3 bottomNext = new Vector3(next.x, bottomNextY, next.y);
        Vector3 topCurr = new Vector3(curr.x, topCurrY, curr.y);
        Vector3 topNext = new Vector3(next.x, topNextY, next.y);

        AddFace(bottomCurr, topCurr, topNext, frame, buffers, triangles);
        AddFace(bottomCurr, topNext, bottomNext, frame, buffers, triangles);
    }

    /// <summary>
    /// Clip the polygon to the requested side (sign) of the ridge (Sutherland–Hodgman).
    /// Preserve CCW winding; the cut remains an edge on the ridge.
    /// </summary>
    private static Vector2[] ClipToRidgeSide(Vector2[] polygon, in RoofFrame frame, int sign)
    {
        var result = new List<Vector2>(polygon.Length + 4);
        int count = polygon.Length;

        for (int i = 0; i < count; i++)
        {
            Vector2 curr = polygon[i];
            Vector2 next = polygon[(i + 1) % count];
            float distCurr = sign * frame.Across(curr);
            float distNext = sign * frame.Across(next);

            if (distCurr >= 0f)
            {
                result.Add(curr);
            }
            if ((distCurr > 0f && distNext < 0f) || (distCurr < 0f && distNext > 0f))
            {
                result.Add(Vector2.Lerp(curr, next, distCurr / (distCurr - distNext)));
            }
        }

        return result.Count >= 3 ? result.ToArray() : null;
    }

    /// <summary>
    /// Add one triangle. Use face normals and UV distances along the ridge (U) and slope (V).
    /// Skip degenerate triangles with near-zero area.
    /// </summary>
    private static void AddFace(Vector3 a, Vector3 b, Vector3 c,
        in RoofFrame frame, RoofBuffers buffers, List<int> triangles)
    {
        Vector3 faceNormal = Vector3.Cross(b - a, c - a);
        if (faceNormal.sqrMagnitude < 1e-8f)
        {
            return;
        }
        faceNormal.Normalize();

        int startIndex = buffers.Vertices.Count;
        AddVertex(a, faceNormal, frame, buffers);
        AddVertex(b, faceNormal, frame, buffers);
        AddVertex(c, faceNormal, frame, buffers);

        triangles.Add(startIndex);
        triangles.Add(startIndex + 1);
        triangles.Add(startIndex + 2);
    }

    private static void AddVertex(Vector3 position, Vector3 faceNormal, in RoofFrame frame, RoofBuffers buffers)
    {
        buffers.Vertices.Add(position);
        buffers.Normals.Add(faceNormal);

        Vector2 planar = new Vector2(position.x, position.z) - frame.Center;
        float alongRidge = Vector2.Dot(planar, frame.RidgeDir);
        float acrossRidge = Vector2.Dot(planar, frame.WidthDir);
        // Distance along the descending slope from ridge to eave. Include height difference to remain continuous across gable walls.
        float slopeDistance = Mathf.Sqrt(acrossRidge * acrossRidge +
            (position.y - frame.BaseHeight) * (position.y - frame.BaseHeight));
        buffers.Uvs.Add(new Vector2(alongRidge * UvScale, slopeDistance * UvScale));
    }
}
