using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 海岸の岸壁（MODEL_PORT）のメッシュ生成器。
///
/// 海岸土地区画（PARCEL_SHORE）に立地する港湾（BUILDING_PORT）に適用する。
/// building_shape=SHAPE_FULL の底面ポリゴン（=海岸線でクリップ済みの土地区画形状。海側境界が海岸線曲線）を
/// 海岸線でカットし、厚みのある板（岸壁スラブ）を上下に押し出して生成する。
/// モジュラーの壁・角パーツは使わず、純粋にプロシージャルにスラブを作る。
///
/// visual_variant(1)/visual_variant2 … 岸壁の左端/右端の突き出し量（0=海岸線ぴったり、1=区画外周の弦まで）
/// visual_variant3 … 海面からの高さ（0=海面0m、1=10m）
///
/// 詳細仕様は genesis-docs/docs/geometries/F_建物メッシュの生成.md §18 を参照。
/// </summary>
public class PortBuildingMeshBuilder : IBuildingMeshGenerator
{
    /// <summary>visual_variant3=1.0 のときの海面からの高さ(m)。</summary>
    private const float MAX_HEIGHT = 10f;

    /// <summary>底面の辺が海岸線に接していると判定する距離閾値(m)。</summary>
    private const float COAST_EDGE_THRESHOLD = 3f;

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
        float variant3 = Mathf.Clamp01(buildingData.visual_variant3);

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

        // 1. 海側境界（海岸線エッジ）を特定し、visual_variant1/2 で突き出しを補間した岸壁フットプリントを得る。
        Vector2[] quayFootprint = BuildQuayFootprint(footprint, centerPointWorld, variant1, variant2, out Vector2 seawardDir);

        // 2. スラブ上面のローカルY。
        //    上面のワールドY = 海面(0m) + visual_variant3 × 10m。GameObject は center_point 位置の地形高さに
        //    配置されるため、ローカルYは centerPointWorld.y を差し引く。
        float topLocalY = variant3 * MAX_HEIGHT - centerPointWorld.y;

        // 3. スラブメッシュ（上面=topLocalY、下面=各頂点の地形=海底高さ）。subMesh0=上面、subMesh1=側面+下面。
        Mesh slabMesh = BuildSlabMesh(quayFootprint, topLocalY, centerPointWorld);
        if (slabMesh == null)
        {
            return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, 1, LocalConstants.FLOOR_HEIGHT);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // スラブのマテリアル: 上面=ceil、側面/下面=floor（無ければデフォルトマテリアル）。
        Material topMaterial = BuildingMeshUtils.SelectRoofMaterial(prefix, 0, null);
        Material sideMaterial = BuildingMeshUtils.SelectFloorMaterial(prefix, 0, null);
        AppendMesh(slabMesh, new[] { topMaterial, sideMaterial }, combineInstances, materials);
        temporaryMeshes.Add(slabMesh);

        // 4. 塔屋（岸壁中央・海側境界方向・上面高さ）。
        PlaceRoofprop(prefix, quayFootprint, seawardDir, topLocalY, combineInstances, materials, temporaryMeshes);

        // 5. メッシュ結合。
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // 6. コライダー（岸壁フットプリントを上面高さまで押し出し）。
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(quayFootprint, Mathf.Max(0.1f, topLocalY));

        var result = new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    /// <summary>
    /// 底面ポリゴンの海側境界（海岸線エッジ）を特定し、visual_variant1/2 に応じて
    /// 海岸線位置 ↔ 区画外周の弦の間で突き出しを補間した「岸壁フットプリント」を生成する。
    /// 海岸線が未ロード、または海側境界を特定できない場合は元の底面をそのまま返す
    /// （=海岸線を無視した、土地区画なりの岸壁）。
    /// </summary>
    /// <param name="seawardDir">海側境界の向き（左端A→右端B）。塔屋の向きに使う。</param>
    private static Vector2[] BuildQuayFootprint(Vector2[] footprint, Vector3 centerPointWorld,
        float variant1, float variant2, out Vector2 seawardDir)
    {
        seawardDir = Vector2.zero;
        int n = footprint.Length;

        if (!CoastlineStore.IsLoaded || !CoastlineStore.HasCoastline)
        {
            return (Vector2[])footprint.Clone();
        }

        // 各辺の中点をワールド座標に変換し、海岸線に接しているか（海側か）を判定する。
        bool[] isSea = new bool[n];
        int seaCount = 0;
        for (int i = 0; i < n; i++)
        {
            Vector2 mid = (footprint[i] + footprint[(i + 1) % n]) * 0.5f;
            float wx = mid.x + centerPointWorld.x;
            float wz = mid.y + centerPointWorld.z;
            if (CoastlineStore.IsNearCoastline(wx, wz, COAST_EDGE_THRESHOLD))
            {
                isSea[i] = true;
                seaCount++;
            }
        }

        // 海側境界が無い／全周が海なら、カットせず元形状を使う。
        if (seaCount == 0 || seaCount >= n)
        {
            return (Vector2[])footprint.Clone();
        }

        // 最長の連続した海側ラン（巡回）を探す。陸地に挟まれた海岸線エッジ列を1本の海側境界とみなす。
        int bestStart = -1, bestLen = 0;
        for (int i = 0; i < n; i++)
        {
            if (isSea[i] && !isSea[(i + n - 1) % n])
            {
                int len = 0;
                int k = i;
                while (isSea[k] && len < n)
                {
                    len++;
                    k = (k + 1) % n;
                }
                if (len > bestLen)
                {
                    bestLen = len;
                    bestStart = i;
                }
            }
        }
        if (bestStart < 0)
        {
            return (Vector2[])footprint.Clone();
        }

        // 海側エッジ run = 辺 [bestStart .. bestStart+bestLen-1]。
        // 海側頂点 = 頂点 [bestStart .. bestStart+bestLen]（巡回）。両端 A/B が陸側境界との接続点。
        int vCount = bestLen + 1;
        int[] seaVerts = new int[vCount];
        for (int i = 0; i < vCount; i++)
        {
            seaVerts[i] = (bestStart + i) % n;
        }

        Vector2 a = footprint[seaVerts[0]];
        Vector2 b = footprint[seaVerts[vCount - 1]];
        seawardDir = b - a;

        // 海側頂点の弧長を計算し、A(0.0)〜B(1.0) のパラメータ frac を求める。
        float[] cumulative = new float[vCount];
        cumulative[0] = 0f;
        for (int i = 1; i < vCount; i++)
        {
            cumulative[i] = cumulative[i - 1] + Vector2.Distance(footprint[seaVerts[i - 1]], footprint[seaVerts[i]]);
        }
        float total = cumulative[vCount - 1];

        Vector2[] result = (Vector2[])footprint.Clone();
        for (int i = 0; i < vCount; i++)
        {
            float frac = total > 1e-5f ? cumulative[i] / total : (float)i / Mathf.Max(1, vCount - 1);
            Vector2 chordPoint = Vector2.Lerp(a, b, frac);  // 弦（最大突き出し線）上の対応点
            float weight = Mathf.Lerp(variant1, variant2, frac);  // 突き出し量（左端=variant1、右端=variant2）
            Vector2 original = footprint[seaVerts[i]];  // 海岸線上の元頂点
            result[seaVerts[i]] = Vector2.Lerp(original, chordPoint, weight);
        }
        return result;
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
