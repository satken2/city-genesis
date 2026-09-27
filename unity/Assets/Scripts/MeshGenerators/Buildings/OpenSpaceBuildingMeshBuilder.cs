using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;
using NetTopologySuite.Geometries;
using NetTopologySuite.Triangulate.Polygon;

/// <summary>配置計画を地形追従面と実寸FBXへ変換する。経路探索データは変更しない。</summary>
public sealed class OpenSpaceBuildingMeshBuilder : IBuildingMeshGenerator
{
    static readonly string[] Parts={"floor","site_asphalt","site_paving","site_grass","site_marking","site_curb","site_yellow","site_tactile",
        "site_stop","site_bench","site_tree","site_shelter","site_light","site_clock","site_taxi","site_bollard","site_planter","site_vending"};
    const float SideStep=1.25f; // 縁石の側面を地形へ沿わせる分割の最大長(m)
    const float SideFooting=.05f; // 側面の下端を地面より下げ、斜面で隙間を見せない量(m)
    public BuildingMeshResult Generate(BuildingSummary data,bool isDynamic=false)
    {
        BuildingMeshUtils.ResolveParcelContext(data,out _,out Vector3 center);
        var naming=BuildingMeshUtils.ResolveModelNaming(data);
        var parts=new Dictionary<string,GameObject>();
        foreach(string part in Parts)
        {
            var prefab=BuildingMeshUtils.GetPartBySuffixScan(naming.Prefix,part,BuildingMeshUtils.ComputeVariantNumber(data.visual_variant));
            if(prefab!=null)parts[part]=prefab;
        }
        var plan=BuildingRoadAccessIndex.GetOpenSpacePlan(data);
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
                Triangulate(surface,out var points,out var triangles);
                Mesh mesh=TriangleSurfaceMesh(points,triangles,center,terrain,surface.Lift);
                if(mesh==null)continue;
                owned.Add(mesh);Append(mesh,new[]{mat},Matrix4x4.identity);
                if(surface.Side==null)continue;
                Mesh side=SideMesh(surface,center,terrain);
                if(side==null)continue;
                owned.Add(side);Append(side,new[]{Resolve(surface.Side)??mat},Matrix4x4.identity);
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
                var position=new Vector3(prop.Position.x,y+.02f+PropLift(plan,prop.Position),prop.Position.y);
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
    /// <summary>装飾は置いた位置の縁石で上げた面(歩道・島)の天端に載せる。</summary>
    static float PropLift(OpenSpaceLayout.Plan plan,Vector2 position)
    {
        float lift=0;
        foreach(var surface in plan.Surfaces)
        {
            if(surface.Side==null || surface.Lift<=lift || !PolygonUtils.Contains(surface.Polygon,position))continue;
            bool inHole=false;
            if(surface.Holes!=null)foreach(var hole in surface.Holes)inHole|=PolygonUtils.Contains(hole,position);
            if(!inHole)lift=surface.Lift;
        }
        return lift;
    }
    /// <summary>面を反時計回りの三角形にする。穴のある面は制約付きドロネー分割、それ以外は耳切り。</summary>
    public static void Triangulate(OpenSpaceLayout.Surface surface,out List<Vector2> points,out List<int> triangles)
    {
        points=new List<Vector2>();triangles=new List<int>();
        if(surface.Holes==null || surface.Holes.Length==0)
        {
            var outer=Oriented(surface.Polygon,true);
            points.AddRange(outer);
            triangles.AddRange(BuildingMeshBuilder.TriangulateOrFan(outer));
            return;
        }
        var factory=new GeometryFactory();
        LinearRing Ring(Vector2[] ring)
        {
            var coords=new Coordinate[ring.Length+1];
            for(int i=0;i<ring.Length;i++)coords[i]=new Coordinate(ring[i].x,ring[i].y);
            coords[ring.Length]=coords[0].Copy();
            return factory.CreateLinearRing(coords);
        }
        var holes=new LinearRing[surface.Holes.Length];
        for(int i=0;i<holes.Length;i++)holes[i]=Ring(surface.Holes[i]);
        var polygon=factory.CreatePolygon(Ring(surface.Polygon),holes);
        var index=new Dictionary<Vector2,int>();
        var result=ConstrainedDelaunayTriangulator.Triangulate(polygon);
        for(int g=0;g<result.NumGeometries;g++)
        {
            var c=result.GetGeometryN(g).Coordinates;
            if(c.Length<4)continue;
            var tri=new[]{new Vector2((float)c[0].X,(float)c[0].Y),new Vector2((float)c[1].X,(float)c[1].Y),new Vector2((float)c[2].X,(float)c[2].Y)};
            // XZの反時計回りにそろえる(地形格子の切り抜きと上向きの法線がこの向きを前提にする)。
            if((tri[1].x-tri[0].x)*(tri[2].y-tri[0].y)-(tri[1].y-tri[0].y)*(tri[2].x-tri[0].x)<0){var swap=tri[1];tri[1]=tri[2];tri[2]=swap;}
            foreach(var v in tri)
            {
                if(!index.TryGetValue(v,out int i)){i=points.Count;index.Add(v,i);points.Add(v);}
                triangles.Add(i);
            }
        }
    }
    static Vector2[] Oriented(Vector2[] ring,bool counterClockwise)
    {
        var copy=(Vector2[])ring.Clone();
        if((PolygonUtils.SignedArea(copy)>0)!=counterClockwise)System.Array.Reverse(copy);
        return copy;
    }
    static Mesh SurfaceMesh(Vector2[] polygon,Vector3 center,bool terrain,float lift)
    {
        var outer=Oriented(polygon,true);
        return TriangleSurfaceMesh(new List<Vector2>(outer),BuildingMeshBuilder.TriangulateOrFan(outer),center,terrain,lift);
    }
    static Mesh TriangleSurfaceMesh(List<Vector2> points,List<int> triangles,Vector3 center,bool terrain,float lift)
    {
        if(triangles.Count<3)return null;
        if(terrain)
        {
            Mesh aligned=BuildingMeshBuilder.GenerateTerrainSurfaceMesh(points,triangles,center,ModularBodyBuilder.GROUND_OFFSET+lift,true);
            if(aligned!=null)return aligned;
        }
        var work=new List<Vector2>(points);
        var subdivided=BuildingMeshBuilder.SubdivideTrianglesByEdgeLength(work,triangles,2.5f);
        var vertices=new Vector3[work.Count];var uv=new Vector2[work.Count];
        for(int i=0;i<work.Count;i++)
        {
            Vector2 p=work[i];
            float y=terrain?Utils.GetGroundHeight(new Vector3(center.x+p.x,1000,center.z+p.y))-center.y:0;
            vertices[i]=new Vector3(p.x,y+ModularBodyBuilder.GROUND_OFFSET+lift,p.y);uv[i]=p;
        }
        var indices=new List<int>();
        foreach(var (a,b,c) in subdivided){indices.Add(a);indices.Add(c);indices.Add(b);}
        var mesh=new Mesh {indexFormat=UnityEngine.Rendering.IndexFormat.UInt32,vertices=vertices,uv=uv,triangles=indices.ToArray()};
        mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
    }
    /// <summary>縁石で上げた面の側面。外周は外向き、穴は穴の内側向きに、地面の少し下から天端まで張る。</summary>
    static Mesh SideMesh(OpenSpaceLayout.Surface surface,Vector3 center,bool terrain)
    {
        var vertices=new List<Vector3>();var uv=new List<Vector2>();var indices=new List<int>();
        float top=ModularBodyBuilder.GROUND_OFFSET+surface.Lift;
        float Ground(Vector2 p)=>terrain?Utils.GetGroundHeight(new Vector3(center.x+p.x,1000,center.z+p.y))-center.y:0f;
        void Wall(Vector2[] ring)
        {
            float along=0;
            for(int i=0;i<ring.Length;i++)
            {
                Vector2 a=ring[i],b=ring[(i+1)%ring.Length];
                float length=Vector2.Distance(a,b);
                int steps=Mathf.Max(1,Mathf.CeilToInt(length/SideStep));
                for(int k=0;k<steps;k++)
                {
                    Vector2 p0=Vector2.Lerp(a,b,k/(float)steps),p1=Vector2.Lerp(a,b,(k+1)/(float)steps);
                    float g0=Ground(p0),g1=Ground(p1);
                    int start=vertices.Count;
                    vertices.Add(new Vector3(p0.x,g0-SideFooting,p0.y));vertices.Add(new Vector3(p0.x,g0+top,p0.y));
                    vertices.Add(new Vector3(p1.x,g1+top,p1.y));vertices.Add(new Vector3(p1.x,g1-SideFooting,p1.y));
                    float u0=along+length*k/steps,u1=along+length*(k+1)/steps;
                    uv.Add(new Vector2(u0,0));uv.Add(new Vector2(u0,top+SideFooting));uv.Add(new Vector2(u1,top+SideFooting));uv.Add(new Vector2(u1,0));
                    // 反時計回りの外周の辺a→bでは右手(屋外)が法線になる並び。
                    indices.Add(start);indices.Add(start+1);indices.Add(start+2);
                    indices.Add(start);indices.Add(start+2);indices.Add(start+3);
                }
                along+=length;
            }
        }
        Wall(Oriented(surface.Polygon,true));
        if(surface.Holes!=null)foreach(var hole in surface.Holes)Wall(Oriented(hole,false));
        if(indices.Count==0)return null;
        var mesh=new Mesh {indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
        mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.SetTriangles(indices,0);
        mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
    }
}
