using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Construction-site mesh generator for constructing (STATUS_CONSTRUCTING) / demolishing (STATUS_DEMOLISHING) buildings.
/// BuildingMeshBuilder.Generate dispatches here by status before any model_type handling,
/// regardless of model_type (never generates the completed mesh).
///
/// Construction and demolition share composition and stacking logic; only parts, machinery types, and whether scaffolding grows
/// or shrinks with progress differ. These differences are encapsulated in <see cref="ConstructionSiteProfile"/> (passed to the constructor),
/// allowing one builder to serve both states.
///
/// Output:
/// - Site dirt: a ground panel covering the entire site (parcel polygon, or footprint if unavailable), drawn by the same rule as
///   site ground panels (ModularBodyBuilder.AppendGroundPanel: terrain-following, deferred low-priority work).
///   Material: Resources/ConstructionDirt (falls back to ConstructionMaterial → BuildingMaterial if missing).
/// - Foundation: the same Y-extruded footprint base as completed buildings (BuildingMeshBuilder.GenerateFoundationMesh),
///   using dirt material. Only for model_type values supporting slopes (IsApplicableModelType).
/// - Scaffolding rings: place profile scaffold wall/corner parts around the footprint
///   with fence-style logic to make a one-floor ring, then stack vertically for the number of floors matching progress
///   (builtFloors; increases during construction and decreases to the remaining fraction during demolition).
/// - Machinery: selected by profile MachineKind.
///   StackedCrane (construction): stack crane-base / crane-mid / crane-top vertically using prebuilt rules.
///     Scale base/mid in Y to floor height; top stays actual size. Only buildings meeting MachineMinFloors.
///   RoofVehicle (demolition): place one actual-size excavator on the scaffolding top (top-down demolition, no floor-count gate).
///
/// Part fallbacks: a{variant} → b{bd_type} (existing prefix resolution) → standard parts.
/// Models/Buildings/common/（BuildingMeshUtils.GetPartWithCommonFallback）。
/// Optional assets keep rendering functional even if part/material assets are missing
/// (skip scaffolding/machinery; render dirt with fallback materials).
///
/// Progress uses API construction_progress (= percentage of procured construction volume, calculated by the server).
/// The backend bumps version when progress increases, so each epoch's incremental load
/// reruns this builder, extending scaffolding/machinery (lowering them during demolition).
///
/// Known simplifications:
/// - MODEL_MODULAR_GROUND (fields/gardens), open spaces, MODEL_PORT (quays), MODEL_RAIL_YARD (depots), and MODEL_OPEN_PIT (open pits) have site dirt only (no scaffolding/machinery).
/// - MODEL_PORT sites include coastline, so dirt may be rendered above sea level.
/// </summary>
public class ConstructingBuildingMeshBuilder : IBuildingMeshGenerator
{
    private static readonly StageMarker scaffoldMarker = new StageMarker("Building.Site.Scaffold");
    private static readonly StageMarker craneMarker = new StageMarker("Building.Site.Crane");
    private static readonly StageMarker roofMachineMarker = new StageMarker("Building.Site.RoofMachine");
    private static readonly StageMarker combineMarker = new StageMarker("Building.Site.Combine");

    private readonly ConstructionSiteProfile profile;

    /// <summary>
    /// Shared construction/demolition builder. profile selects parts and machinery type.
    /// A null profile falls back to the construction preset.
    /// </summary>
    public ConstructingBuildingMeshBuilder(ConstructionSiteProfile profile)
    {
        this.profile = profile ?? ConstructionSiteProfile.Construction;
    }

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        return Generate(buildingData, isDynamic, false);
    }

    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic, bool animateCrane)
    {
        if (buildingData == null)
        {
            Debug.LogError("ConstructingBuildingMeshBuilder: buildingData が null です。");
            return null;
        }

        BuildingMeshUtils.InitializeCache();

        int modelType = BuildingMeshBuilder.GetModelType(buildingData);

        // Building footprint (local coordinates, CCW). Allow null so site dirt still renders for invalid polygons.
        Vector2[] footprint = (buildingData.polygonArray != null && buildingData.polygonArray.Length >= 3)
            ? BuildingMeshUtils.GetFootprint(buildingData)
            : null;

        // Site context (parcel polygon in local XZ relative to center_point).
        // Use the completed-building height reference so switching from construction to completion does not shift the building vertically.
        Vector2[] parcelFootprint = BuildingMeshUtils.ResolveParcelContext(buildingData, out ParcelSummary parcel, out Vector3 centerPointWorld);

        // Dirt-mound polygon (local XZ). Use the footprint if no parcel exists.
        Vector2[] dirtPolygon = parcelFootprint ?? footprint;

        if (dirtPolygon == null)
        {
            Debug.LogWarning($"ConstructingBuildingMeshBuilder: polygon も parcel も不正なため空メッシュを返します (agent_id={buildingData.agent_id})。");
            return new BuildingMeshResult(new Mesh(), null);
        }

        // Progress % → standing floors. Show at least one level at 0% or null (unprocured), making a started site visible.
        int floors = Mathf.Max(1, buildingData.floors ?? 1);
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        float progressPercent = GeneratedModelUtils.ToFloat(buildingData.construction_progress);
        float progress01 = Mathf.Clamp01(progressPercent / 100f);
        float standing01 = profile.ShrinksWithProgress ? 1f - progress01 : progress01;
        int builtFloors = Mathf.Clamp(Mathf.CeilToInt(standing01 * floors), 1, floors);

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> allMaterials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();
        ConstructionCraneAnimator.Placement craneTop = null;

        Material dirtMaterial = BuildingMeshBuilder.GetConstructionDirtMaterial();

        // --- 1. Site dirt ---
        ModularBodyBuilder.AppendGroundPanel(dirtPolygon, dirtMaterial, centerPointWorld, isDynamic,
            combineInstances, allMaterials, temporaryMeshes);

        // --- 2. Foundation (slope base). Same footprint extrusion as completed buildings, with dirt material ---
        // model_type values without completed foundations (GROUND / PORT / PREBUILT) also omit them during construction
        // (so foundation presence stays consistent when construction completes).
        if (footprint != null && BuildingMeshBuilder.IsApplicableModelType(modelType))
        {
            Mesh foundationMesh = BuildingMeshBuilder.GenerateFoundationMesh(footprint, centerPointWorld);
            if (foundationMesh != null)
            {
                combineInstances.Add(new CombineInstance { mesh = foundationMesh, subMeshIndex = 0, transform = Matrix4x4.identity });
                allMaterials.Add(dirtMaterial);
                temporaryMeshes.Add(foundationMesh);
            }
        }

        // --- 3. Scaffolding rings and crane ---
        // Ground assets (fields/gardens), quays, depots, and open pits have no scaffolding/crane (dirt-only site).
        bool wantsStructures = footprint != null
            && modelType != Constants.MODEL_MODULAR_GROUND
            && !BuildingMeshBuilder.IsOpenSpaceModel(modelType)
            && modelType != Constants.MODEL_PORT
            && modelType != Constants.MODEL_RAIL_YARD
            && modelType != Constants.MODEL_OPEN_PIT;

        if (wantsStructures)
        {
            string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
            float visualVariant = buildingData.visual_variant;

            AddScaffoldRings(footprint, prefix, visualVariant, floorHeight, builtFloors,
                profile.ScaffoldWallPart, profile.ScaffoldCornerPart,
                combineInstances, allMaterials, temporaryMeshes);

            // Machinery. Selected by profile kind (construction=tower crane / demolition=rooftop excavator).
            // RoofVehicle has MachineMinFloors=0, so place it regardless of floor count.
            if (floors >= profile.MachineMinFloors)
            {
                if (profile.MachineKind == SiteMachineKind.StackedCrane)
                {
                    craneTop = AddCrane(footprint, prefix, visualVariant, buildingData.visual_variant2, floorHeight, builtFloors,
                        profile.CraneBasePart, profile.CraneMidPart, profile.CraneTopPart,
                        combineInstances, allMaterials, temporaryMeshes, animateCrane);
                }
                else // RoofVehicle
                {
                    AddRoofMachine(footprint, prefix, visualVariant, buildingData.visual_variant2, builtFloors * floorHeight,
                        profile.RoofMachinePart, combineInstances, allMaterials, temporaryMeshes);
                }
            }

            // Close the constructed floors' top with a footprint-shaped panel (ceil material).
            // Scaffolding rings are hollow; without this, the interior is visible from above.
            AddConstructionCeil(footprint, prefix, visualVariant, builtFloors * floorHeight,
                profile.CeilPart, combineInstances, allMaterials, temporaryMeshes);
        }

        if (combineInstances.Count == 0 && !BuildingExteriorRenderer.HasCapturedWork)
        {
            foreach (Mesh temp in temporaryMeshes)
            {
                Object.Destroy(temp);
            }
            return new BuildingMeshResult(new Mesh(), null);
        }

        // --- 4. Combine ---
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        using (combineMarker.Auto())
        {
            // Dirt-only sites (no scaffolding) have nothing to combine until the deferred dirt panel is built.
            if (combineInstances.Count > 0) finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
            finalMesh.RecalculateNormals();
            finalMesh.RecalculateBounds();
        }

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // --- 5. Collider ---
        // Use the scaffolding box for picking. Crane protrusions may remain outside the pickable area
        // (same approach as prebuilt top protrusions). ParcelObject handles site (dirt) picking.
        Vector2[] colliderFootprint = footprint ?? dirtPolygon;
        float colliderHeight = Mathf.Max(builtFloors * floorHeight, floorHeight);
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(colliderFootprint, colliderHeight);

        var result = new BuildingMeshResult(finalMesh, allMaterials.ToArray(), colliderMesh) { CraneTop = craneTop };
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    // ------------------------------------------------------------------
    // Scaffolding.
    // ------------------------------------------------------------------

    /// <summary>
    /// Generate/register builtFloors levels of scaffolding (profile wall/corner parts).
    /// Use the same wall/corner placement logic as normal buildings (ModularBodyBuilder.GenerateScaffoldSegments)
    /// directly on the footprint. Do nothing if no wall part exists.
    /// </summary>
    private static void AddScaffoldRings(Vector2[] footprint, string prefix, float visualVariant,
        float floorHeight, int builtFloors, string wallPart, string cornerPart,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        using var scope = scaffoldMarker.Auto();
        GameObject wallModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, wallPart, visualVariant);
        if (wallModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[CBMB] 足場: {wallPart} パーツなし (prefix={prefix})。足場をスキップします。");
            return;
        }
        Material[] wallMaterials = BuildingMeshUtils.GetModuleMaterials(wallModule);

        GameObject cornerModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, cornerPart, visualVariant);
        Material[] cornerMaterials = cornerModule != null ? BuildingMeshUtils.GetModuleMaterials(cornerModule) : null;

        ModularBodyBuilder.GenerateScaffoldSegments(
            footprint, wallModule, wallMaterials, cornerModule, cornerMaterials,
            floorHeight, builtFloors, combineInstances, allMaterials, temporaryMeshes);
    }

    /// <summary>
    /// Close the constructed floors' top with a footprint-shaped horizontal panel (hide the construction interior from above).
    /// Get the material from ceil parts (a{variant}→b{bd_type}→common); if unavailable,
    /// fall back to the default building material.
    /// </summary>
    private static void AddConstructionCeil(Vector2[] footprint, string prefix, float visualVariant, float height,
        string ceilPartName,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        if (footprint == null || footprint.Length < 3) return;

        Mesh ceilMesh = BuildingMeshBuilder.GenerateHorizontalPolygonMesh(footprint, height);
        if (ceilMesh == null) return;

        Material ceilMat = null;
        GameObject ceilPart = BuildingMeshUtils.GetPartWithCommonFallback(prefix, ceilPartName, visualVariant);
        if (ceilPart != null)
        {
            Material[] mats = BuildingMeshUtils.GetModuleMaterials(ceilPart);
            if (mats != null && mats.Length > 0) ceilMat = mats[0];
        }
        if (ceilMat == null) ceilMat = BuildingMeshBuilder.GetDefaultBuildingMaterial();

        combineInstances.Add(new CombineInstance { mesh = ceilMesh, subMeshIndex = 0, transform = Matrix4x4.identity });
        allMaterials.Add(ceilMat);
        temporaryMeshes.Add(ceilMesh);
    }

    // ------------------------------------------------------------------
    // Machinery (crane / ground machinery).
    // ------------------------------------------------------------------

    /// <summary>
    /// Determine machinery position/orientation (shared by cranes and ground machinery).
    /// Position is the footprint centroid plus a visual_variant2-seeded offset; yaw follows the first edge
    /// (same method as rooftop props in GenerateRoofprop).
    /// </summary>
    private static void ComputeMachinePlacement(Vector2[] footprint, float visualVariant2,
        out Vector2 xzOffset, out Quaternion rotation)
    {
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < footprint.Length; i++)
        {
            centroid += footprint[i];
        }
        centroid /= footprint.Length;

        float seed = Mathf.Clamp01(visualVariant2);
        float angle = seed * Mathf.PI * 2f;
        const float radius = 0.5f;
        xzOffset = new Vector2(centroid.x + Mathf.Cos(angle) * radius, centroid.y + Mathf.Sin(angle) * radius);

        Vector2 firstEdge = footprint.Length >= 2 ? (footprint[1] - footprint[0]).normalized : Vector2.right;
        float edgeAngle = Mathf.Atan2(firstEdge.y, firstEdge.x) * Mathf.Rad2Deg;
        rotation = Quaternion.Euler(0, -edgeAngle + 90, 0);
    }

    /// <summary>
    /// Register a tower crane (base/mid/top) using prebuilt stacking rules.
    /// Total levels = builtFloors + CONSTRUCTION_CRANE_EXTRA_LEVELS (tracks construction progress, always
    /// slightly taller than the building). base fills the first level; mid fills the rest (Y-only floor-height scaling);
    /// top rests above at actual size. Do nothing if both base and mid are missing
    /// (top alone would leave the jib on the ground).
    /// </summary>
    private static ConstructionCraneAnimator.Placement AddCrane(Vector2[] footprint, string prefix, float visualVariant, float visualVariant2,
        float floorHeight, int builtFloors,
        string basePart, string midPart, string topPart,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes, bool animateCrane)
    {
        using var scope = craneMarker.Auto();
        GameObject baseModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, basePart, visualVariant);
        GameObject midModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, midPart, visualVariant);
        GameObject topModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, topPart, visualVariant);

        if (baseModule == null && midModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[CBMB] クレーン: {basePart} / {midPart} パーツなし (prefix={prefix})。クレーンをスキップします。");
            return null;
        }

        ComputeMachinePlacement(footprint, visualVariant2, out Vector2 xzOffset, out Quaternion rotation);

        int totalLevels = builtFloors + LocalConstants.CONSTRUCTION_CRANE_EXTRA_LEVELS;
        float stackY = 0f;
        int remainingLevels = totalLevels;

        // Stack crane parts with alignByOrigin=true, assuming origin (y=0) is the connection point (bottom).
        // This aligns connections precisely even for top parts projecting below the origin, avoiding gaps between levels.
        if (baseModule != null)
        {
            BuildingMeshUtils.AddStackedModule(baseModule, stackY, floorHeight,
                combineInstances, allMaterials, temporaryMeshes, xzOffset, rotation, alignByOrigin: true);
            stackY += floorHeight;
            remainingLevels--;
        }

        if (midModule != null)
        {
            for (int i = 0; i < remainingLevels; i++)
            {
                BuildingMeshUtils.AddStackedModule(midModule, stackY, floorHeight,
                    combineInstances, allMaterials, temporaryMeshes, xzOffset, rotation, alignByOrigin: true);
                stackY += floorHeight;
            }
        }

        if (topModule != null)
        {
            // For rendering-only rotation, separate the top, whose origin is the connection point, from the combined body.
            if (animateCrane)
                return new ConstructionCraneAnimator.Placement(topModule,
                    new Vector3(xzOffset.x, stackY, xzOffset.y), rotation);

            // Keep top at actual size without scaling; place origin (=top of the highest mid=connection point) at stackY.
            BuildingMeshUtils.AddStackedModule(topModule, stackY, null,
                combineInstances, allMaterials, temporaryMeshes, xzOffset, rotation, alignByOrigin: true);
        }
        return null;
    }

    /// <summary>
    /// Place one actual-size rooftop machine (e.g. demolition excavator) on the scaffolding top (same height as AddConstructionCeil panel).
    /// Do not stack levels or scale Y as for cranes. Position/orientation use the same seeded logic as cranes.
    /// Do nothing if the part is absent (optional).
    /// </summary>
    private static void AddRoofMachine(Vector2[] footprint, string prefix, float visualVariant, float visualVariant2,
        float topY, string machinePart,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        using var scope = roofMachineMarker.Auto();
        GameObject machineModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, machinePart, visualVariant);
        if (machineModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[CBMB] 屋上建機: {machinePart} パーツなし (prefix={prefix})。建機をスキップします。");
            return;
        }

        ComputeMachinePlacement(footprint, visualVariant2, out Vector2 xzOffset, out Quaternion rotation);

        // fitHeight=null avoids Y scaling; alignByOrigin=true places model origin (y=0)=contact point on the top surface.
        BuildingMeshUtils.AddStackedModule(machineModule, topY, null,
            combineInstances, allMaterials, temporaryMeshes, xzOffset, rotation, alignByOrigin: true);
    }
}
