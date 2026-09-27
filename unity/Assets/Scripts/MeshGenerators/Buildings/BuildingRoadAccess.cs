using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>敷地の開口。XZは建物中心からの相対座標、Yは舗装面。描画LODや建物IDで形を変えない。</summary>
public sealed class BuildingRoadOpening
{
    public Vector3 A, B, Outward;
    public Vector2[] Apron;
    public bool FollowsTerrain;
    public float? MinGround;  // 舗装の基準の地面の下限(絶対標高)。港の施設は岸壁の天端より下に置かないため
    public string Flow = "both";
    public Vector3 Position => (A + B) * .5f;
    public float Width => Vector3.Distance(A, B);
}

/// <summary>配置計画とモデル内の非描画マーカーから、道路へ開く間口を読む。</summary>
public static class BuildingRoadAccess
{
    public const float ApronDepth = 2f;

    public static Quaternion PrebuiltRotation(Vector2[] footprint)
    {
        Vector2 edge = (footprint[1] - footprint[0]).normalized;
        return Quaternion.Euler(0, -Mathf.Atan2(edge.y, edge.x) * Mathf.Rad2Deg + 90f, 0);
    }

    /// <summary>road_access_{both|entry|exit}_* の子 a/b/inside を使う。Blenderの同名サフィックスは無視する。</summary>
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
        // 間口の正は無印モデル。カメラ距離でマーカーの選択を変えない。
        var baseModel = ModelManager.Instance?.GetModel($"Models/Buildings/{prefix}/0/base-prebuilt", true);
        var midModel = ModelManager.Instance?.GetModel($"Models/Buildings/{prefix}/0/mid-prebuilt", true);
        var topModel = ModelManager.Instance?.GetModel($"Models/Buildings/{prefix}/0/top-prebuilt", true);
        int midCount = Mathf.Max(0, Mathf.Max(1, data.floors ?? 1) - (baseModel != null ? 1 : 0) - (topModel != null ? 1 : 0));
        var model = baseModel != null ? baseModel : midModel != null && midCount > 0 ? midModel : topModel;
        result = ReadModel(model, PrebuiltRotation(footprint));
        if (model != null && model == midModel && result.Count > 0)
        {
            // 地上段がmidなら、描画と同じ原点基準の階高スケールを舗装高にも適用する。
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
    /// 港の施設モジュール(port_building)のマーカーを、描画と同じ置き方(<see cref="PortLayout.PlaceModule"/>)で建物中心基準へ移す。
    /// モジュールは区画の辺0の中点の地面(岸壁の天端 <see cref="PortBuildingMeshBuilder.QUAY_TOP_HEIGHT"/> 以上)に接地するので、
    /// 舗装高は開口の地面に追従させ、同じ下限を持たせる。
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
