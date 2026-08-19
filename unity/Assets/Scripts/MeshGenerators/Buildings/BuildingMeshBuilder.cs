// Assets/Scripts/MeshGenerators/Buildings/BuildingMeshBuilder.cs
using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 建物メッシュ生成のエントリポイント兼ユーティリティクラス。
/// model_type に応じて適切な下位ビルダー（Modular / Prebuilt / Default）へ処理を委譲する。
/// また、メッシュ結合・簡易建物生成・コライダー生成・塔屋配置などの共通処理も提供する。
/// </summary>
public class BuildingMeshBuilder : IBuildingMeshGenerator
{
    private static readonly IBuildingMeshGenerator modularGenerator = new ModularBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator prebuiltGenerator = new PreBuiltBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator portGenerator = new PortBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator mashupGenerator = new MashupBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator constructingGenerator = new ConstructingBuildingMeshBuilder(ConstructionSiteProfile.Construction);
    private static readonly IBuildingMeshGenerator demolishingGenerator = new ConstructingBuildingMeshBuilder(ConstructionSiteProfile.Demolition);
    private static readonly IBuildingMeshGenerator collapsedGenerator = new CollapsedBuildingMeshBuilder();

    /// <summary>
    /// BuildingSummary から建物メッシュを生成する。
    /// model_type に応じて生成方式を分岐する。
    /// isDynamic が true の場合、モジュラー・既成建物のフェンス・タイル・地面パネルを地形に追従させる。
    /// </summary>
    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null)
        {
            Debug.LogError("BuildingMeshBuilder.Generate: BuildingSummary が null です。");
            return null;
        }

        // 工事中/解体中/廃墟は model_type に関わらず専用メッシュで描画する。model_type 分岐より
        // 先に status で判定することで Near/Far・DetailWorker・直接呼び出しの全生成経路をカバーする。
        // - 建設中(STATUS_CONSTRUCTING): 土マウンド + 足場 + クレーン
        // - 解体中(STATUS_DEMOLISHING): 土マウンド + 色違い足場 + ショベルカー（建設中と同一ロジック・パーツ差分のみ）
        // - 廃墟(STATUS_COLLAPSED): 瓦礫マウンド + 任意の倒壊片。
        //   焼失(status_reason == REASON_FIRE)は焦げた瓦礫マテリアルで描き分ける（ビルダー内で選択）。
        if (buildingData.status == Constants.STATUS_CONSTRUCTING)
        {
            return EnsureMaterials(constructingGenerator.Generate(buildingData, isDynamic));
        }
        if (buildingData.status == Constants.STATUS_DEMOLISHING)
        {
            return EnsureMaterials(demolishingGenerator.Generate(buildingData, isDynamic));
        }
        if (buildingData.status == Constants.STATUS_COLLAPSED)
        {
            return EnsureMaterials(collapsedGenerator.Generate(buildingData, isDynamic));
        }

        int modelType = GetModelType(buildingData);

        BuildingMeshResult result;
        switch (modelType)
        {
            case Constants.MODEL_MODULAR_FLAT:
            case Constants.MODEL_MODULAR_HOUSE:
            case Constants.MODEL_MODULAR_GABLE:
            case Constants.MODEL_MODULAR_SHED:
            case Constants.MODEL_MODULAR_GROUND:
                result = modularGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_PREBUILT:
                result = prebuiltGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_PORT:
                result = portGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_MASHUP:
                result = mashupGenerator.Generate(buildingData, isDynamic);
                break;
            default:
                UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                    $"[BMB] Generate: variant={buildingData.variant} は既知の model_type に該当せず（modelType={modelType}）。白い箱にフォールバックします。");
                Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
                int floors = Mathf.Max(1, buildingData.floors ?? 1);
                float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
                result = GenerateSimpleBuilding(footprint, floors, floorHeight);
                break;
        }

        return EnsureMaterials(result);
    }

    /// <summary>
    /// MasterDataStore から該当建物の model_type を取得する。
    /// 取得できない場合は MODEL_MODULAR_FLAT をデフォルトとする。
    /// </summary>
    public static int GetModelType(BuildingSummary buildingData)
    {
        if (MasterDataStore.Instance == null || !MasterDataStore.Instance.IsInitialized)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[BMB] GetModelType: MasterDataStore 未初期化 (Instance={(MasterDataStore.Instance != null)}). variant={buildingData.variant} → 既定 MODEL_MODULAR_FLAT");
            return Constants.MODEL_MODULAR_FLAT;
        }

        MasterDataStore.AgentMasterItem agentMaster = null;
        try
        {
            agentMaster = MasterDataStore.Instance.GetAgentMaster(buildingData.variant);
        }
        catch (System.Exception ex)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING,
                $"[BMB] GetModelType: variant={buildingData.variant} が agent_master に存在しません ({ex.GetType().Name})。既定 MODEL_MODULAR_FLAT にフォールバック");
            return Constants.MODEL_MODULAR_FLAT;
        }

        if (agentMaster?.model_type != null)
        {
            return agentMaster.model_type.Value;
        }
        return Constants.MODEL_MODULAR_FLAT;
    }

    /// <summary>
    /// 同一マテリアルのサブメッシュを統合してドローコールを最小化する。
    /// </summary>
    public static BuildingMeshResult MergeSubMeshes(BuildingMeshResult meshResult)
    {
        if (meshResult == null || meshResult.Mesh == null)
        {
            return meshResult;
        }

        Mesh mesh = meshResult.Mesh;
        int originalSubMeshCount = mesh.subMeshCount;

        if (originalSubMeshCount <= 1)
        {
            return meshResult;
        }

        Material[] materials = meshResult.Materials;

        const long NullMaterialKey = long.MinValue;
        Dictionary<long, int> materialKeyToIndex = new Dictionary<long, int>();
        List<Material> mergedMaterials = new List<Material>();
        List<List<int>> mergedTriangles = new List<List<int>>();

        for (int subMeshIndex = 0; subMeshIndex < originalSubMeshCount; subMeshIndex++)
        {
            Material material = (materials != null && subMeshIndex < materials.Length)
                ? materials[subMeshIndex]
                : null;

            long materialKey = material != null ? material.GetEntityId() : NullMaterialKey;
            if (!materialKeyToIndex.TryGetValue(materialKey, out int mergedIndex))
            {
                mergedIndex = mergedMaterials.Count;
                materialKeyToIndex.Add(materialKey, mergedIndex);
                mergedMaterials.Add(material);
                mergedTriangles.Add(new List<int>());
            }

            int[] triangles = mesh.GetTriangles(subMeshIndex);
            if (triangles != null && triangles.Length > 0)
            {
                mergedTriangles[mergedIndex].AddRange(triangles);
            }
        }

        List<Material> finalMaterials = new List<Material>();
        List<List<int>> finalTriangles = new List<List<int>>();
        for (int i = 0; i < mergedTriangles.Count; i++)
        {
            if (mergedTriangles[i].Count == 0)
            {
                continue;
            }

            finalMaterials.Add(mergedMaterials[i]);
            finalTriangles.Add(mergedTriangles[i]);
        }

        if (finalTriangles.Count == 0)
        {
            return meshResult;
        }

        mesh.subMeshCount = finalTriangles.Count;
        for (int i = 0; i < finalTriangles.Count; i++)
        {
            mesh.SetTriangles(finalTriangles[i], i);
        }

        return new BuildingMeshResult(mesh, finalMaterials.ToArray(), meshResult.ColliderMesh);
    }

    private static Material cachedDefaultBuildingMaterial;

    /// <summary>
    /// Resources/BuildingMaterial を読み込んで返す。
    /// キャッシュしており、初回呼び出し時のみ Resources.Load を行う。
    /// </summary>
    public static Material GetDefaultBuildingMaterial()
    {
        if (cachedDefaultBuildingMaterial != null)
        {
            return cachedDefaultBuildingMaterial;
        }

        cachedDefaultBuildingMaterial = Resources.Load<Material>("BuildingMaterial");
        if (cachedDefaultBuildingMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/BuildingMaterial が見つかりません。マテリアルの共有に失敗します。");
        }

        return cachedDefaultBuildingMaterial;
    }

    /// <summary>
    /// BuildingMeshResult のマテリアル配列を検証し、null または空の場合はデフォルトマテリアルを割り当てる。
    /// 配列内に null が含まれる場合もその要素をデフォルトマテリアルで置き換える。
    /// </summary>
    public static BuildingMeshResult EnsureMaterials(BuildingMeshResult result)
    {
        if (result == null || result.Mesh == null)
        {
            return result;
        }

        Material fallback = GetDefaultBuildingMaterial();
        if (fallback == null)
        {
            return result;
        }

        // Materials が null または空の場合、デフォルトマテリアルを1つ割り当てる
        if (result.Materials == null || result.Materials.Length == 0)
        {
            return new BuildingMeshResult(result.Mesh, new[] { fallback }, result.ColliderMesh);
        }

        // 配列内に null が含まれているか確認
        bool containsNull = false;
        for (int i = 0; i < result.Materials.Length; i++)
        {
            if (result.Materials[i] == null)
            {
                containsNull = true;
                break;
            }
        }

        if (!containsNull)
        {
            return result;
        }

        Material[] resolved = new Material[result.Materials.Length];
        for (int i = 0; i < result.Materials.Length; i++)
        {
            resolved[i] = result.Materials[i] ?? fallback;
        }

        return new BuildingMeshResult(result.Mesh, resolved, result.ColliderMesh);
    }

    /// <summary>
    /// GLBモジュールがない場合のフォールバック（シンプルな直方体建物）。
    /// </summary>
    public static BuildingMeshResult GenerateSimpleBuilding(Vector2[] footprint, int floors, float floorHeight)
    {
        if (footprint == null || footprint.Length < 3)
        {
            Debug.LogWarning($"BuildingMeshBuilder.GenerateSimpleBuilding: footprint が不正です（頂点数 < 3）。");
            return new BuildingMeshResult(new Mesh(), null);
        }

        float buildingHeight = floors * floorHeight;
        Vector2[] normalizedFootprint = (Vector2[])footprint.Clone();

        if (SignedArea(normalizedFootprint) < 0f)
        {
            System.Array.Reverse(normalizedFootprint);
        }

        List<int> polygonTriangles = TriangulatePolygon(normalizedFootprint);
        if (polygonTriangles.Count == 0)
        {
            polygonTriangles = GenerateTriangleFanIndices(normalizedFootprint.Length);
        }

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        AddHorizontalSurface(vertices, triangles, normalizedFootprint, polygonTriangles, 0f, false);
        AddHorizontalSurface(vertices, triangles, normalizedFootprint, polygonTriangles, buildingHeight, true);

        int n = normalizedFootprint.Length;

        // 側面
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            Vector3 v0 = new Vector3(normalizedFootprint[i].x, 0, normalizedFootprint[i].y);
            Vector3 v1 = new Vector3(normalizedFootprint[next].x, 0, normalizedFootprint[next].y);
            Vector3 v2 = new Vector3(normalizedFootprint[i].x, buildingHeight, normalizedFootprint[i].y);
            Vector3 v3 = new Vector3(normalizedFootprint[next].x, buildingHeight, normalizedFootprint[next].y);

            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            triangles.Add(vertices.Count - 3);
            triangles.Add(vertices.Count - 1);
            triangles.Add(vertices.Count - 2);

            vertices.Add(v1);
            vertices.Add(v3);
            vertices.Add(v2);
            triangles.Add(vertices.Count - 3);
            triangles.Add(vertices.Count - 1);
            triangles.Add(vertices.Count - 2);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        Mesh colliderMesh = GenerateColliderMesh(normalizedFootprint, buildingHeight);

        return new BuildingMeshResult(mesh, null, colliderMesh);
    }

    /// <summary>
    /// 底面ポリゴンを押し出したコライダーメッシュを生成する。
    /// </summary>
    public static Mesh GenerateColliderMesh(Vector2[] footprint, float buildingHeight)
    {
        if (footprint == null || footprint.Length < 3)
        {
            return null;
        }

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        int n = footprint.Length;

        // 下面 (y=0, CCW → 法線下向き)
        int bottomStart = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(new Vector3(footprint[i].x, 0, footprint[i].y));
        }
        for (int i = 1; i < n - 1; i++)
        {
            triangles.Add(bottomStart);
            triangles.Add(bottomStart + i);
            triangles.Add(bottomStart + i + 1);
        }

        // 上面 (y=buildingHeight, CW → 法線上向き)
        int topStart = vertices.Count;
        for (int i = 0; i < n; i++)
        {
            vertices.Add(new Vector3(footprint[i].x, buildingHeight, footprint[i].y));
        }
        for (int i = 1; i < n - 1; i++)
        {
            triangles.Add(topStart);
            triangles.Add(topStart + i + 1);
            triangles.Add(topStart + i);
        }

        // 側面
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int bi = bottomStart + i;
            int bn = bottomStart + next;
            int ti = topStart + i;
            int tn = topStart + next;

            vertices.Add(vertices[bi]);
            vertices.Add(vertices[bn]);
            vertices.Add(vertices[ti]);
            triangles.Add(vertices.Count - 3);
            triangles.Add(vertices.Count - 1);
            triangles.Add(vertices.Count - 2);

            vertices.Add(vertices[bn]);
            vertices.Add(vertices[tn]);
            vertices.Add(vertices[ti]);
            triangles.Add(vertices.Count - 3);
            triangles.Add(vertices.Count - 1);
            triangles.Add(vertices.Count - 2);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// 塔屋（roofprop）を配置する。
    /// </summary>
    public static void GenerateRoofprop(string prefix, int suffix, float visual_variant2, Vector2[] footprint, float buildingHeight, List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        GameObject roofpropModule = BuildingMeshUtils.GetPartNoPosition(prefix, "roofprop", suffix, true);
        if (roofpropModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[BMB] 塔屋: パーツなし ({prefix}/{suffix}/roofprop)");
            return;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[BMB] 塔屋: パーツ取得成功");

        MeshFilter mf = roofpropModule.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        float seed = Mathf.Clamp01(visual_variant2);
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < footprint.Length; i++)
        {
            centroid += footprint[i];
        }
        centroid /= footprint.Length;

        float angle = seed * Mathf.PI * 2f;
        float radius = 0.5f;
        float offsetX = Mathf.Cos(angle) * radius;
        float offsetZ = Mathf.Sin(angle) * radius;

        Vector3 position3D = new Vector3(centroid.x + offsetX, buildingHeight, centroid.y + offsetZ);

        Vector2 firstEdge = footprint.Length >= 2 ? (footprint[1] - footprint[0]).normalized : Vector2.right;
        float edgeAngle = Mathf.Atan2(firstEdge.y, firstEdge.x) * Mathf.Rad2Deg;
        Quaternion rotation = Quaternion.Euler(0, -edgeAngle + 90, 0);

        Vector3 origSize = mf.sharedMesh.bounds.size;
        float scale = 1f;

        Mesh meshInstance = Object.Instantiate(mf.sharedMesh);
        Vector3[] vertices = meshInstance.vertices;
        Matrix4x4 matrix = Matrix4x4.TRS(position3D, rotation, new Vector3(scale, scale, scale));
        for (int v = 0; v < vertices.Length; v++)
        {
            vertices[v] = matrix.MultiplyPoint3x4(vertices[v]);
        }
        meshInstance.vertices = vertices;
        meshInstance.RecalculateNormals();
        meshInstance.RecalculateBounds();

        int subMeshCount = meshInstance.subMeshCount;
        Material[] moduleMaterials = BuildingMeshUtils.GetModuleMaterials(roofpropModule);
        for (int si = 0; si < subMeshCount; si++)
        {
            CombineInstance ci = new CombineInstance
            {
                mesh = meshInstance,
                subMeshIndex = si,
                transform = Matrix4x4.identity
            };
            combineInstances.Add(ci);

            Material mat = null;
            if (moduleMaterials != null && moduleMaterials.Length > 0)
            {
                mat = moduleMaterials[Mathf.Min(si, moduleMaterials.Length - 1)];
            }
            allMaterials.Add(mat);
        }
        temporaryMeshes.Add(meshInstance);
    }

    /// <summary>
    /// エントランス(entrance)を建物前面の辺の中央・地面レベルに配置する。
    /// パーツ {prefix}/{suffix}/entrance が存在する場合のみ配置する(オプショナル)。
    ///
    /// - 前面の辺は底面ポリゴンの最初の辺(頂点0 → 頂点1)とする(塔屋の向き決定 §12.3 と同じ規約)。
    /// - 原点は地面(ローカルY=0)かつ壁のアライメント面(前面の辺の直線上)。
    ///   壁パーツと同じ場所・同じ回転規約(辺方向へ向けてY軸180°反転。GenerateWallSegments参照)で
    ///   配置するため、壁パーツと同じ座標系で著作すれば壁面に重なる。
    /// - スケールは一切行わない(実寸)。庇や階段が階高・辺長に引き伸ばされないため。
    /// </summary>
    public static void GenerateEntrance(string prefix, int suffix, Vector2[] footprint, List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        if (footprint == null || footprint.Length < 3)
        {
            return;
        }

        GameObject entranceModule = BuildingMeshUtils.GetPartNoPosition(prefix, "entrance", suffix, true);
        if (entranceModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[BMB] エントランス: パーツなし ({prefix}/{suffix}/entrance)");
            return;
        }

        MeshFilter mf = entranceModule.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        Vector2 p0 = footprint[0];
        Vector2 p1 = footprint[1];
        Vector2 edge = p1 - p0;
        if (edge.sqrMagnitude < 1e-6f) return;

        Vector3 position3D = new Vector3((p0.x + p1.x) * 0.5f, 0f, (p0.y + p1.y) * 0.5f);

        // 壁系パーツと同じ回転規約: 辺方向へ向けたうえでY軸まわりに180°反転する
        float angle = Mathf.Atan2(edge.y, edge.x) * Mathf.Rad2Deg;
        Quaternion rotation = Quaternion.Euler(0, -angle + 180f, 0);

        Mesh meshInstance = Object.Instantiate(mf.sharedMesh);
        Vector3[] vertices = meshInstance.vertices;
        Matrix4x4 matrix = Matrix4x4.TRS(position3D, rotation, Vector3.one);
        for (int v = 0; v < vertices.Length; v++)
        {
            vertices[v] = matrix.MultiplyPoint3x4(vertices[v]);
        }
        meshInstance.vertices = vertices;
        meshInstance.RecalculateNormals();
        meshInstance.RecalculateBounds();

        int subMeshCount = meshInstance.subMeshCount;
        Material[] moduleMaterials = BuildingMeshUtils.GetModuleMaterials(entranceModule);
        for (int si = 0; si < subMeshCount; si++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = meshInstance,
                subMeshIndex = si,
                transform = Matrix4x4.identity
            });

            Material mat = null;
            if (moduleMaterials != null && moduleMaterials.Length > 0)
            {
                mat = moduleMaterials[Mathf.Min(si, moduleMaterials.Length - 1)];
            }
            allMaterials.Add(mat);
        }
        temporaryMeshes.Add(meshInstance);

        UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[BMB] エントランス: 配置成功 ({prefix}/{suffix}/entrance)");
    }

    /// <summary>
    /// 底面形状と同じ形状の水平ポリゴンを指定高さに生成する。
    /// 屋上パネルや地面パネルの生成に使用する。
    /// </summary>
    public static Mesh GenerateHorizontalPolygonMesh(Vector2[] footprint, float height)
    {
        if (footprint == null || footprint.Length < 3)
        {
            return null;
        }

        Mesh mesh = new Mesh();
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        for (int i = 1; i < footprint.Length - 1; i++)
        {
            vertices.Add(new Vector3(footprint[0].x, height, footprint[0].y));
            vertices.Add(new Vector3(footprint[i].x, height, footprint[i].y));
            vertices.Add(new Vector3(footprint[i + 1].x, height, footprint[i + 1].y));

            triangles.Add(vertices.Count - 3);
            triangles.Add(vertices.Count - 1);
            triangles.Add(vertices.Count - 2);
        }

        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private const float TriangulationEpsilon = 1e-5f;

    private static void AddHorizontalSurface(List<Vector3> vertices, List<int> triangles, Vector2[] footprint, List<int> triangleIndices, float height, bool invert)
    {
        if (footprint == null || triangleIndices == null || triangleIndices.Count < 3)
        {
            return;
        }

        for (int i = 0; i < triangleIndices.Count; i += 3)
        {
            int indexStart = vertices.Count;
            for (int k = 0; k < 3; k++)
            {
                Vector2 point = footprint[triangleIndices[i + k]];
                vertices.Add(new Vector3(point.x, height, point.y));
            }

            if (invert)
            {
                triangles.Add(indexStart);
                triangles.Add(indexStart + 2);
                triangles.Add(indexStart + 1);
            }
            else
            {
                triangles.Add(indexStart);
                triangles.Add(indexStart + 1);
                triangles.Add(indexStart + 2);
            }
        }
    }

    private static List<int> TriangulatePolygon(Vector2[] polygon)
    {
        List<int> triangles = new List<int>();
        if (polygon == null || polygon.Length < 3)
        {
            return triangles;
        }

        List<int> vertexIndices = new List<int>(polygon.Length);
        for (int i = 0; i < polygon.Length; i++)
        {
            vertexIndices.Add(i);
        }

        int iterationLimit = polygon.Length * polygon.Length;
        int iterations = 0;

        while (vertexIndices.Count > 3 && iterations < iterationLimit)
        {
            bool earFound = false;
            for (int i = 0; i < vertexIndices.Count; i++)
            {
                int prev = vertexIndices[(i - 1 + vertexIndices.Count) % vertexIndices.Count];
                int curr = vertexIndices[i];
                int next = vertexIndices[(i + 1) % vertexIndices.Count];

                if (!IsTriangleConvex(polygon[prev], polygon[curr], polygon[next]))
                {
                    continue;
                }

                bool containsVertex = false;
                for (int j = 0; j < vertexIndices.Count; j++)
                {
                    int candidate = vertexIndices[j];
                    if (candidate == prev || candidate == curr || candidate == next)
                    {
                        continue;
                    }

                    if (IsPointInTriangle(polygon[candidate], polygon[prev], polygon[curr], polygon[next]))
                    {
                        containsVertex = true;
                        break;
                    }
                }

                if (containsVertex)
                {
                    continue;
                }

                triangles.Add(prev);
                triangles.Add(curr);
                triangles.Add(next);
                vertexIndices.RemoveAt(i);
                earFound = true;
                break;
            }

            if (!earFound)
            {
                Debug.LogWarning("BuildingMeshBuilder: 凹多角形の耳切り処理で耳が見つかりませんでした。ファン型の三角形にフォールバックします。");
                return new List<int>();
            }

            iterations++;
        }

        if (vertexIndices.Count == 3)
        {
            triangles.Add(vertexIndices[0]);
            triangles.Add(vertexIndices[1]);
            triangles.Add(vertexIndices[2]);
        }

        return triangles;
    }

    /// <summary>
    /// ポリゴンを三角形分割し、頂点インデックス列を返す公開ヘルパー。
    /// 耳切り法（ear clipping）で分割し、失敗した場合はファン型にフォールバックする。
    /// 凹ポリゴン（L字の岸壁など）でも破綻しない分割が必要な箇所から再利用する。
    /// 入力は CCW に正規化済みであることを前提とする。
    /// </summary>
    public static List<int> TriangulateOrFan(Vector2[] polygon)
    {
        if (polygon == null || polygon.Length < 3)
        {
            return new List<int>();
        }

        List<int> triangles = TriangulatePolygon(polygon);
        if (triangles.Count == 0)
        {
            triangles = GenerateTriangleFanIndices(polygon.Length);
        }
        return triangles;
    }

    /// <summary>細分化の三角形数の安全上限。巨大区画でのメモリ・処理時間の暴走を防ぐ。</summary>
    public const int MAX_SUBDIVIDED_TRIANGLES = 20000;

    /// <summary>
    /// 三角形群を細分化し、最長辺が maxEdge を超える三角形を最長辺の中点で再帰分割する。
    /// 中点は無向の辺キーで溶接するため、辺を共有する隣接三角形が同じ中点を使い、
    /// T字接合による隙間ができない。生成した中点頂点は verts に追記される
    /// （呼び出し側は verts を頂点配列としてそのまま使う）。
    /// 上限 MAX_SUBDIVIDED_TRIANGLES に達した時点で残りは分割せずに採用する（穴は開かない）。
    /// 起伏地形へ追従させる面メッシュ（地面パネル・土マウンド）で内部頂点を作るために使う。
    /// </summary>
    public static List<(int a, int b, int c)> SubdivideTrianglesByEdgeLength(
        List<Vector2> verts, List<int> baseTriangles, float maxEdge)
    {
        var finalTriangles = new List<(int a, int b, int c)>();
        if (verts == null || baseTriangles == null || baseTriangles.Count < 3)
        {
            return finalTriangles;
        }

        var midpointCache = new Dictionary<long, int>();
        var work = new Stack<(int a, int b, int c)>();
        for (int i = 0; i + 2 < baseTriangles.Count; i += 3)
        {
            work.Push((baseTriangles[i], baseTriangles[i + 1], baseTriangles[i + 2]));
        }

        while (work.Count > 0 && finalTriangles.Count + work.Count < MAX_SUBDIVIDED_TRIANGLES)
        {
            var (a, b, c) = work.Pop();
            float ab = Vector2.Distance(verts[a], verts[b]);
            float bc = Vector2.Distance(verts[b], verts[c]);
            float ca = Vector2.Distance(verts[c], verts[a]);
            float longest = Mathf.Max(ab, Mathf.Max(bc, ca));

            if (longest <= maxEdge)
            {
                finalTriangles.Add((a, b, c));
                continue;
            }

            // 最長辺を中点で分割（向きは元三角形を維持する）
            if (longest == ab)
            {
                int m = GetOrCreateMidpoint(a, b, verts, midpointCache);
                work.Push((a, m, c));
                work.Push((m, b, c));
            }
            else if (longest == bc)
            {
                int m = GetOrCreateMidpoint(b, c, verts, midpointCache);
                work.Push((a, b, m));
                work.Push((a, m, c));
            }
            else
            {
                int m = GetOrCreateMidpoint(c, a, verts, midpointCache);
                work.Push((a, b, m));
                work.Push((m, b, c));
            }
        }
        // 上限到達で work に残った三角形はそのまま採用する（細分化されないだけで穴は開かない）
        while (work.Count > 0)
        {
            finalTriangles.Add(work.Pop());
        }

        return finalTriangles;
    }

    /// <summary>有向辺(a→b)を一意に表すキー。細分化・スカート境界判定で共有する。</summary>
    public static long EdgeKey(int a, int b) => ((long)a << 32) | (uint)b;

    /// <summary>
    /// 辺(a-b)の中点頂点のインデックスを返す。同じ辺を共有する隣接三角形が同じ中点を
    /// 使うよう、無向の辺キーでキャッシュする（T字接合による隙間の防止）。
    /// </summary>
    private static int GetOrCreateMidpoint(int a, int b, List<Vector2> verts, Dictionary<long, int> cache)
    {
        long key = a < b ? EdgeKey(a, b) : EdgeKey(b, a);
        if (cache.TryGetValue(key, out int existing))
        {
            return existing;
        }
        int index = verts.Count;
        verts.Add((verts[a] + verts[b]) * 0.5f);
        cache.Add(key, index);
        return index;
    }

    private static List<int> GenerateTriangleFanIndices(int vertexCount)
    {
        List<int> triangles = new List<int>();
        for (int i = 1; i < vertexCount - 1; i++)
        {
            triangles.Add(0);
            triangles.Add(i);
            triangles.Add(i + 1);
        }
        return triangles;
    }

    private static bool IsTriangleConvex(Vector2 prev, Vector2 current, Vector2 next)
    {
        return Cross(prev, current, next) > TriangulationEpsilon;
    }

    private static bool IsPointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        if (point == a || point == b || point == c)
        {
            return false;
        }

        float cross1 = Cross(a, b, point);
        float cross2 = Cross(b, c, point);
        float cross3 = Cross(c, a, point);

        bool hasNegative = cross1 < -TriangulationEpsilon || cross2 < -TriangulationEpsilon || cross3 < -TriangulationEpsilon;
        bool hasPositive = cross1 > TriangulationEpsilon || cross2 > TriangulationEpsilon || cross3 > TriangulationEpsilon;

        return !(hasNegative && hasPositive);
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 ab = b - a;
        Vector2 ac = c - a;
        return ab.x * ac.y - ab.y * ac.x;
    }

    private static float SignedArea(Vector2[] polygon)
    {
        float area = 0f;
        for (int i = 0; i < polygon.Length; i++)
        {
            int j = (i + 1) % polygon.Length;
            area += polygon[i].x * polygon[j].y - polygon[j].x * polygon[i].y;
        }
        return area * 0.5f;
    }

    /// <summary>
    /// 傾斜地対応（区画基準標高への接地 + 底面フットプリントを押し出したコンクリート基盤の生成）の
    /// 対象となる model_type かどうかを返す。
    ///
    /// - 対象: MODEL_MODULAR_FLAT / HOUSE / GABLE / SHED。建物は parcel.altitude（接道の路面高さ）の
    ///   水平面に接地し、傾斜地の谷側の隙間は基盤（GenerateFoundationMesh）が埋める。
    /// - 対象外: MODEL_MODULAR_GROUND（畑・庭は地形に沿う地面表現）、MODEL_PREBUILT、
    ///   MODEL_PORT（岸壁は独自の海底追従スラブを持つ）、MODEL_MASHUP（各サブ建物が
    ///   自分の配置座標の地形標高に個別に接地する）。これらは建物中心位置の地形標高に
    ///   直接接地し、基盤も生成しない。
    /// </summary>
    public static bool IsApplicableModelType(int modelType)
    {
        return modelType == Constants.MODEL_MODULAR_FLAT ||
               modelType == Constants.MODEL_MODULAR_HOUSE ||
               modelType == Constants.MODEL_MODULAR_GABLE ||
               modelType == Constants.MODEL_MODULAR_SHED;
    }

    private static Material cachedConstructionDirtMaterial;

    /// <summary>
    /// 工事現場の土マテリアル(Resources/ConstructionDirt)を返す。
    /// アセットが未配置の場合は、従来の建設中マテリアル(Resources/ConstructionMaterial・黄色)
    /// → デフォルト建物マテリアルの順にフォールバックし、表示が成立するようにする。
    /// 建物の土マウンドとパスの土ストリップの両方から参照される。
    /// </summary>
    public static Material GetConstructionDirtMaterial()
    {
        if (cachedConstructionDirtMaterial != null) return cachedConstructionDirtMaterial;

        cachedConstructionDirtMaterial = Resources.Load<Material>("ConstructionDirt");
        if (cachedConstructionDirtMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/ConstructionDirt が見つかりません。ConstructionMaterial にフォールバックします。");
            cachedConstructionDirtMaterial = Resources.Load<Material>("ConstructionMaterial");
        }
        if (cachedConstructionDirtMaterial == null)
        {
            cachedConstructionDirtMaterial = GetDefaultBuildingMaterial();
        }
        return cachedConstructionDirtMaterial;
    }

    private static Material cachedCollapsedRubbleMaterial;

    /// <summary>
    /// 倒壊(STATUS_COLLAPSED)の瓦礫マテリアル(Resources/CollapsedRubble)を返す。
    /// アセットが未配置の場合は、工事現場の土マテリアル(GetConstructionDirtMaterial・
    /// さらにその先のフォールバック込み)へ委譲し、表示が成立するようにする。
    /// </summary>
    public static Material GetCollapsedRubbleMaterial()
    {
        if (cachedCollapsedRubbleMaterial != null) return cachedCollapsedRubbleMaterial;

        cachedCollapsedRubbleMaterial = Resources.Load<Material>("CollapsedRubble");
        if (cachedCollapsedRubbleMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/CollapsedRubble が見つかりません。工事現場の土マテリアルにフォールバックします。");
            cachedCollapsedRubbleMaterial = GetConstructionDirtMaterial();
        }
        return cachedCollapsedRubbleMaterial;
    }

    private static Material cachedBurnedRubbleMaterial;

    /// <summary>
    /// 焼失(status_reason == REASON_FIRE)の焦げた瓦礫マテリアル(Resources/CollapsedRubbleBurned)を返す。
    /// アセットが未配置の場合は通常の瓦礫マテリアル(GetCollapsedRubbleMaterial・
    /// さらにその先のフォールバック込み)へ委譲し、表示が成立するようにする。
    /// </summary>
    public static Material GetBurnedRubbleMaterial()
    {
        if (cachedBurnedRubbleMaterial != null) return cachedBurnedRubbleMaterial;

        cachedBurnedRubbleMaterial = Resources.Load<Material>("CollapsedRubbleBurned");
        if (cachedBurnedRubbleMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/CollapsedRubbleBurned が見つかりません。通常の瓦礫マテリアルにフォールバックします。");
            cachedBurnedRubbleMaterial = GetCollapsedRubbleMaterial();
        }
        return cachedBurnedRubbleMaterial;
    }

    private static Material cachedCommonConcreteMaterial;

    /// <summary>
    /// 基盤（GenerateFoundationMesh）用の全建物共通コンクリートマテリアル
    /// (Resources/COMMON_CONCRETE) を返す。アセットが未配置の場合は警告を出して
    /// デフォルト建物マテリアルにフォールバックする（描画は破綻させない）。
    /// </summary>
    public static Material GetCommonConcreteMaterial()
    {
        if (cachedCommonConcreteMaterial != null) return cachedCommonConcreteMaterial;
        cachedCommonConcreteMaterial = Resources.Load<Material>("COMMON_CONCRETE");
        if (cachedCommonConcreteMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/COMMON_CONCRETE が見つかりません。デフォルト建物マテリアルにフォールバックします。");
            cachedCommonConcreteMaterial = GetDefaultBuildingMaterial();
        }
        return cachedCommonConcreteMaterial;
    }

    /// <summary>
    /// 建物底面（footprint）をY方向に押し出したコンクリート基盤メッシュを生成する（建物ローカル座標）。
    ///
    /// 傾斜地対応の対象建物（IsApplicableModelType）は土地区画の基準標高（parcel.altitude =
    /// 接道の路面高さ）の水平面に接地するため、谷側では底面と地形の間に隙間ができて建物が
    /// 浮いて見える。この隙間を埋めるため、上面 = 建物の接地面（ローカルY=0）、下面 = 底面頂点で
    /// サンプリングした最低地形標高の5m下（最低でも接地面の10m下）までの柱状メッシュを張る。
    /// 頂点間で地形が局所的に落ち込んでも突き抜けないよう、下面には余裕を持たせている。
    ///
    /// - footprint は center_point 基準のローカルXZ座標・CCW（BuildingMeshUtils.GetFootprint と同じ規約）。
    /// - 対象 model_type かどうかの判定は呼び出し側の責務。
    /// - マテリアルは全建物共通の GetCommonConcreteMaterial()（工事中は土マテリアル）を割り当てる想定で、
    ///   UVは上面・側面ともワールドスケール（10mで1タイル）のタイリング。
    /// </summary>
    public static Mesh GenerateFoundationMesh(Vector2[] footprint, Vector3 centerPointWorld)
    {
        if (footprint == null || footprint.Length < 3) return null;

        float altitude = centerPointWorld.y;
        int n = footprint.Length;

        // 底面頂点位置の地形標高の最低値。基盤の下端はここからさらに5m下げ、
        // 頂点間のサンプリング漏れによる「地形との隙間」を防ぐ。
        float minTerrainHeight = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            float h = HeightmapTextureManager.GetTerrainHeight(
                new Vector3(footprint[i].x + centerPointWorld.x, 1000f, footprint[i].y + centerPointWorld.z));
            if (h < minTerrainHeight)
            {
                minTerrainHeight = h;
            }
        }
        if (minTerrainHeight == float.MaxValue) minTerrainHeight = altitude - 5f;

        float topLocalY = 0f;
        float bottomLocalY = Mathf.Min(minTerrainHeight - 5f - altitude, -10f);

        List<int> polygonTriangles = TriangulateOrFan(footprint);
        if (polygonTriangles.Count < 3) return null;

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();

        // 上面。CCWポリゴンをXZ平面にそのまま貼ると法線が下向きになるため、巻き順を反転して上向きにする。
        // 地面パネル（GROUND_OFFSET=0.005m 浮き）のわずかに下になるため、Zファイティングしない。
        int topStart = vertices.Count;
        for (int j = 0; j < n; j++)
        {
            vertices.Add(new Vector3(footprint[j].x, topLocalY, footprint[j].y));
            uvs.Add(new Vector2(footprint[j].x * 0.1f, footprint[j].y * 0.1f));
        }
        for (int i = 0; i < polygonTriangles.Count; i += 3)
        {
            triangles.Add(topStart + polygonTriangles[i]);
            triangles.Add(topStart + polygonTriangles[i + 2]);
            triangles.Add(topStart + polygonTriangles[i + 1]);
        }

        // 下面（地中に埋まるが、水密メッシュとして閉じておく）
        int bottomStart = vertices.Count;
        for (int j = 0; j < n; j++)
        {
            vertices.Add(new Vector3(footprint[j].x, bottomLocalY, footprint[j].y));
            uvs.Add(new Vector2(footprint[j].x * 0.1f, footprint[j].y * 0.1f));
        }
        for (int i = 0; i < polygonTriangles.Count; i += 3)
        {
            triangles.Add(bottomStart + polygonTriangles[i]);
            triangles.Add(bottomStart + polygonTriangles[i + 1]);
            triangles.Add(bottomStart + polygonTriangles[i + 2]);
        }

        // 側面。UVのUは外周に沿った累積距離で、辺をまたいでもタイリングが連続する。
        float accumulateDistance = 0f;
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            float dist = Vector2.Distance(footprint[i], footprint[next]);

            int sideStart = vertices.Count;
            vertices.Add(new Vector3(footprint[i].x, bottomLocalY, footprint[i].y));
            vertices.Add(new Vector3(footprint[next].x, bottomLocalY, footprint[next].y));
            vertices.Add(new Vector3(footprint[i].x, topLocalY, footprint[i].y));
            vertices.Add(new Vector3(footprint[next].x, topLocalY, footprint[next].y));

            float uStart = accumulateDistance * 0.1f;
            float uEnd = (accumulateDistance + dist) * 0.1f;
            float vBottom = bottomLocalY * 0.1f;
            float vTop = topLocalY * 0.1f;

            uvs.Add(new Vector2(uStart, vBottom));
            uvs.Add(new Vector2(uEnd, vBottom));
            uvs.Add(new Vector2(uStart, vTop));
            uvs.Add(new Vector2(uEnd, vTop));

            triangles.Add(sideStart + 0);
            triangles.Add(sideStart + 2);
            triangles.Add(sideStart + 1);

            triangles.Add(sideStart + 1);
            triangles.Add(sideStart + 2);
            triangles.Add(sideStart + 3);

            accumulateDistance += dist;
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
