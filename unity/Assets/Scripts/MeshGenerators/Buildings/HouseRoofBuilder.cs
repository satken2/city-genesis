// Assets/Scripts/MeshGenerators/Buildings/HouseRoofBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 住宅風の三角屋根（切妻屋根）のメッシュを生成するクラス。
/// 建物底面を少し拡張したポリゴンから棟線を求め、二段構成で屋根を押し出してメッシュ化する。
/// </summary>
public static class HouseRoofBuilder
{
    private static readonly string[] RoofMaterialKeys = new[]
    {
        "B02_ROOF_01", "B02_ROOF_02", "B02_ROOF_03", "B02_ROOF_04", "B02_ROOF_05",
        "B02_ROOF_06", "B02_ROOF_07", "B02_ROOF_08", "B02_ROOF_09", "B02_ROOF_10"
    };

    private static readonly string[] RoofMaterialPathFormats = new[]
    {
        "B02/{0}",
        "{0}"
    };

    private static readonly Dictionary<string, Material> RoofMaterialCache = new Dictionary<string, Material>();

    /// <summary>
    /// 建物底面を少し拡張したポリゴンから棟線を求め、二段構成で三角屋根を押し出してメッシュ化する。
    /// </summary>
    /// <param name="footprint">建物底面の頂点（CCW）</param>
    /// <param name="baseHeight">屋根底面の高さ（建物高さ）</param>
    /// <param name="visualVariant">屋根マテリアル選択用バリアント</param>
    public static BuildingMeshResult GenerateRoof(Vector2[] footprint, float baseHeight, float visualVariant = 0f)
    {
        if (footprint == null || footprint.Length < 3)
        {
            Debug.LogError("HouseRoofBuilder.GenerateRoof: footprint is null or less than 3");
            return null;
        }

        const float roofOffsetScale = 0.15f;
        const float roofRiseFactor = 0.6f;

        // 底面を少し縮小／拡張して屋根の底面ポリゴンを作る
        Vector2[] roofBaseFootprint = OffsetPolygonOutward(footprint, baseHeight * roofOffsetScale);
        if (roofBaseFootprint == null || roofBaseFootprint.Length < 3)
        {
            roofBaseFootprint = footprint;
        }

        // 棟線ポリラインを取得（四角形なら一直線、L 字なら折れ曲がり）
        if (!TryResolveRoofAxis(roofBaseFootprint, out Vector2[] axisPoints) ||
            axisPoints == null || axisPoints.Length < 2)
        {
            Vector2 s = roofBaseFootprint[0];
            Vector2 e = roofBaseFootprint[roofBaseFootprint.Length / 2];
            Vector2 m = (s + e) * 0.5f;
            axisPoints = new[] { s, m, e };
        }

        // 棟線を上下に持ち上げた屋根形状
        float roofRise = baseHeight * roofRiseFactor;
        float roofTopHeight = baseHeight + roofRise;

        // 棟線の両端だけセットバックする
        float ridgeSetback = roofRise * 2f;
        ApplyRidgeSetback(axisPoints, ridgeSetback);

        int baseVertexCount = roofBaseFootprint.Length;
        Vector3[] basePositions = new Vector3[baseVertexCount];
        Vector3[] ridgePositions = new Vector3[baseVertexCount];

        for (int i = 0; i < baseVertexCount; i++)
        {
            Vector2 p = roofBaseFootprint[i];
            basePositions[i] = new Vector3(p.x, baseHeight, p.y);
        }

        // L 字(= 6 頂点)かどうかで分岐
        bool lShapeHandled = false;
        if (roofBaseFootprint.Length == 6 && axisPoints.Length >= 3)
        {
            lShapeHandled = ComputeRidgePointsForLShape(
                roofBaseFootprint, axisPoints, roofTopHeight, ridgePositions);
        }

        // L 字でなければ従来の距離ベース射影
        if (!lShapeHandled)
        {
            for (int i = 0; i < baseVertexCount; i++)
            {
                Vector2 ridge2D = ComputeRidgePoint(roofBaseFootprint[i], axisPoints);
                ridgePositions[i] = new Vector3(ridge2D.x, roofTopHeight, ridge2D.y);
            }
        }

        // メッシュ生成
        List<Vector3> vertices = new List<Vector3>(baseVertexCount * 6);
        List<int> triangles = new List<int>(baseVertexCount * 6);
        List<Vector3> normals = new List<Vector3>(baseVertexCount * 6);

        // 底面と棟線をつなぐパネルを生成
        for (int i = 0; i < baseVertexCount; i++)
        {
            int next = (i + 1) % baseVertexCount;

            Vector3 bottomCurr = basePositions[i];
            Vector3 bottomNext = basePositions[next];
            Vector3 ridgeCurr = ridgePositions[i];
            Vector3 ridgeNext = ridgePositions[next];

            // 2 三角形で四辺形を張る
            AddLinearFace(bottomCurr, ridgeCurr, ridgeNext, vertices, triangles, normals);
            AddLinearFace(bottomCurr, ridgeNext, bottomNext, vertices, triangles, normals);
        }

        Mesh roofMesh = new Mesh();
        roofMesh.vertices = vertices.ToArray();
        roofMesh.triangles = triangles.ToArray();
        roofMesh.normals = normals.ToArray();
        roofMesh.RecalculateBounds();

        Material roofMaterial = SelectRoofMaterial(visualVariant);
        Material[] materials = roofMaterial != null ? new[] { roofMaterial } : null;
        return new BuildingMeshResult(roofMesh, materials);
    }

    // 棟線ポリラインの「両端だけ」を内側にセットバックする。
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
    /// 線分状の面（三角形）を追加し、頂点法線を面法線で設定する。
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

        Vector2 concave = footprint[ic];
        float maxDist2 = -1f;
        int farIndex = -1;
        for (int i = 0; i < n; i++)
        {
            if (i == ic) continue;
            float d2 = (footprint[i] - concave).sqrMagnitude;
            if (d2 > maxDist2)
            {
                maxDist2 = d2;
                farIndex = i;
            }
        }
        if (farIndex < 0)
            return false;

        Vector2 opposite = footprint[farIndex];
        corner = (concave + opposite) * 0.5f;

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

        float maxDist2 = -1f;
        int oppositeIndex = -1;
        Vector2 concave = footprint[ic];

        for (int i = 0; i < n; i++)
        {
            if (i == ic || i == iPrev || i == iNext)
                continue;

            float d2 = (footprint[i] - concave).sqrMagnitude;
            if (d2 > maxDist2)
            {
                maxDist2 = d2;
                oppositeIndex = i;
            }
        }
        if (oppositeIndex < 0)
            return false;

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
            Vector2 ab = b - a;
            float len = ab.magnitude;
            if (len < 1e-5f) return a;

            Vector2 dir = ab / len;
            float t = Mathf.Clamp01(Vector2.Dot(basePoint - a, dir) / len);
            return a + ab * t;
        }

        Vector2 best = axisPoints[0];
        float bestDist2 = float.MaxValue;

        for (int i = 0; i < axisPoints.Length - 1; i++)
        {
            Vector2 a = axisPoints[i];
            Vector2 b = axisPoints[i + 1];
            Vector2 ab = b - a;
            float len = ab.magnitude;
            if (len < 1e-5f) continue;

            Vector2 dir = ab / len;
            float t = Mathf.Clamp01(Vector2.Dot(basePoint - a, dir) / len);
            Vector2 proj = a + ab * t;
            float d2 = (basePoint - proj).sqrMagnitude;

            if (d2 < bestDist2)
            {
                bestDist2 = d2;
                best = proj;
            }
        }

        return best;
    }

    private static Vector2[] OffsetPolygonOutward(Vector2[] footprint, float offset)
    {
        if (footprint == null || footprint.Length < 3)
        {
            return null;
        }

        int count = footprint.Length;
        Vector2[] offsetPoints = new Vector2[count];

        for (int i = 0; i < count; i++)
        {
            Vector2 prev = footprint[(i - 1 + count) % count];
            Vector2 curr = footprint[i];
            Vector2 next = footprint[(i + 1) % count];

            Vector2 prevDir = (curr - prev);
            Vector2 nextDir = (next - curr);
            if (prevDir.sqrMagnitude < 1e-6f || nextDir.sqrMagnitude < 1e-6f)
            {
                offsetPoints[i] = curr;
                continue;
            }

            Vector2 prevNorm = prevDir.normalized;
            Vector2 nextNorm = nextDir.normalized;
            Vector2 prevOutward = new Vector2(prevNorm.y, -prevNorm.x);
            Vector2 nextOutward = new Vector2(nextNorm.y, -nextNorm.x);
            Vector2 bisector = prevOutward + nextOutward;

            if (bisector.sqrMagnitude < 1e-6f)
            {
                offsetPoints[i] = curr + nextOutward * offset;
                continue;
            }

            Vector2 offsetDir = bisector.normalized;
            float alignment = Mathf.Clamp(Vector2.Dot(offsetDir, prevOutward), 0.1f, 1f);
            offsetPoints[i] = curr + offsetDir * (offset / alignment);
        }

        return offsetPoints;
    }

    private static Material SelectRoofMaterial(float visualVariant)
    {
        if (RoofMaterialKeys == null || RoofMaterialKeys.Length == 0)
        {
            return null;
        }

        float normalized = Mathf.Clamp01(visualVariant);
        int materialCount = RoofMaterialKeys.Length;
        int rawIndex = Mathf.RoundToInt(normalized * materialCount);
        int selectedIndex = Mathf.Clamp(rawIndex - 1, 0, materialCount - 1);
        return LoadRoofMaterial(RoofMaterialKeys[selectedIndex]);
    }

    private static Material LoadRoofMaterial(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (RoofMaterialCache.TryGetValue(key, out Material cached))
        {
            return cached;
        }

        Material material = null;
        foreach (string format in RoofMaterialPathFormats)
        {
            string path = string.Format(format, key);
            material = Resources.Load<Material>(path);
            if (material != null)
            {
                break;
            }
        }

        RoofMaterialCache[key] = material;
        return material;
    }
}
