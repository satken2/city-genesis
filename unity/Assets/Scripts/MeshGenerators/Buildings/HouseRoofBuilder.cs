// Assets/Scripts/MeshGenerators/Buildings/HouseRoofBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Residential triangular (hip-style) roof mesh generator.
/// Find the ridge from the eave-expanded footprint; project footprint vertices onto it to create sloped surfaces.
///
/// Geometry/materials follow other roof conventions (flat/gable): ridge rise is
/// a fixed ratio of floor height (not building height), eave overhang is fixed, and materials resolve from
/// `ceil` parts.
/// </summary>
public static class HouseRoofBuilder
{
    /// <summary>
    /// Ratio of floor height determining ridge rise (wall top to ridge).
    /// Proportional building height would make roofs oversized only on tall buildings; use floor height, as in gable roofs
    /// (<see cref="GableRoofBuilder"/>), to keep roof height consistent across the streetscape.
    /// </summary>
    private const float RiseFloorHeightRatio = 0.5f;

    /// <summary>Eave overhang (m). Roof extension beyond walls. Same value as gable roofs.</summary>
    private const float EaveOverhang = 0.4f;

    /// <summary>Ratio of ridge rise determining inward setback of ridge endpoints.</summary>
    private const float RidgeSetbackRiseRatio = 2f;

    /// <summary>
    /// Find the ridge from the eave-expanded footprint and extrude a triangular roof into a mesh.
    /// </summary>
    /// <param name="footprint">Building footprint vertices (CCW).</param>
    /// <param name="baseHeight">Roof-base height = wall top (rendered body height).</param>
    /// <param name="floorHeight">Height per floor. Ridge rise is fixed at half this.</param>
    /// <param name="prefix">Model prefix (for ceil lookup).</param>
    /// <param name="resolvedSuffix">Resolved suffix (for ceil lookup).</param>
    /// <param name="fallbackMaterials">Fallback materials when ceil is absent.</param>
    public static BuildingMeshResult GenerateRoof(
        Vector2[] footprint, float baseHeight, float floorHeight,
        string prefix, int resolvedSuffix, Material[] fallbackMaterials)
    {
        if (footprint == null || footprint.Length < 3)
        {
            Debug.LogError("HouseRoofBuilder.GenerateRoof: footprint is null or less than 3");
            return null;
        }

        // Expand the footprint outward by the eave overhang to form the roof-base polygon.
        Vector2[] roofBaseFootprint = BuildingMeshUtils.OffsetPolygonOutward(footprint, EaveOverhang);
        if (roofBaseFootprint == null || roofBaseFootprint.Length < 3)
        {
            roofBaseFootprint = footprint;
        }

        // Get ridge polyline (straight for rectangles, bent for L shapes).
        if (!TryResolveRoofAxis(roofBaseFootprint, out Vector2[] axisPoints) ||
            axisPoints == null || axisPoints.Length < 2)
        {
            Vector2 s = roofBaseFootprint[0];
            Vector2 e = roofBaseFootprint[roofBaseFootprint.Length / 2];
            Vector2 m = (s + e) * 0.5f;
            axisPoints = new[] { s, m, e };
        }

        // Roof shape with the ridge raised. Rise depends on floor height, not building height.
        float roofRise = (floorHeight > 0f ? floorHeight : LocalConstants.FLOOR_HEIGHT) * RiseFloorHeightRatio;
        float roofTopHeight = baseHeight + roofRise;

        // Set back only the two ridge endpoints.
        float ridgeSetback = roofRise * RidgeSetbackRiseRatio;
        ApplyRidgeSetback(axisPoints, ridgeSetback);

        int baseVertexCount = roofBaseFootprint.Length;
        Vector3[] basePositions = new Vector3[baseVertexCount];
        Vector3[] ridgePositions = new Vector3[baseVertexCount];

        for (int i = 0; i < baseVertexCount; i++)
        {
            Vector2 p = roofBaseFootprint[i];
            basePositions[i] = new Vector3(p.x, baseHeight, p.y);
        }

        // Branch on whether the shape is an L (= six vertices).
        bool lShapeHandled = false;
        if (roofBaseFootprint.Length == 6 && axisPoints.Length >= 3)
        {
            lShapeHandled = ComputeRidgePointsForLShape(
                roofBaseFootprint, axisPoints, roofTopHeight, ridgePositions);
        }

        // For non-L shapes, use the existing distance-based projection.
        if (!lShapeHandled)
        {
            for (int i = 0; i < baseVertexCount; i++)
            {
                Vector2 ridge2D = ComputeRidgePoint(roofBaseFootprint[i], axisPoints);
                ridgePositions[i] = new Vector3(ridge2D.x, roofTopHeight, ridge2D.y);
            }
        }

        // Generate mesh.
        List<Vector3> vertices = new List<Vector3>(baseVertexCount * 6);
        List<int> triangles = new List<int>(baseVertexCount * 6);
        List<Vector3> normals = new List<Vector3>(baseVertexCount * 6);

        // Generate panels connecting the footprint to the ridge.
        for (int i = 0; i < baseVertexCount; i++)
        {
            int next = (i + 1) % baseVertexCount;

            Vector3 bottomCurr = basePositions[i];
            Vector3 bottomNext = basePositions[next];
            Vector3 ridgeCurr = ridgePositions[i];
            Vector3 ridgeNext = ridgePositions[next];

            // Form a quad with two triangles.
            AddLinearFace(bottomCurr, ridgeCurr, ridgeNext, vertices, triangles, normals);
            AddLinearFace(bottomCurr, ridgeNext, bottomNext, vertices, triangles, normals);
        }

        Mesh roofMesh = new Mesh();
        roofMesh.vertices = vertices.ToArray();
        roofMesh.triangles = triangles.ToArray();
        roofMesh.normals = normals.ToArray();
        roofMesh.RecalculateBounds();

        Material roofMaterial = BuildingMeshUtils.SelectRoofMaterial(prefix, resolvedSuffix, fallbackMaterials);
        return new BuildingMeshResult(roofMesh, roofMaterial != null ? new[] { roofMaterial } : null);
    }

    // Set back only the two endpoints of the ridge polyline inward.
    private static void ApplyRidgeSetback(Vector2[] axisPoints, float setback)
    {
        if (axisPoints == null || axisPoints.Length < 2)
            return;

        if (setback <= 0f)
            return;

        int n = axisPoints.Length;

        if (n == 3)
        {
            Vector2 p0 = axisPoints[0];
            Vector2 p1 = axisPoints[1];
            Vector2 p2 = axisPoints[2];

            Vector2 d01 = p1 - p0;
            float len01 = d01.magnitude;
            if (len01 > 1e-5f)
            {
                float t = Mathf.Min(1f, setback / len01);
                axisPoints[0] = p0 + d01 * t;
            }

            Vector2 d21 = p1 - p2;
            float len21 = d21.magnitude;
            if (len21 > 1e-5f)
            {
                float t = Mathf.Min(1f, setback / len21);
                axisPoints[2] = p2 + d21 * t;
            }

            return;
        }

        {
            Vector2 p0 = axisPoints[0];
            Vector2 p1 = axisPoints[1];
            Vector2 d01 = p1 - p0;
            float len01 = d01.magnitude;
            if (len01 > 1e-5f)
            {
                float t = Mathf.Min(1f, setback / len01);
                axisPoints[0] = p0 + d01 * t;
            }

            int last = n - 1;
            Vector2 pn = axisPoints[last];
            Vector2 pn_1 = axisPoints[last - 1];
            Vector2 dn = pn_1 - pn;
            float lenn = dn.magnitude;
            if (lenn > 1e-5f)
            {
                float t = Mathf.Min(1f, setback / lenn);
                axisPoints[last] = pn + dn * t;
            }
        }
    }

    /// <summary>
    /// Add a triangular face and assign face normals to its vertices.
    /// </summary>
    private static void AddLinearFace(Vector3 a, Vector3 b, Vector3 c, List<Vector3> vertices,
        List<int> triangles, List<Vector3> normals)
    {
        int startIndex = vertices.Count;
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);

        Vector3 faceNormal = Vector3.Cross(b - a, c - a).normalized;
        normals.Add(faceNormal);
        normals.Add(faceNormal);
        normals.Add(faceNormal);

        triangles.Add(startIndex);
        triangles.Add(startIndex + 1);
        triangles.Add(startIndex + 2);
    }

    private static bool TryResolveRoofAxis(Vector2[] footprint, out Vector2[] axisPoints)
    {
        axisPoints = null;

        if (footprint.Length == 4)
        {
            ResolveQuadrilateralAxis(footprint, out Vector2 s, out Vector2 e);
            Vector2 mid = (s + e) * 0.5f;
            axisPoints = new[] { s, mid, e };
            return true;
        }

        if (footprint.Length == 6 && TryResolveLShapeAxis(footprint, out Vector2 a0, out Vector2 ac, out Vector2 a1))
        {
            axisPoints = new[] { a0, ac, a1 };
            return true;
        }

        if (TryResolveFallbackAxis(footprint, out Vector2 fs, out Vector2 fe))
        {
            Vector2 mid = (fs + fe) * 0.5f;
            axisPoints = new[] { fs, mid, fe };
            return true;
        }

        return false;
    }

    private static bool TryResolveLShapeAxis(Vector2[] footprint,
        out Vector2 end0, out Vector2 corner, out Vector2 end1)
    {
        end0 = end1 = corner = Vector2.zero;
        int n = footprint.Length;
        if (n != 6) return false;

        int concaveIndex = -1;
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = footprint[(i - 1 + n) % n];
            Vector2 curr = footprint[i];
            Vector2 next = footprint[(i + 1) % n];

            Vector2 e0 = curr - prev;
            Vector2 e1 = next - curr;
            float cross = e0.x * e1.y - e0.y * e1.x;

            if (cross < 0f)
            {
                concaveIndex = i;
                break;
            }
        }

        if (concaveIndex < 0)
            return false;

        int ic = concaveIndex;
        int iPrev = (ic - 1 + n) % n;
        int iPrevPrev = (ic - 2 + n) % n;
        int iNext = (ic + 1) % n;
        int iNextNext = (ic + 2) % n;

        // In a six-vertex L shape, the convex vertex opposite the concave vertex is always three positions ahead.
        // Distance-based searches misidentify thin-wing L shapes because the vertex two positions ahead can be farther away.
        corner = (footprint[ic] + footprint[(ic + 3) % n]) * 0.5f;

        end0 = (footprint[iPrevPrev] + footprint[iPrev]) * 0.5f;
        end1 = (footprint[iNext] + footprint[iNextNext]) * 0.5f;

        return true;
    }

    private static void ResolveQuadrilateralAxis(Vector2[] footprint, out Vector2 axisStart, out Vector2 axisEnd)
    {
        Vector2 mid01 = (footprint[0] + footprint[1]) * 0.5f;
        Vector2 mid23 = (footprint[2] + footprint[3]) * 0.5f;
        Vector2 mid12 = (footprint[1] + footprint[2]) * 0.5f;
        Vector2 mid30 = (footprint[3] + footprint[0]) * 0.5f;

        float dist01 = Vector2.Distance(mid01, mid23);
        float dist12 = Vector2.Distance(mid12, mid30);

        if (dist01 >= dist12)
        {
            axisStart = mid01;
            axisEnd = mid23;
        }
        else
        {
            axisStart = mid12;
            axisEnd = mid30;
        }
    }

    private static bool TryResolveFallbackAxis(Vector2[] footprint, out Vector2 axisStart, out Vector2 axisEnd)
    {
        axisStart = axisEnd = footprint[0];
        float bestDistance = 0f;

        for (int i = 0; i < footprint.Length; i++)
        {
            for (int j = i + 1; j < footprint.Length; j++)
            {
                float distance = Vector2.Distance(footprint[i], footprint[j]);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    axisStart = footprint[i];
                    axisEnd = footprint[j];
                }
            }
        }

        return bestDistance > 0.01f;
    }

    private static bool ComputeRidgePointsForLShape(
        Vector2[] footprint,
        Vector2[] axisPoints,
        float roofTopHeight,
        Vector3[] ridgePositions)
    {
        int n = footprint.Length;
        if (n != 6 || axisPoints == null || axisPoints.Length < 3)
            return false;

        int concaveIndex = -1;
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = footprint[(i - 1 + n) % n];
            Vector2 curr = footprint[i];
            Vector2 next = footprint[(i + 1) % n];

            Vector2 e0 = curr - prev;
            Vector2 e1 = next - curr;
            float cross = e0.x * e1.y - e0.y * e1.x;

            if (cross < 0f)
            {
                concaveIndex = i;
                break;
            }
        }
        if (concaveIndex < 0)
            return false;

        int ic = concaveIndex;
        int iPrev = (ic - 1 + n) % n;
        int iPrevPrev = (ic - 2 + n) % n;
        int iNext = (ic + 1) % n;
        int iNextNext = (ic + 2) % n;

        // The opposite vertex is three positions ahead, matching TryResolveLShapeAxis. All six vertices receive ridge positions.
        int oppositeIndex = (ic + 3) % n;

        Vector3 ridgeEnd0 = new Vector3(axisPoints[0].x, roofTopHeight, axisPoints[0].y);
        Vector3 ridgeCorner = new Vector3(axisPoints[1].x, roofTopHeight, axisPoints[1].y);
        Vector3 ridgeEnd1 = new Vector3(axisPoints[2].x, roofTopHeight, axisPoints[2].y);

        ridgePositions[iPrevPrev] = ridgeEnd0;
        ridgePositions[iPrev] = ridgeEnd0;
        ridgePositions[iNext] = ridgeEnd1;
        ridgePositions[iNextNext] = ridgeEnd1;

        ridgePositions[ic] = ridgeCorner;
        ridgePositions[oppositeIndex] = ridgeCorner;

        return true;
    }

    private static Vector2 ComputeRidgePoint(Vector2 basePoint, Vector2[] axisPoints)
    {
        if (axisPoints == null || axisPoints.Length == 0)
            return basePoint;

        if (axisPoints.Length == 1)
            return axisPoints[0];

        if (axisPoints.Length == 2)
        {
            Vector2 a = axisPoints[0];
            Vector2 b = axisPoints[1];
            return PolylineUtils.ClosestPointOnSegment(basePoint, a, b);
        }

        Vector2 best = axisPoints[0];
        float bestDist2 = float.MaxValue;

        for (int i = 0; i < axisPoints.Length - 1; i++)
        {
            Vector2 a = axisPoints[i];
            Vector2 b = axisPoints[i + 1];
            if ((b - a).sqrMagnitude < 1e-10f) continue;
            Vector2 proj = PolylineUtils.ClosestPointOnSegment(basePoint, a, b);
            float d2 = (basePoint - proj).sqrMagnitude;

            if (d2 < bestDist2)
            {
                bestDist2 = d2;
                best = proj;
            }
        }

        return best;
    }
}
