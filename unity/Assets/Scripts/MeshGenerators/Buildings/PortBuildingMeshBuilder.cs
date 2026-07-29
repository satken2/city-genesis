using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;
using NetTopologySuite.Geometries;

/// <summary>
/// 海岸の岸壁（MODEL_PORT）のメッシュ生成器。
///
/// 海岸土地区画（PARCEL_SHORE）に立地する港湾（BUILDING_PORT）に適用する。
/// バックエンドは海岸区画のポリゴンを海岸線でクリップせず元の形状のまま保持する（B_土地形状 B.5）。
/// building_shape=SHAPE_FULL の底面ポリゴン（=土地区画の元形状。海側にはみ出している）の外周と
/// 海岸線の交点から岸壁の左端A・右端Bを求め、それぞれを海側へ突き出した2点を結ぶ**直線**で
/// 底面をカットした形状（海側境界は常に直線）を、厚みのある板（岸壁スラブ）として上下に押し出して生成する。
/// モジュラーの壁・角パーツは使わず、純粋にプロシージャルにスラブを作る。
///
/// visual_variant(1)/visual_variant2 … 岸壁の左端/右端の突き出し量（0=最小突き出し、1=区画の海側いっぱいまで）
/// 上面の高さは海面から2m固定（visual_variant3 は使わない）。
/// 上面は敷地全体の地面パネル(draw_floor)と同じ floor マテリアルで床続きに描画し、側面/下面は ceil を使う。
/// 水際（海側の直線エッジ）には荷役クレーン（port_crane）を等間隔に並べる。
///
/// 詳細仕様は genesis-docs/docs/geometries/F_建物メッシュの生成.md §18 を参照。
/// </summary>
public class PortBuildingMeshBuilder : IBuildingMeshGenerator
{
    /// <summary>岸壁上面の海面からの高さ(m)。visual_variant によらず固定。</summary>
    private const float QUAY_TOP_HEIGHT = 2f;

    /// <summary>突き出し量の下限（区画の海側奥行きに対する比率）。visual_variant=0 でも岸壁はこの割合だけ海へ張り出す。</summary>
    private const float MIN_PROTRUSION_RATIO = 0.2f;

    /// <summary>荷役クレーンの設置間隔（パーツ実寸幅に対する倍率）。1.0 だと隣同士が接するため余白を持たせる。</summary>
    private const float CRANE_SPACING_RATIO = 4.5f;

    /// <summary>1つの岸壁に並べる荷役クレーンの上限。巨大な岸壁でドローコールが膨らむのを防ぐ。</summary>
    private const int MAX_CRANE_COUNT = 8;

    /// <summary>海側エッジ上とみなす距離の許容差(m)。カット直線と岸壁フットプリントの辺の照合に使う。</summary>
    private const float SEAWARD_EDGE_EPSILON = 0.05f;

    /// <summary>
    /// 岸壁の海側境界（A'-B' の直線カットで生じたエッジ）を表す。
    /// 塔屋の向きと荷役クレーンの配置に使う。カットできなかった建物では <see cref="Valid"/> が false。
    /// 座標・方向はすべて center_point 基準のローカルXZ。
    /// </summary>
    private struct QuaySeawardEdge
    {
        public bool Valid;
        /// <summary>カット直線上の基準点（突き出し後の左端 A'）。</summary>
        public Vector2 Origin;
        /// <summary>カット直線の向き（A'→B'）。</summary>
        public Vector2 Dir;
        /// <summary>カット直線に垂直な、海を向く単位法線。</summary>
        public Vector2 Normal;
    }

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null || buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"[PortBMB] 底面ポリゴンが不正です (Building ID: {buildingData?.agent_id})。白い箱にフォールバックします。");
            return BuildingMeshBuilder.GenerateSimpleBuilding(null, 1, LocalConstants.FLOOR_HEIGHT);
        }

        // 底面（CCW、center_point 相対のローカル座標）
        Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
        string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
        float variant1 = Mathf.Clamp01(buildingData.visual_variant);
        float variant2 = Mathf.Clamp01(buildingData.visual_variant2);

        // center_point のワールド座標（バックエンド座標）と地形高さ。
        // ModularBuildingMeshBuilder と同じ取得経路（HeightmapTextureManager.GetTerrainHeight）を使い、
        // BuildingManager が GameObject を配置する高さと基準を揃える。
        Vector3 centerPointWorld = Vector3.zero;
        if (buildingData.centerPointArray != null && buildingData.centerPointArray.Length >= 2)
        {
            float cx = buildingData.centerPointArray[0];
            float cz = buildingData.centerPointArray[1];
            float cy = HeightmapTextureManager.GetTerrainHeight(new Vector3(cx, 1000f, cz));
            centerPointWorld = new Vector3(cx, cy, cz);
        }

        // 1. 海岸線との交点から左端A・右端Bを求め、visual_variant1/2 の突き出しを反映した
        //    直線カットで岸壁フットプリントを得る。
        Vector2[] quayFootprint = BuildQuayFootprint(footprint, centerPointWorld, variant1, variant2, out QuaySeawardEdge seaward);

        // 2. スラブ上面のローカルY。
        //    上面のワールドY = 海面(0m) + 2m 固定。GameObject は center_point 位置の地形高さに
        //    配置されるため、ローカルYは centerPointWorld.y を差し引く。
        float topLocalY = QUAY_TOP_HEIGHT - centerPointWorld.y;

        // 3. スラブメッシュ（上面=topLocalY、下面=各頂点の地形=海底高さ）。subMesh0=上面、subMesh1=側面+下面。
        Mesh slabMesh = BuildSlabMesh(quayFootprint, topLocalY, centerPointWorld);
        if (slabMesh == null)
        {
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // スラブのマテリアル: 上面=floor（敷地全体の地面パネルと同一マテリアルで床続きにする）、
        // 側面/下面=ceil（岸壁の壁面）。地面パネルが無効(draw_floor未設定等)の場合は floor パーツ直参照、
        // それも無ければデフォルトマテリアル。
        Material floorMaterial = BuildingMeshUtils.ResolveFloorMaterialFromMaster(buildingData, prefix);
        Material topMaterial = floorMaterial != null ? floorMaterial : BuildingMeshUtils.SelectFloorMaterial(prefix, 0, null);
        Material sideMaterial = BuildingMeshUtils.SelectRoofMaterial(prefix, 0, null);
        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[PortBMB] agent={buildingData.agent_id} prefix={prefix} " +
            $"topMat={(topMaterial != null ? topMaterial.name : "null")} sideMat={(sideMaterial != null ? sideMaterial.name : "null")} " +
            $"floorPanel={(floorMaterial != null ? "あり" : "なし")} topY={topLocalY:F2} vv=({variant1:F2},{variant2:F2})");
        AppendMesh(slabMesh, new[] { topMaterial, sideMaterial }, combineInstances, materials);
        temporaryMeshes.Add(slabMesh);

        // 4. 塔屋（岸壁中央・海側境界方向・上面高さ）。
        PlaceRoofprop(prefix, quayFootprint, seaward.Dir, topLocalY, combineInstances, materials, temporaryMeshes);

        // 5. 荷役クレーン（水際の直線エッジに沿って等間隔）。
        PlacePortCranes(prefix, quayFootprint, seaward, topLocalY, combineInstances, materials, temporaryMeshes);

        // 6. 敷地の装飾（フェンス・タイル・地面パネル）。モジュラー/prebuilt と共通の処理で、
        //    地面パネルは draw_floor=yes のとき敷地（土地区画）全体に地形追従で描かれ、
        //    岸壁上面と同一の floor マテリアルで床続きになる。
        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(
            buildingData, useParcelAltitude: false, out _, out _);
        Vector2[] sitePolygon = (fenceFootprint != null && fenceFootprint.Length >= 3) ? fenceFootprint : footprint;
        ModularBodyBuilder.GenerateSiteDecorations(
            prefix, 0,
            fenceFootprint, sitePolygon, sitePolygon,
            floorMaterial,
            centerPointWorld, deformToTerrain: isDynamic,
            combineInstances, materials, temporaryMeshes);

        // 7. メッシュ結合。
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // 8. コライダー（岸壁フットプリントを上面高さまで押し出し）。
        //    クレーンのブームは海側へ張り出すがクリック対象には含めない（工事中のクレーン §19.7 と同じ割り切り）。
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(quayFootprint, Mathf.Max(0.1f, topLocalY));

        var result = new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    // 海岸線リングを偶奇合成した陸地領域（NTS Geometry、ワールド座標）のキャッシュ。
    // 海岸線はセッション中不変なので、CoastlineStore.Version が変わったときだけ再構築する。
    private static Geometry cachedLandGeometry;
    private static int cachedCoastlineVersion = -1;

    /// <summary>
    /// 海岸線の全リングから陸地領域の NTS ジオメトリを構築して返す（キャッシュ付き）。
    /// リングは外周と穴（湖）を区別せず保持されているため、偶奇合成（XOR）で陸地領域になる
    /// （<see cref="CoastlineStore.IsLand"/> の even-odd 判定と同じ解釈）。
    /// 陸地が存在しない場合は null を返す。
    /// </summary>
    private static Geometry GetLandGeometry()
    {
        if (cachedCoastlineVersion == CoastlineStore.Version)
        {
            return cachedLandGeometry;
        }
        cachedCoastlineVersion = CoastlineStore.Version;
        cachedLandGeometry = null;

        var gf = new GeometryFactory();
        Geometry land = null;
        foreach (Vector2[] ring in CoastlineStore.Rings)
        {
            if (ring == null || ring.Length < 3)
            {
                continue;
            }
            // NTS の LinearRing は始点と終点の厳密一致を要求するため、
            // 終端が始点と（ほぼ）同じでも必ず始点の複製で閉じる。
            bool closed = (ring[0] - ring[ring.Length - 1]).sqrMagnitude < 1e-6f;
            int count = ring.Length + (closed ? 0 : 1);
            var coords = new Coordinate[count];
            for (int i = 0; i < ring.Length; i++)
            {
                coords[i] = new Coordinate(ring[i].x, ring[i].y);
            }
            coords[count - 1] = new Coordinate(ring[0].x, ring[0].y);

            Geometry poly;
            try
            {
                poly = gf.CreatePolygon(coords);
                if (!poly.IsValid)
                {
                    poly = poly.Buffer(0);
                }
            }
            catch (System.Exception)
            {
                continue;
            }
            if (poly.IsEmpty)
            {
                continue;
            }
            land = land == null ? poly : land.SymmetricDifference(poly);
        }
        cachedLandGeometry = land;
        return land;
    }

    /// <summary>
    /// 底面ポリゴン（center_point 相対のローカル座標）から岸壁フットプリントを生成する。
    ///
    /// 底面外周と海岸線（陸地境界）の交点のうち最も離れた2点を岸壁の左端A・右端Bとし、
    /// それぞれを海側へ visual_variant1/2 に応じた距離だけ突き出した A'・B' を結ぶ直線で
    /// 底面をカットして陸側を残す。海側境界は海岸線をなぞらず、常に1本の直線になる。
    /// 突き出し距離 = lerp(MIN_PROTRUSION_RATIO, 1, variant) × 奥行き上限
    /// （奥行き上限 = 弦A-Bから底面の海側最遠頂点までの距離）。
    /// カットは底面ポリゴンとの交差なので、岸壁は常に土地区画の範囲内に収まる。
    /// 海岸線未ロード・交点2つ未満・海側頂点なし・幾何演算失敗の場合は元の底面をそのまま返す。
    /// </summary>
    /// <param name="seaward">海側境界（直線カットで生じたエッジ）。塔屋の向きと荷役クレーンの配置に使う。カット不能時は Valid=false。</param>
    private static Vector2[] BuildQuayFootprint(Vector2[] footprint, Vector3 centerPointWorld,
        float variant1, float variant2, out QuaySeawardEdge seaward)
    {
        seaward = default;
        int n = footprint.Length;

        if (!CoastlineStore.IsLoaded || !CoastlineStore.HasCoastline)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[PortBMB] 海岸線未ロード (IsLoaded={CoastlineStore.IsLoaded})。カットせず元形状を使用");
            return (Vector2[])footprint.Clone();
        }

        Geometry land = GetLandGeometry();
        if (land == null || land.IsEmpty)
        {
            return (Vector2[])footprint.Clone();
        }

        try
        {
            var gf = new GeometryFactory();
            var coords = new Coordinate[n + 1];
            for (int i = 0; i < n; i++)
            {
                coords[i] = new Coordinate(footprint[i].x + centerPointWorld.x, footprint[i].y + centerPointWorld.z);
            }
            coords[n] = new Coordinate(coords[0].X, coords[0].Y);
            Geometry footprintPoly = gf.CreatePolygon(coords);
            if (!footprintPoly.IsValid)
            {
                footprintPoly = footprintPoly.Buffer(0);
            }

            // 底面外周と海岸線の交点を列挙し、最も離れた2点を左端A・右端Bとする。
            var hits = new List<Vector2>();
            CollectCoordinates(footprintPoly.Boundary.Intersection(land.Boundary), hits);
            if (hits.Count < 2)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                    $"[PortBMB] 底面外周と海岸線の交点が{hits.Count}点のためカットせず元形状を使用（全域が陸または海）");
                return (Vector2[])footprint.Clone();
            }
            Vector2 a = Vector2.zero, b = Vector2.zero;
            float bestSq = -1f;
            for (int i = 0; i < hits.Count; i++)
            {
                for (int j = i + 1; j < hits.Count; j++)
                {
                    float sq = (hits[i] - hits[j]).sqrMagnitude;
                    if (sq > bestSq)
                    {
                        bestSq = sq;
                        a = hits[i];
                        b = hits[j];
                    }
                }
            }
            if (bestSq < 1f)
            {
                return (Vector2[])footprint.Clone();
            }

            // 海側方向の決定: 陸地に含まれない底面頂点が、弦A-Bに対して平均的に存在する側を海側とする。
            Vector2 u = (b - a).normalized;
            Vector2 perp = new Vector2(-u.y, u.x);
            float sideSum = 0f;
            var seaOffsets = new List<float>();
            for (int i = 0; i < n; i++)
            {
                float wx = footprint[i].x + centerPointWorld.x;
                float wz = footprint[i].y + centerPointWorld.z;
                if (!CoastlineStore.IsLand(wx, wz))
                {
                    float s = Vector2.Dot(new Vector2(wx, wz) - a, perp);
                    sideSum += s;
                    seaOffsets.Add(s);
                }
            }
            if (seaOffsets.Count == 0)
            {
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                    "[PortBMB] 海側の底面頂点が無いためカットせず元形状を使用");
                return (Vector2[])footprint.Clone();
            }
            if (sideSum < 0f)
            {
                perp = -perp;
                for (int i = 0; i < seaOffsets.Count; i++)
                {
                    seaOffsets[i] = -seaOffsets[i];
                }
            }
            // A→B を「海を左手に見る」向きへ正規化し、左端=A(variant1)・右端=B(variant2) の対応を決定的にする。
            if (Vector2.Dot(perp, new Vector2(-u.y, u.x)) < 0f)
            {
                Vector2 tmp = a;
                a = b;
                b = tmp;
                u = -u;
            }

            // 突き出しの奥行き上限 = 弦から底面の海側最遠頂点までの距離。
            float maxDepth = 0f;
            foreach (float s in seaOffsets)
            {
                if (s > maxDepth)
                {
                    maxDepth = s;
                }
            }
            if (maxDepth < 0.5f)
            {
                return (Vector2[])footprint.Clone();
            }

            float depthLeft = Mathf.Lerp(MIN_PROTRUSION_RATIO, 1f, variant1) * maxDepth;
            float depthRight = Mathf.Lerp(MIN_PROTRUSION_RATIO, 1f, variant2) * maxDepth;
            Vector2 aOut = a + perp * depthLeft;
            Vector2 bOut = b + perp * depthRight;
            Vector2 edgeDir = (bOut - aOut).normalized;

            // カット線 A'-B' の陸側半平面（十分大きな矩形）と底面の交差 = 岸壁フットプリント。
            const float L = 1e4f;
            Vector2 r1 = aOut - edgeDir * L;
            Vector2 r2 = bOut + edgeDir * L;
            Vector2 r3 = r2 - perp * L;
            Vector2 r4 = r1 - perp * L;
            Geometry landSideRect = gf.CreatePolygon(new[]
            {
                new Coordinate(r1.x, r1.y),
                new Coordinate(r2.x, r2.y),
                new Coordinate(r3.x, r3.y),
                new Coordinate(r4.x, r4.y),
                new Coordinate(r1.x, r1.y),
            });

            Geometry quay = footprintPoly.Intersection(landSideRect);
            if (quay == null || quay.IsEmpty || quay.Area < 1.0)
            {
                return (Vector2[])footprint.Clone();
            }
            Polygon best = null;
            for (int i = 0; i < quay.NumGeometries; i++)
            {
                if (quay.GetGeometryN(i) is Polygon p && (best == null || p.Area > best.Area))
                {
                    best = p;
                }
            }
            if (best == null)
            {
                return (Vector2[])footprint.Clone();
            }

            // 外周リングをローカル座標へ戻す。閉路の重複点と 1cm 未満の近接重複点は
            // 三角形分割の縮退を避けるため取り除く。
            Coordinate[] ring = best.ExteriorRing.Coordinates;
            var result = new List<Vector2>(ring.Length);
            for (int i = 0; i < ring.Length - 1; i++)
            {
                var v = new Vector2((float)ring[i].X - centerPointWorld.x, (float)ring[i].Y - centerPointWorld.z);
                if (result.Count > 0 && (v - result[result.Count - 1]).sqrMagnitude < 1e-4f)
                {
                    continue;
                }
                result.Add(v);
            }
            while (result.Count > 1 && (result[0] - result[result.Count - 1]).sqrMagnitude < 1e-4f)
            {
                result.RemoveAt(result.Count - 1);
            }
            if (result.Count < 3)
            {
                return (Vector2[])footprint.Clone();
            }

            Vector2[] quayFootprint = result.ToArray();
            if (BuildingMeshUtils.SignedArea(quayFootprint) < 0f)
            {
                System.Array.Reverse(quayFootprint);
            }

            // 海側境界の記述。法線は「カット直線に垂直」かつ「海を向く側」を採る。
            // 左右の突き出し量が違うと edgeDir は弦 A-B から傾くため、perp をそのまま使わず edgeDir から作り直す。
            Vector2 cutNormal = new Vector2(edgeDir.y, -edgeDir.x);
            if (Vector2.Dot(cutNormal, perp) < 0f)
            {
                cutNormal = -cutNormal;
            }
            seaward = new QuaySeawardEdge
            {
                Valid = true,
                Origin = new Vector2(aOut.x - centerPointWorld.x, aOut.y - centerPointWorld.z),
                Dir = edgeDir,
                Normal = cutNormal,
            };
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[PortBMB] 直線カット適用: 頂点 {n}→{quayFootprint.Length}, 突き出し 左{depthLeft:F1}m/右{depthRight:F1}m (上限{maxDepth:F1}m)");
            return quayFootprint;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PortBMB] 岸壁の直線カットに失敗しました: {ex.Message}。元の形状を使用します。");
            return (Vector2[])footprint.Clone();
        }
    }

    /// <summary>Geometry（Point / MultiPoint / LineString / Collection）から頂点座標を再帰的に収集する。</summary>
    private static void CollectCoordinates(Geometry geom, List<Vector2> output)
    {
        if (geom == null || geom.IsEmpty)
        {
            return;
        }
        if (geom is GeometryCollection)
        {
            for (int i = 0; i < geom.NumGeometries; i++)
            {
                CollectCoordinates(geom.GetGeometryN(i), output);
            }
            return;
        }
        foreach (Coordinate c in geom.Coordinates)
        {
            output.Add(new Vector2((float)c.X, (float)c.Y));
        }
    }

    /// <summary>
    /// 岸壁フットプリントから厚みのあるスラブメッシュを生成する。
    /// 上面は <paramref name="topLocalY"/> の水平面、下面は各頂点位置の地形（海底）高さに合わせる。
    /// subMesh0=上面、subMesh1=側面+下面 の2サブメッシュ構成で返す。
    /// 巻き方は <see cref="BuildingMeshBuilder.GenerateSimpleBuilding"/> と同じ（外向き法線）。
    /// </summary>
    private static Mesh BuildSlabMesh(Vector2[] polygon, float topLocalY, Vector3 centerPointWorld)
    {
        if (polygon == null || polygon.Length < 3)
        {
            return null;
        }

        Vector2[] p = (Vector2[])polygon.Clone();
        if (BuildingMeshUtils.SignedArea(p) < 0f)
        {
            System.Array.Reverse(p);
        }
        int n = p.Length;

        // 各頂点の下面ローカルY = その水平位置の地形（海底）高さ − center_point 位置の地形高さ。
        // 上面より上に来ないようにクランプし、浅瀬で地形が上面を超える場合のメッシュ反転を防ぐ。
        float[] bottomY = new float[n];
        for (int i = 0; i < n; i++)
        {
            float wx = p[i].x + centerPointWorld.x;
            float wz = p[i].y + centerPointWorld.z;
            float seabed = HeightmapTextureManager.GetTerrainHeight(new Vector3(wx, 1000f, wz));
            bottomY[i] = Mathf.Min(seabed - centerPointWorld.y, topLocalY - 0.05f);
        }

        List<int> capTriangles = BuildingMeshBuilder.TriangulateOrFan(p);

        List<Vector3> vertices = new List<Vector3>();
        List<int> topTriangles = new List<int>();
        List<int> sideTriangles = new List<int>();

        // 上面（topLocalY、上向き法線 → 巻きを反転）
        int topStart = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(new Vector3(p[i].x, topLocalY, p[i].y));
        }
        for (int t = 0; t < capTriangles.Count; t += 3)
        {
            topTriangles.Add(topStart + capTriangles[t]);
            topTriangles.Add(topStart + capTriangles[t + 2]);
            topTriangles.Add(topStart + capTriangles[t + 1]);
        }

        // 下面（頂点ごとの bottomY、下向き法線 → 自然な巻き）
        int bottomStart = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(new Vector3(p[i].x, bottomY[i], p[i].y));
        }
        for (int t = 0; t < capTriangles.Count; t += 3)
        {
            sideTriangles.Add(bottomStart + capTriangles[t]);
            sideTriangles.Add(bottomStart + capTriangles[t + 1]);
            sideTriangles.Add(bottomStart + capTriangles[t + 2]);
        }

        // 側面（各辺を下面→上面へ押し出し。GenerateSimpleBuilding と同じ巻き）
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int s = vertices.Count;
            vertices.Add(new Vector3(p[i].x, bottomY[i], p[i].y));        // s   : bottom i
            vertices.Add(new Vector3(p[next].x, bottomY[next], p[next].y)); // s+1 : bottom next
            vertices.Add(new Vector3(p[i].x, topLocalY, p[i].y));          // s+2 : top i
            vertices.Add(new Vector3(p[next].x, topLocalY, p[next].y));    // s+3 : top next

            // tri1 = (bottom_i, top_i, bottom_next)
            sideTriangles.Add(s);
            sideTriangles.Add(s + 2);
            sideTriangles.Add(s + 1);
            // tri2 = (bottom_next, top_i, top_next)
            sideTriangles.Add(s + 1);
            sideTriangles.Add(s + 2);
            sideTriangles.Add(s + 3);
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(topTriangles, 0);
        mesh.SetTriangles(sideTriangles, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// 塔屋（roofprop）を岸壁の中央に、海側境界の向きに合わせて配置する。
    /// visual_variant によるシード散らしは行わない（§12 の通常塔屋との差分）。
    /// パーツは閾値フォルダ 0 から取得する（visual_variant は岸壁の幾何に消費されるため）。
    /// </summary>
    private static void PlaceRoofprop(string prefix, Vector2[] quayFootprint, Vector2 seawardDir, float topLocalY,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        GameObject roofpropModule = BuildingMeshUtils.GetPartNoPosition(prefix, "roofprop", 0, true);
        if (roofpropModule == null)
        {
            return;
        }

        MeshFilter mf = roofpropModule.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            return;
        }

        // 岸壁の重心
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < quayFootprint.Length; i++)
        {
            centroid += quayFootprint[i];
        }
        centroid /= quayFootprint.Length;

        // 海側境界の向き（特定できなければ最初の辺の向き）
        Vector2 dir = seawardDir.sqrMagnitude > 1e-6f
            ? seawardDir.normalized
            : (quayFootprint.Length >= 2 ? (quayFootprint[1] - quayFootprint[0]).normalized : Vector2.right);
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        Quaternion rotation = Quaternion.Euler(0, -angle + 90, 0);

        Vector3 position = new Vector3(centroid.x, topLocalY, centroid.y);
        Mesh meshInstance = Object.Instantiate(mf.sharedMesh);
        Vector3[] vertices = meshInstance.vertices;
        Matrix4x4 matrix = Matrix4x4.TRS(position, rotation, Vector3.one);
        for (int v = 0; v < vertices.Length; v++)
        {
            vertices[v] = matrix.MultiplyPoint3x4(vertices[v]);
        }
        meshInstance.vertices = vertices;
        meshInstance.RecalculateNormals();
        meshInstance.RecalculateBounds();

        Material[] moduleMaterials = BuildingMeshUtils.GetModuleMaterials(roofpropModule);
        for (int s = 0; s < meshInstance.subMeshCount; s++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = meshInstance,
                subMeshIndex = s,
                transform = Matrix4x4.identity
            });
            Material mat = (moduleMaterials != null && moduleMaterials.Length > 0)
                ? moduleMaterials[Mathf.Min(s, moduleMaterials.Length - 1)]
                : null;
            materials.Add(mat);
        }
        temporaryMeshes.Add(meshInstance);
    }

    /// <summary>
    /// 荷役クレーン（port_crane）を岸壁の水際＝海側の直線エッジに沿って等間隔に並べる。
    ///
    /// パーツは一切スケールしない（実寸）。モデル原点が水際の境界線上・岸壁上面の高さに一致し、
    /// ローカル+Z が海を向く（＝ローカルXが水際に沿う）ように回転させる。
    /// 基数はエッジ長 ÷（パーツ実寸幅 × <see cref="CRANE_SPACING_RATIO"/>）で決まり、
    /// エッジ全体に等間隔・両端の余白が等しくなるよう配置する。
    /// クレーン1基の幅に満たない水際には置かない。
    ///
    /// 海側境界が特定できなかった建物（海岸線未ロード・カット不能）では水際が定まらないため配置しない。
    /// </summary>
    private static void PlacePortCranes(string prefix, Vector2[] quayFootprint, QuaySeawardEdge seaward, float topLocalY,
        List<CombineInstance> combineInstances, List<Material> materials, List<Mesh> temporaryMeshes)
    {
        if (!seaward.Valid || quayFootprint == null || quayFootprint.Length < 3)
        {
            return;
        }

        GameObject cranePrefab = BuildingMeshUtils.GetPartNoPosition(prefix, "port_crane", 0, true);
        if (cranePrefab == null)
        {
            return;
        }

        // 複数オブジェクト構成のFBXでも欠けないよう、プレハブ配下の全メッシュを結合して使う。
        Mesh craneMesh = BuildingMeshUtils.CombineModuleMeshes(cranePrefab, out Material[] craneMaterials);
        if (craneMesh == null)
        {
            return;
        }
        temporaryMeshes.Add(craneMesh);

        float craneWidth = Mathf.Max(craneMesh.bounds.size.x, 0.5f);
        float spacing = craneWidth * CRANE_SPACING_RATIO;
        Quaternion rotation = Quaternion.LookRotation(new Vector3(seaward.Normal.x, 0f, seaward.Normal.y), Vector3.up);

        int placed = 0;
        int n = quayFootprint.Length;
        for (int i = 0; i < n && placed < MAX_CRANE_COUNT; i++)
        {
            Vector2 p = quayFootprint[i];
            Vector2 q = quayFootprint[(i + 1) % n];

            // カット直線上に乗っている辺だけが水際。両端の法線方向オフセットが0とみなせるかで判定する。
            if (Mathf.Abs(Vector2.Dot(p - seaward.Origin, seaward.Normal)) > SEAWARD_EDGE_EPSILON ||
                Mathf.Abs(Vector2.Dot(q - seaward.Origin, seaward.Normal)) > SEAWARD_EDGE_EPSILON)
            {
                continue;
            }

            float length = (q - p).magnitude;
            if (length < craneWidth)
            {
                continue;
            }

            int count = Mathf.Clamp(Mathf.FloorToInt(length / spacing), 1, MAX_CRANE_COUNT - placed);
            Vector2 dir = (q - p) / length;
            for (int c = 0; c < count; c++)
            {
                Vector2 at = p + dir * (length * (c + 0.5f) / count);
                Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(at.x, topLocalY, at.y), rotation, Vector3.one);
                for (int s = 0; s < craneMesh.subMeshCount; s++)
                {
                    combineInstances.Add(new CombineInstance
                    {
                        mesh = craneMesh,
                        subMeshIndex = s,
                        transform = matrix
                    });
                    Material mat = (craneMaterials != null && craneMaterials.Length > 0)
                        ? craneMaterials[Mathf.Min(s, craneMaterials.Length - 1)]
                        : null;
                    materials.Add(mat);
                }
                placed++;
            }
        }

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
            $"[PortBMB] 荷役クレーン: {placed}基配置 (幅{craneWidth:F1}m 間隔{spacing:F1}m)");
    }

    /// <summary>
    /// メッシュの各サブメッシュを CombineInstance リストに追加する。サブメッシュごとに対応するマテリアルを積む。
    /// </summary>
    private static void AppendMesh(Mesh mesh, Material[] subMeshMaterials,
        List<CombineInstance> combineInstances, List<Material> materials)
    {
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = mesh,
                subMeshIndex = s,
                transform = Matrix4x4.identity
            });
            materials.Add(s < subMeshMaterials.Length ? subMeshMaterials[s] : null);
        }
    }
}
