using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

/// <summary>配置計画を地形追従面と実寸FBXへ変換する。経路探索データは変更しない。</summary>
public sealed class OpenSpaceBuildingMeshBuilder : IBuildingMeshGenerator
{
    static readonly string[] Parts={"floor","site_asphalt","site_paving","site_grass","site_marking","site_stop","site_bench","site_tree","site_shelter"};
    public BuildingMeshResult Generate(BuildingSummary data,bool isDynamic=false)
    {
        BuildingMeshUtils.ResolveParcelContext(data,false,out _,out Vector3 center);
        // SHAPE_FULLの建物輪郭は区画全面かつ最初の辺が接道側に正規化されている。
        var polygon=BuildingMeshUtils.GetFootprint(data);
        var naming=BuildingMeshUtils.ResolveModelNaming(data);
        var parts=new Dictionary<string,GameObject>();
        foreach(string part in Parts)
        {
            var prefab=BuildingMeshUtils.GetPartBySuffixScan(naming.Prefix,part,BuildingMeshUtils.ComputeVariantNumber(data.visual_variant));
            if(prefab!=null)parts[part]=prefab;
        }
        int modelType=MasterDataStore.Instance.GetAgentMaster(data.variant).model_type ?? Constants.MODEL_PARKING;
        var plan=OpenSpaceLayout.Create(polygon,modelType,data.visual_variant2);
        return Build(plan,parts,center,true,BuildingMeshUtils.ResolveFloorMaterialFromMaster(data,naming.Prefix));
    }
    /// <summary>検証・展示も本編と同じ配置と実FBXを使う。flatは地形の無い展示専用。</summary>
    public static BuildingMeshResult Build(OpenSpaceLayout.Plan plan,Dictionary<string,GameObject> parts,Vector3 center,bool terrain,Material floorOverride=null)
    {
        var instances=new List<CombineInstance>();var materials=new List<Material>();var owned=new List<Mesh>();
        var colliders=new List<CombineInstance>();
        var loaded=new Dictionary<string,Mesh>();var loadedMaterials=new Dictionary<string,Material[]>();
        Material Resolve(string key)
        {
            if(parts.TryGetValue(key,out var prefab))
            {
                var mats=BuildingMeshUtils.GetModuleMaterials(prefab);
                if(mats!=null && mats.Length>0)return mats[0];
            }
            return floorOverride;
        }
        void Append(Mesh mesh,Material[] mats,Matrix4x4 transform)
        {
            if(mesh==null || mats==null || mats.Length==0)return;
            for(int s=0;s<mesh.subMeshCount;s++)
            {
                instances.Add(new CombineInstance {mesh=mesh,subMeshIndex=s,transform=transform});
                materials.Add(mats[Mathf.Min(s,mats.Length-1)]);
            }
        }
        try
        {
            if(plan.Polygon==null || plan.Polygon.Length<3)return null;
            Mesh floor=terrain?ModularBodyBuilder.GenerateTerrainConformingFloorMesh(plan.Polygon,center):BuildingMeshBuilder.GenerateHorizontalPolygonMesh(plan.Polygon,0f);
            if(floor!=null)
            {
                owned.Add(floor);colliders.Add(new CombineInstance {mesh=floor,transform=Matrix4x4.identity});
                var mat=floorOverride??Resolve("floor")??BuildingMeshBuilder.GetCommonConcreteMaterial();
                Append(floor,new[]{mat},Matrix4x4.identity);
            }
            foreach(var surface in plan.Surfaces)
            {
                Material mat=Resolve(surface.Material);if(mat==null)continue;
                Mesh mesh=SurfaceMesh(surface.Polygon,center,terrain,surface.Lift);
                if(mesh==null)continue;
                owned.Add(mesh);Append(mesh,new[]{mat},Matrix4x4.identity);
            }
            foreach(var prop in plan.Props)
            {
                if(!parts.TryGetValue(prop.Part,out var prefab))continue;
                if(!loaded.TryGetValue(prop.Part,out Mesh mesh))
                {
                    mesh=BuildingMeshUtils.CombineModuleMeshes(prefab,out Material[] partMaterials);
                    if(mesh==null)continue;
                    owned.Add(mesh);loaded[prop.Part]=mesh;loadedMaterials[prop.Part]=partMaterials;
                }
                Quaternion rotation=Quaternion.LookRotation(new Vector3(prop.Forward.x,0,prop.Forward.y));
                Bounds b=mesh.bounds;
                var rect=new Vector2[4];
                for(int i=0;i<4;i++)
                {
                    var local=new Vector3(i==0||i==3?b.min.x:b.max.x,0,i<2?b.min.z:b.max.z);
                    var transformed=rotation*local;
                    rect[i]=prop.Position+new Vector2(transformed.x,transformed.z);
                }
                // 実FBXの占有範囲で再確認し、モデル差し替え時の境界への突き出しも防ぐ。
                if(!OpenSpaceLayout.ContainsPolygon(plan.Polygon,rect))continue;
                float y=terrain?Utils.GetGroundHeight(new Vector3(center.x+prop.Position.x,1000,center.z+prop.Position.y))-center.y:0f;
                var position=new Vector3(prop.Position.x,y+.02f,prop.Position.y);
                Append(mesh,loadedMaterials[prop.Part],Matrix4x4.TRS(position,rotation,Vector3.one));
                var collider=BuildingMeshBuilder.GenerateColliderMesh(rect,Mathf.Max(.1f,b.max.y));
                if(collider!=null){owned.Add(collider);colliders.Add(new CombineInstance {mesh=collider,transform=Matrix4x4.Translate(new Vector3(0,y,0))});}
            }
            var result=new Mesh {name="OpenSpace",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            result.CombineMeshes(instances.ToArray(),false,true);result.RecalculateBounds();
            var collision=new Mesh {name="OpenSpaceCollider",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            collision.CombineMeshes(colliders.ToArray(),true,true);collision.RecalculateBounds();
            return BuildingMeshBuilder.MergeSubMeshes(new BuildingMeshResult(result,materials.ToArray(),collision));
        }
        finally {foreach(var mesh in owned)Object.Destroy(mesh);}
    }
    static Mesh SurfaceMesh(Vector2[] polygon,Vector3 center,bool terrain,float lift)
    {
        if(terrain)
        {
            Mesh aligned=BuildingMeshBuilder.GenerateTerrainSurfaceMesh(polygon,center,ModularBodyBuilder.GROUND_OFFSET+lift);
            if(aligned!=null)return aligned;
        }
        var points=new List<Vector2>(polygon);
        var triangles=BuildingMeshBuilder.TriangulateOrFan(polygon);
        if(triangles.Count<3)return null;
        var subdivided=BuildingMeshBuilder.SubdivideTrianglesByEdgeLength(points,triangles,2.5f);
        var vertices=new Vector3[points.Count];var uv=new Vector2[points.Count];
        for(int i=0;i<points.Count;i++)
        {
            Vector2 p=points[i];
            float y=terrain?Utils.GetGroundHeight(new Vector3(center.x+p.x,1000,center.z+p.y))-center.y:0;
            vertices[i]=new Vector3(p.x,y+ModularBodyBuilder.GROUND_OFFSET+lift,p.y);uv[i]=p;
        }
        var indices=new List<int>();
        foreach(var (a,b,c) in subdivided){indices.Add(a);indices.Add(c);indices.Add(b);}
        var mesh=new Mesh {indexFormat=UnityEngine.Rendering.IndexFormat.UInt32,vertices=vertices,uv=uv,triangles=indices.ToArray()};
        mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
    }
}
