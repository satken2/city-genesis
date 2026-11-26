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
        "site_hedge","site_bin","site_drink","site_joint","site_flagpole",
        "site_diamond","site_track","site_pitch","site_court","site_backstop","site_goal","site_net","site_fence","site_floodlight","site_stand"};
    // 番号付きの部品。1から連番である分だけ使う。
    static readonly string[] Series={"site_tree","site_shrub","site_feature","site_play","site_flower"};
    // Plans placed from part sizes (parks, plants) are costly to rebuild on every render-LOD change, so reuse them per building while the inputs match.
    static readonly Dictionary<int,(string signature,OpenSpaceLayout.Plan plan)> sizedPlans=new Dictionary<int,(string,OpenSpaceLayout.Plan)>();
    const int SizedPlanCacheSize=256;
    static OpenSpaceBuildingMeshBuilder()=>AssetPackStore.CityChanging+=sizedPlans.Clear;
    const float SideStep=1.25f; // Maximum subdivision length for terrain-following curb sides (m).
    const float ClickTolerance=.25f; // Gap (m) allowed between the terrain and the surface to click on, made in place of the deferred ground panel.
    const float SideFooting=.05f; // Lower side bottoms below ground by this amount to hide slope gaps (m).
    public BuildingMeshResult Generate(BuildingSummary data,bool isDynamic=false)
    {
        BuildingMeshUtils.ResolveParcelContext(data,out _,out Vector3 center);
        var naming=BuildingMeshUtils.ResolveModelNaming(data);
        int modelType=MasterDataStore.Instance?.GetAgentMaster(data.variant)?.model_type??Constants.MODEL_PARKING;
        var parts=new Dictionary<string,GameObject>();
        var paths=CollectParts(naming.Prefix,data.visual_variant,OpenSpaceLayout.IsFacilityModel(modelType),parts);
        bool sized=modelType==Constants.MODEL_PARK || modelType==Constants.MODEL_PLAZA || OpenSpaceLayout.IsFacilityModel(modelType);
        var plan=sized?SizedPlan(data,modelType,paths):BuildingRoadAccessIndex.GetOpenSpacePlan(data);
        // Parks draw their thousands of trees and shrubs by instancing, plants their pipes and lights, open pits the machines in the pit. Parking lots and rotaries combine everything.
        return Build(plan,parts,center,true,BuildingMeshUtils.ResolveFloorMaterialFromMaster(data,naming.Prefix),sized?paths:null,naming.Prefix);
    }
    /// <summary>
    /// The parking lot around a modular building whose model folder has the stall lines' material part (site_marking): the ground,
    /// the lines and planted strip, and the things standing on the lot, for combining with the building's body. Parked cars follow
    /// the building's operation rate. Null for buildings without the part or without a parcel.
    /// </summary>
    public static BuildingMeshResult BuildLot(BuildingSummary data,string prefix,Vector3 center,Material floorMaterial)
    {
        if(BuildingMeshUtils.FindPartSuffix(prefix,"site_marking",BuildingMeshUtils.ResolveModelId(prefix,data.visual_variant))<0)return null;
        if(BuildingMeshUtils.ResolveParcelContext(data,out _,out _)==null)return null;
        var parts=new Dictionary<string,GameObject>();
        var paths=CollectParts(prefix,data.visual_variant,true,parts);
        return Build(SizedPlan(data,Constants.MODEL_MODULAR_FLAT,paths,true),parts,center,true,floorMaterial,paths);
    }
    /// <summary>
    /// Resolve the parts a plan uses from the selected model folder: those of plants and parking lots (facility), or of open spaces.
    /// Returns their resource paths (before the render-LOD suffix).
    /// </summary>
    static Dictionary<string,string> CollectParts(string prefix,float visualVariant,bool facility,Dictionary<string,GameObject> parts)
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
        if(facility)
        {
            foreach(string part in OpenSpaceLayout.FacilityParts)Collect(part);
            foreach(string series in OpenSpaceLayout.FacilitySeries)for(int n=1;Collect(series+n);n++){}
            return paths;
        }
        foreach(string part in Parts)Collect(part);
        foreach(string series in Series)for(int n=1;Collect(series+n);n++){}
        return paths;
    }
    /// <summary>Plan of a plant (<see cref="OpenSpaceLayout.IsFacilityModel"/>), as the detailed mesh places it. For the distant mesh.</summary>
    public static OpenSpaceLayout.Plan FacilityPlan(BuildingSummary data,int modelType)
    {
        return SizedPlan(data,modelType,CollectParts(BuildingMeshUtils.GetGlbPrefix1(data),data.visual_variant,true,null));
    }
    /// <summary>
    /// Plan placed from the actual sizes of the parts in the selected model folder, so those sizes are inputs too.
    /// Its site is the footprint, or the parcel for model types whose plan covers it (<see cref="OpenSpaceLayout.CoversParcel"/>)
    /// and for the parking lot around a building (lot), which the footprint stands on.
    /// </summary>
    static OpenSpaceLayout.Plan SizedPlan(BuildingSummary data,int modelType,Dictionary<string,string> paths,bool lot=false)
    {
        var key=new System.Text.StringBuilder();
        void Add(float value)=>key.Append(value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append(':');
        Add(modelType);Add(lot?1:0);Add(data.visual_variant2);Add(data.visual_variant3);
        Vector2[] footprint=BuildingMeshUtils.GetFootprint(data),site=footprint;
        if(lot || OpenSpaceLayout.CoversParcel(modelType))site=OpenSpaceLayout.LotFacingRoad(BuildingMeshUtils.ResolveParcelContext(data,out _,out _),site);
        foreach(Vector2 point in site){Add(point.x);Add(point.y);}
        if(lot)foreach(Vector2 point in footprint){Add(point.x);Add(point.y);}
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
        var plan=lot?OpenSpaceLayout.CreateLot(site,footprint,data.visual_variant2,sizes)
            :OpenSpaceLayout.Create(site,modelType,data.visual_variant2,data.visual_variant3,sizes);
        if(sizedPlans.Count>=SizedPlanCacheSize)sizedPlans.Clear();
        sizedPlans[data.agent_id]=(signature,plan);
        return plan;
    }
    /// <summary>
    /// Validation/exhibits use the same placement and actual FBXs as gameplay. flat is only for terrain-free exhibits.
    /// With paths (part resource paths), shown buildings (<see cref="InstancedPartRenderer.Sink"/> set) hand decorations to instancing instead of combining them;
    /// a plant's units are always combined. fencePrefix is the model prefix whose fence parts enclose a plan with a gate.
    /// While a shown building is generated (<see cref="BuildingExteriorRenderer.Capture"/> set), the ground (the panel and the surfaces
    /// flush with it) becomes deferred low-priority work like other buildings' ground panels, and only a coarse surface to click on is made now.
    /// A bare plan (<see cref="OpenSpaceLayout.Plan.Bare"/>) gets that surface to click on and no ground.
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
            // Like other buildings' ground panels: coincident with the terrain at close-up, within GROUND_PANEL_TOLERANCE elsewhere.
            // Decided here because deferred jobs run with the Normal LOD set.
            float tolerance=BuildingMeshUtils.CurrentDrawLod==DrawLod.Close?ModularBodyBuilder.GROUND_PANEL_EXACT:ModularBodyBuilder.GROUND_PANEL_TOLERANCE;
            Material floorMaterial=floorOverride??Resolve("floor")??BuildingMeshBuilder.GetCommonConcreteMaterial();
            var flush=new List<(OpenSpaceLayout.Surface surface,Material material)>();
            foreach(var surface in plan.Surfaces)
            {
                if(surface.Side!=null)continue;
                Material mat=Resolve(surface.Material);
                if(mat!=null)flush.Add((surface,mat));
            }
            // The ground: the panel, and the surfaces flush with it laid on the panel's own triangles. Returns the panel.
            // As deferred work it is drawn apart from the building's mesh, which skips that mesh's material swap, so it takes the swapped materials itself.
            Mesh Ground(List<CombineInstance> meshes,List<Material> mats,List<Mesh> temporary,bool apart)
            {
                void Add(Mesh mesh,Material mat)
                {
                    if(apart)mat=BuildingManager.GetSharedDepthOffsetMaterials(new[]{mat})[0];
                    temporary.Add(mesh);
                    for(int s=0;s<mesh.subMeshCount;s++){meshes.Add(new CombineInstance {mesh=mesh,subMeshIndex=s,transform=Matrix4x4.identity});mats.Add(mat);}
                }
                Mesh floor=terrain?ModularBodyBuilder.GenerateTerrainConformingFloorMesh(plan.Polygon,center,tolerance):BuildingMeshBuilder.GenerateHorizontalPolygonMesh(plan.Polygon,0f);
                if(floor==null)return null;
                Add(floor,floorMaterial);
                PanelIndex panel=terrain?new PanelIndex(floor):null;
                foreach(var (surface,mat) in flush)
                {
                    Triangulate(surface,out var points,out var triangles);
                    Mesh mesh=panel!=null?panel.Drape(points,triangles,surface.Lift):TriangleSurfaceMesh(points,triangles,center,terrain,surface.Lift);
                    if(mesh!=null)Add(mesh,mat);
                }
                return floor;
            }
            Mesh click;
            float clickLift=0f;
            if(terrain && (plan.Bare || BuildingExteriorRenderer.Capture!=null))
            {
                if(!plan.Bare)BuildingExteriorRenderer.Capture.Add((meshes,mats,temporary)=>Ground(meshes,mats,temporary,true));
                click=ModularBodyBuilder.GenerateTerrainConformingFloorMesh(plan.Polygon,center,ClickTolerance);
                if(click!=null)owned.Add(click);
                // Raised by the most the coarse surface can dip (twice the tolerance, at its fans), so no part of it lies under the terrain.
                clickLift=2f*ClickTolerance;
            }
            else if(plan.Bare){click=BuildingMeshBuilder.GenerateHorizontalPolygonMesh(plan.Polygon,0f);if(click!=null)owned.Add(click);}
            else click=Ground(instances,materials,owned,false);
            if(click!=null)colliders.Add(new CombineInstance {mesh=click,transform=Matrix4x4.Translate(new Vector3(0,clickLift,0))});
            // A raised surface meets its sides, which read the terrain, so it is cut by the terrain grid instead of lying on the panel.
            foreach(var surface in plan.Surfaces)
            {
                if(surface.Side==null)continue;
                Material mat=Resolve(surface.Material);if(mat==null)continue;
                Triangulate(surface,out var points,out var triangles);
                Mesh mesh=TriangleSurfaceMesh(points,triangles,center,terrain,surface.Lift);
                if(mesh==null)continue;
                owned.Add(mesh);Append(mesh,new[]{mat},Matrix4x4.identity);
                Mesh side=SideMesh(surface,center,terrain);
                if(side==null)continue;
                owned.Add(side);Append(side,new[]{Resolve(surface.Side)??mat},Matrix4x4.identity);
            }
            var sink=paths!=null?InstancedPartRenderer.Sink:null;
            var raised=new List<OpenSpaceLayout.Surface>();
            foreach(var surface in plan.Surfaces)if(surface.Side!=null)raised.Add(surface);
            var props=plan.Props;
            if(plan.Pit!=null){props=new List<OpenSpaceLayout.Prop>(plan.Props);AddPitCrews(plan,center,terrain,parts,props);}
            foreach(var prop in props)
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
                    var local=Vector3.Scale(new Vector3(i==0||i==3?b.min.x:b.max.x,0,i<2?b.min.z:b.max.z),prop.Scale);
                    var transformed=rotation*local;
                    rect[i]=prop.Position+new Vector2(transformed.x,transformed.z);
                }
                // Recheck actual FBX occupancy to prevent boundary protrusion when models are replaced.
                if(!prop.Fitted && !OpenSpaceLayout.ContainsPolygon(plan.Polygon,rect))continue;
                Vector2 foot=prop.Ground??prop.Position;
                float y=terrain?Utils.GetGroundHeight(new Vector3(center.x+foot.x,1000,center.z+foot.y))-center.y:0f;
                var position=new Vector3(prop.Position.x,y+.02f+PropLift(raised,prop.Position),prop.Position.y);
                var matrix=Matrix4x4.TRS(position,rotation,prop.Scale);
                if(mesh==null)sink.Add(new InstancedPartRenderer.Instance {path=path,local=matrix,use=prop.Use});
                else
                {
                    Append(mesh,loadedMaterials[prop.Part],matrix);
                    if(prop.Unit)BuildingMotionParts.Collect(prefab,prefab.transform,matrix,rotation);
                }
                // A car that comes and goes is not part of what a click on the building hits.
                if(prop.Use>0f)continue;
                var collider=BuildingMeshBuilder.GenerateColliderMesh(rect,Mathf.Max(.1f,b.max.y*prop.Scale.y));
                if(collider!=null){owned.Add(collider);colliders.Add(new CombineInstance {mesh=collider,transform=Matrix4x4.Translate(new Vector3(0,y,0))});}
            }
            // Lanes follow the terrain like fences, deferred with the other site decorations while a shown building is generated.
            if(plan.Lanes.Count>0 && parts.TryGetValue("lane",out var lane))
            {
                void Lanes(List<CombineInstance> meshes,List<Material> mats,List<Mesh> temporary)
                {
                    foreach(var (a,b) in plan.Lanes)ModularBodyBuilder.AddPartRun(a,b,lane,center,terrain,meshes,mats,temporary);
                }
                if(BuildingExteriorRenderer.Capture!=null && BuildingExteriorRenderer.DeferSiteDecorations)BuildingExteriorRenderer.Capture.Add(Lanes);
                else Lanes(instances,materials,owned);
            }
            // The fence follows the site edge and opens at the gate, as the shared site decorations place it for other buildings.
            if(fencePrefix!=null && plan.Gate.HasValue)
                ModularBodyBuilder.GenerateSiteDecorations(fencePrefix,0,plan.Polygon,null,null,null,center,terrain,instances,materials,owned,
                    plan.Gate,OpenSpaceLayout.FacilityGateWidth*.5f);
            var result=new Mesh {name="OpenSpace",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            // Nothing is left to draw now when the ground is deferred and the plan places no props.
            if(instances.Count>0)result.CombineMeshes(instances.ToArray(),false,true);
            result.RecalculateBounds();
            var collision=new Mesh {name="OpenSpaceCollider",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            collision.CombineMeshes(colliders.ToArray(),true,true);collision.RecalculateBounds();
            return BuildingMeshBuilder.MergeSubMeshes(new BuildingMeshResult(result,materials.ToArray(),collision));
        }
        finally {foreach(var mesh in owned)Object.Destroy(mesh);}
    }
    /// <summary>
    /// The ground panel's triangles, bucketed in plan for laying flat surfaces on them (<see cref="Drape"/>). A surface cut by the
    /// panel's own triangles stays parallel to the panel whatever tolerance the panel follows the terrain with, and its cost follows
    /// the panel's triangle count rather than the terrain grid.
    /// </summary>
    sealed class PanelIndex
    {
        readonly Vector3[] vertices,normals;
        readonly int[] triangles;
        readonly List<int>[] buckets;
        readonly int[] seen;
        readonly Vector2 origin;
        readonly float cell;
        readonly int width,height;
        int stamp;
        public PanelIndex(Mesh floor)
        {
            vertices=floor.vertices;normals=floor.normals;triangles=floor.triangles;
            if(normals.Length!=vertices.Length){normals=new Vector3[vertices.Length];for(int i=0;i<normals.Length;i++)normals[i]=Vector3.up;}
            Bounds b=floor.bounds;
            int count=triangles.Length/3;
            // About two triangles per bucket on an even panel.
            cell=Mathf.Clamp(Mathf.Sqrt(2f*b.size.x*b.size.z/Mathf.Max(1,count)),2f,64f);
            origin=new Vector2(b.min.x,b.min.z);
            width=Mathf.Max(1,Mathf.CeilToInt(b.size.x/cell));height=Mathf.Max(1,Mathf.CeilToInt(b.size.z/cell));
            buckets=new List<int>[width*height];
            seen=new int[count];
            for(int t=0;t<count;t++)
            {
                Vector3 p0=vertices[triangles[3*t]],p1=vertices[triangles[3*t+1]],p2=vertices[triangles[3*t+2]];
                Range(Mathf.Min(p0.x,p1.x,p2.x),Mathf.Min(p0.z,p1.z,p2.z),Mathf.Max(p0.x,p1.x,p2.x),Mathf.Max(p0.z,p1.z,p2.z),out int x0,out int z0,out int x1,out int z1);
                for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)(buckets[z*width+x]??=new List<int>()).Add(t);
            }
        }
        void Range(float minX,float minZ,float maxX,float maxZ,out int x0,out int z0,out int x1,out int z1)
        {
            x0=Mathf.Clamp(Mathf.FloorToInt((minX-origin.x)/cell),0,width-1);x1=Mathf.Clamp(Mathf.FloorToInt((maxX-origin.x)/cell),0,width-1);
            z0=Mathf.Clamp(Mathf.FloorToInt((minZ-origin.y)/cell),0,height-1);z1=Mathf.Clamp(Mathf.FloorToInt((maxZ-origin.y)/cell),0,height-1);
        }
        static float Cross(Vector2 a,Vector2 b,Vector2 c)=>(b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);
        /// <summary>Keep the part of a convex polygon to the left of a→b.</summary>
        static void Clip(List<Vector2> polygon,List<Vector2> scratch,Vector2 a,Vector2 b)
        {
            scratch.Clear();
            for(int i=0;i<polygon.Count;i++)
            {
                Vector2 p=polygon[i],q=polygon[(i+1)%polygon.Count];
                float dp=Cross(a,b,p),dq=Cross(a,b,q);
                if(dp>=0)scratch.Add(p);
                if((dp>0 && dq<0)||(dp<0 && dq>0))scratch.Add(p+(q-p)*(dp/(dp-dq)));
            }
            polygon.Clear();polygon.AddRange(scratch);
        }
        /// <summary>
        /// The surface (triangles counterclockwise in plan) cut by the panel's triangles and lifted by lift above them.
        /// UVs are local XZ (1m = 1), normals the panel's. Null where the surface misses the panel.
        /// </summary>
        public Mesh Drape(List<Vector2> points,List<int> source,float lift)
        {
            var outVertices=new List<Vector3>();var outNormals=new List<Vector3>();var outUv=new List<Vector2>();var indices=new List<int>();
            var polygon=new List<Vector2>(8);var scratch=new List<Vector2>(8);
            for(int k=0;k+2<source.Count;k+=3)
            {
                Vector2 a=points[source[k]],b=points[source[k+1]],c=points[source[k+2]];
                if(Cross(a,b,c)<0){var swap=b;b=c;c=swap;}
                Range(Mathf.Min(a.x,b.x,c.x),Mathf.Min(a.y,b.y,c.y),Mathf.Max(a.x,b.x,c.x),Mathf.Max(a.y,b.y,c.y),out int x0,out int z0,out int x1,out int z1);
                stamp++;
                for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)
                {
                    var bucket=buckets[z*width+x];if(bucket==null)continue;
                    foreach(int t in bucket)
                    {
                        if(seen[t]==stamp)continue;
                        seen[t]=stamp;
                        int i0=triangles[3*t],i1=triangles[3*t+1],i2=triangles[3*t+2];
                        Vector3 v0=vertices[i0],v1=vertices[i1],v2=vertices[i2];
                        Vector2 p0=new Vector2(v0.x,v0.z),p1=new Vector2(v1.x,v1.z),p2=new Vector2(v2.x,v2.z);
                        float area=Cross(p0,p1,p2);
                        if(Mathf.Abs(area)<1e-9f)continue;
                        polygon.Clear();polygon.Add(p0);
                        if(area>0){polygon.Add(p1);polygon.Add(p2);}else{polygon.Add(p2);polygon.Add(p1);}
                        Clip(polygon,scratch,a,b);Clip(polygon,scratch,b,c);Clip(polygon,scratch,c,a);
                        if(polygon.Count<3)continue;
                        int first=outVertices.Count;
                        foreach(Vector2 q in polygon)
                        {
                            float w0=Cross(q,p1,p2)/area,w1=Cross(p0,q,p2)/area,w2=1f-w0-w1;
                            outVertices.Add(new Vector3(q.x,w0*v0.y+w1*v1.y+w2*v2.y+lift,q.y));
                            outNormals.Add((w0*normals[i0]+w1*normals[i1]+w2*normals[i2]).normalized);
                            outUv.Add(q);
                        }
                        for(int i=1;i+1<polygon.Count;i++){indices.Add(first);indices.Add(first+i+1);indices.Add(first+i);}
                    }
                }
            }
            if(indices.Count==0)return null;
            var mesh=new Mesh {indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
            mesh.SetVertices(outVertices);mesh.SetNormals(outNormals);mesh.SetUVs(0,outUv);mesh.SetTriangles(indices,0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
    const int PitSamples=12;        // Ground samples per side of the excavation's bounding rectangle when looking for its working level.
    const float PitCrewArea=10000f; // Excavation area per excavator and haul truck (m²).
    const float PitLevelStep=1f;    // Ground heights within this count as the same level (m).
    const float PitTruckReach=8f;   // From an excavator to the truck it loads (m).
    const float PitCrewSpacing=60f; // Least distance between crews (m), so the crews of a large excavation spread over the level being cut.
    /// <summary>
    /// Excavators and haul trucks at work in an excavation: one crew per PitCrewArea on the level being cut, each with the heap
    /// it is loading from behind the excavator; a drill rig stands by the first crew. The level being cut is the lowest ground of
    /// an open pit, where the crews gather toward the middle, and the floor of a quarry (the ground at Plan.PitFloor), where they
    /// work at the far end from the forecourt, under the face. The crews are placed from the current terrain rather than by the
    /// plan, so they follow the excavation as it advances. A coarse sample is enough: the level being cut is flat and wide.
    /// </summary>
    static void AddPitCrews(OpenSpaceLayout.Plan plan,Vector3 center,bool terrain,Dictionary<string,GameObject> parts,List<OpenSpaceLayout.Prop> props)
    {
        if(!parts.ContainsKey("site_excavator") && !parts.ContainsKey("site_truck"))return;
        Vector2[] pit=plan.Pit;
        float Ground(Vector2 p)=>terrain?Utils.GetGroundHeight(new Vector3(center.x+p.x,1000,center.z+p.y)):0f;
        Vector2 min=pit[0],max=pit[0],middle=Vector2.zero;
        foreach(var p in pit){min=Vector2.Min(min,p);max=Vector2.Max(max,p);middle+=p/pit.Length;}
        Vector2 step=(max-min)/PitSamples;
        var spots=new List<(int level,float distance,Vector2 point)>();
        for(int i=0;i<PitSamples*PitSamples;i++)
        {
            var p=min+new Vector2((i%PitSamples+.5f)*step.x,(i/PitSamples+.5f)*step.y);
            if(!PolygonUtils.Contains(pit,p))continue;
            spots.Add((Mathf.RoundToInt(Ground(p)/PitLevelStep),plan.PitFloor.HasValue?-(p-plan.PitFloor.Value).sqrMagnitude:(p-middle).sqrMagnitude,p));
        }
        if(spots.Count==0)return;
        int working=plan.PitFloor.HasValue?Mathf.RoundToInt(Ground(plan.PitFloor.Value)/PitLevelStep):int.MinValue;
        if(!plan.PitFloor.HasValue)foreach(var spot in spots)working=working==int.MinValue?spot.level:Mathf.Min(working,spot.level);
        spots.Sort((a,b)=>Mathf.Abs(a.level-working)!=Mathf.Abs(b.level-working)?Mathf.Abs(a.level-working).CompareTo(Mathf.Abs(b.level-working)):a.distance.CompareTo(b.distance));
        int crews=Mathf.Max(1,Mathf.RoundToInt(Mathf.Abs(PolygonUtils.SignedArea(pit))/PitCrewArea));
        var placed=new List<Vector2>();
        void Stand(string part,Vector2 position,Vector2 forward)
        {
            if(PolygonUtils.Contains(pit,position))props.Add(new OpenSpaceLayout.Prop {Part=part,Position=position,Forward=forward,Fitted=true});
        }
        for(int i=0;i<spots.Count && placed.Count<crews;i++)
        {
            bool crowded=false;
            foreach(var other in placed)crowded|=(other-spots[i].point).sqrMagnitude<PitCrewSpacing*PitCrewSpacing;
            if(crowded)continue;
            placed.Add(spots[i].point);
            // The excavator's boom (model +X, to the left of its front) reaches over the truck, which stands across it.
            float angle=CollapsedBuildingMeshBuilder.Hash01(i,Mathf.RoundToInt(spots[i].point.x),Mathf.RoundToInt(spots[i].point.y),40)*Mathf.PI*2f;
            var facing=new Vector2(Mathf.Cos(angle),Mathf.Sin(angle));
            var boom=new Vector2(-facing.y,facing.x);
            Stand("site_excavator",spots[i].point,facing);
            Stand("site_truck",spots[i].point+boom*PitTruckReach,boom);
            Stand("site_heap1",spots[i].point-boom*PitTruckReach,facing);
            if(placed.Count==1)Stand("site_drill",spots[i].point-facing*PitTruckReach*2f,boom);
        }
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
    /// <summary>
    /// Triangulate surfaces counterclockwise: ear clipping, or constrained Delaunay for holes and for rings where ear clipping finds
    /// no ear (long thin bands whose collinear vertices a parcel's rotation leaves slightly off their line).
    /// </summary>
    public static void Triangulate(OpenSpaceLayout.Surface surface,out List<Vector2> points,out List<int> triangles)
    {
        points=new List<Vector2>();triangles=new List<int>();
        bool plain=surface.Holes==null || surface.Holes.Length==0;
        if(plain)
        {
            var outer=Oriented(surface.Polygon,true);
            var ears=BuildingMeshBuilder.TriangulateEarClip(outer);
            if(ears.Count>0){points.AddRange(outer);triangles.AddRange(ears);return;}
        }
        var factory=new GeometryFactory();
        LinearRing Ring(Vector2[] ring)
        {
            var coords=new Coordinate[ring.Length+1];
            for(int i=0;i<ring.Length;i++)coords[i]=new Coordinate(ring[i].x,ring[i].y);
            coords[ring.Length]=coords[0].Copy();
            return factory.CreateLinearRing(coords);
        }
        var holes=new LinearRing[plain?0:surface.Holes.Length];
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
