using System;
using System.Collections.Generic;
using GeneratedModels;
using UnityEngine;

/// <summary>送電線の鉄塔・碍子取付点・径間のたるみを同じ配置計画から生成する。</summary>
public sealed class PowerLinePath : IPathMeshGenerator
{
    public static Func<int, List<PathSummary>> ConnectedPaths;
    public static bool Handles(PathSummary path) => path != null && (path.path_variant == Constants.PATH_VARIANT_GRID || path.path_variant == 1102);
    public struct Support
    {
        public Vector3 position;
        public Quaternion rotation;
        public int variant, node;
        public float railLeft, railRight, feedSide;
        public bool IsRailGantry => railLeft > 0;
    }

    public static Vector3 Attachment(int variant, int circuit, int bundle = 0)
    {
        if (variant == 1102)
            return circuit == 2 ? new Vector3(0, 12.8f, 0) : new Vector3(circuit == 0 ? -2.7f : 2.7f, 12.8f, 0);
        int level = circuit / 2;
        return new Vector3((circuit % 2 == 0 ? -1 : 1) * (6.8f - .7f * level) + (bundle == 0 ? -.22f : .22f), 23 + 5 * level, 0);
    }

    static float BaseHeight(int variant) => MasterDataStore.Instance?.GetPathMaster(variant)?.base_height ?? (variant == Constants.PATH_VARIANT_GRID ? 20 : 10);
    static Vector3 Point(List<float> p, float baseHeight)
    {
        var v = new Vector3(p[0], 0, p[p.Count - 1]);
        v.y = Utils.GetGroundHeight(v) + (p.Count > 2 ? p[1] : baseHeight) - baseHeight;
        return v;
    }
    static Vector3 Direction(Vector3 v)
    {
        v.y = 0;
        return v.sqrMagnitude > .0001f ? v.normalized : Vector3.forward;
    }

    public static Support Endpoint(PathSummary path, int node, Vector3 position, Vector3 forward)
    {
        PathSummary chosen = path;
        var connected = node > 0 ? ConnectedPaths?.Invoke(node) : null;
        if (connected != null)
            foreach (var candidate in connected)
                if (Handles(candidate) && candidate.path_id != path.path_id && (candidate.path_variant < chosen.path_variant ||
                    candidate.path_variant == chosen.path_variant && candidate.path_id < chosen.path_id)) chosen = candidate;
        var a = chosen.GetStartPointList();
        var b = chosen.GetEndPointList();
        if (node > 0 && a != null && b != null && a.Count >= 2 && b.Count >= 2)
        {
            bool start = chosen.start_node_id == node;
            position = Point(start ? a : b, BaseHeight(chosen.path_variant));
            var waypoints = chosen.GetWaypointList();
            Vector3 next = Point(start ? b : a, BaseHeight(chosen.path_variant));
            if (waypoints != null && waypoints.Count > 0)
                next = waypoints[start ? 0 : waypoints.Count - 1].GetAbsolutePosition();
            forward = Direction(next - position);
        }
        PathSummary rail = null;
        if (connected != null)
            foreach (var candidate in connected)
                if (PowerLineRailGantry.CanConnect(chosen, candidate) && (rail == null || candidate.path_id < rail.path_id)) rail = candidate;
        if (rail != null && PowerLineRailGantry.TryPlan(rail, node, chosen.path_variant, forward, out var gantry)) return gantry;
        // 軸の符号を固定し、始端／終端が逆のパスでも左右の回路を交差させない。
        forward = Direction(forward);
        if (forward.z < -.0001f || Mathf.Abs(forward.z) < .0001f && forward.x < 0) forward = -forward;
        return new Support { position = position, rotation = Quaternion.LookRotation(forward), variant = chosen.path_variant, node = node };
    }

    public static List<Support> Plan(PathSummary path, List<Vector3> points)
    {
        var result = new List<Support>();
        if (points == null || points.Count < 2) return result;
        var anchors = new List<Vector3> { points[0] };
        var waypoints = path.GetWaypointList();
        if (waypoints != null) foreach (var wp in waypoints)
            anchors.Add(wp.GetAbsolutePosition() - Vector3.up * BaseHeight(path.path_variant));
        anchors.Add(points[points.Count - 1]);
        float spacing = Mathf.Max(10, MasterDataStore.Instance?.GetPathMaster(path.path_variant)?.pillar_spacing ?? 120);
        for (int span = 0; span < anchors.Count - 1; span++)
        {
            var a = anchors[span]; var b = anchors[span + 1];
            int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a,b) / spacing));
            for (int j = span == 0 ? 0 : 1; j <= count; j++)
            {
                float t = (float)j / count;
                Vector3 p = Vector3.Lerp(a,b,t);
                if (j > 0 && j < count && PathManager.GetPathFollowTerrain(path))
                    p.y = Utils.GetGroundHeight(p) + Mathf.Lerp(a.y-Utils.GetGroundHeight(a), b.y-Utils.GetGroundHeight(b),t);
                int node = span == 0 && j == 0 ? path.start_node_id : span == anchors.Count-2 && j == count ? path.end_node_id : 0;
                result.Add(Endpoint(path,node,p,b-a));
            }
        }
        return result;
    }

    public PathMeshResult Generate(PathSummary path, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        var root = new GameObject($"PowerLine_{path.path_id}");
        PathDrawContext.SuppressDetailRange = true;
        GenerateInto(root,path,points,PathDrawContext.CurrentDrawLod);
        var elevated = new List<Vector3>();
        foreach (var p in points) elevated.Add(p + Vector3.up * (path.path_variant == Constants.PATH_VARIANT_GRID ? 26 : 13));
        return new PathMeshResult(PathMeshFactory.BuildStripMesh(elevated,path.path_variant == Constants.PATH_VARIANT_GRID ? 14 : 6),true,root);
    }

    public static void GenerateInto(GameObject root, PathSummary path, List<Vector3> points, DrawLod lod)
    {
        var supports = Plan(path,points);
        var reference = PathDrawContext.ResolvePrefab($"Models/Paths/p{path.path_variant}/pillar_cap",null,out _);
        Material metal = null;
        if (reference != null)
            foreach (var renderer in reference.GetComponentsInChildren<MeshRenderer>())
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && material.name.StartsWith("PATH_METAL",StringComparison.Ordinal))
                        metal = RoadPath.GetOrCreateRoadMaterial(material);
        metal ??= RoadPath.GetOrCreateRoadMaterialByName("BuildingMaterial");
        var wires = new PathStructureMesh(root,"PowerConductors",new[] { metal });
        bool far = lod == DrawLod.Far;
        bool constructing = path.status == Constants.STATUS_CONSTRUCTING;
        float progress = constructing ? ConstructionPathBuilder.Progress(path) : 1;
        for (int i = 0; i < supports.Count; i++)
        {
            var s = supports[i];
            if (constructing && (float)(i+1)/supports.Count > ConstructionPathBuilder.PierProgress(progress)) continue;
            var holder = new GameObject("PowerSupport"); holder.transform.SetParent(root.transform,false);
            var visual = new GameObject("TowerVisual"); visual.transform.SetParent(holder.transform,false);
            if (s.IsRailGantry) PowerLineRailGantry.Generate(visual,s,metal,lod);
            else GenerateTower(visual,s,metal,far);
            if (s.node > 0) holder.AddComponent<PowerLineSupport>().Initialize(s.node,path.path_id,visual,lod);
            if (i == 0) continue;
            if (constructing && (float)i/(supports.Count-1) > ConstructionPathBuilder.DeckProgress(progress)) continue;
            var previous = supports[i-1];
            bool large = path.path_variant == Constants.PATH_VARIANT_GRID;
            int circuits = large ? 6 : 3;
            int bundles = large && !far ? 2 : 1;
            for (int circuit=0; circuit<circuits; circuit++) for (int bundle=0;bundle<bundles;bundle++)
            {
                Vector3 Attach(Support support)
                {
                    return SupportAttachment(support,circuit,bundle,far);
                }
                Cable(wires,Attach(previous),Attach(s),far,large ? .045f : .032f);
            }
            Cable(wires,ShieldAttachment(previous),ShieldAttachment(s),far,.025f);
        }
        wires.Finish();
    }

    public static Vector3 SupportAttachment(Support support, int circuit, int bundle = 0, bool far = false)
    {
        int c = support.variant == 1102 ? circuit % 3 : circuit;
        if (support.IsRailGantry) return PowerLineRailGantry.Attachment(support,c,bundle,far);
        var local = Attachment(support.variant,c,bundle);
        if (far && support.variant == Constants.PATH_VARIANT_GRID) local.x += .22f;
        return support.position + support.rotation * local;
    }

    public static Vector3 ShieldAttachment(Support support) => support.IsRailGantry
        ? PowerLineRailGantry.ShieldAttachment(support)
        : support.position + Vector3.up * (support.variant == Constants.PATH_VARIANT_GRID ? 38 : 17);

    static void GenerateTower(GameObject visual, Support s, Material metal, bool far)
    {
        var prefab = far ? null : PathDrawContext.ResolvePrefab($"Models/Paths/p{s.variant}/pillar_cap",null,out _);
        if (prefab != null)
        {
            var tower = UnityEngine.Object.Instantiate(prefab,visual.transform);
            tower.transform.SetPositionAndRotation(s.position,s.rotation);
            foreach (var renderer in tower.GetComponentsInChildren<MeshRenderer>())
            {
                var mats = renderer.sharedMaterials;
                for (int k=0;k<mats.Length;k++) mats[k]=RoadPath.GetOrCreateRoadMaterial(mats[k]);
                renderer.sharedMaterials=mats;
            }
        }
        else GenerateFarTower(visual,s,metal);
        var foundation = new PathStructureMesh(visual,"TowerFoundations",new[] { metal });
        float foot = s.variant == Constants.PATH_VARIANT_GRID ? 3.5f : .9f;
        for (int x=-1;x<=1;x+=2) for(int z=-1;z<=1;z+=2)
        {
            var top=s.position+s.rotation*new Vector3(x*foot,0,z*foot);
            var bottom=new Vector3(top.x,Utils.GetGroundHeight(top),top.z);
            if(top.y>bottom.y+.1f) foundation.Bar(bottom,top,s.variant==Constants.PATH_VARIANT_GRID?.22f:.12f,sides:4);
        }
        foundation.Finish();
    }

    public static float Sag(float length) => Mathf.Min(length * .045f, 6);
    static void Cable(PathStructureMesh mesh, Vector3 a, Vector3 b, bool far, float radius)
    {
        float length=Vector3.Distance(a,b);
        int steps=Mathf.Clamp(Mathf.CeilToInt(length/(far?18:5)),4,80);
        Vector3 previous=a;
        for (int i=1;i<=steps;i++)
        {
            float t=(float)i/steps;
            Vector3 p=Vector3.Lerp(a,b,t)-Vector3.up*(Sag(length)*4*t*(1-t));
            mesh.Bar(previous,p,radius,sides:far?3:6);
            previous=p;
        }
    }

    static void GenerateFarTower(GameObject parent, Support s, Material material)
    {
        var mesh=new PathStructureMesh(parent,"FarPowerTower",new[] {material});
        bool large=s.variant==Constants.PATH_VARIANT_GRID;
        float width=large?3.5f:.9f, height=large?38:17;
        Vector3 P(float x,float y,float z)=>s.position+s.rotation*new Vector3(x,y,z);
        for(int x=-1;x<=1;x+=2) for(int z=-1;z<=1;z+=2)
            mesh.Bar(P(x*width,0,z*width),P(x*.35f,height,z*.35f),large?.16f:.08f,sides:4);
        int levels=large?3:2;
        for(int i=0;i<levels;i++)
        {
            float y=large?25+5*i:i==0?13.8f:16, reach=large?6.8f-.7f*i:i==0?2.7f:2.3f;
            mesh.Bar(P(-reach,y,0),P(reach,y,0),.12f,sides:4);
            if(large || i==0)
                foreach(int side in new[]{-1,1}) mesh.Bar(P(side*reach,y,0),P(side*reach,y-(large?2:1),0),.12f,sides:4);
        }
        for(int i=0;i<(large?5:3);i++)
        {
            float a=height*i/(large?5:3), b=height*(i+1)/(large?5:3);
            float wa=Mathf.Lerp(width,.35f,a/height),wb=Mathf.Lerp(width,.35f,b/height);
            mesh.Bar(P(-wa,a,0),P(wb,b,0),.075f,sides:3);
            mesh.Bar(P(wa,a,0),P(-wb,b,0),.075f,sides:3);
        }
        mesh.Finish();
    }
}
