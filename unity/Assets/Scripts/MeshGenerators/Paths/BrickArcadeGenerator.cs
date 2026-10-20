using Dreamteck.Splines;
using GeneratedModels;
using UnityEngine;

/// <summary>Preserve authored arch periods and map leg bottoms to local ground. Width follows PathDrawContext.Warp scaling (S curves at joins).</summary>
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
        // Author parts with top at origin height (track-bed surface), bottom below origin. Stretch sideways to full path width and bottom to local ground.
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
            // Share ground/spline evaluation for vertices at the same longitudinal cross-section, avoiding one query per brick vertex.
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
            // Test the entire arch including opening space; omit the whole period if obstructed.
            var occupied=new Bounds(world[0],Vector3.zero);
            for(int i=1;i<world.Length;i++) occupied.Encapsulate(world[i]);
            // Adjacent tracks at the same elevation run above the bed, so exclude them from support-structure occupancy.
            occupied.SetMinMax(occupied.min,new Vector3(occupied.max.x,
                Mathf.Max(occupied.min.y,occupied.max.y-.01f),occupied.max.z));
            if(!PillarClearance.Intersects(occupied,obstacles)) batch.Append(source,world);
        }
        batch.Finish();
    }
}
