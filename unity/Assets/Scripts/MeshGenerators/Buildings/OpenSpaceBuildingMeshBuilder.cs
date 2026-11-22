using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;
using NetTopologySuite.Geometries;
using NetTopologySuite.Triangulate.Polygon;

/// <summary>Convert placement plans to terrain-following surfaces and actual-size FBXs. Do not alter pathfinding data.</summary>
public sealed class OpenSpaceBuildingMeshBuilder : IBuildingMeshGenerator
{
    static readonly string[] Parts={"floor","site_asphalt","site_paving","site_grass","site_marking","site_curb","site_yellow","site_tactile","site_gravel","site_sand",
        "site_stop","site_bench","site_tree","site_shelter","site_light","site_clock","site_taxi","site_bollard","site_planter","site_vending",
        "site_hedge","site_bin","site_drink"};
    // 番号付きの部品。1から連番である分だけ使う。
    static readonly string[] Series={"site_tree","site_shrub","site_feature","site_play","site_flower"};
    // Plans placed from part sizes (parks, plants) are costly to rebuild on every render-LOD change, so reuse them per building while the inputs match.
    static readonly Dictionary<int,(string signature,OpenSpaceLayout.Plan plan)> sizedPlans=new Dictionary<int,(string,OpenSpaceLayout.Plan)>();
    const int SizedPlanCacheSize=256;
    static OpenSpaceBuildingMeshBuilder()=>AssetPackStore.CityChanging+=sizedPlans.Clear;
    const float SideStep=1.25f; // Maximum subdivision length for terrain-following curb sides (m).
    const float SideFooting=.05f; // Lower side bottoms below ground by this amount to hide slope gaps (m).
    public BuildingMeshResult Generate(BuildingSummary data,bool isDynamic=false)
    {
        BuildingMeshUtils.ResolveParcelContext(data,out _,out Vector3 center);
        var naming=BuildingMeshUtils.ResolveModelNaming(data);
        int modelType=MasterDataStore.Instance?.GetAgentMaster(data.variant)?.model_type??Constants.MODEL_PARKING;
        var parts=new Dictionary<string,GameObject>();
        var paths=CollectParts(naming.Prefix,data.visual_variant,modelType,parts);
        bool sized=modelType==Constants.MODEL_PARK || OpenSpaceLayout.IsFacilityModel(modelType);
        var plan=sized?SizedPlan(data,modelType,paths):BuildingRoadAccessIndex.GetOpenSpacePlan(data);
        // Parks draw their thousands of trees and shrubs by instancing, plants their pipes and lights. Parking lots and rotaries combine everything.
        return Build(plan,parts,center,true,BuildingMeshUtils.ResolveFloorMaterialFromMaster(data,naming.Prefix),sized?paths:null,naming.Prefix);
    }
    /// <summary>Resolve the parts a model type's plan uses from the selected model folder. Returns their resource paths (before the render-LOD suffix).</summary>
    static Dictionary<string,string> CollectParts(string prefix,float visualVariant,int modelType,Dictionary<string,GameObject> parts)
    {
        var paths=new Dictionary<string,string>();
        int modelId=BuildingMeshUtils.ResolveModelId(prefix,visualVariant);
        bool Collect(string part)
        {
            int suffix=BuildingMeshUtils.FindPartSuffix(prefix,part,modelId);
            var prefab=suffix<0?null:BuildingMeshUtils.GetPartNoPosition(prefix,part,suffix,true);
            if(prefab==null)return false;
            if(parts!=null)parts[part]=prefab;
            paths[part]=BuildingMeshUtils.PartPath(prefix,part,suffix);
            return true;
        }
        if(OpenSpaceLayout.IsFacilityModel(modelType))
        {
            foreach(string part in OpenSpaceLayout.FacilityParts)Collect(part);
            return paths;
        }
        foreach(string part in Parts)Collect(part);
        foreach(string series in Series)for(int n=1;Collect(series+n);n++){}
        return paths;
    }
    /// <summary>Plan of a plant (<see cref="OpenSpaceLayout.IsFacilityModel"/>), as the detailed mesh places it. For the distant mesh.</summary>
    public static OpenSpaceLayout.Plan FacilityPlan(BuildingSummary data,int modelType)
    {
        return SizedPlan(data,modelType,CollectParts(BuildingMeshUtils.GetGlbPrefix1(data),data.visual_variant,modelType,null));
    }
    /// <summary>Plan placed from the actual sizes of the parts in the selected model folder, so those sizes are inputs too.</summary>
    static OpenSpaceLayout.Plan SizedPlan(BuildingSummary data,int modelType,Dictionary<string,string> paths)
    {
        var key=new System.Text.StringBuilder();
        void Add(float value)=>key.Append(value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append(':');
        Add(modelType);Add(data.visual_variant2);Add(data.visual_variant3);
        if(data.polygonArray!=null)foreach(var point in data.polygonArray)foreach(float v in point)Add(v);
        foreach(var pair in paths)key.Append(pair.Value).Append(':');
        string signature=key.ToString();
        if(sizedPlans.TryGetValue(data.agent_id,out var cached) && cached.signature==signature)return cached.plan;
        var sizes=new Dictionary<string,Vector2>();
        foreach(var pair in paths)
        {
            var model=InstancedPartRenderer.GetModel(pair.Value);
            if(model?.render==null)continue;
            // Parts are authored with the origin at their horizontal center; measure the farthest reach from the origin so an off-center model still fits.
            Bounds b=model.layout;
            sizes[pair.Key]=new Vector2(2*Mathf.Max(-b.min.x,b.max.x),2*Mathf.Max(-b.min.z,b.max.z));
        }
        var plan=OpenSpaceLayout.Create(BuildingMeshUtils.GetFootprint(data),modelType,data.visual_variant2,data.visual_variant3,sizes);
        if(sizedPlans.Count>=SizedPlanCacheSize)sizedPlans.Clear();
        sizedPlans[data.agent_id]=(signature,plan);
        return plan;
    }
    /// <summary>
    /// Validation/exhibits use the same placement and actual FBXs as gameplay. flat is only for terrain-free exhibits.
    /// With paths (part resource paths), shown buildings (<see cref="InstancedPartRenderer.Sink"/> set) hand decorations to instancing instead of combining them;
    /// a plant's units are always combined. fencePrefix is the model prefix whose fence parts enclose a plan with a gate.
    /// </summary>
    public static BuildingMeshResult Build(OpenSpaceLayout.Plan plan,Dictionary<string,GameObject> parts,Vector3 center,bool terrain,Material floorOverride=null,
        Dictionary<string,string> paths=null,string fencePrefix=null)
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
            var sink=paths!=null?InstancedPartRenderer.Sink:null;
            var raised=new List<OpenSpaceLayout.Surface>();
            foreach(var surface in plan.Surfaces)if(surface.Side!=null)raised.Add(surface);
            foreach(var prop in plan.Props)
            {
                if(!parts.TryGetValue(prop.Part,out var prefab))continue;
                Bounds b;Mesh mesh=null;string path=null;
                if(sink!=null && !prop.Unit && paths.TryGetValue(prop.Part,out path))
                {
                    var model=InstancedPartRenderer.GetModel(path);
                    if(model?.render==null)continue;
                    b=model.layout;
                }
                else
                {
                    if(!loaded.TryGetValue(prop.Part,out mesh))
                    {
                        mesh=BuildingMeshUtils.CombineModuleMeshes(prefab,out Material[] partMaterials);
                        if(mesh==null)continue;
                        owned.Add(mesh);loaded[prop.Part]=mesh;loadedMaterials[prop.Part]=partMaterials;
                    }
                    b=mesh.bounds;
                }
                // Props are authored at actual size with origin at horizontal contact-plane center and front toward local +Z (Blender −Y).
                Quaternion rotation=Quaternion.LookRotation(new Vector3(prop.Forward.x,0,prop.Forward.y));
                var rect=new Vector2[4];
                for(int i=0;i<4;i++)
                {
                    var local=new Vector3(i==0||i==3?b.min.x:b.max.x,0,i<2?b.min.z:b.max.z)*prop.Scale;
                    var transformed=rotation*local;
                    rect[i]=prop.Position+new Vector2(transformed.x,transformed.z);
                }
                // Recheck actual FBX occupancy to prevent boundary protrusion when models are replaced.
                if(!prop.Fitted && !OpenSpaceLayout.ContainsPolygon(plan.Polygon,rect))continue;
                float y=terrain?Utils.GetGroundHeight(new Vector3(center.x+prop.Position.x,1000,center.z+prop.Position.y))-center.y:0f;
                var position=new Vector3(prop.Position.x,y+.02f+PropLift(raised,prop.Position),prop.Position.y);
                var matrix=Matrix4x4.TRS(position,rotation,Vector3.one*prop.Scale);
                if(mesh==null)sink.Add(new InstancedPartRenderer.Instance {path=path,local=matrix});
                else
                {
                    Append(mesh,loadedMaterials[prop.Part],matrix);
                    if(prop.Unit)BuildingMotionParts.Collect(prefab,prefab.transform,matrix,rotation);
                }
                var collider=BuildingMeshBuilder.GenerateColliderMesh(rect,Mathf.Max(.1f,b.max.y*prop.Scale));
                if(collider!=null){owned.Add(collider);colliders.Add(new CombineInstance {mesh=collider,transform=Matrix4x4.Translate(new Vector3(0,y,0))});}
            }
            // The fence follows the site edge and opens at the gate, as the shared site decorations place it for other buildings.
            if(fencePrefix!=null && plan.Gate.HasValue)
                ModularBodyBuilder.GenerateSiteDecorations(fencePrefix,0,plan.Polygon,null,null,null,center,terrain,instances,materials,owned,
                    plan.Gate,OpenSpaceLayout.FacilityGateWidth*.5f);
            var result=new Mesh {name="OpenSpace",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            result.CombineMeshes(instances.ToArray(),false,true);result.RecalculateBounds();
            var collision=new Mesh {name="OpenSpaceCollider",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            collision.CombineMeshes(colliders.ToArray(),true,true);collision.RecalculateBounds();
            return BuildingMeshBuilder.MergeSubMeshes(new BuildingMeshResult(result,materials.ToArray(),collision));
        }
        finally {foreach(var mesh in owned)Object.Destroy(mesh);}
    }
    /// <summary>Decorations rest on the curb-raised top (sidewalk/island) at their placement.</summary>
    static float PropLift(List<OpenSpaceLayout.Surface> raised,Vector2 position)
    {
        float lift=0;
        foreach(var surface in raised)
        {
            if(surface.Lift<=lift || !PolygonUtils.Contains(surface.Polygon,position))continue;
            bool inHole=false;
            if(surface.Holes!=null)foreach(var hole in surface.Holes)inHole|=PolygonUtils.Contains(hole,position);
            if(!inHole)lift=surface.Lift;
        }
        return lift;
    }
    /// <summary>Triangulate surfaces counterclockwise: constrained Delaunay for holes, otherwise ear clipping.</summary>
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
            // Normalize XZ winding to counterclockwise (terrain-grid clipping and upward normals assume this).
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
    /// <summary>Curb-raised surface sides. Extend from slightly underground to top, facing outward on the perimeter and into holes.</summary>
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
                    // On a CCW perimeter edge a→b, this ordering gives a rightward (exterior) normal.
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
