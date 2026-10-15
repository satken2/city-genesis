using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>アーチの著作周期を保ち、脚の下端を各地点の地盤へ写像する。横幅は PathDrawContext.Warp の倍率(つなぎ目のS字)に従う。</summary>
public static class BrickArcadeGenerator
{
    public static bool HasArcade(PathSummary path) =>
        PathDrawContext.ResolvePrefab($"Models/Paths/p{path.path_variant}/arcade",null,out _) != null;

    public static void Generate(GameObject parent, SplineComputer spline, PathSummary path, float width)
    {
        var prefab=PathDrawContext.ResolvePrefab($"Models/Paths/p{path.path_variant}/arcade",null,out _);
        if(prefab==null) return;
        var filter=prefab.GetComponentInChildren<MeshFilter>();
        var renderer=prefab.GetComponentInChildren<MeshRenderer>();
        if(filter==null || renderer==null) return;
        Mesh source=filter.sharedMesh;
        if(source==null || source.vertexCount>RoadPath.SPLINE_MESH_VERTEX_BUDGET) return;
        var bounds=source.bounds;
        // 部品は上端を原点の高さ（道床面）に、下端を原点より下に作る。横はパスの全幅へ、下端は各地点の地盤まで伸ばす。
        if(bounds.size.z<.01f || bounds.size.x<.01f || bounds.min.y>=0) return;
        float length=spline.CalculateLength();
        if(length<.1f) return;
        int tiles=Mathf.Max(1,Mathf.CeilToInt(length/bounds.size.z));
        var batch=new PathStructureMesh(parent,"BrickArcade",renderer.sharedMaterials);
        var vertices=source.vertices;
        var world=new Vector3[vertices.Length];
        var obstacles=PillarClearance.NeighborProvider?.Invoke(path);
        var warp=PathDrawContext.Warp;
        for(int tile=0;tile<tiles;tile++)
        {
            if(!PathDrawContext.IntersectDetailRange((double)tile/tiles,(double)(tile+1)/tiles,out _,out _)) continue;
            // 同じ長手断面の地盤・スプライン評価を共有し、レンガの頂点数だけ問い合わせない。
            var sections=new System.Collections.Generic.Dictionary<float,(Vector3 point,Vector3 right,float depth,float scale)>();
            for(int i=0;i<vertices.Length;i++)
            {
                Vector3 v=vertices[i];
                if(!sections.TryGetValue(v.z,out var section))
                {
                    float d=(tile+(v.z-bounds.min.z)/bounds.size.z)*length/tiles;
                    double percent=RoadPath.ClampSplinePercent(spline.Travel(0,d,Spline.Direction.Forward));
                    var sample=spline.Evaluate(percent);
                    Vector3 point=sample.position+Vector3.up*RoadPartHeights.BODY_ABOVE_SPLINE;
                    section=(point,Vector3.Cross(Vector3.up,sample.forward).normalized,Mathf.Max(0,point.y-Utils.GetGroundHeight(point)),
                        warp?.Scale!=null?warp.Scale(percent):1f);
                    sections.Add(v.z,section);
                }
                world[i]=section.point+section.right*((v.x-bounds.center.x)*width*section.scale/bounds.size.x)
                    +Vector3.up*(v.y/-bounds.min.y*section.depth);
            }
            // 開口内の空間も含めてアーチ全体を判定し、干渉する周期を丸ごと省く。
            var occupied=new Bounds(world[0],Vector3.zero);
            for(int i=1;i<world.Length;i++) occupied.Encapsulate(world[i]);
            // 同じ標高の隣接線路は道床上を通るため、支持構造の占有に含めない。
            occupied.SetMinMax(occupied.min,new Vector3(occupied.max.x,
                Mathf.Max(occupied.min.y,occupied.max.y-.01f),occupied.max.z));
            if(!PillarClearance.Intersects(occupied,obstacles)) batch.Append(source,world);
        }
        batch.Finish();
    }
}
