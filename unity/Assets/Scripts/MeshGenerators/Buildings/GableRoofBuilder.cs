// Assets/Scripts/MeshGenerators/Buildings/GableRoofBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 切妻屋根（ゲーブルルーフ）のメッシュを生成するクラス。
/// 壁・角・地面の生成は MODEL_MODULAR_FLAT と同じで、屋根だけをこのクラスで差し替える。
/// 建物底面を軒の出だけ外側に広げたポリゴンから最小面積の外接矩形を求め、その長辺方向を
/// 棟線として二枚の勾配面を張る。棟線は妻側の端まで伸ばすため、棟線の両端には自動的に
/// 垂直な三角形（妻壁）が生成される。
/// マテリアルは平屋根（MODEL_MODULAR_FLAT）と同じく `ceil` パーツから取得する。
/// </summary>
public static class GableRoofBuilder
{
    /// <summary>軒の出（m）。</summary>
    private const float EaveOverhang = 0.4f;

    /// <summary>屋根勾配（棟の高さ / 軒から棟までの水平距離）。</summary>
    private const float PitchRatio = 0.5f;

    /// <summary>棟の高さの下限・上限（m）。</summary>
    private const float MinRise = 0.6f;
    private const float MaxRise = 5.0f;

    /// <summary>UV のワールド座標スケール（1m あたりの UV 量）。</summary>
    private const float UvScale = 0.2f;

    /// <summary>
    /// 切妻屋根のメッシュを生成する。
    /// マテリアルが取得できない場合でも、最終的なメッシュ結合段階でデフォルトマテリアルが
    /// 割り当てられるよう、メッシュ自体は破棄せずに結果を返す。
    /// </summary>
    /// <param name="footprint">建物底面の頂点</param>
    /// <param name="baseHeight">屋根底面（軒）の高さ = 躯体の描画高さ</param>
    /// <param name="prefix">モデルのプレフィックス（ceil パーツ解決用）</param>
    /// <param name="resolvedSuffix">解決済みのサフィックス（ceil パーツ解決用）</param>
    /// <param name="fallbackMaterials">ceil パーツが無い場合のフォールバックマテリアル</param>
    public static BuildingMeshResult GenerateRoof(
        Vector2[] footprint, float baseHeight, string prefix, int resolvedSuffix, Material[] fallbackMaterials)
    {
        if (footprint == null || footprint.Length < 3)
        {
            Debug.LogError("GableRoofBuilder.GenerateRoof: footprint is null or less than 3");
            return null;
        }

        // 耳切り法とオフセット方向（外向き法線）は CCW を前提とするため正規化する
        Vector2[] normalized = (Vector2[])footprint.Clone();
        if (BuildingMeshUtils.SignedArea(normalized) < 0f)
        {
            System.Array.Reverse(normalized);
        }

        // 軒の出だけ外側に広げたポリゴンを屋根の底面（軒線）とする
        Vector2[] eaveFootprint = OffsetPolygonOutward(normalized, EaveOverhang);
        if (eaveFootprint == null || eaveFootprint.Length < 3)
        {
            eaveFootprint = normalized;
        }

        // 最小面積の外接矩形から棟線方向（長辺）と半幅（短辺の半分）を求める
        if (!TryResolveRidgeFrame(eaveFootprint, out Vector2 center, out Vector2 ridgeDir,
                out Vector2 widthDir, out float halfLength, out float halfWidth))
        {
            // 方向を決められない退化ポリゴンは平屋根にフォールバックする
            Debug.LogWarning("GableRoofBuilder.GenerateRoof: 棟線方向を決定できないため平屋根にフォールバックします。");
            return FlatRoofBuilder.GenerateRoof(footprint, baseHeight, prefix, resolvedSuffix, fallbackMaterials);
        }

        float rise = Mathf.Clamp(halfWidth * PitchRatio, MinRise, MaxRise);
        float ridgeHeight = baseHeight + rise;

        // 棟線は妻側の端まで伸ばす。これにより妻側の面が垂直な三角形（妻壁）になる。
        Vector2 ridgeStart = center - ridgeDir * halfLength;
        Vector2 ridgeEnd = center + ridgeDir * halfLength;

        int count = eaveFootprint.Length;
        var vertices = new List<Vector3>(count * 6);
        var triangles = new List<int>(count * 6);
        var normals = new List<Vector3>(count * 6);
        var uvs = new List<Vector2>(count * 6);

        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;

            Vector2 eaveCurr2D = eaveFootprint[i];
            Vector2 eaveNext2D = eaveFootprint[next];
            Vector2 ridgeCurr2D = ProjectOnSegment(eaveCurr2D, ridgeStart, ridgeEnd);
            Vector2 ridgeNext2D = ProjectOnSegment(eaveNext2D, ridgeStart, ridgeEnd);

            Vector3 eaveCurr = new Vector3(eaveCurr2D.x, baseHeight, eaveCurr2D.y);
            Vector3 eaveNext = new Vector3(eaveNext2D.x, baseHeight, eaveNext2D.y);
            Vector3 ridgeCurr = new Vector3(ridgeCurr2D.x, ridgeHeight, ridgeCurr2D.y);
            Vector3 ridgeNext = new Vector3(ridgeNext2D.x, ridgeHeight, ridgeNext2D.y);

            // 軒線と棟線をつなぐ四辺形。妻側では棟側が 1 点に退化し、垂直な妻壁の三角形になる。
            AddFace(eaveCurr, ridgeCurr, ridgeNext, center, ridgeDir, widthDir, baseHeight,
                vertices, triangles, normals, uvs);
            AddFace(eaveCurr, ridgeNext, eaveNext, center, ridgeDir, widthDir, baseHeight,
                vertices, triangles, normals, uvs);
        }

        // 軒下から屋根裏が透けて見えないよう、軒の高さに下向きの天井面を張る
        AddDownwardCeiling(eaveFootprint, baseHeight, center, ridgeDir, widthDir,
            vertices, triangles, normals, uvs);

        Mesh roofMesh = new Mesh();
        roofMesh.vertices = vertices.ToArray();
        roofMesh.triangles = triangles.ToArray();
        roofMesh.normals = normals.ToArray();
        roofMesh.uv = uvs.ToArray();
        roofMesh.RecalculateBounds();

        Material roofMaterial = BuildingMeshUtils.SelectRoofMaterial(prefix, resolvedSuffix, fallbackMaterials);
        return new BuildingMeshResult(roofMesh, roofMaterial != null ? new[] { roofMaterial } : null);
    }

    /// <summary>
    /// 三角形を 1 枚追加する。法線は面法線、UV は棟線方向（U）と勾配方向の距離（V）で与える。
    /// 面積がほぼ 0 の退化三角形は追加しない。
    /// </summary>
    private static void AddFace(Vector3 a, Vector3 b, Vector3 c,
        Vector2 center, Vector2 ridgeDir, Vector2 widthDir, float baseHeight,
        List<Vector3> vertices, List<int> triangles, List<Vector3> normals, List<Vector2> uvs)
    {
        Vector3 faceNormal = Vector3.Cross(b - a, c - a);
        if (faceNormal.sqrMagnitude < 1e-8f)
        {
            return;
        }
        faceNormal.Normalize();

        int startIndex = vertices.Count;
        AddVertex(a, faceNormal, center, ridgeDir, widthDir, baseHeight, vertices, normals, uvs);
        AddVertex(b, faceNormal, center, ridgeDir, widthDir, baseHeight, vertices, normals, uvs);
        AddVertex(c, faceNormal, center, ridgeDir, widthDir, baseHeight, vertices, normals, uvs);

        triangles.Add(startIndex);
        triangles.Add(startIndex + 1);
        triangles.Add(startIndex + 2);
    }

    private static void AddVertex(Vector3 position, Vector3 faceNormal,
        Vector2 center, Vector2 ridgeDir, Vector2 widthDir, float baseHeight,
        List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs)
    {
        vertices.Add(position);
        normals.Add(faceNormal);

        Vector2 planar = new Vector2(position.x, position.z) - center;
        float alongRidge = Vector2.Dot(planar, ridgeDir);
        float acrossRidge = Vector2.Dot(planar, widthDir);
        // 棟から軒へ下る斜面に沿った距離。妻壁でも連続するよう高さ差を含めて測る。
        float slopeDistance = Mathf.Sqrt(acrossRidge * acrossRidge +
            (position.y - baseHeight) * (position.y - baseHeight));
        uvs.Add(new Vector2(alongRidge * UvScale, slopeDistance * UvScale));
    }

    /// <summary>
    /// 軒の高さに下向きの天井面（耳切り法で三角形分割）を張る。
    /// CCW ポリゴンの分割順をそのまま使うと下向きの面になる。
    /// </summary>
    private static void AddDownwardCeiling(Vector2[] polygon, float height,
        Vector2 center, Vector2 ridgeDir, Vector2 widthDir,
        List<Vector3> vertices, List<int> triangles, List<Vector3> normals, List<Vector2> uvs)
    {
        List<int> indices = BuildingMeshBuilder.TriangulateOrFan(polygon);
        if (indices == null || indices.Count < 3)
        {
            return;
        }

        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            Vector3 p0 = new Vector3(polygon[indices[i]].x, height, polygon[indices[i]].y);
            Vector3 p1 = new Vector3(polygon[indices[i + 1]].x, height, polygon[indices[i + 1]].y);
            Vector3 p2 = new Vector3(polygon[indices[i + 2]].x, height, polygon[indices[i + 2]].y);

            AddFace(p0, p1, p2, center, ridgeDir, widthDir, height, vertices, triangles, normals, uvs);
        }
    }

    /// <summary>
    /// 点をセグメント上に射影する（セグメント外は端点にクランプ）。
    /// </summary>
    private static Vector2 ProjectOnSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSq = ab.sqrMagnitude;
        if (lengthSq < 1e-8f)
        {
            return a;
        }
        float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSq);
        return a + ab * t;
    }

    /// <summary>
    /// ポリゴンの最小面積外接矩形（回転キャリパー）を求め、長辺方向を棟線方向として返す。
    /// </summary>
    /// <param name="center">外接矩形の中心</param>
    /// <param name="ridgeDir">棟線方向（長辺方向、正規化済み）</param>
    /// <param name="widthDir">棟線と直交する方向（短辺方向、正規化済み）</param>
    /// <param name="halfLength">棟線方向の半長</param>
    /// <param name="halfWidth">棟線と直交する方向の半幅</param>
    private static bool TryResolveRidgeFrame(Vector2[] polygon,
        out Vector2 center, out Vector2 ridgeDir, out Vector2 widthDir,
        out float halfLength, out float halfWidth)
    {
        center = Vector2.zero;
        ridgeDir = Vector2.right;
        widthDir = Vector2.up;
        halfLength = 0f;
        halfWidth = 0f;

        int n = polygon.Length;
        float bestArea = float.MaxValue;
        bool found = false;

        for (int i = 0; i < n; i++)
        {
            Vector2 edge = polygon[(i + 1) % n] - polygon[i];
            if (edge.sqrMagnitude < 1e-6f)
            {
                continue;
            }

            Vector2 axisX = edge.normalized;
            Vector2 axisY = new Vector2(-axisX.y, axisX.x);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int j = 0; j < n; j++)
            {
                float x = Vector2.Dot(polygon[j], axisX);
                float y = Vector2.Dot(polygon[j], axisY);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            float sizeX = maxX - minX;
            float sizeY = maxY - minY;
            if (sizeX < 1e-4f || sizeY < 1e-4f)
            {
                continue;
            }

            float area = sizeX * sizeY;
            if (area >= bestArea)
            {
                continue;
            }

            bestArea = area;
            found = true;
            center = axisX * ((minX + maxX) * 0.5f) + axisY * ((minY + maxY) * 0.5f);

            // 長辺を棟線方向にする
            if (sizeX >= sizeY)
            {
                ridgeDir = axisX;
                widthDir = axisY;
                halfLength = sizeX * 0.5f;
                halfWidth = sizeY * 0.5f;
            }
            else
            {
                ridgeDir = axisY;
                widthDir = axisX;
                halfLength = sizeY * 0.5f;
                halfWidth = sizeX * 0.5f;
            }
        }

        return found && halfWidth > 1e-3f && halfLength > 1e-3f;
    }

    /// <summary>
    /// ポリゴン（CCW）を外側に一定距離オフセットする（各頂点の角の二等分方向へ移動）。
    /// </summary>
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

            Vector2 prevDir = curr - prev;
            Vector2 nextDir = next - curr;
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
}
