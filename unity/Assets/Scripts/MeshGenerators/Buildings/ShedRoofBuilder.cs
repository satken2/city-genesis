// Assets/Scripts/MeshGenerators/Buildings/ShedRoofBuilder.cs
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 片流れ屋根（トタン屋根の粗末な小屋）のメッシュを生成するクラス。
/// 壁・角・地面の生成は MODEL_MODULAR_FLAT と同じで、屋根だけをこのクラスで差し替える。
///
/// 屋根は棟を持たない**厚みの薄い1枚の板**で、建物の短辺方向へ一方向だけ傾ける。
/// 板の裏面は「低い側の壁の位置」でちょうど躯体の天端を通り、「高い側の壁の位置」で
/// 立ち上がりぶん高くなる。軒の出のぶんだけ板は壁の外側へ張り出し、低い側では壁より
/// 下へ、高い側では上へ抜ける。
///
/// 板が塞がない壁の天端と裏面の隙間（低い側で高さ 0、高い側で立ち上がりぶんの矩形、
/// 側面で三角形）は、切妻屋根と同じく無地の壁（`generic-wall` パーツのマテリアル）の
/// 板で塞ぐ。
///
/// マテリアルは屋根面が平屋根（MODEL_MODULAR_FLAT）と同じ `ceil` パーツ、妻壁が
/// `generic-wall` パーツで、サブメッシュを分けて返す。
/// </summary>
public static class ShedRoofBuilder
{
    /// <summary>軒の出（m）。壁面から屋根板が張り出す量。</summary>
    private const float EaveOverhang = 0.3f;

    /// <summary>屋根板の厚み（m、垂直方向）。薄板の波トタンを想定して切妻屋根より薄くする。</summary>
    private const float RoofThickness = 0.08f;

    /// <summary>
    /// 高い側の立ち上がり（低い側の壁の天端から高い側の壁の天端までの高さ）を決める、
    /// 階高に対する比率。切妻屋根と同じく建物の間口ではなく階高に固定することで、
    /// 大きな建物でも屋根だけが巨大にならない。片流れは勾配が建物の全幅にかかるため
    /// 切妻（棟の高さ = 階高の半分）より緩めにとる。
    /// </summary>
    private const float RiseFloorHeightRatio = 0.35f;

    /// <summary>UV のワールド座標スケール（1m あたりの UV 量）。</summary>
    private const float UvScale = 0.2f;

    /// <summary>これ未満の高さしかない妻壁は張らない（m）。</summary>
    private const float MinGableWallHeight = 1e-3f;

    /// <summary>
    /// 屋根面の高さを決める幾何フレーム。屋根板の裏面の高さは傾斜方向の座標だけで決まるため、
    /// 板は全体で 1 枚の完全な平面になる。
    /// </summary>
    private readonly struct RoofFrame
    {
        public readonly Vector2 Center;
        /// <summary>軒先・けらばと平行な方向（外接矩形の長辺方向）。UV の U 軸。</summary>
        public readonly Vector2 EaveDir;
        /// <summary>低い側から高い側へ向かう傾斜方向（外接矩形の短辺方向）。</summary>
        public readonly Vector2 SlopeDir;
        /// <summary>傾斜方向の半幅。中心から低い側の壁までの距離でもある。</summary>
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

        /// <summary>低い側の壁を 0 とした、傾斜方向の距離。</summary>
        public float Along(Vector2 point) => Vector2.Dot(point - Center, SlopeDir) + HalfWidth;

        /// <summary>屋根板の裏面の高さ。</summary>
        public float SurfaceY(Vector2 point) => BaseHeight + Along(point) * Slope;

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
    /// 片流れ屋根のメッシュを生成する。
    /// マテリアルが取得できない場合でも、最終的なメッシュ結合段階でデフォルトマテリアルが
    /// 割り当てられるよう、メッシュ自体は破棄せずに結果を返す。
    /// </summary>
    /// <param name="footprint">建物底面の頂点</param>
    /// <param name="baseHeight">壁の天端の高さ = 躯体の描画高さ</param>
    /// <param name="floorHeight">1階あたりの高さ。高い側の立ち上がりはこれに比例させる。</param>
    /// <param name="prefix">モデルのプレフィックス（ceil / generic-wall パーツ解決用）</param>
    /// <param name="resolvedSuffix">解決済みのサフィックス（ceil / generic-wall パーツ解決用）</param>
    /// <param name="fallbackMaterials">パーツが無い場合のフォールバックマテリアル</param>
    public static BuildingMeshResult GenerateRoof(
        Vector2[] footprint, float baseHeight, float floorHeight,
        string prefix, int resolvedSuffix, Material[] fallbackMaterials)
    {
        if (footprint == null || footprint.Length < 3)
        {
            Debug.LogError("ShedRoofBuilder.GenerateRoof: footprint is null or less than 3");
            return null;
        }

        // 耳切り法とオフセット方向（外向き法線）は CCW を前提とするため正規化する
        Vector2[] normalized = (Vector2[])footprint.Clone();
        if (PolygonUtils.SignedArea(normalized) < 0f)
        {
            System.Array.Reverse(normalized);
        }

        // 傾斜方向と半幅は「壁の外周」から決める。軒の出を含めた輪郭から決めると、
        // 屋根面が低い側で壁の天端を通らなくなり妻壁の高さが合わなくなる。
        if (!BuildingMeshUtils.TryResolveMinAreaRect(normalized, out Vector2 center, out Vector2 eaveDir,
                out Vector2 shortDir, out _, out float halfWidth))
        {
            // 方向を決められない退化ポリゴンは平屋根にフォールバックする
            Debug.LogWarning("ShedRoofBuilder.GenerateRoof: 傾斜方向を決定できないため平屋根にフォールバックします。");
            return FlatRoofBuilder.GenerateRoof(footprint, baseHeight, prefix, resolvedSuffix, fallbackMaterials);
        }

        // 前面（頂点0→頂点1 の辺。塔屋・エントランスと同じ規約）側が低くなるように傾ける。
        // 軒先が接道側へ向き、街路からは低い側の軒を見上げる形になる。
        Vector2 slopeDir = ResolveUphillDirection(normalized, shortDir);

        // 立ち上がりは建物高さではなく階高で決める。勾配は建物の間口（全幅）ごとに変わり、
        // 間口が広い建物ほど緩勾配になる。
        float rise = (floorHeight > 0f ? floorHeight : LocalConstants.FLOOR_HEIGHT) * RiseFloorHeightRatio;
        // 屋根面が低い側の壁の位置でちょうど天端を通るよう、勾配は立ち上がりと全幅から逆算する。
        float slope = rise / (halfWidth * 2f);
        var frame = new RoofFrame(center, eaveDir, slopeDir, halfWidth, slope, baseHeight);

        // 軒の出だけ外側に広げたポリゴンが屋根板の輪郭になる。
        Vector2[] eaveFootprint = BuildingMeshUtils.OffsetPolygonOutward(normalized, EaveOverhang);
        if (eaveFootprint == null || eaveFootprint.Length < 3)
        {
            eaveFootprint = normalized;
        }

        var buffers = new RoofBuffers();

        // 屋根板は棟を持たないので、輪郭全体を 1 枚の平らな板として張る
        AddRoofBoard(eaveFootprint, frame, buffers);

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
    /// 傾斜の向き（低い側 → 高い側）を決める。前面の辺（頂点0→頂点1）が向く側が低くなるよう、
    /// 前面の外向き法線と逆側を高い側にする。前面の辺が傾斜方向とほぼ直交していて
    /// どちらとも言えない場合は短辺方向をそのまま使う。
    /// </summary>
    private static Vector2 ResolveUphillDirection(Vector2[] wallPolygon, Vector2 shortDir)
    {
        Vector2 frontEdge = wallPolygon[1] - wallPolygon[0];
        if (frontEdge.sqrMagnitude < 1e-6f)
        {
            return shortDir;
        }

        // CCW ポリゴンの外向き法線
        Vector2 frontNormal = frontEdge.normalized;
        Vector2 frontOutward = new Vector2(frontNormal.y, -frontNormal.x);

        return Vector2.Dot(frontOutward, shortDir) > 0f ? -shortDir : shortDir;
    }

    /// <summary>
    /// 屋根板（厚みのある1枚の板）を張る。裏面・表面・外周の小口の3種で構成する。
    /// 棟が無いので、切妻屋根と違って小口を省く辺は無い。
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

            float currY = frame.SurfaceY(curr);
            float nextY = frame.SurfaceY(next);
            AddVerticalBand(curr, next, currY, nextY, currY + RoofThickness, nextY + RoofThickness,
                frame, buffers, buffers.RoofTriangles);
        }
    }

    /// <summary>
    /// 壁の天端と屋根板の裏面の間にできる隙間を、垂直な板（妻壁）で塞ぐ。
    /// 低い側では屋根面が壁の位置でちょうど天端を通るため高さ 0 に潰れ、高い側では
    /// 立ち上がりぶんの矩形、側面では低い側から高い側へ上がる三角形になる。
    /// 屋根面は 1 枚の平面で折れないため、切妻屋根と違って辺を分割する必要は無い。
    /// </summary>
    private static void AddGableWalls(Vector2[] wallPolygon, in RoofFrame frame, RoofBuffers buffers)
    {
        int count = wallPolygon.Length;
        for (int i = 0; i < count; i++)
        {
            Vector2 curr = wallPolygon[i];
            Vector2 next = wallPolygon[(i + 1) % count];

            // 屋根面は壁の内側で必ず天端以上になるが、数値誤差で下回っても穴を作らないようクランプする
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
    /// 三角形を 1 枚追加する。法線は面法線、UV は軒先方向（U）と勾配に沿った距離（V）で与える。
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

        Vector2 planar = new Vector2(position.x, position.z);
        float alongEave = Vector2.Dot(planar - frame.Center, frame.EaveDir);
        // 低い側の軒先から高い側へ上る斜面に沿った距離。妻壁でも連続するよう高さ差を含めて測る。
        float acrossSlope = frame.Along(planar);
        float slopeDistance = Mathf.Sqrt(acrossSlope * acrossSlope +
            (position.y - frame.BaseHeight) * (position.y - frame.BaseHeight));
        buffers.Uvs.Add(new Vector2(alongEave * UvScale, slopeDistance * UvScale));
    }
}
