using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 工事中(STATUS_CONSTRUCTING)のパスを覆う「土ストリップ」メッシュの生成ユーティリティ。
/// PathManager.GenerateConstructingPathMesh から呼ばれる。
/// 建物の土マウンド(ConstructingBuildingMeshBuilder)と共通の土ノイズ(DirtNoise)もここに置く。
///
/// 形状: 点列に沿った帯で、断面は「左右端2頂点（地表に接地）+ 中央寄り2頂点（厚み +
/// 土ノイズの凹凸で盛り上げ）」の台形マウンド。左右端が接地するため側面は不要で、
/// 地形との隙間・Z-fightingも起きにくい。両端は端面キャップで閉じる。
/// ランダム性はジオメトリ密度ではなくノイズで出す:
/// - 高さには多オクターブのPerlinノイズ(fbm)を乗せる
/// - 土の縁(幅)も低周波ノイズで左右独立に揺らし、直線的な輪郭を崩す
/// - UVはワールドXZ座標ベースで、マテリアル側の模様が経路の伸縮で歪まない
///
/// 高さは入力点のYを使わず、各点のXZ位置の地形標高 + ROAD_SPLINE_OFFSET を使う。
/// 高架のパスでも「土は地表に盛られ、柱だけが立っている」工事現場の見た目にするため
/// （柱の描画は RoadPath.GeneratePillars が担当し、このユーティリティの責務外）。
/// </summary>
public static class ConstructionMeshUtil
{
    /// <summary>Perlinノイズ入力の負座標対策オフセット。負側では模様が座標に対して対称になるため避ける。</summary>
    private const float NOISE_INPUT_OFFSET = 10000f;

    /// <summary>土の縁の幅を揺らす低周波ノイズのワールド周波数(1/m)。</summary>
    private const float WIDTH_JITTER_NOISE_SCALE = 0.05f;

    /// <summary>幅の揺らぎの振幅(半幅に対する割合)。0.15なら半幅が±15%揺れる。</summary>
    private const float WIDTH_JITTER_RATIO = 0.15f;

    /// <summary>
    /// 土の高さノイズ(0〜1)。3オクターブのfbmで、大きなうねり(波長 約6〜7m)に
    /// 中規模の起伏(約2.5m)と細かい土塊(約1m)を重ねる。
    /// 入力はワールド座標なので、再生成しても模様は安定する。
    /// 建物の土マウンド(ConstructingBuildingMeshBuilder)と土ストリップの両方から使う。
    /// </summary>
    public static float DirtNoise(float worldX, float worldZ)
    {
        float x = (worldX + NOISE_INPUT_OFFSET) * LocalConstants.CONSTRUCTION_DIRT_NOISE_SCALE;
        float z = (worldZ + NOISE_INPUT_OFFSET) * LocalConstants.CONSTRUCTION_DIRT_NOISE_SCALE;
        // 各オクターブの位相をずらし、同心状のアーティファクトを避ける
        return Mathf.PerlinNoise(x, z) * 0.55f
             + Mathf.PerlinNoise(x * 2.7f + 31.4f, z * 2.7f + 47.2f) * 0.30f
             + Mathf.PerlinNoise(x * 6.1f + 113.9f, z * 6.1f + 71.3f) * 0.15f;
    }

    /// <summary>
    /// 点列に沿った土ストリップメッシュを生成する（ワールド座標）。
    /// </summary>
    /// <param name="points">パスの点列（PathManager.CalculatePathPoints の出力、または描画区間分割後のrun）。Yは無視され地形標高に置き換えられる</param>
    /// <param name="width">土の基準全幅(m)。通常は「マスターの道路幅 + CONSTRUCTION_PATH_DIRT_EXTRA_WIDTH」。実際の幅は縁ノイズで±揺れる</param>
    public static Mesh BuildDirtStripMesh(List<Vector3> points, float width)
    {
        if (points == null || points.Count < 2 || width <= 0f)
        {
            return null;
        }

        float baseHalfWidth = width * 0.5f;
        // 中央寄り頂点（盛り上げの頂部）の中心からの距離。全幅の1/4に置くと自然な台形になる
        float crestRatio = 0.5f;
        float thickness = LocalConstants.CONSTRUCTION_DIRT_THICKNESS;
        float bumpAmp = LocalConstants.CONSTRUCTION_DIRT_BUMP_AMPLITUDE;

        int pointCount = points.Count;

        // 各点の地表Y（地形標高 + 道路スプラインと同じ浮かせ量）
        float[] groundY = new float[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            groundY[i] = HeightmapTextureManager.GetTerrainHeight(new Vector3(points[i].x, 1000f, points[i].z))
                + RoadPartHeights.ROAD_SPLINE_OFFSET;
        }

        var vertices = new List<Vector3>(pointCount * 4);
        var uvs = new List<Vector2>(pointCount * 4);
        var triangles = new List<int>((pointCount - 1) * 18 + 12);

        // 断面: [0]=左端(接地) [1]=左クレスト(盛り上げ) [2]=右クレスト [3]=右端(接地)
        for (int i = 0; i < pointCount; i++)
        {
            Vector3 direction = ComputeSmoothedDirection(points, i);
            Vector3 binormal = Vector3.Cross(Vector3.up, direction);
            if (binormal.sqrMagnitude < 1e-6f)
            {
                binormal = Vector3.right;
            }
            binormal.Normalize();

            Vector3 basePoint = new Vector3(points[i].x, groundY[i], points[i].z);

            // 土の縁の揺らぎ: 低周波ノイズで半幅を±WIDTH_JITTER_RATIO 変化させ、直線的な輪郭を崩す。
            // 左右で別の位相を使い、幅だけでなく中心線に対する非対称も出す
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

            // 多オクターブの土ノイズ（建物の土マウンドと共通）
            float crestLeftY = thickness + DirtNoise(leftCrestPos.x, leftCrestPos.z) * bumpAmp;
            float crestRightY = thickness + DirtNoise(rightCrestPos.x, rightCrestPos.z) * bumpAmp;

            vertices.Add(leftEdge);
            vertices.Add(leftCrestPos + Vector3.up * crestLeftY);
            vertices.Add(rightCrestPos + Vector3.up * crestRightY);
            vertices.Add(rightEdge);

            // UVはワールドXZ座標ベース。マテリアル側の模様が経路の伸縮で歪まず、隣接区間とも連続する
            uvs.Add(new Vector2(leftEdge.x * 0.1f, leftEdge.z * 0.1f));
            uvs.Add(new Vector2(leftCrestPos.x * 0.1f, leftCrestPos.z * 0.1f));
            uvs.Add(new Vector2(rightCrestPos.x * 0.1f, rightCrestPos.z * 0.1f));
            uvs.Add(new Vector2(rightEdge.x * 0.1f, rightEdge.z * 0.1f));
        }

        // 断面間を3本の帯（左斜面・天面・右斜面）で繋ぐ。
        // 断面頂点は左(-binormal)→右(+binormal)の昇順なので、上向きの面になる巻き順は
        // (今の左, 次の左, 今の右) となる（進行方向×右方向の外積が上を向く並び）。
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

        // 端面キャップ（交差点の切断面で土の断面が見えるよう閉じる）
        AddEndCap(triangles, 0, flip: true);
        AddEndCap(triangles, (pointCount - 1) * 4, flip: false);

        Mesh mesh = new Mesh();
        if (vertices.Count > 65000)
        {
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        }
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// 台形断面（4頂点）の端面を2枚の三角形で閉じる。
    /// flip=true は始端（進行方向の逆を向く面）、false は終端。
    /// </summary>
    private static void AddEndCap(List<int> triangles, int baseIndex, bool flip)
    {
        // 断面頂点 [0]左端 [1]左クレスト [2]右クレスト [3]右端 を扇状に分割
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

    /// <summary>
    /// 前後の点とのベクトルを平均したスムーズな進行方向（PathMeshFactory.BuildStripMesh と同じ考え方）。
    /// </summary>
    private static Vector3 ComputeSmoothedDirection(List<Vector3> points, int index)
    {
        Vector3 direction = Vector3.zero;
        if (index > 0)
        {
            direction += (points[index] - points[index - 1]).normalized;
        }
        if (index < points.Count - 1)
        {
            direction += (points[index + 1] - points[index]).normalized;
        }
        if (direction.sqrMagnitude < 1e-6f)
        {
            direction = Vector3.forward;
        }
        return direction.normalized;
    }
}
