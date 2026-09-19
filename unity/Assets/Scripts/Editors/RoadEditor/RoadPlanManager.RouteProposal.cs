using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

public partial class RoadPlanManager
{
    private string routeRevision = Guid.NewGuid().ToString("N");
    private List<PathBuildPoint> routeUndoPoints;
    private bool routeUndoCurved;
    private string routeUndoRevision;

    private sealed class ProposedRoute
    {
        public float[][] points;
        public float[][] controls_in;
        public float[][] controls_out;
        public float[] embankments;
        public int[] generated;
        public string height_mode;
    }

    /// <summary>探索用の座標は絶対標高。保存用の相対座標と混在させない。</summary>
    private Dictionary<string, object> BuildRouteProposalPayload()
    {
        var positions = new List<float[]>();
        var incoming = new List<float[]>();
        var outgoing = new List<float[]>();
        var embankments = new List<float>();
        var generated = new List<int>();
        foreach (var point in points)
        {
            positions.Add(RouteVector(point.position));
            incoming.Add(RouteVector(point.control_in ?? point.position));
            outgoing.Add(RouteVector(point.control_out ?? point.position));
            embankments.Add(point.embankmentHeight);
            generated.Add(point.proposalGenerated ? 1 : 0);
        }

        var components = new List<Dictionary<string, object>>();
        var offsets = LateralOffsets != null && LateralOffsets.Length > 0
            ? LateralOffsets : new[] { 0f };
        for (int i = 0; i < offsets.Length; i++)
        {
            components.Add(new Dictionary<string, object>
            {
                ["path_variant"] = PathSubtype,
                ["lateral_offset"] = offsets[i],
                ["height_offset"] = 0f,
                ["travel_direction"] = TravelDirections != null && i < TravelDirections.Length
                    ? TravelDirections[i] : Constants.DIR_BOTH,
            });
        }
        var master = MasterDataStore.Instance?.GetPathMaster(PathSubtype);
        return new Dictionary<string, object>
        {
            ["revision"] = routeRevision,
            ["points"] = positions,
            ["controls_in"] = incoming,
            ["controls_out"] = outgoing,
            ["embankments"] = embankments,
            ["generated"] = generated,
            ["can_undo"] = routeUndoPoints != null && routeUndoRevision == routeRevision,
            // 提案UIは独立した2D編集ウィンドウにあるため、探索条件も形状と同じ通知へ載せる。
            ["enabled"] = IsCurveAllowed() && IsAlignmentTarget() && !IsStationHouse(),
            ["height_editable"] = IsHeightEditable(),
            ["path_variant"] = PathSubtype,
            ["follow_terrain"] = PathFollowTerrainOverride,
            ["components"] = components,
            ["platform_side"] = PlatformSide,
            ["align_station_tracks"] = PathType == Constants.PATH_TRACK
                && master != null && (master.platform_width ?? 0f) > 0f,
            ["exclude_path_id"] = pathId != 0 ? (object)pathId : null,
        };
    }

    private static float[] RouteVector(Vector3 v) => new[] { v.x, v.y, v.z };

    private static bool ValidRouteVectors(float[][] values, int count)
    {
        if (values == null || values.Length != count) return false;
        foreach (var row in values)
        {
            if (row == null || row.Length != 3) return false;
            foreach (float v in row) if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        }
        return true;
    }

    /// <summary>応答待ちの編集やセッション切替を世代で検出し、古い提案で上書きしない。</summary>
    private void ApplyRouteProposal(string revision, string json)
    {
        if (revision != routeRevision || !IsCurveAllowed()) { EmitShapeUpdate(); return; }
        ProposedRoute route;
        try { route = JsonConvert.DeserializeObject<ProposedRoute>(json ?? "null"); }
        catch (JsonException) { return; }
        int count = route?.points?.Length ?? 0;
        if (count < 2 || count > 40 || !ValidRouteVectors(route.points, count)
            || !ValidRouteVectors(route.controls_in, count) || !ValidRouteVectors(route.controls_out, count)
            || route.embankments == null || route.embankments.Length != count
            || route.generated == null || route.generated.Length != count
            || (route.height_mode != "fixed" && route.height_mode != "adjust")) return;
        if (route.height_mode == "adjust" && !IsHeightEditable()) return;

        var original = points.FindAll(p => !p.proposalGenerated);
        var replacement = new List<PathBuildPoint>();
        int authored = 0;
        for (int i = 0; i < count; i++)
        {
            float emb = route.embankments[i];
            if (float.IsNaN(emb) || float.IsInfinity(emb) || Mathf.Abs(emb) > 5f) return;
            if (route.generated[i] != 0 && route.generated[i] != 1) return;
            var a = route.points[i];
            var p = new Vector3(a[0], a[1], a[2]);
            bool generated = route.generated[i] == 1;
            if ((i == 0 || i == count - 1) && generated) return;
            if (!generated)
            {
                if (authored >= original.Count) return;
                Vector3 old = original[authored].position;
                if (Mathf.Abs(old.x-p.x) > .01f || Mathf.Abs(old.z-p.z) > .01f) return;
                if ((route.height_mode == "fixed" || authored == 0 || authored == original.Count-1)
                    && Mathf.Abs(old.y-p.y) > .02f) return;
                if (Mathf.Abs(original[authored].embankmentHeight-emb) > .001f) return;
                // 固定した座標は応答の丸め値で置き換えず、編集時の値をそのまま保つ。
                p.x = old.x; p.z = old.z;
                if (route.height_mode == "fixed" || authored == 0 || authored == original.Count-1) p.y = old.y;
                authored++;
            }
            var point = new PathBuildPoint(p, i == 0 ? PathPointRole.Start : i == count-1 ? PathPointRole.End : PathPointRole.Waypoint);
            point.proposalGenerated = generated;
            point.embankmentHeight = emb;
            var ci = route.controls_in[i]; var co = route.controls_out[i];
            if (i > 0) { point.control_in = new Vector3(ci[0], ci[1], ci[2]); point.control_in_manual = true; }
            if (i < count-1) { point.control_out = new Vector3(co[0], co[1], co[2]); point.control_out_manual = true; }
            replacement.Add(point);
        }
        if (authored != original.Count) return;
        routeUndoPoints = new List<PathBuildPoint>(points);
        routeUndoCurved = Curved;
        points.Clear(); points.AddRange(replacement);
        Curved = true;
        SelectPoint(-1);
        InvalidateShapeCache();
        routeUndoRevision = routeRevision;
        Update3DPreview();
        EmitShapeUpdate(finalized: true);
        EmitSelectNode();
    }

    private void UndoRouteProposal(string revision)
    {
        if (revision != routeRevision || routeUndoRevision != routeRevision || routeUndoPoints == null) return;
        points.Clear(); points.AddRange(routeUndoPoints);
        Curved = routeUndoCurved;
        routeUndoPoints = null;
        SelectPoint(-1);
        InvalidateShapeCache();
        Update3DPreview();
        EmitShapeUpdate(finalized: true);
        EmitSelectNode();
    }
}
