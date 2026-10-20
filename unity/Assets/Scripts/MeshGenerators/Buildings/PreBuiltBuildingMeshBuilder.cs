using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Special building mesh generator using prebuilt parts.
/// Stack base-prebuilt / mid-prebuilt / top-prebuilt from the bottom.
///
/// Part meanings/scaling:
/// - base-prebuilt … first floor, placed at actual size without scaling.
/// - mid-prebuilt  … one repeated floor, scaled only in Y
///   to height_per_floor (XZ remain actual size). Repeated middle parts control height,
///   matching modular mid-level conventions.
/// - top-prebuilt  … top, placed at actual size (spires or complete models).
///
/// Stacking rules:
/// - base/top each consume one logical floor (floors); mid repeats for remaining floors.
/// - Part Y always follows the floor-height grid (level × floor height). Unscaled base/top
///   dimensions differing from floor height may leave gaps/overlaps with the next level; accepted by design.
/// - Advance levels only for parts whose files exist, so top rests on the ground
///   when base/mid are absent (allowing a complete model in top alone).
/// - Collider follows actual rendered top, keeping top/base pickable even when they exceed logical height
/// (floors × floor height).
/// - Do not generate top-closing panels. Unlike modular wall parts, prebuilt parts
///   are complete 3D models and must close their visible top surfaces themselves.
///
/// Site decorations share ModularBodyBuilder.GenerateSiteDecorations with modular buildings:
/// parcel-perimeter fences (fence-w / fence-c), site tiles (tile), scatter outside the footprint (scatter),
/// and ground panels (floor). All are optional, rendered only when corresponding parts exist
/// (ground panels require floor parts; no default-material fallback).
///
/// Excluded from slope handling (IsApplicableModelType): buildings rest on center terrain elevation
/// (BuildingManager placement rules), with no concrete foundation.
/// Thus ultra-near (isDynamic) fences/tiles/ground panels follow terrain
/// (same treatment as MODEL_MODULAR_GROUND).
/// </summary>
public class PreBuiltBuildingMeshBuilder : IBuildingMeshGenerator
{
    /// <summary>
    /// Generate a prebuilt building mesh from BuildingSummary.
    /// With isDynamic=true, fences/tiles/ground panels follow terrain.
    /// </summary>
    public BuildingMeshResult Generate(BuildingSummary buildingData, bool isDynamic = false)
    {
        if (buildingData == null)
        {
            Debug.LogError("PreBuiltBuildingMeshBuilder: buildingData が null です。");
            return null;
        }

        BuildingMeshUtils.InitializeCache();

        int floors = Mathf.Max(1, buildingData.floors ?? 1);
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        string glbPrefix = BuildingMeshUtils.GetGlbPrefix1(buildingData);

        GameObject baseModule = BuildingMeshUtils.GetPartNoPosition(glbPrefix, "base-prebuilt", 0, true);
        GameObject middleModule = BuildingMeshUtils.GetPartNoPosition(glbPrefix, "mid-prebuilt", 0, true);
        GameObject topModule = BuildingMeshUtils.GetPartNoPosition(glbPrefix, "top-prebuilt", 0, true);

        if (baseModule == null && middleModule == null && topModule == null)
        {
            Debug.LogWarning($"PreBuiltBuildingMeshBuilder: プレハブモジュールが見つかりませんでした (prefix={glbPrefix})。白い箱にフォールバックします。");
            return GenerateFallbackBox(buildingData);
        }

        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Material> materials = new List<Material>();
        List<Mesh> temporaryMeshes = new List<Mesh>();

        // --- Stacking ---
        // Stack parts on the floor-height grid. Advance levels only for parts with existing files,
        // so top rests on the ground even without base/mid.
        // maxDrawnTop is the actual rendered mesh top (used for collider height).
        // Unscaled base/top dimensions may differ from floor height, leaving gaps/overlaps; intentional.
        float stackY = 0f;
        float maxDrawnTop = 0f;

        Vector2[] footprint = (buildingData.polygonArray != null && buildingData.polygonArray.Length >= 3)
            ? BuildingMeshUtils.GetFootprint(buildingData)
            : null;

        Quaternion? rotation = null;
        if (footprint != null && footprint.Length >= 2)
        {
            rotation = BuildingRoadAccess.PrebuiltRotation(footprint);
        }

        // fitHeight=null means actual size without scaling (base/top). Only mid Y-scales to floor height.
        // Use alignByOrigin: true to align model origin to ground (or previous level's top).
        if (baseModule != null)
        {
            float drawn = BuildingMeshUtils.AddStackedModule(baseModule, stackY, null, combineInstances, materials, temporaryMeshes, rotation: rotation, alignByOrigin: true);
            maxDrawnTop = Mathf.Max(maxDrawnTop, stackY + drawn);
            stackY += floorHeight;
        }

        if (middleModule != null)
        {
            // base/top consume one floor each; mid fills remaining floors.
            int midCount = Mathf.Max(0, floors - (baseModule != null ? 1 : 0) - (topModule != null ? 1 : 0));
            for (int i = 0; i < midCount; i++)
            {
                BuildingMeshUtils.AddStackedModule(middleModule, stackY, floorHeight, combineInstances, materials, temporaryMeshes, rotation: rotation, alignByOrigin: true);
                stackY += floorHeight;
                maxDrawnTop = Mathf.Max(maxDrawnTop, stackY);
            }
        }

        if (topModule != null)
        {
            float drawn = BuildingMeshUtils.AddStackedModule(topModule, stackY, null, combineInstances, materials, temporaryMeshes, rotation: rotation, alignByOrigin: true);
            maxDrawnTop = Mathf.Max(maxDrawnTop, stackY + drawn);
        }

        // --- Site decorations (fences/tiles/scatter/ground panels) ---
        // Share modular logic: parcel-perimeter fences, site tiles/ground panels, scatter outside the footprint.
        // Render fences/tiles/scatter only when parts exist in a{variant} (or fallback b{bd_type}).
        // Draw ground panels only for master draw_floor=yes; resolve floor-part materials
        // via a{variant}→b{bd_type}→common (draw only for non-null floorMaterial).
        // Prebuilt buildings rest at center terrain elevation (excluded from slope handling),
        // so ultra-near (isDynamic) fences/tiles/ground panels follow terrain.
        Vector2[] fenceFootprint = BuildingMeshUtils.ResolveParcelContext(
            buildingData, out _, out Vector3 centerPointWorld);
        Vector2[] sitePolygon = (fenceFootprint != null && fenceFootprint.Length >= 3) ? fenceFootprint : footprint;
        Material floorMaterial = BuildingMeshUtils.ResolveFloorMaterialFromMaster(buildingData, glbPrefix);
        ModularBodyBuilder.GenerateSiteDecorations(
            glbPrefix, 0,
            fenceFootprint, sitePolygon, sitePolygon,
            floorMaterial,
            centerPointWorld, deformToTerrain: isDynamic,
            combineInstances, materials, temporaryMeshes,
            scatterFootprint: footprint, scatterSeed: buildingData.visual_variant2);

        if (combineInstances.Count == 0)
        {
            CleanupMeshes(temporaryMeshes);
            return GenerateFallbackBox(buildingData);
        }

        Mesh finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
        finalMesh.RecalculateBounds();
        finalMesh.RecalculateNormals();

        CleanupMeshes(temporaryMeshes);

        // Match collider to actual rendered height, keeping parts pickable even when actual dimensions
        // exceed logical height (floors × floor height), such as wind turbines.
        Vector2[] colliderFootprint = footprint;
        float colliderHeight = Mathf.Max(maxDrawnTop, floorHeight);
        Mesh colliderMesh = BuildingMeshBuilder.GenerateColliderMesh(colliderFootprint, colliderHeight);

        return new BuildingMeshResult(finalMesh, materials.ToArray(), colliderMesh);
    }

    /// <summary>
    /// White-box fallback when no part could be obtained.
    /// </summary>
    private static BuildingMeshResult GenerateFallbackBox(BuildingSummary buildingData)
    {
        if (buildingData.polygonArray == null || buildingData.polygonArray.Length < 3)
        {
            Debug.LogWarning($"PreBuiltBuildingMeshBuilder: polygon が不正なため空メッシュを返します (agent_id={buildingData.agent_id})。");
            return new BuildingMeshResult(new Mesh(), null);
        }

        Vector2[] footprint = BuildingMeshUtils.GetFootprint(buildingData);
        int floors = Mathf.Max(1, buildingData.floors ?? 1);
        float floorHeight = BuildingMeshUtils.GetFloorHeight(buildingData);
        return BuildingMeshBuilder.GenerateSimpleBuilding(footprint, floors, floorHeight);
    }

    private static void CleanupMeshes(List<Mesh> meshes)
    {
        foreach (var mesh in meshes)
        {
            if (mesh != null)
            {
                Object.Destroy(mesh);
            }
        }
    }
}
