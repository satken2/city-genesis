// Assets/Scripts/MeshGenerators/Buildings/GableRoofBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 切妻屋根（ゲーブルルーフ）のメッシュを生成するクラス。
/// 壁・角・地面の生成は MODEL_MODULAR_FLAT と同じで、屋根だけをこのクラスで差し替える。
///
/// 屋根は棟線で分けた**厚みのある2枚の板**として作る。板の勾配は、建物外周（壁の位置）で
/// 板の裏面が躯体の天端をちょうど通るように決めるため、軒の出のぶんだけ板は壁の外側かつ
/// 下側へ張り出し、その下が軒下になる。
/// 板は棟をまたぐだけで妻側を塞がないので、妻側では壁の天端と板の裏面の間に三角形の隙間が
/// 空く。ここは窓のない無地の壁（`generic-wall` パーツのマテリアル）の板で塞ぐ（妻壁）。
///
/// マテリアルは屋根面が平屋根（MODEL_MODULAR_FLAT）と同じ `ceil` パーツ、妻壁が
/// `generic-wall` パーツで、サブメッシュを分けて返す。
/// </summary>
public static class GableRoofBuilder
{
    /// <summary>軒の出（m）。壁面から屋根板が張り出す量で、この下が軒下になる。</summary>
    private const float EaveOverhang = 0.4f;

    /// <summary>屋根板の厚み（m、垂直方向）。軒先・けらばの小口の高さになる。</summary>
    private const float RoofThickness = 0.25f;

    /// <summary>
    /// 棟の立ち上がり（壁の天端から棟までの高さ）を決める、階高に対する比率。
    /// 建物の間口ではなく階高に固定することで、大きな建物でも屋根だけが巨大にならず、
    /// 同じ街並みの中で屋根の見た目の高さが揃う。
    /// </summary>
    private const float RiseFloorHeightRatio = 0.5f;

    /// <summary>UV のワールド座標スケール（1m あたりの UV 量）。</summary>
    private const float UvScale = 0.2f;

    /// <summary>棟線上にあるとみなす、棟線からの距離の閾値（m）。</summary>
    private const float RidgeEpsilon = 1e-4f;

    /// <summary>これ未満の高さしかない妻壁は張らない（m）。</summary>
    private const float MinGableWallHeight = 1e-3f;

    /// <summary>
    /// 屋根面の高さを決める幾何フレーム。屋根板の裏面の高さは棟線からの水平距離だけで決まるため、
    /// 板は棟線の左右それぞれで完全な平面になる。
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

        /// <summary>棟線からの符号付き距離。正負が棟線のどちら側かを表す。</summary>
        public float Across(Vector2 point) => Vector2.Dot(point - Center, WidthDir);

        /// <summary>屋根板の裏面の高さ。</summary>
        public float SurfaceY(Vector2 point) => RidgeY - Mathf.Abs(Across(point)) * Slope;

        /// <summary>屋根板の裏面（offsetY=0）または表面（offsetY=厚み）上の点。</summary>
        public Vector3 SurfacePoint(Vector2 point, float offsetY)
            => new Vector3(point.x, SurfaceY(point) + offsetY, point.y);
    }

    /// <summary>
    /// 面を貯めるバッファ。屋根面と妻壁はマテリアルが異なるためサブメッシュを分ける。
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
    /// 切妻屋根のメッシュを生成する。
    /// マテリアルが取得できない場合でも、最終的なメッシュ結合段階でデフォルトマテリアルが
    /// 割り当てられるよう、メッシュ自体は破棄せずに結果を返す。
    /// </summary>
    /// <param name="footprint">建物底面の頂点</param>
    /// <param name="baseHeight">壁の天端の高さ = 躯体の描画高さ</param>
    /// <param name="floorHeight">1階あたりの高さ。棟の立ち上がりはこの半分に固定する。</param>
    /// <param name="prefix">モデルのプレフィックス（ceil / generic-wall パーツ解決用）</param>
    /// <param name="resolvedSuffix">解決済みのサフィックス（ceil / generic-wall パーツ解決用）</param>
    /// <param name="fallbackMaterials">パーツが無い場合のフォールバックマテリアル</param>
    public static BuildingMeshResult GenerateRoof(
        Vector2[] footprint, float baseHeight, float floorHeight,
        string prefix, int resolvedSuffix, Material[] fallbackMaterials)
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

        // 棟線の向きと半幅は「壁の外周」から決める。軒の出を含めた輪郭から決めると、
        // 屋根面が壁の天端を通らなくなり妻壁の高さが合わなくなる。
        if (!TryResolveRidgeFrame(normalized, out Vector2 center, out Vector2 ridgeDir,
                out Vector2 widthDir, out _, out float halfWidth))
        {
            // 方向を決められない退化ポリゴンは平屋根にフォールバックする
            Debug.LogWarning("GableRoofBuilder.GenerateRoof: 棟線方向を決定できないため平屋根にフォールバックします。");
            return FlatRoofBuilder.GenerateRoof(footprint, baseHeight, prefix, resolvedSuffix, fallbackMaterials);
        }

        // 棟の立ち上がりは階高の半分に固定する。勾配は建物の間口（半幅）ごとに変わり、
        // 間口が広い建物ほど緩勾配になる。
        float rise = (floorHeight > 0f ? floorHeight : LocalConstants.FLOOR_HEIGHT) * RiseFloorHeightRatio;
        // 屋根面が壁の位置でちょうど天端を通るよう、勾配は棟高と半幅から逆算する。
        float slope = rise / halfWidth;
        var frame = new RoofFrame(center, ridgeDir, widthDir, baseHeight + rise, slope, baseHeight);

        // 軒の出だけ外側に広げたポリゴンが屋根板の輪郭になる。
        Vector2[] eaveFootprint = OffsetPolygonOutward(normalized, EaveOverhang);
        if (eaveFootprint == null || eaveFootprint.Length < 3)
        {
            eaveFootprint = normalized;
        }

        var buffers = new RoofBuffers();

        // 屋根板は棟線で左右に割り、それぞれ1枚の平らな板として張る
        for (int side = -1; side <= 1; side += 2)
        {
            Vector2[] half = ClipToRidgeSide(eaveFootprint, frame, side);
            if (half != null)
            {
                AddRoofBoard(half, frame, buffers);
            }
        }

        // 壁の天端と屋根板の裏面の隙間（妻壁）を塞ぐ
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
    /// 棟線の片側の屋根板（厚みのある1枚の板）を張る。
    /// 裏面・表面・小口の3種で構成する。棟線上の辺だけは反対側の板と接するので小口を張らない。
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

            // 裏面（軒下から見える面）。CCW の分割順をそのまま使うと下向きの面になる。
            AddFace(frame.SurfacePoint(p0, 0f), frame.SurfacePoint(p1, 0f), frame.SurfacePoint(p2, 0f),
                frame, buffers, buffers.RoofTriangles);
            // 表面。巻き順を反転して上向きにする。
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
    /// 壁の天端と屋根板の裏面の間にできる隙間を、垂直な板（妻壁）で塞ぐ。
    /// 平側では屋根面が壁の位置でちょうど天端を通るため高さ 0 に潰れ、妻側では棟までの三角形になる。
    /// 棟線をまたぐ辺は屋根面が折れる位置で2本に分ける。
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
        // 屋根面は壁より内側で必ず天端以上になるが、数値誤差で下回っても穴を作らないようクランプする
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
    /// 2点間に垂直な帯を張る。CCW ポリゴンでは「下端 → 上端 → 次の上端」の順で外向きの面になる。
    /// 高さが 0 に潰れる側は退化三角形として AddFace が捨てるため、台形・三角形のどちらにもなる。
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
    /// ポリゴンを棟線で切り、指定した側（sign）だけを残す（Sutherland–Hodgman）。
    /// CCW の並び順は保たれ、切り口は棟線上の辺として残る。
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
    /// 三角形を 1 枚追加する。法線は面法線、UV は棟線方向（U）と勾配方向の距離（V）で与える。
    /// 面積がほぼ 0 の退化三角形は追加しない。
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
        // 棟から軒へ下る斜面に沿った距離。妻壁でも連続するよう高さ差を含めて測る。
        float slopeDistance = Mathf.Sqrt(acrossRidge * acrossRidge +
            (position.y - frame.BaseHeight) * (position.y - frame.BaseHeight));
        buffers.Uvs.Add(new Vector2(alongRidge * UvScale, slopeDistance * UvScale));
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
