// Assets/Scripts/MeshGenerators/Buildings/CollapsedBuildingMeshBuilder.cs
using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Rubble mesh generator for collapsed buildings (STATUS_COLLAPSED).
/// BuildingMeshBuilder.Generate dispatches here by status before any model_type handling,
/// regardless of model_type (never generates the completed mesh).
/// Fire damage (status_reason == REASON_FIRE) is distinguished by replacing the rubble mound material
/// with charred rubble (CollapsedRubbleBurned).
///
/// Output:
/// - Rubble mound: covers the entire site (parcel polygon, or footprint if unavailable), with thickness and
///   Perlin-noise bumps. Reuses construction dirt-mound generation (ConstructingBuildingMeshBuilder.GenerateDirtMoundMesh)
///   with thicker, rougher rubble parameters (CONSTRUCTION_RUBBLE_*).
///   Material: Resources/CollapsedRubble (falls back to ConstructionDirt → default if missing).
///   Follows arbitrary polygons exactly, staying inside nonrectangular parcels.
/// - Optional collapsed fragment: if a collapse part exists, fit one to the footprint's XZ bounding box
///   with nonuniform scaling (a prebuilt collapsed structure, rubble pile, etc.).
///   Keep height low. The mound alone is sufficient when no part exists (optional).
///
/// Parts/materials are optional so missing assets do not break rendering (same approach as construction sites).
/// </summary>
public class CollapsedBuildingMeshBuilder : IBuildingMeshGenerator
{
    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null)
        {
            Debug.LogError("CollapsedBuildingMeshBuilder: buildingData が null です。");
            return null;
        }

        BuildingMeshUtils.InitializeCache();

        int modelType = BuildingMeshBuilder.GetModelType(buildingData);

        // Building footprint (local coordinates, CCW). Allow null to render rubble even when polygon is invalid.
        Vector2[] footprint = (buildingData.polygonArray != null && buildingData.polygonArray.Length >= 3)
            ? BuildingMeshUtils.GetFootprint(buildingData)
            : null;

        // Site context. Use the same height reference as construction/completed buildings so state transitions do not shift buildings vertically.
        Vector2[] parcelFootprint = BuildingMeshUtils.ResolveParcelContext(buildingData, out ParcelSummary parcel, out Vector3 centerPointWorld);

        // Rubble-mound polygon (local XZ). Use the footprint if no parcel exists.
        Vector2[] rubblePolygon = parcelFootprint ?? footprint;

        if (rubblePolygon == null)
        {
            Debug.LogWarning($"CollapsedBuildingMeshBuilder: polygon も parcel も不正なため空メッシュを返します (agent_id={buildingData.agent_id})。");
            return new BuildingMeshResult(new Mesh(), null);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> allMaterials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // --- 1. Rubble mound (reuse dirt-mound generation with rubble parameters) ---
        // Distinguish fire damage (status_reason == REASON_FIRE) from deterioration collapse with the charred rubble material.
        Material rubbleMaterial = (buildingData.status_reason == Constants.REASON_FIRE)
            ? BuildingMeshBuilder.GetBurnedRubbleMaterial()
            : BuildingMeshBuilder.GetCollapsedRubbleMaterial();
        Mesh moundMesh = ConstructingBuildingMeshBuilder.GenerateDirtMoundMesh(
            rubblePolygon, centerPointWorld, isDynamic,
            thickness: LocalConstants.CONSTRUCTION_RUBBLE_THICKNESS,
            bumpAmplitude: LocalConstants.CONSTRUCTION_RUBBLE_BUMP_AMPLITUDE);
        if (moundMesh != null)
        {
            combineInstances.Add(new CombineInstance { mesh = moundMesh, subMeshIndex = 0, transform = Matrix4x4.identity });
            allMaterials.Add(rubbleMaterial);
            temporaryMeshes.Add(moundMesh);
        }

        // --- 2. Optional collapsed fragment (fit collapse part to footprint) ---
        // No structural fragments for ground assets (fields/gardens), quays, depots, or open pits (rubble only).
        bool wantsFragment = footprint != null
            && modelType != Constants.MODEL_MODULAR_GROUND
            && !BuildingMeshBuilder.IsOpenSpaceModel(modelType)
            && modelType != Constants.MODEL_PORT
            && modelType != Constants.MODEL_RAIL_YARD
            && modelType != Constants.MODEL_OPEN_PIT;

        if (wantsFragment)
        {
            string prefix = BuildingMeshUtils.ResolveModelNaming(buildingData).Prefix;
            float visualVariant = buildingData.visual_variant;
            AddCollapseFragment(footprint, prefix, visualVariant,
                combineInstances, allMaterials, temporaryMeshes);
        }

        if (combineInstances.Count == 0)
        {
            foreach (Mesh temp in temporaryMeshes)
            {
                Object.Destroy(temp);
            }
            return new BuildingMeshResult(new Mesh(), null);
        }

        // --- 3. Combine ---
        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateNormals();
        finalMesh.RecalculateBounds();

        foreach (Mesh temp in temporaryMeshes)
        {
            Object.Destroy(temp);
        }

        // --- 4. Collider ---
        // Rubble is low, so extrude footprint (or parcel) to approximate rubble-top height for a low box.
        Vector2[] colliderFootprint = footprint ?? rubblePolygon;
        float colliderHeight = LocalConstants.CONSTRUCTION_RUBBLE_THICKNESS + LocalConstants.CONSTRUCTION_RUBBLE_BUMP_AMPLITUDE;
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(colliderFootprint, colliderHeight);

        var result = new BuildingMeshResult(finalMesh, allMaterials.ToArray(), colliderMesh);
        return BuildingMeshBuilder.MergeSubMeshes(result);
    }

    /// <summary>
    /// Fit and register a collapsed fragment (collapse part) in the footprint's XZ bounding box.
    /// - XZ: nonuniformly scale model XZ bounds to footprint AABB (with COLLAPSE_FRAGMENT_FILL margin).
    ///   Fit the AABB even for arbitrary polygons to limit protrusion beyond nonrectangular parcels.
    /// - Y: use actual size, capped by COLLAPSE_FRAGMENT_MAX_HEIGHT to keep rubble low.
    /// - Position: align the model's XZ center to the footprint AABB center and bottom to ground (local Y=0).
    /// Do nothing if no part exists (optional).
    /// </summary>
    private static void AddCollapseFragment(Vector2[] footprint, string prefix, float visualVariant,
        List<CombineInstance> combineInstances, List<Material> allMaterials, List<Mesh> temporaryMeshes)
    {
        GameObject fragmentModule = BuildingMeshUtils.GetPartWithCommonFallback(prefix, "collapse", visualVariant);
        if (fragmentModule == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_BUILDING, $"[CLPS] 倒壊片: collapse パーツなし (prefix={prefix})。瓦礫マウンドのみで描画します。");
            return;
        }

        Mesh moduleMesh = BuildingMeshUtils.CombineModuleMeshes(fragmentModule, out Material[] moduleMaterials);
        if (moduleMesh == null || moduleMesh.subMeshCount == 0)
        {
            if (moduleMesh != null) Object.Destroy(moduleMesh);
            return;
        }

        // Footprint AABB (local XZ).
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < footprint.Length; i++)
        {
            minX = Mathf.Min(minX, footprint[i].x);
            maxX = Mathf.Max(maxX, footprint[i].x);
            minZ = Mathf.Min(minZ, footprint[i].y);
            maxZ = Mathf.Max(maxZ, footprint[i].y);
        }
        float footprintSizeX = Mathf.Max(maxX - minX, 0.01f);
        float footprintSizeZ = Mathf.Max(maxZ - minZ, 0.01f);
        Vector2 footprintCenter = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);

        Bounds bounds = moduleMesh.bounds;
        float moduleSizeX = Mathf.Max(bounds.size.x, 0.01f);
        float moduleSizeZ = Mathf.Max(bounds.size.z, 0.01f);
        float moduleSizeY = Mathf.Max(bounds.size.y, 0.01f);

        float fill = LocalConstants.COLLAPSE_FRAGMENT_FILL;
        float scaleX = (footprintSizeX * fill) / moduleSizeX;
        float scaleZ = (footprintSizeZ * fill) / moduleSizeZ;
        // Use actual Y size (=1), shrinking only above the height cap (keep rubble low without needless stretching).
        float scaleY = Mathf.Min(1f, LocalConstants.COLLAPSE_FRAGMENT_MAX_HEIGHT / moduleSizeY);

        Vector3[] vertices = moduleMesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            // Center the model's XZ at origin, scale, then translate to footprint center. Align bottom to ground (Y=0).
            v.x = (v.x - bounds.center.x) * scaleX + footprintCenter.x;
            v.z = (v.z - bounds.center.z) * scaleZ + footprintCenter.y;
            v.y = (v.y - bounds.min.y) * scaleY;
            vertices[i] = v;
        }
        moduleMesh.vertices = vertices;
        moduleMesh.RecalculateNormals();
        moduleMesh.RecalculateBounds();

        for (int subIndex = 0; subIndex < moduleMesh.subMeshCount; subIndex++)
        {
            combineInstances.Add(new CombineInstance
            {
                mesh = moduleMesh,
                subMeshIndex = subIndex,
                transform = Matrix4x4.identity,
            });
            Material material = (moduleMaterials != null && subIndex < moduleMaterials.Length) ? moduleMaterials[subIndex] : null;
            allMaterials.Add(material);
        }
        temporaryMeshes.Add(moduleMesh);
    }
}
