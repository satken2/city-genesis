// Assets/Scripts/MeshGenerators/Buildings/BuildingMeshBuilder.cs
using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Entry point and utility class for building mesh generation.
/// Delegates to the appropriate builder (Modular / Prebuilt / Default) based on model_type.
/// Also provides shared operations such as mesh combining, simple buildings, colliders, and rooftop props.
/// </summary>
public class BuildingMeshBuilder : IBuildingMeshGenerator
{
    private static readonly IBuildingMeshGenerator modularGenerator = new ModularBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator prebuiltGenerator = new PreBuiltBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator portGenerator = new PortBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator mashupGenerator = new MashupBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator openPitGenerator = new MashupBuildingMeshBuilder(openPit: true);
    private static readonly IBuildingMeshGenerator railYardGenerator = new RailYardBuildingMeshBuilder();
    private static readonly IBuildingMeshGenerator openSpaceGenerator = new OpenSpaceBuildingMeshBuilder();
    private static readonly ConstructingBuildingMeshBuilder constructingGenerator = new ConstructingBuildingMeshBuilder(ConstructionSiteProfile.Construction);
    private static readonly IBuildingMeshGenerator demolishingGenerator = new ConstructingBuildingMeshBuilder(ConstructionSiteProfile.Demolition);
    private static readonly IBuildingMeshGenerator collapsedGenerator = new CollapsedBuildingMeshBuilder();

    // Profiler markers for building display stages share the "Building." prefix, so one Profiler search shows the whole breakdown.
    private static readonly StageMarker generateMarker = new StageMarker("Building.Generate");
    private static readonly StageMarker modularMarker = new StageMarker("Building.Gen.Modular");
    private static readonly StageMarker prebuiltMarker = new StageMarker("Building.Gen.Prebuilt");
    private static readonly StageMarker portMarker = new StageMarker("Building.Gen.Port");
    private static readonly StageMarker mashupMarker = new StageMarker("Building.Gen.Mashup");
    private static readonly StageMarker openPitMarker = new StageMarker("Building.Gen.OpenPit");
    private static readonly StageMarker railYardMarker = new StageMarker("Building.Gen.RailYard");
    private static readonly StageMarker openSpaceMarker = new StageMarker("Building.Gen.OpenSpace");
    private static readonly StageMarker constructingMarker = new StageMarker("Building.Gen.Constructing");
    private static readonly StageMarker demolishingMarker = new StageMarker("Building.Gen.Demolishing");
    private static readonly StageMarker collapsedMarker = new StageMarker("Building.Gen.Collapsed");
    private static readonly StageMarker farMarker = new StageMarker("Building.Gen.Far");
    private static readonly StageMarker foundationMarker = new StageMarker("Building.Foundation");
    private static readonly StageMarker roofpropMarker = new StageMarker("Building.Roofprop");
    private static readonly StageMarker entranceMarker = new StageMarker("Building.Entrance");
    private static readonly StageMarker colliderMarker = new StageMarker("Building.ColliderMesh");
    private static readonly StageMarker mergeSubMeshesMarker = new StageMarker("Building.MergeSubMeshes");

    /// <summary>
    /// Generate a building mesh from BuildingSummary.
    /// Choose the generation method based on model_type.
    /// When isDynamic is true, modular/prebuilt fences and tiles follow the terrain (ground panels only at close-up).
    /// </summary>
    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        return Generate(buildingData, isDynamic, DrawLod.Normal);
    }

    /// <summary>
    /// Generate with a rendering LOD. Far stretches a distant model to the outline or uses a box (<see cref="GenerateFarBuilding"/>);
    /// Close uses the same assembly but prefers `_close` parts (BuildingMeshUtils.CurrentDrawLod).
    /// isDynamic is a compatibility argument for terrain conformity, independent of rendering LOD.
    /// animate is for shown buildings: the crane top (CraneTop) and the model's moving parts (Motion) are returned
    /// instead of being combined, and the caller must attach them before initializing the visual mode.
    /// separateExterior is for shown buildings: ground panels (for every model type) and other site decorations (where applicable)
    /// become ExteriorJobs, and scatter parts become Scatter for <see cref="BuildingScatterRenderer"/> instead of being combined.
    /// </summary>
    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic, DrawLod drawLod, bool separateExterior = false, bool animate = false)
    {
        using var scope = generateMarker.Auto();
        if (buildingData == null)
        {
            Debug.LogError("BuildingMeshBuilder.Generate: BuildingSummary が null です。");
            return null;
        }
        if (drawLod == DrawLod.Far || drawLod == DrawLod.Hidden)
        {
            return EnsureMaterials(GenerateFarBuilding(buildingData));
        }

        DrawLod previous = BuildingMeshUtils.CurrentDrawLod;
        var previousCapture = BuildingExteriorRenderer.Capture;
        bool previousDefer = BuildingExteriorRenderer.DeferSiteDecorations;
        var previousSink = BuildingScatterRenderer.Sink;
        var previousMotion = BuildingMotionParts.Sink;
        int modelType = GetModelType(buildingData);
        var jobs = separateExterior ? new List<BuildingExteriorRenderer.Build>() : null;
        var scatter = separateExterior ? new List<BuildingScatterRenderer.Instance>() : null;
        var motion = animate ? new BuildingMotionParts() : null;
        BuildingMeshUtils.CurrentDrawLod = drawLod;
        BuildingExteriorRenderer.Capture = jobs;
        BuildingExteriorRenderer.DeferSiteDecorations = separateExterior && (IsApplicableModelType(modelType) || modelType == Constants.MODEL_PREBUILT);
        BuildingScatterRenderer.Sink = scatter;
        BuildingMotionParts.Sink = motion;
        try
        {
            var result = GenerateDetailed(buildingData, isDynamic, animate);
            if (result != null)
            {
                result.ExteriorJobs = jobs;
                result.Scatter = scatter;
                result.Motion = motion != null && !motion.IsEmpty ? motion : null;
            }
            return result;
        }
        finally
        {
            BuildingMeshUtils.CurrentDrawLod = previous;
            BuildingExteriorRenderer.Capture = previousCapture;
            BuildingExteriorRenderer.DeferSiteDecorations = previousDefer;
            BuildingScatterRenderer.Sink = previousSink;
            BuildingMotionParts.Sink = previousMotion;
        }
    }

    /// <summary>
    /// Building mesh for distant (Far) rendering. Regardless of building type, stretch a distant model to the outline (<see cref="FarBuildingMeshBuilder"/>);
    /// if unavailable, extrude the footprint into a box with height = floor count × floor height.
    /// Distant buildings are the most numerous, so avoid full-detail assembly followed by material replacement.
    /// During construction/demolition, use a one-floor box instead of a distant model so the unfinished state remains visible from afar;
    /// for collapsed buildings, use rubble height (0.5 floors).
    /// </summary>
    public static BuildingMeshResult GenerateFarBuilding(BuildingSummary buildingData)
    {
        using var scope = farMarker.Auto();
        int status = buildingData.status;
        if (status != Constants.STATUS_CONSTRUCTING && status != Constants.STATUS_DEMOLISHING && status != Constants.STATUS_COLLAPSED)
        {
            BuildingMeshResult far = FarBuildingMeshBuilder.Generate(buildingData);
            if (far != null) return far;
        }
        return GenerateSimpleBuildingWithHeight(BuildingMeshUtils.GetFootprint(buildingData),
            GetFarBoxHeight(buildingData.variant, buildingData.landmark, buildingData.floors ?? 1, status));
    }

    /// <summary>Distant box height: one floor during construction/demolition, rubble height (0.5 floors) when collapsed, otherwise floor count × floor height.</summary>
    public static float GetFarBoxHeight(int variant, int landmark, int floors, int status)
    {
        float floorHeight = BuildingMeshUtils.GetFloorHeight(variant, landmark);
        if (status == Constants.STATUS_CONSTRUCTING || status == Constants.STATUS_DEMOLISHING) return floorHeight;
        if (status == Constants.STATUS_COLLAPSED) return floorHeight * 0.5f;
        return Mathf.Max(1, floors) * floorHeight;
    }

    public static bool IsOpenSpaceModel(int modelType) => modelType == Constants.MODEL_PARKING
        || modelType == Constants.MODEL_ROTARY || modelType == Constants.MODEL_PARK;

    private BuildingMeshResult GenerateDetailed(BuildingSummary buildingData, bool isDynamic, bool animateCrane)
    {

        // Construction/demolition/ruins use dedicated meshes regardless of model_type. Checking status before
        // branching on model_type covers every generation route: Near/Far, DetailWorker, and direct calls.
        // - Constructing (STATUS_CONSTRUCTING): site dirt + scaffolding + crane.
        // - Demolishing (STATUS_DEMOLISHING): site dirt + scaffolding with sound barriers + rooftop excavator (same builder as construction; height decreases with progress).
        // - Ruins (STATUS_COLLAPSED): rubble mound + optional collapsed fragments.
        //   Fire damage (status_reason == REASON_FIRE) uses a charred rubble material (selected within the builder).
        if (buildingData.status == Constants.STATUS_CONSTRUCTING)
        {
            using (constructingMarker.Auto())
                return EnsureMaterials(constructingGenerator.Generate(buildingData, isDynamic, animateCrane));
        }
        if (buildingData.status == Constants.STATUS_DEMOLISHING)
        {
            using (demolishingMarker.Auto())
                return EnsureMaterials(demolishingGenerator.Generate(buildingData, isDynamic));
        }
        if (buildingData.status == Constants.STATUS_COLLAPSED)
        {
            using (collapsedMarker.Auto())
                return EnsureMaterials(collapsedGenerator.Generate(buildingData, isDynamic));
        }

        int modelType = GetModelType(buildingData);

        BuildingMeshResult result;
        switch (modelType)
        {
            case Constants.MODEL_PARKING:
            case Constants.MODEL_ROTARY:
            case Constants.MODEL_PARK:
                using (openSpaceMarker.Auto())
                    result = openSpaceGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_MODULAR_FLAT:
            case Constants.MODEL_MODULAR_HOUSE:
            case Constants.MODEL_MODULAR_GABLE:
            case Constants.MODEL_MODULAR_SHED:
            case Constants.MODEL_MODULAR_GROUND:
                using (modularMarker.Auto())
                    result = modularGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_PREBUILT:
                using (prebuiltMarker.Auto())
                    result = prebuiltGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_PORT:
                using (portMarker.Auto())
                    result = portGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_MASHUP:
                using (mashupMarker.Auto())
                    result = mashupGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_OPEN_PIT:
                using (openPitMarker.Auto())
                    result = openPitGenerator.Generate(buildingData, isDynamic);
                break;
            case Constants.MODEL_RAIL_YARD:
                using (railYardMarker.Auto())
                    result = railYardGenerator.Generate(buildingData, isDynamic);
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
    /// Get the building's model_type from MasterDataStore.
    /// Default to MODEL_MODULAR_FLAT if unavailable.
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
    /// Merge submeshes with the same material to minimize draw calls.
    /// </summary>
    public static BuildingMeshResult MergeSubMeshes(BuildingMeshResult meshResult)
    {
        using var scope = mergeSubMeshesMarker.Auto();
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

        Dictionary<EntityId, int> materialKeyToIndex = new Dictionary<EntityId, int>();
        List<Material> mergedMaterials = new List<Material>();
        List<List<int>> mergedTriangles = new List<List<int>>();

        for (int subMeshIndex = 0; subMeshIndex < originalSubMeshCount; subMeshIndex++)
        {
            Material material = (materials != null && subMeshIndex < materials.Length)
                ? materials[subMeshIndex]
                : null;

            EntityId materialKey = material != null ? material.GetEntityId() : EntityId.None;
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

        return new BuildingMeshResult(mesh, finalMaterials.ToArray(), meshResult.ColliderMesh)
        { ExteriorJobs = meshResult.ExteriorJobs, Scatter = meshResult.Scatter, CraneTop = meshResult.CraneTop };
    }

    private static Material cachedDefaultBuildingMaterial;

    // Shared materials are resolved by name and may come from an asset pack. Reload them when switching cities.
    static BuildingMeshBuilder() => AssetPackStore.CityChanging += ForgetNamedMaterials;

    private static void ForgetNamedMaterials()
    {
        cachedDefaultBuildingMaterial = null;
        cachedConstructionDirtMaterial = null;
        cachedCollapsedRubbleMaterial = null;
        cachedBurnedRubbleMaterial = null;
        cachedCommonConcreteMaterial = null;
    }

    /// <summary>
    /// Load and return the default building material (BuildingMaterial, from the asset pack if present).
    /// Cached; loaded only on the first call.
    /// </summary>
    public static Material GetDefaultBuildingMaterial()
    {
        if (cachedDefaultBuildingMaterial != null)
        {
            return cachedDefaultBuildingMaterial;
        }

        cachedDefaultBuildingMaterial = AssetPackStore.LoadNamedMaterial("BuildingMaterial");
        if (cachedDefaultBuildingMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/BuildingMaterial が見つかりません。マテリアルの共有に失敗します。");
        }

        return cachedDefaultBuildingMaterial;
    }

    /// <summary>
    /// Validate BuildingMeshResult's material array and assign the default material if null or empty.
    /// Also replace null elements in the array with the default material.
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

        // Assign one default material if Materials is null or empty.
        if (result.Materials == null || result.Materials.Length == 0)
        {
            return new BuildingMeshResult(result.Mesh, new[] { fallback }, result.ColliderMesh)
            { ExteriorJobs = result.ExteriorJobs, Scatter = result.Scatter, CraneTop = result.CraneTop };
        }

        // Check for null elements in the array.
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

        return new BuildingMeshResult(result.Mesh, resolved, result.ColliderMesh)
        { ExteriorJobs = result.ExteriorJobs, Scatter = result.Scatter, CraneTop = result.CraneTop };
    }

    /// <summary>
    /// Fallback when no GLB module is available (a simple cuboid building).
    /// </summary>
    public static BuildingMeshResult GenerateSimpleBuilding(Vector2[] footprint, int floors, float floorHeight)
    {
        return GenerateSimpleBuildingWithHeight(footprint, floors * floorHeight);
    }

    /// <summary>Simple building with an explicit height (distant box).</summary>
    public static BuildingMeshResult GenerateSimpleBuildingWithHeight(Vector2[] footprint, float buildingHeight)
    {
        if (footprint == null || footprint.Length < 3)
        {
            Debug.LogWarning($"BuildingMeshBuilder.GenerateSimpleBuilding: footprint が不正です（頂点数 < 3）。");
            return new BuildingMeshResult(new Mesh(), null);
        }

        Vector2[] normalizedFootprint = (Vector2[])footprint.Clone();

        if (PolygonUtils.SignedArea(normalizedFootprint) < 0f)
        {
            System.Array.Reverse(normalizedFootprint);
        }

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        AppendExtrudedFootprint(normalizedFootprint, buildingHeight, Vector3.zero, vertices, triangles);

        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        Mesh colliderMesh = GenerateColliderMesh(normalizedFootprint, buildingHeight);

        return new BuildingMeshResult(mesh, null, colliderMesh);
    }

    /// <summary>
    /// Add a box extruded from a counterclockwise footprint (bottom, top, and sides) to the vertex/triangle lists, shifted by offset.
    /// Shared by distant boxes and the combined citywide ultra-far mesh (CityOverviewManager).
    /// </summary>
    public static void AppendExtrudedFootprint(Vector2[] footprint, float buildingHeight, Vector3 offset, List<Vector3> vertices, List<int> triangles)
    {
        int first = vertices.Count;
        List<int> polygonTriangles = TriangulateOrFan(footprint);

        AddHorizontalSurface(vertices, triangles, footprint, polygonTriangles, 0f, false);
        AddHorizontalSurface(vertices, triangles, footprint, polygonTriangles, buildingHeight, true);

        int n = footprint.Length;

        // Sides.
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            Vector3 v0 = new Vector3(footprint[i].x, 0, footprint[i].y);
            Vector3 v1 = new Vector3(footprint[next].x, 0, footprint[next].y);
            Vector3 v2 = new Vector3(footprint[i].x, buildingHeight, footprint[i].y);
            Vector3 v3 = new Vector3(footprint[next].x, buildingHeight, footprint[next].y);

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

        if (offset == Vector3.zero) return;
        for (int i = first; i < vertices.Count; i++) vertices[i] += offset;
    }

    /// <summary>
    /// Generate a collider mesh by extruding the footprint polygon.
    /// </summary>
    public static Mesh GenerateColliderMesh(Vector2[] footprint, float buildingHeight)
    {
        using var scope = colliderMarker.Auto();
        if (footprint == null || footprint.Length < 3)
        {
            return null;
        }

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        int n = footprint.Length;

        // Bottom (y=0, CCW → downward normal).
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

        // Top (y=buildingHeight, CW → upward normal).
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

        // Sides.
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
    /// Place rooftop props (roofprop).
    /// </summary>
    public static void GenerateRoofprop(string prefix, int suffix, float visual_variant2, Vector2[] footprint, float buildingHeight, List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        using var scope = roofpropMarker.Auto();
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

        // Use the same orientation as prebuilt parts (BuildingRoadAccess.PrebuiltRotation), authored with the origin on the roof surface.
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
        BuildingMotionParts.Collect(roofpropModule, mf.transform, matrix, rotation);

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
    /// Entrance bounds (local coordinates of the mesh placed by GenerateEntrance). Enclose both standard and close-up `_close` parts. False if no standard part exists.
    /// ExteriorStairBuilder uses these bounds for body setback and fit checks, keeping the body outline identical across rendering LODs;
    /// extra forward projections of close-up parts also fit within the original footprint.
    /// </summary>
    public static bool TryGetEntranceBounds(string prefix, int suffix, out Bounds bounds)
    {
        bounds = default;
        if (ModelManager.Instance == null) return false;
        string path = $"Models/Buildings/{prefix}/{suffix}/entrance";
        Mesh plain = EntranceMesh(ModelManager.Instance.GetModel(path, true));
        if (plain == null) return false;
        bounds = plain.bounds;
        Mesh close = EntranceMesh(ModelManager.Instance.GetModel(path + BuildingMeshUtils.CLOSE_SUFFIX, true));
        if (close != null) bounds.Encapsulate(close.bounds);
        return true;
    }

    /// <summary>Mesh to place as the entrance. Use only the first MeshFilter's mesh.</summary>
    static Mesh EntranceMesh(GameObject module)
    {
        MeshFilter mf = module != null ? module.GetComponentInChildren<MeshFilter>() : null;
        return mf != null ? mf.sharedMesh : null;
    }

    /// <summary>
    /// Place an entrance at ground level in the center of the building's front edge.
    /// Place it only if the optional part {prefix}/{suffix}/entrance exists.
    ///
    /// - The front is the footprint polygon's first edge (vertex 0 → vertex 1), following the rooftop-prop orientation convention.
    ///   Pass the body outline with its front edge set back so exterior projections fit within the original occupied outline (ExteriorStairBuilder).
    /// - The origin is at ground level (local Y=0) on the wall alignment plane (the line of the front edge).
    ///   Placement uses the same position and rotation convention as wall parts (face along the edge, then rotate 180° about Y; see GenerateWallSegments),
    ///   so parts authored in the wall-part coordinate system align with the wall.
    /// - Apply no scaling (actual dimensions), so canopies and stairs are not stretched to the floor height or edge length.
    /// </summary>
    public static void GenerateEntrance(string prefix, int suffix, Vector2[] footprint, List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        using var scope = entranceMarker.Auto();
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

        Mesh sourceMesh = EntranceMesh(entranceModule);
        if (sourceMesh == null) return;

        Vector2 p0 = footprint[0];
        Vector2 p1 = footprint[1];
        Vector2 edge = p1 - p0;
        if (edge.sqrMagnitude < 1e-6f) return;

        Vector3 position3D = new Vector3((p0.x + p1.x) * 0.5f, 0f, (p0.y + p1.y) * 0.5f);

        // Same rotation convention as wall parts: face along the edge, then rotate 180° about the Y axis.
        float angle = Mathf.Atan2(edge.y, edge.x) * Mathf.Rad2Deg;
        Quaternion rotation = Quaternion.Euler(0, -angle + 180f, 0);

        Mesh meshInstance = Object.Instantiate(sourceMesh);
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
    /// Generate a horizontal polygon matching the footprint at the specified height.
    /// Used for roof and ground panels.
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

    /// <summary>
    /// Ear-clipping triangulation. Supports concave polygons, but returns an empty list when no ear can be found
    /// due to self-intersections or duplicate vertices. Use when callers must choose how to handle failure (fan fill or exception);
    /// use <see cref="TriangulateOrFan"/> for surfaces where missing geometry is worse than visual artifacts. Assumes CCW input.
    /// </summary>
    public static List<int> TriangulateEarClip(Vector2[] polygon)
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
    /// Public helper that triangulates a polygon and returns vertex indices.
    /// Uses ear clipping, falling back to a triangle fan on failure.
    /// Reusable where concave polygons (such as L-shaped quays) require valid triangulation.
    /// Assumes input is normalized to CCW.
    /// </summary>
    public static List<int> TriangulateOrFan(Vector2[] polygon)
    {
        if (polygon == null || polygon.Length < 3)
        {
            return new List<int>();
        }

        List<int> triangles = TriangulateEarClip(polygon);
        if (triangles.Count == 0)
        {
            Debug.LogWarning("BuildingMeshBuilder: 凹多角形の耳切り処理で耳が見つかりませんでした。ファン型の三角形にフォールバックします。");
            triangles = GenerateTriangleFanIndices(polygon.Length);
        }
        return triangles;
    }

    /// <summary>Safety limit on subdivided triangle count. Prevents excessive memory use and processing time for large parcels.</summary>
    public const int MAX_SUBDIVIDED_TRIANGLES = 20000;

    /// <summary>
    /// Subdivide triangles recursively at the midpoint of the longest edge if it exceeds maxEdge.
    /// Midpoints are welded using undirected edge keys, so adjacent triangles sharing an edge use the same midpoint
    /// and avoid cracks from T-junctions. Append generated midpoint vertices to verts
    /// (callers can use verts directly as the vertex array).
    /// Once MAX_SUBDIVIDED_TRIANGLES is reached, retain the rest without subdivision (no holes).
    /// Used to add interior vertices to surface meshes that follow uneven terrain (ground panels and rubble mounds).
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

            // Split the longest edge at its midpoint (preserving the original triangle's winding).
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
        // Retain triangles left in work when the limit is reached (unsubdivided, but without holes).
        while (work.Count > 0)
        {
            finalTriangles.Add(work.Pop());
        }

        return finalTriangles;
    }

    /// <summary>
    /// Flip interior triangulation edges using the Delaunay criterion (Lawson flips) to remove fans of thin triangles produced by ear clipping.
    /// Leave boundary edges with a triangle on only one side unchanged, preserving the outline. Pass XZ-counterclockwise triangles; replaced in place.
    /// </summary>
    public static void FlipToDelaunay(IReadOnlyList<Vector2> verts, List<(int a, int b, int c)> triangles)
    {
        if (verts == null || triangles == null) return;
        var mesh = new EdgeIndexedTriangulation(verts, triangles);
        mesh.PushAllEdges();
        mesh.Legalize();
    }

    /// <summary>
    /// Insert points one at a time into a Delaunay triangulation and flip surrounding edges to preserve the Delaunay criterion.
    /// Do not insert points on edges or outside the triangulation. Pass XZ-counterclockwise triangles.
    /// Nearby consecutive points are faster because the containing-triangle search starts at the last insertion.
    /// </summary>
    public static void InsertInteriorPoints(List<Vector2> verts, List<(int a, int b, int c)> triangles, IEnumerable<Vector2> points)
    {
        if (verts == null || triangles == null || points == null) return;
        var mesh = new EdgeIndexedTriangulation(verts, triangles);
        foreach (Vector2 point in points)
        {
            int container = mesh.Locate(point);
            if (container < 0) continue;
            var (a, b, c) = triangles[container];
            int index = verts.Count;
            verts.Add(point);
            mesh.Replace(container, (a, b, index));
            mesh.Add((b, c, index));
            mesh.Add((c, a, index));
            mesh.Push(a, b); mesh.Push(b, c); mesh.Push(c, a);
            mesh.Legalize();
        }
    }

    /// <summary>Maximum edge flips per triangle. Stops even if floating-point error prevents convergence (the triangulation remains valid).</summary>
    private const int MAX_DELAUNAY_FLIPS_PER_TRIANGLE = 64;

    /// <summary>Triangulation indexed by directed edges for local edge flips and point location.</summary>
    private sealed class EdgeIndexedTriangulation
    {
        private readonly IReadOnlyList<Vector2> verts;
        private readonly List<(int a, int b, int c)> triangles;
        private readonly Dictionary<long, int> owner;
        private readonly Stack<(int u, int v)> pending = new Stack<(int u, int v)>();
        private int flipBudget;
        private int lastFound;

        public EdgeIndexedTriangulation(IReadOnlyList<Vector2> verts, List<(int a, int b, int c)> triangles)
        {
            this.verts = verts;
            this.triangles = triangles;
            owner = new Dictionary<long, int>(triangles.Count * 3);
            for (int t = 0; t < triangles.Count; t++) Index(t);
            flipBudget = Mathf.Max(1, triangles.Count) * MAX_DELAUNAY_FLIPS_PER_TRIANGLE;
        }

        public void Push(int u, int v) => pending.Push((u, v));

        public void PushAllEdges()
        {
            foreach (var (a, b, c) in triangles) { Push(a, b); Push(b, c); Push(c, a); }
        }

        public void Add((int a, int b, int c) triangle)
        {
            triangles.Add(triangle);
            Index(triangles.Count - 1);
            flipBudget += MAX_DELAUNAY_FLIPS_PER_TRIANGLE;
        }

        /// <summary>Replace a triangle. During edge flips, the other triangle has already inherited the same directed edge, so remove only index entries pointing to this triangle.</summary>
        public void Replace(int t, (int a, int b, int c) triangle)
        {
            var (a, b, c) = triangles[t];
            Unindex(Key(a, b), t); Unindex(Key(b, c), t); Unindex(Key(c, a), t);
            triangles[t] = triangle;
            Index(t);
        }

        private void Unindex(long key, int t)
        {
            if (owner.TryGetValue(key, out int current) && current == t) owner.Remove(key);
        }

        public void Legalize()
        {
            while (pending.Count > 0 && flipBudget > 0)
            {
                var (u, v) = pending.Pop();
                if (!owner.TryGetValue(Key(u, v), out int t) || !owner.TryGetValue(Key(v, u), out int n)) continue;
                int p = Opposite(triangles[t], u, v);
                int q = Opposite(triangles[n], v, u);
                if (!IsInsideCircumcircle(verts[u], verts[v], verts[p], verts[q])) continue;
                if (Cross2D(verts[u], verts[q], verts[p]) <= 1e-9 || Cross2D(verts[q], verts[v], verts[p]) <= 1e-9) continue;
                // Replace diagonal u-v of (u,v,p)+(v,u,q) with p-q, then recheck the quadrilateral's four outer edges.
                Replace(t, (u, q, p));
                Replace(n, (q, v, p));
                Push(u, q); Push(q, v); Push(v, p); Push(p, u);
                flipBudget--;
            }
            pending.Clear();
        }

        /// <summary>
        /// Find the triangle containing a point. Walk across edges toward the point from the last triangle found; inspect all triangles upon reaching the boundary
        /// (a concave outline may prevent a straight walk). Return -1 for points on edges.
        /// </summary>
        public int Locate(Vector2 point)
        {
            int t = lastFound < triangles.Count ? lastFound : 0;
            for (int step = 0; step <= triangles.Count; step++)
            {
                var (a, b, c) = triangles[t];
                int next = -1;
                if (Cross2D(verts[a], verts[b], point) < 0 && owner.TryGetValue(Key(b, a), out int nab)) next = nab;
                else if (Cross2D(verts[b], verts[c], point) < 0 && owner.TryGetValue(Key(c, b), out int nbc)) next = nbc;
                else if (Cross2D(verts[c], verts[a], point) < 0 && owner.TryGetValue(Key(a, c), out int nca)) next = nca;
                if (next < 0) break;
                t = next;
            }
            if (Contains(t, point)) return lastFound = t;
            for (int i = triangles.Count - 1; i >= 0; i--)
                if (Contains(i, point)) return lastFound = i;
            return -1;
        }

        private bool Contains(int t, Vector2 point)
        {
            var (a, b, c) = triangles[t];
            return Cross2D(verts[a], verts[b], point) > 1e-6 && Cross2D(verts[b], verts[c], point) > 1e-6 && Cross2D(verts[c], verts[a], point) > 1e-6;
        }

        private void Index(int t)
        {
            var (a, b, c) = triangles[t];
            owner[Key(a, b)] = t; owner[Key(b, c)] = t; owner[Key(c, a)] = t;
        }

        private static long Key(int from, int to) => ((long)from << 32) | (uint)to;

        /// <summary>Remaining vertex of the triangle with directed edge from→to.</summary>
        private static int Opposite((int a, int b, int c) triangle, int from, int to)
        {
            if (triangle.a == from && triangle.b == to) return triangle.c;
            if (triangle.b == from && triangle.c == to) return triangle.a;
            return triangle.b;
        }
    }

    private static double Cross2D(Vector2 a, Vector2 b, Vector2 c)
        => ((double)b.x - a.x) * ((double)c.y - a.y) - ((double)b.y - a.y) * ((double)c.x - a.x);

    /// <summary>Whether point d lies inside the circumcircle of counterclockwise triangle abc. Use double precision relative to d even for large world coordinates.</summary>
    private static bool IsInsideCircumcircle(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        double adx = (double)a.x - d.x, ady = (double)a.y - d.y;
        double bdx = (double)b.x - d.x, bdy = (double)b.y - d.y;
        double cdx = (double)c.x - d.x, cdy = (double)c.y - d.y;
        double det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy)
            - (bdx * bdx + bdy * bdy) * (adx * cdy - cdx * ady)
            + (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
        return det > 1e-9;
    }

    /// <summary>
    /// Clip a site against terrain-grid triangles. Interpolate interior heights on the same triangles
    /// to avoid intersecting the ground due to differing triangulation or bilinear interpolation.
    /// Return null when terrain is unloaded so callers can fall back to a temporary mesh.
    /// </summary>
    public static Mesh GenerateTerrainSurfaceMesh(Vector2[] footprint, Vector3 center, float offset)
    {
        return GenerateTerrainSurfaceMesh(footprint, TriangulateOrFan(footprint), center, offset, false);
    }

    /// <summary>
    /// Clip a triangulated surface (including surfaces with holes) against the terrain grid. Pass XZ-counterclockwise triangles.
    /// With withUv, use local XZ coordinates (1m=1) as UVs so paving and other textures have consistent density regardless of site orientation.
    /// </summary>
    public static Mesh GenerateTerrainSurfaceMesh(IReadOnlyList<Vector2> footprint, IReadOnlyList<int> source,
        Vector3 center, float offset, bool withUv)
    {
        if (!Utils.TryGetTerrainGrid(center, out Vector2 origin, out Vector2 spacing)) return null;
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var vertexMap = new Dictionary<Vector2Int, int>();
        var heights = new Dictionary<Vector2, float>();
        var polygon = new List<Vector2>(8);
        var scratch = new List<Vector2>(8);
        Vector2 centerXZ = new Vector2(center.x, center.z);

        float Height(Vector2 p)
        {
            if (!heights.TryGetValue(p, out float h))
            {
                h = Utils.GetGroundHeight(new Vector3(p.x, 0f, p.y));
                heights.Add(p, h);
            }
            return h;
        }

        void Emit(Vector2 a, Vector2 b, Vector2 c, Vector2 p0, Vector2 p1, Vector2 p2)
        {
            polygon.Clear();
            polygon.Add(p0);
            polygon.Add(p1);
            polygon.Add(p2);
            ClipTerrainPolygon(ref polygon, ref scratch, a, b);
            ClipTerrainPolygon(ref polygon, ref scratch, b, c);
            ClipTerrainPolygon(ref polygon, ref scratch, c, a);
            if (polygon.Count < 3) return;
            float area = Cross(a, b, c);
            float ha = Height(a), hb = Height(b), hc = Height(c);
            int first = -1, previous = -1;
            foreach (Vector2 p in polygon)
            {
                Vector2 local = p - centerXZ;
                // Weld intersections from different source edges to 0.1mm precision to prevent normal seams.
                var key = new Vector2Int(Mathf.RoundToInt(local.x * 10000f), Mathf.RoundToInt(local.y * 10000f));
                if (!vertexMap.TryGetValue(key, out int index))
                {
                    float h = (Cross(p, b, c) * ha + Cross(a, p, c) * hb + Cross(a, b, p) * hc) / area;
                    index = vertices.Count;
                    vertices.Add(new Vector3(local.x, h - center.y + offset, local.y));
                    vertexMap.Add(key, index);
                }
                if (first < 0) first = index;
                else if (previous >= 0 && first != previous && previous != index && first != index)
                {
                    indices.Add(first);
                    indices.Add(index);
                    indices.Add(previous);
                }
                previous = index;
            }
        }

        for (int t = 0; t < source.Count; t += 3)
        {
            Vector2 a = footprint[source[t]] + centerXZ;
            Vector2 b = footprint[source[t + 1]] + centerXZ;
            Vector2 c = footprint[source[t + 2]] + centerXZ;
            int x0 = Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - origin.x) / spacing.x);
            int x1 = Mathf.CeilToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - origin.x) / spacing.x);
            int z0 = Mathf.FloorToInt((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - origin.y) / spacing.y);
            int z1 = Mathf.CeilToInt((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - origin.y) / spacing.y);
            for (int z = z0; z < z1; z++)
            {
                for (int x = x0; x < x1; x++)
                {
                    Vector2 lo = origin + new Vector2(x * spacing.x, z * spacing.y);
                    Vector2 hi = lo + spacing;
                    Vector2 right = new Vector2(hi.x, lo.y);
                    Vector2 top = new Vector2(lo.x, hi.y);
                    Emit(lo, right, hi, a, b, c);
                    Emit(lo, hi, top, a, b, c);
                }
            }
        }
        if (indices.Count == 0) return null;
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(indices, 0);
        if (withUv)
        {
            var uv = new List<Vector2>(vertices.Count);
            foreach (Vector3 v in vertices) uv.Add(new Vector2(v.x, v.z));
            mesh.SetUVs(0, uv);
        }
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void ClipTerrainPolygon(ref List<Vector2> input, ref List<Vector2> output, Vector2 a, Vector2 b)
    {
        output.Clear();
        if (input.Count == 0) return;
        Vector2 previous = input[input.Count - 1];
        float previousSide = Cross(a, b, previous);
        foreach (Vector2 current in input)
        {
            float side = Cross(a, b, current);
            if ((side >= 0f) != (previousSide >= 0f))
                output.Add(Vector2.LerpUnclamped(previous, current, previousSide / (previousSide - side)));
            if (side >= 0f) output.Add(current);
            previous = current;
            previousSide = side;
        }
        var swap = input;
        input = output;
        output = swap;
    }

    /// <summary>Unique key for directed edge (a→b). Shared by subdivision and skirt-boundary checks.</summary>
    public static long EdgeKey(int a, int b) => ((long)a << 32) | (uint)b;

    /// <summary>
    /// Return the midpoint vertex index for edge (a-b). Cache by undirected edge key so adjacent triangles
    /// sharing an edge use the same midpoint (preventing cracks from T-junctions).
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

    /// <summary>
    /// Whether model_type supports sloped terrain
    /// (a concrete foundation extruded from the footprint). For every model_type, the contact height is the terrain elevation at the building center
    /// (sea level only for sea parcels); this check does not change it.
    ///
    /// - Supported: MODEL_MODULAR_FLAT / HOUSE / GABLE / SHED. The building body rests on a horizontal plane at contact height,
    ///   so the foundation (GenerateFoundationMesh) fills the gap on the downhill side.
    /// - Excluded: MODEL_MODULAR_GROUND (fields/gardens follow the terrain), MODEL_PREBUILT,
    ///   MODEL_PORT (quays have their own seabed-following slab), and MODEL_MASHUP (each sub-building
    ///   rests independently at the terrain elevation of its placement). No foundation is generated for these.
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
    /// Return the construction-site dirt material (Resources/ConstructionDirt).
    /// If the asset is missing, fall back to the existing construction material (Resources/ConstructionMaterial, yellow),
    /// then the default building material, so rendering still works.
    /// Referenced by both construction-site dirt and path dirt strips.
    /// </summary>
    public static Material GetConstructionDirtMaterial()
    {
        if (cachedConstructionDirtMaterial != null) return cachedConstructionDirtMaterial;

        cachedConstructionDirtMaterial = AssetPackStore.LoadNamedMaterial("ConstructionDirt");
        if (cachedConstructionDirtMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/ConstructionDirt が見つかりません。ConstructionMaterial にフォールバックします。");
            cachedConstructionDirtMaterial = AssetPackStore.LoadNamedMaterial("ConstructionMaterial");
        }
        if (cachedConstructionDirtMaterial == null)
        {
            cachedConstructionDirtMaterial = GetDefaultBuildingMaterial();
        }
        return cachedConstructionDirtMaterial;
    }

    private static Material cachedCollapsedRubbleMaterial;

    /// <summary>
    /// Return the rubble material (Resources/CollapsedRubble) for collapsed buildings (STATUS_COLLAPSED).
    /// If the asset is missing, delegate to the construction-site dirt material (GetConstructionDirtMaterial,
    /// including its further fallbacks) so rendering still works.
    /// </summary>
    public static Material GetCollapsedRubbleMaterial()
    {
        if (cachedCollapsedRubbleMaterial != null) return cachedCollapsedRubbleMaterial;

        cachedCollapsedRubbleMaterial = AssetPackStore.LoadNamedMaterial("CollapsedRubble");
        if (cachedCollapsedRubbleMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/CollapsedRubble が見つかりません。工事現場の土マテリアルにフォールバックします。");
            cachedCollapsedRubbleMaterial = GetConstructionDirtMaterial();
        }
        return cachedCollapsedRubbleMaterial;
    }

    private static Material cachedBurnedRubbleMaterial;

    /// <summary>
    /// Return the charred rubble material (Resources/CollapsedRubbleBurned) for fire damage (status_reason == REASON_FIRE).
    /// If the asset is missing, delegate to the regular rubble material (GetCollapsedRubbleMaterial,
    /// including its further fallbacks) so rendering still works.
    /// </summary>
    public static Material GetBurnedRubbleMaterial()
    {
        if (cachedBurnedRubbleMaterial != null) return cachedBurnedRubbleMaterial;

        cachedBurnedRubbleMaterial = AssetPackStore.LoadNamedMaterial("CollapsedRubbleBurned");
        if (cachedBurnedRubbleMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/CollapsedRubbleBurned が見つかりません。通常の瓦礫マテリアルにフォールバックします。");
            cachedBurnedRubbleMaterial = GetCollapsedRubbleMaterial();
        }
        return cachedBurnedRubbleMaterial;
    }

    private static Material cachedCommonConcreteMaterial;

    /// <summary>
    /// Return the shared concrete material for all building foundations (GenerateFoundationMesh)
    /// from Resources/COMMON_CONCRETE. If the asset is missing, log a warning and
    /// fall back to the default building material (keep rendering functional).
    /// </summary>
    public static Material GetCommonConcreteMaterial()
    {
        if (cachedCommonConcreteMaterial != null) return cachedCommonConcreteMaterial;
        cachedCommonConcreteMaterial = AssetPackStore.LoadNamedMaterial("COMMON_CONCRETE");
        if (cachedCommonConcreteMaterial == null)
        {
            Debug.LogWarning("BuildingMeshBuilder: Resources/COMMON_CONCRETE が見つかりません。デフォルト建物マテリアルにフォールバックします。");
            cachedCommonConcreteMaterial = GetDefaultBuildingMaterial();
        }
        return cachedCommonConcreteMaterial;
    }

    /// <summary>
    /// Generate a concrete foundation mesh by extruding the footprint along Y (building-local coordinates).
    ///
    /// Buildings supporting sloped terrain (IsApplicableModelType) rest on a horizontal plane at the building center's terrain elevation,
    /// leaving a gap between the footprint and terrain downhill, which makes the building appear to float.
    /// Fill this gap with a column mesh whose top is the building's contact plane (local Y=0) and bottom is
    /// 5m below the lowest terrain elevation sampled at footprint vertices (at least 10m below the contact plane).
    /// The bottom has extra depth so local dips between vertices do not expose gaps.
    ///
    /// - footprint uses local XZ coordinates relative to center_point, CCW (same convention as BuildingMeshUtils.GetFootprint).
    /// - The caller decides whether model_type is supported.
    /// - Intended material: shared GetCommonConcreteMaterial() (dirt during construction);
    ///   top and side UVs use world-scale tiling (one tile per 10m).
    /// </summary>
    public static Mesh GenerateFoundationMesh(Vector2[] footprint, Vector3 centerPointWorld)
    {
        using var scope = foundationMarker.Auto();
        if (footprint == null || footprint.Length < 3) return null;

        float baseY = centerPointWorld.y;
        int n = footprint.Length;

        // Lowest terrain elevation at footprint vertices. Lower the foundation bottom a further 5m
        // to prevent gaps caused by unsampled terrain between vertices.
        float minTerrainHeight = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            float h = Utils.GetGroundHeight(
                new Vector3(footprint[i].x + centerPointWorld.x, 1000f, footprint[i].y + centerPointWorld.z));
            if (h < minTerrainHeight)
            {
                minTerrainHeight = h;
            }
        }
        if (minTerrainHeight == float.MaxValue) minTerrainHeight = baseY - 5f;

        float topLocalY = 0f;
        float bottomLocalY = Mathf.Min(minTerrainHeight - 5f - baseY, -10f);

        List<int> polygonTriangles = TriangulateOrFan(footprint);
        if (polygonTriangles.Count < 3) return null;

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();

        // Top. A CCW polygon placed directly on XZ has a downward normal; reverse the winding to face upward.
        // Slightly below the ground panel (raised by GROUND_OFFSET=0.005m), avoiding Z-fighting.
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

        // Bottom (buried underground, but closed to make the mesh watertight).
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

        // Sides. UV U is cumulative distance around the perimeter, keeping tiling continuous across edges.
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
