// Assets/Scripts/MeshGenerators/Buildings/ShedRoofBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Shed-roof mesh generator (simple corrugated-metal hut).
/// Walls/corners/ground match MODEL_MODULAR_FLAT; only the roof is replaced.
///
/// Roof is one thin board without a ridge, sloping along the building's short direction.
/// Its underside passes exactly through body top at the lower wall and rises by the specified amount
/// at the higher wall. Eave overhang extends beyond walls, below the low side
/// and above the high side.
///
/// Close gaps between wall tops/board underside (zero height on low side, rise-height rectangle on high side,
/// triangles on the sides) with plain-wall boards using `generic-wall` material,
/// as for gable roofs.
///
/// Roof surfaces use `ceil` like flat roofs (MODEL_MODULAR_FLAT), gable walls use
/// `generic-wall`; return separate submeshes.
/// </summary>
public static class ShedRoofBuilder
{
    /// <summary>Eave overhang (m), roof-board projection beyond walls.</summary>
    private const float EaveOverhang = 0.3f;

    /// <summary>Roof-board thickness (m, vertical). Thinner than gable roofs to represent thin corrugated metal.</summary>
    private const float RoofThickness = 0.08f;

    /// <summary>
    /// Ratio of floor height determining high-side rise (low-wall top to high-wall top).
    /// Like gable roofs, fix to floor height instead of building width
    /// to prevent oversized roofs on large buildings. As shed slopes span full width,
    /// use a gentler rise than gable roofs (ridge height = half floor height).
    /// </summary>
    private const float RiseFloorHeightRatio = 0.35f;

    /// <summary>World-coordinate UV scale (UV units per 1m).</summary>
    private const float UvScale = 0.2f;

    /// <summary>Omit gable walls below this height (m).</summary>
    private const float MinGableWallHeight = 1e-3f;

    /// <summary>
    /// Geometric frame determining roof height. Underside height depends only on the slope-direction coordinate,
    /// so the entire board is one perfect plane.
    /// </summary>
    private readonly struct RoofFrame
    {
        public readonly Vector2 Center;
        /// <summary>Direction parallel to eave/verge (bounding rectangle's long direction). UV U axis.</summary>
        public readonly Vector2 EaveDir;
        /// <summary>Slope direction from low to high side (bounding rectangle's short direction).</summary>
        public readonly Vector2 SlopeDir;
        /// <summary>Half-width along slope; also center-to-low-wall distance.</summary>
        public readonly float HalfWidth;
        public readonly float Slope;
        public readonly float BaseHeight;

        public RoofFrame(Vector2 center, Vector2 eaveDir, Vector2 slopeDir,
            float halfWidth, float slope, float baseHeight)
        {
            Center = center;
            EaveDir = eaveDir;
            SlopeDir = slopeDir;
            HalfWidth = halfWidth;
            Slope = slope;
            BaseHeight = baseHeight;
        }

        /// <summary>Slope-direction distance with low wall at zero.</summary>
        public float Along(Vector2 point) => Vector2.Dot(point - Center, SlopeDir) + HalfWidth;

        /// <summary>Roof-board underside height.</summary>
        public float SurfaceY(Vector2 point) => BaseHeight + Along(point) * Slope;

        /// <summary>Point on underside (offsetY=0) or top (offsetY=thickness).</summary>
        public Vector3 SurfacePoint(Vector2 point, float offsetY)
            => new Vector3(point.x, SurfaceY(point) + offsetY, point.y);
    }

    /// <summary>
    /// Face buffer. Roof and gable-wall materials differ, requiring separate submeshes.
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
    /// Generate a shed-roof mesh.
    /// Even without a material, retain/return the mesh so the final combining stage
    /// can assign a default material.
    /// </summary>
    /// <param name="footprint">Building footprint vertices.</param>
    /// <param name="baseHeight">Wall-top height = rendered body height.</param>
    /// <param name="floorHeight">Height per floor. High-side rise is proportional to this.</param>
    /// <param name="prefix">Model prefix (ceil / generic-wall lookup).</param>
    /// <param name="resolvedSuffix">Resolved suffix (ceil / generic-wall lookup).</param>
    /// <param name="fallbackMaterials">Fallback materials when parts are absent.</param>
    public static BuildingMeshResult GenerateRoof(
        Vector2[] footprint, float baseHeight, float floorHeight,
        string prefix, int resolvedSuffix, Material[] fallbackMaterials)
    {
        if (footprint == null || footprint.Length < 3)
        {
            Debug.LogError("ShedRoofBuilder.GenerateRoof: footprint is null or less than 3");
            return null;
        }

        // Normalize because ear clipping/offset outward normals assume CCW.
        Vector2[] normalized = (Vector2[])footprint.Clone();
        if (PolygonUtils.SignedArea(normalized) < 0f)
        {
            System.Array.Reverse(normalized);
        }

        // Determine slope direction/half-width from wall outline. Including eaves
        // would misalign low-side roof/wall tops and gable-wall height.
        if (!BuildingMeshUtils.TryResolveMinAreaRect(normalized, out Vector2 center, out Vector2 eaveDir,
                out Vector2 shortDir, out _, out float halfWidth))
        {
            // Fall back to flat roofs for degenerate polygons without a determinable direction.
            Debug.LogWarning("ShedRoofBuilder.GenerateRoof: 傾斜方向を決定できないため平屋根にフォールバックします。");
            return FlatRoofBuilder.GenerateRoof(footprint, baseHeight, prefix, resolvedSuffix, fallbackMaterials);
        }

        // Slope downward toward the front (edge vertex 0→1, same convention as rooftop props/entrances).
        // Eaves face the road, exposing the underside of the low eaves from the street.
        Vector2 slopeDir = ResolveUphillDirection(normalized, shortDir);

        // Rise depends on floor height, not building height. Slope varies with full building width,
        // becoming gentler on wider buildings.
        float rise = (floorHeight > 0f ? floorHeight : LocalConstants.FLOOR_HEIGHT) * RiseFloorHeightRatio;
        // Derive slope from rise/full width so the roof passes exactly through the low wall top.
        float slope = rise / (halfWidth * 2f);
        var frame = new RoofFrame(center, eaveDir, slopeDir, halfWidth, slope, baseHeight);

        // The eave-expanded polygon forms the roof-board outline.
        Vector2[] eaveFootprint = BuildingMeshUtils.OffsetPolygonOutward(normalized, EaveOverhang);
        if (eaveFootprint == null || eaveFootprint.Length < 3)
        {
            eaveFootprint = normalized;
        }

        var buffers = new RoofBuffers();

        // Without a ridge, create one flat board across the whole outline.
        AddRoofBoard(eaveFootprint, frame, buffers);

        // Close gaps between wall tops and board underside (gable walls).
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
    /// Determine slope direction (low → high). Make the high side opposite the front edge's (vertex 0→1)
    /// outward normal so the front stays low. If the front edge is nearly perpendicular to slope direction
    /// and neither side is clear, retain the short direction.
    /// </summary>
    private static Vector2 ResolveUphillDirection(Vector2[] wallPolygon, Vector2 shortDir)
    {
        Vector2 frontEdge = wallPolygon[1] - wallPolygon[0];
        if (frontEdge.sqrMagnitude < 1e-6f)
        {
            return shortDir;
        }

        // CCW polygon outward normal.
        Vector2 frontNormal = frontEdge.normalized;
        Vector2 frontOutward = new Vector2(frontNormal.y, -frontNormal.x);

        return Vector2.Dot(frontOutward, shortDir) > 0f ? -shortDir : shortDir;
    }

    /// <summary>
    /// Create one thick roof board with underside/top/perimeter edge faces.
    /// Unlike gable roofs, no ridge means no omitted edge faces.
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

            // Underside (visible under eaves). CCW triangulation directly faces downward.
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

            float currY = frame.SurfaceY(curr);
            float nextY = frame.SurfaceY(next);
            AddVerticalBand(curr, next, currY, nextY, currY + RoofThickness, nextY + RoofThickness,
                frame, buffers, buffers.RoofTriangles);
        }
    }

    /// <summary>
    /// Close wall-top/board-underside gaps with vertical boards (gable walls).
    /// At the low wall, the roof passes exactly through wall top, giving zero height; the high side
    /// forms a rise-height rectangle, and side walls form triangles rising from low to high.
    /// The roof is one unbroken plane; unlike gable roofs, edges need no splitting.
    /// </summary>
    private static void AddGableWalls(Vector2[] wallPolygon, in RoofFrame frame, RoofBuffers buffers)
    {
        int count = wallPolygon.Length;
        for (int i = 0; i < count; i++)
        {
            Vector2 curr = wallPolygon[i];
            Vector2 next = wallPolygon[(i + 1) % count];

            // Roof lies at least at wall top inside walls; clamp numerical undershoots to prevent holes.
            float topCurrY = Mathf.Max(frame.SurfaceY(curr), frame.BaseHeight);
            float topNextY = Mathf.Max(frame.SurfaceY(next), frame.BaseHeight);

            if (topCurrY - frame.BaseHeight < MinGableWallHeight && topNextY - frame.BaseHeight < MinGableWallHeight)
            {
                continue;
            }

            AddVerticalBand(curr, next, frame.BaseHeight, frame.BaseHeight, topCurrY, topNextY,
                frame, buffers, buffers.GableTriangles);
        }
    }

    /// <summary>
    /// Add a vertical strip between two points. For CCW polygons, bottom → top → next top faces outward.
    /// AddFace discards zero-height degenerate triangles, supporting either trapezoids or triangles.
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
    /// Add one triangle with face normals and UV distances along eaves (U) and slope (V).
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

        Vector2 planar = new Vector2(position.x, position.z);
        float alongEave = Vector2.Dot(planar - frame.Center, frame.EaveDir);
        // Distance uphill along the slope from low eaves to high side. Include height difference for gable-wall continuity.
        float acrossSlope = frame.Along(planar);
        float slopeDistance = Mathf.Sqrt(acrossSlope * acrossSlope +
            (position.y - frame.BaseHeight) * (position.y - frame.BaseHeight));
        buffers.Uvs.Add(new Vector2(alongEave * UvScale, slopeDistance * UvScale));
    }
}
