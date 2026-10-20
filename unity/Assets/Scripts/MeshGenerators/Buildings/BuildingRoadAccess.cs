using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>Site opening. XZ relative to the building center; Y at the paved surface. Shape is independent of rendering LOD and building ID.</summary>
public sealed class BuildingRoadOpening
{
    public Vector3 A, B, Outward;
    public Vector2[] Apron;
    public bool FollowsTerrain;
    public float? MinGround;  // Minimum ground reference for pavement (absolute elevation), keeping port facilities above the quay top.
    public string Flow = "both";
    public Vector3 Position => (A + B) * .5f;
    public float Width => Vector3.Distance(A, B);
}

/// <summary>Read the road-facing opening from the placement plan and non-rendered model markers.</summary>
public static class BuildingRoadAccess
{
    public const float ApronDepth = 2f;

    /// <summary>
    /// Orientation of prebuilt-style parts (prebuilt building levels, whole-building distant models, and port facility modules). Authored with local +Z
    /// along the front edge (vertex 0→1) and +X toward the road (−Y and −X in Blender), 90° different from wall parts.
    /// </summary>
    public static Quaternion PrebuiltRotation(Vector2[] footprint)
    {
        Vector2 edge = (footprint[1] - footprint[0]).normalized;
        return Quaternion.Euler(0, -Mathf.Atan2(edge.y, edge.x) * Mathf.Rad2Deg + 90f, 0);
    }

    /// <summary>
    /// Use children a/b/inside of road_access_{both|entry|exit}_*. Ignore Blender's same-name suffixes.
    /// a/b are pavement edge endpoints (distance gives opening width, midpoint gives connection); inside lies inward from the midpoint to determine the road-facing direction.
    /// </summary>
    public static List<BuildingRoadOpening> ReadModel(GameObject model, Quaternion rotation)
    {
        var result = new List<BuildingRoadOpening>();
        if (model == null) return result;
        var roots = new List<Transform>(model.GetComponentsInChildren<Transform>(true));
        roots.Sort((a, b) => StringComparer.Ordinal.Compare(a.name, b.name));
        foreach (var root in roots)
        {
            if (!root.name.StartsWith("road_access_", StringComparison.Ordinal)) continue;
            string[] name = root.name.Split('_');
            if (name.Length < 4 || (name[2] != "both" && name[2] != "entry" && name[2] != "exit")) continue;
            Transform a = null, b = null, inside = null;
            foreach (Transform child in root)
            {
                string key = child.name.Split('.')[0];
                if (key == "a") a = child;
                if (key == "b") b = child;
                if (key == "inside") inside = child;
            }
            if (a == null || b == null || inside == null) continue;
            Vector3 Local(Transform t) => rotation * model.transform.InverseTransformPoint(t.position);
            Vector3 pa = Local(a), pb = Local(b), mid = (pa + pb) * .5f;
            Vector3 outward = Vector3.ProjectOnPlane(mid - Local(inside), Vector3.up).normalized;
            if (Vector3.Distance(pa, pb) < .2f || Mathf.Abs(pa.y - pb.y) > .005f || outward.sqrMagnitude < .9f) continue;
            Vector3 along = Vector3.ProjectOnPlane(pb - pa, Vector3.up).normalized;
            if (Mathf.Abs(Vector3.Dot(along, outward)) > .05f) continue;
            result.Add(new BuildingRoadOpening { A = pa, B = pb, Outward = outward, Flow = name[2],
                Apron = new[] { XZ(pa), XZ(pb), XZ(pb - outward * ApronDepth), XZ(pa - outward * ApronDepth) } });
        }
        return result;
    }

    public static List<BuildingRoadOpening> Resolve(BuildingSummary data)
    {
        var result = new List<BuildingRoadOpening>();
        if (data == null || (data.status != Constants.STATUS_ACTIVE && data.status != Constants.STATUS_PAUSED
            && data.status != Constants.STATUS_SCHEDULED_DEMOLITION)) return result;
        var master = MasterDataStore.Instance?.GetAgentMaster(data.variant);
        if (master == null) return result;
        if (master.model_type == Constants.MODEL_ROTARY)
            return BuildingRoadAccessIndex.GetOpenSpacePlan(data).Openings;
        if (master.model_type == Constants.MODEL_PORT) return ResolvePort(data);
        if (master.model_type != Constants.MODEL_PREBUILT) return result;
        Vector2[] footprint = BuildingMeshUtils.GetFootprint(data);
        if (footprint == null || footprint.Length < 3) return result;
        string prefix = BuildingMeshUtils.GetGlbPrefix1(data);
        // Standard models define openings. Do not change marker selection with camera distance.
        var baseModel = ModelManager.Instance?.GetModel($"Models/Buildings/{prefix}/0/base-prebuilt", true);
        var midModel = ModelManager.Instance?.GetModel($"Models/Buildings/{prefix}/0/mid-prebuilt", true);
        var topModel = ModelManager.Instance?.GetModel($"Models/Buildings/{prefix}/0/top-prebuilt", true);
        int midCount = Mathf.Max(0, Mathf.Max(1, data.floors ?? 1) - (baseModel != null ? 1 : 0) - (topModel != null ? 1 : 0));
        var model = baseModel != null ? baseModel : midModel != null && midCount > 0 ? midModel : topModel;
        result = ReadModel(model, PrebuiltRotation(footprint));
        if (model != null && model == midModel && result.Count > 0)
        {
            // If the ground level is mid, apply the same origin-based floor-height scaling as rendering to pavement height.
            var mesh = BuildingMeshUtils.CombineModuleMeshes(model, out _);
            if (mesh != null)
            {
                float scaleY = BuildingMeshUtils.GetFloorHeight(data) / Mathf.Max(mesh.bounds.max.y, .01f);
                foreach (var opening in result) { opening.A.y *= scaleY; opening.B.y *= scaleY; }
                UnityEngine.Object.Destroy(mesh);
            }
        }
        return result;
    }

    /// <summary>
    /// Transform port_building module markers relative to the building center with the same placement as rendering (<see cref="PortLayout.PlaceModule"/>).
    /// The module rests on the ground at the midpoint of parcel edge 0 (at least the quay top <see cref="PortBuildingMeshBuilder.QUAY_TOP_HEIGHT"/>),
    /// so pavement follows the opening's ground height with the same minimum.
    /// </summary>
    static List<BuildingRoadOpening> ResolvePort(BuildingSummary data)
    {
        var result = new List<BuildingRoadOpening>();
        Vector2[] footprint = BuildingMeshUtils.GetFootprint(data);
        if (footprint == null || footprint.Length < 3) return result;
        var model = PortBuildingMeshBuilder.ResolveModulePart(data);
        var bounds = PortLayout.GetModelBounds(model);
        if (bounds == null) return result;
        var module = PortLayout.PlaceModule(footprint, bounds.Value);
        if (module == null) return result;
        var offset = new Vector3(module.Position.x, 0, module.Position.y);
        foreach (var opening in ReadModel(model, module.Rotation))
        {
            opening.A += offset; opening.B += offset;
            for (int i = 0; i < opening.Apron.Length; i++) opening.Apron[i] += module.Position;
            opening.FollowsTerrain = true;
            opening.MinGround = PortBuildingMeshBuilder.QUAY_TOP_HEIGHT;
            result.Add(opening);
        }
        return result;
    }

    public static Vector2 XZ(Vector3 point) => new Vector2(point.x, point.z);
}
