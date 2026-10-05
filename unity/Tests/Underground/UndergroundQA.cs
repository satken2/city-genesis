using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// 本編の描画・物理・表示設定を独立プロジェクトで動かす。都市データや本編シーンには接続しない。
[InitializeOnLoad]
public static class UndergroundQA
{
    private static Assembly runtime;
    private static int checks;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type T(string name) => runtime.GetType(name, true);
    private static object Field(object obj, string name) => obj.GetType().GetField(name, Flags).GetValue(obj);
    private static void Set(object obj, string name, object value) => (obj.GetType().GetField(name, Flags) ?? throw new Exception("Missing field: " + obj.GetType() + "." + name)).SetValue(obj, value);
    private static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Flags).Invoke(obj, args);
    private static object Static(string type, string name, params object[] args) => T(type).GetMethod(name, Flags).Invoke(null, args);
    private static int C(string type, string name) => (int)(T(type).GetField(name) ?? throw new Exception("Missing constant: " + type + "." + name)).GetValue(null);
    private static Component Add(string type, GameObject go = null)
    {
        var component = (go ?? new GameObject(type)).AddComponent(T(type));
        ((Behaviour)component).enabled = false;
        return component;
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }

    private static void Drain(IEnumerator routine)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (routine.MoveNext())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Coroutine timed out");
            if (routine.Current is IEnumerator nested) Drain(nested);
            else System.Threading.Thread.Yield();
        }
    }

    static UndergroundQA()
    {
        if (SessionState.GetBool("UndergroundQA.Pending", false)) EditorApplication.delayCall += Run;
    }

    public static void Run()
    {
        if (!EditorApplication.isPlaying)
        {
            foreach (string file in Directory.GetFiles("Assets/Models/Paths", "*.fbx", SearchOption.AllDirectories))
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(file.Replace('\\','/'));
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
            }
            SessionState.SetBool("UndergroundQA.Pending", true);
            EditorApplication.EnterPlaymode();
            return;
        }
        SessionState.SetBool("UndergroundQA.Pending", false);
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-sourceRoot") + 1];
        string report = root + "/Temp/UndergroundQA/result.txt";
        try
        {
            var referencePaths = File.ReadAllLines(root + "/Temp/UndergroundQA/references.txt");
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string path in referencePaths)
                    if (Path.GetFileName(path) == name && File.Exists(path)) return Assembly.LoadFrom(path);
                foreach (string dir in new[] { "Temp/bin/Debug", "Assets/Plugins" })
                {
                    string path = Path.Combine(root, dir, name);
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            runtime = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            foreach (string name in new[] { "UndergroundGhost", "UndergroundGround", "UndergroundSoil", "UndergroundShore", "Forest", "OverlayLine" })
            {
                var shader = Resources.Load<Shader>("Shaders/" + name);
                string errors = shader == null ? "missing" : string.Join("; ", Array.ConvertAll(ShaderUtil.GetShaderMessages(shader), message => message.message));
                Check(shader != null && !ShaderUtil.ShaderHasError(shader), "shader import: " + name + ": " + errors);
            }
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;

            var state = Add("GameStateManager");
            state.GetType().GetProperty("IsWorldVisible").SetValue(state, true);
            var modes = Add("VisualModeHandler");
            var master = Add("MasterDataStore");
            var models = Add("ModelManager");
            var modelCache = (IDictionary)Field(models,"_modelCache");
            // 元FBXを通常生成器へ渡す。検証プロジェクトでは既存プレハブの外部GUID依存を持ち込まない。
            foreach (string file in Directory.GetFiles("Assets/Models/Paths", "*.fbx", SearchOption.AllDirectories))
            {
                string assetPath=file.Replace('\\','/');
                var model=AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (model==null) throw new Exception("FBX import failed: "+assetPath);
                // 独立プロジェクトのFBX既定材質はBuilt-in。色と画像を保ってURPの検証用材質へ変換する。
                foreach (var material in model.GetComponentsInChildren<MeshRenderer>().SelectMany(renderer=>renderer.sharedMaterials).Distinct())
                {
                    if (material.shader.name!="Standard") continue;
                    var color=material.color; var texture=material.mainTexture;
                    material.shader=Shader.Find("Universal Render Pipeline/Lit");
                    material.SetColor("_BaseColor",color); material.SetTexture("_BaseMap",texture);
                }
                modelCache[assetPath.Substring("Assets/".Length).Replace(".fbx","")]=model;
            }
            master.GetType().GetProperty("IsInitialized").SetValue(master, true);
            var masters = (IDictionary)Field(master, "pathMasterById");
            foreach (int variant in new[] { 221, 405, 461, 1103 })
            {
                object item = Activator.CreateInstance(masters.GetType().GetGenericArguments()[1]);
                Set(item, "model_type", variant == 1103 ? "none" : variant == 405 || variant == 461 ? "track" : "default");
                Set(item, "width", variant == 1103 ? 3f : variant == 461 ? 5f : 8f);
                Set(item, "type", variant == 1103 ? 11 : variant == 405 || variant == 461 ? 4 : 1);
                Set(item, "lanes", variant == 461 ? 1 : 2);
                if (variant == 461) { Set(item,"platform_width",3f); Set(item,"wire_spacing",35f); Set(item,"wall_width",.2f); }
                Set(item, "sidewalk_width", variant == 221 ? 1.5f : 0f);
                Set(item, "side_width", variant == 221 ? .3f : variant == 461 ? .5f : 0f);
                Set(item, "height", variant == 461 ? 7f : 3f); Set(item, "height_editable", true); Set(item, "follow_terrain", variant == 461);
                masters[variant] = item;
            }
            var data = new TerrainData { heightmapResolution = 65, size = new Vector3(200,100,200) };
            var heights = new float[65,65];
            for (int z = 0; z < 65; z++) for (int x = 0; x < 65; x++) heights[z,x] = .2f+(x/64f-.6f)*.1f;
            data.SetHeights(0,0,heights);
            var terrainGO = Terrain.CreateTerrainGameObject(data);
            terrainGO.transform.position = new Vector3(-100,-20,-100);
            terrainGO.layer = LayerMask.NameToLayer("Ground");
            var terrain = terrainGO.GetComponent<Terrain>();
            terrain.materialTemplate = new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
            var terrainManager = Add("TerrainManager");
            var tiles = (IDictionary)Field(terrainManager,"terrainTiles");
            foreach (int x in new[] {-1,0}) foreach (int z in new[] {-1,0}) tiles[new Vector2Int(x,z)] = terrain;
            Static("Utils","InvalidateHeightCache");
            var terrainVisual = Add("VisualTerrainObject",terrainGO);
            var pathManager = Add("PathManager");
            var managerParent = new GameObject("CellDataLoaderTransform");
            managerParent.transform.position = new Vector3(16,32,-55);
            pathManager.transform.SetParent(managerParent.transform, false);
            pathManager.transform.localRotation = Quaternion.Euler(0,.13f,-.14f);
            var scene = SceneManager.CreateScene("GameScene");
            var pathContainer = new GameObject("Paths");
            SceneManager.MoveGameObjectToScene(pathContainer, scene);
            var paths = (IDictionary)Field(pathManager,"pathDataCache");
            if (paths == null) throw new Exception("Path cache is null");
            object MakePath(int id, int variant, int type, float z, float relativeY, string typeName = "GeneratedModels.PathSummary")
            {
                object path = Activator.CreateInstance(T(typeName));
                Set(path,"path_id",id); Set(path,"path_variant",variant); Set(path,"path_type",type);
                Set(path,"status",C("Constants","STATUS_ACTIVE")); Set(path,"follow_terrain",0);
                Set(path,"start_point",new List<object>{-70d,(double)relativeY,(double)z});
                Set(path,"end_point",new List<object>{70d,(double)relativeY,(double)z});
                paths[id] = path; return path;
            }
            var road = MakePath(1,221,C("Constants","PATH_STREET"),-25,-14);
            var rail = MakePath(2,405,C("Constants","PATH_TRACK"),0,-20);
            var infra = MakePath(3,1103,C("Constants","PATH_ELECTRICITY"),25,-28);
            object drawNormal = Enum.Parse(T("DrawLod"),"Normal");
            object drawClose = Enum.Parse(T("DrawLod"),"Close");
            GameObject Build(object path, object lod = null)
            {
                Call(pathManager,"CreateOrUpdatePath",path,false,true,lod ?? drawNormal,0d,1d);
                var go = (GameObject)Call(pathManager,"GetPathObject",Field(path,"path_id"));
                Call(go.GetComponent(T("VisualModeObject")),"Start");
                var mesh = go.GetComponent<MeshFilter>().sharedMesh;
                if (mesh != null) Drain((IEnumerator)Call(pathManager,"SetColliderAsync",go,mesh));
                return go;
            }
            void FinishUpdates()
            {
                var worker = (IEnumerator)Call(pathManager,"TerrainFollowRebuildWorker");
                for (int i=0;i<200 && (int)pathManager.GetType().GetProperty("PendingTerrainRebuildCount").GetValue(pathManager)>0;i++)
                {
                    worker.MoveNext();
                    if (worker.Current is IEnumerator nested) Drain(nested);
                }
                Check((int)pathManager.GetType().GetProperty("PendingTerrainRebuildCount").GetValue(pathManager)==0,"common rebuild queue finishes");
            }

            var camera = new GameObject("Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
            camera.transform.position = new Vector3(155,120,-180); camera.transform.LookAt(new Vector3(0,-8,0));
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.gray; camera.aspect = 1.6f;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            var sun = new GameObject("Sun").AddComponent<Light>(); sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(55,-25,0); sun.intensity = 1.2f; sun.color=Color.white;
            RenderSettings.ambientMode=AmbientMode.Flat; RenderSettings.ambientLight=Color.gray;
            var sea = GameObject.CreatePrimitive(PrimitiveType.Plane); sea.name="Sea"; sea.tag="SeaObject";
            sea.transform.localScale = new Vector3(20,1,20); sea.transform.position = new Vector3(0,.01f,0);
            var seaVisual = Add("VisualSeaObject",sea);
            int terrainType = C("InputObjectType","TERRAIN"), pathType = C("InputObjectType","PATH");
            int transparent = C("VisualModeTerrain","TRANSPARENT"), normal = C("VisualMode","MODE_NORMAL"), hidden = C("VisualMode","MODE_HIDDEN");
            var far = GameObject.CreatePrimitive(PrimitiveType.Cube);
            far.name = "FarBuilding"; far.transform.position = new Vector3(-45,8,45); far.transform.localScale = new Vector3(12,16,12);
            var farRenderer = far.GetComponent<MeshRenderer>();
            var farOriginal = farRenderer.sharedMaterial;
            var farId = far.AddComponent(T("ObjectIdInfo")); Set(farId,"id",42); Set(farId,"status",C("Constants","STATUS_ACTIVE"));
            var member = far.AddComponent(T("FarBuildingMember"));
            Call(member,"Configure",farRenderer,far.GetComponent<MeshFilter>().sharedMesh);
            far.AddComponent(T("UndergroundGhostObject"));
            var forest = Activator.CreateInstance(T("ForestRenderer"));
            Call(modes,"SetMode",terrainType,transparent);
            Call(terrainVisual,"ApplyVisualMode",transparent); Call(seaVisual,"ApplyVisualMode",transparent);
            Check(!terrain.enabled && terrainGO.GetComponent<TerrainCollider>().enabled,"ground physics remains available");
            Check(!sea.GetComponent<Renderer>().enabled,"sea is hidden below zero elevation");
            Check(terrainGO.transform.Find("UndergroundGroundSurface").gameObject.activeSelf,"translucent ground membrane exists");
            Check(farRenderer.sharedMaterial!=farOriginal,"far building fades with detailed models");
            Call(member,"ApplyVisualMode",hidden); Check(!farRenderer.enabled,"far building hidden setting is respected");
            Call(member,"ApplyVisualMode",normal);
            Static("ObjectVisibilityFilter","SetHidden",new[]{42},null); Call(member,"ApplyVisualMode",normal);
            Check(!farRenderer.enabled,"far building individual filter is respected");
            Static("ObjectVisibilityFilter","Clear"); Call(member,"ApplyVisualMode",normal);
            int buildingType = C("InputObjectType","BUILDING");
            Call(modes,"SetMode",buildingType,hidden);
            Static("ObjectVisibilityFilter","ApplyToObject",far,buildingType,42);
            Check(!farRenderer.enabled && !far.GetComponent<Collider>().enabled,"filter refresh preserves hidden far building layer");
            Call(modes,"SetMode",buildingType,normal);
            Set(farId,"status",C("Constants","STATUS_PENDING"));
            Call(modes,"SetFlags",buildingType,0);
            Check(!farRenderer.enabled,"far planned building respects show-plan setting");
            Set(farId,"status",C("Constants","STATUS_ACTIVE"));
            Call(modes,"SetFlags",buildingType,C("VisualFlag","SHOW_PLAN"));
            var forestMaterial=(Material)Call(forest,"MaterialFor",farOriginal);
            Check(Mathf.Approximately(forestMaterial.GetFloat("_UndergroundAlpha"),.08f) && forestMaterial.GetFloat("_ZWrite")==0 && !forestMaterial.GetShaderPassEnabled("ShadowCaster"),"GPU forest fades without depth or shadow occlusion");
            var propMaterial=(Material)Static("UndergroundMaterials","ForSurface",farOriginal);
            Check(Mathf.Approximately(propMaterial.GetFloat("_Opacity"),.08f) && propMaterial.enableInstancing,"instanced props use the same opacity");
            foreach(var path in new[]{road,rail,infra}) Build(path);
            var proxies = (IDictionary)Field(pathManager,"pathObjectsCache");
            Check(proxies.Count==3,"road, rail and model-less infrastructure are drawn");
            var proxy = (GameObject)proxies[1];
            Check(proxy.transform.parent==pathContainer.transform && proxy.transform.position==Vector3.zero
                && proxy.transform.rotation==Quaternion.identity,"manager translation and rotation do not move path geometry");
            var expected = (List<Vector3>)Call(pathManager,"CalculatePathPoints",road,true);
            Check(Vector3.Distance(proxy.GetComponent<MeshCollider>().bounds.center,
                (expected[0]+expected[expected.Count-1])*.5f)<.2f,"world collider matches path coordinates");
            var proxyRenderers = proxy.GetComponentsInChildren<MeshRenderer>().Where(r=>r.GetComponent(T("OverlayMeshRenderer"))==null).ToArray();
            var markers = proxy.GetComponentsInChildren(T("OverlayMeshRenderer"));
            Check(markers.Length==1,"depth markers share the generated ceiling part");
            var markerMesh = markers[0].GetComponent<MeshFilter>().sharedMesh;
            var markerMaterial = markers[0].GetComponent<Renderer>().sharedMaterial;
            Check(markerMesh.vertexCount>0 && markerMesh.vertexCount%12==0 && markerMesh.triangles.Length==markerMesh.vertexCount/12*18,
                "each depth anchor uses twelve vertices and six triangles");
            Check(markerMaterial.IsKeywordEnabled("OVERLAY_GPU_BILLBOARD") && (int)markerMaterial.GetFloat("_ZTest")==4,"markers use depth-tested GPU billboards");
            var markerVertices = markerMesh.vertices;
            var ceiling = markers[0].transform.parent.GetComponent<MeshFilter>();
            for (int i=0;i<markerVertices.Length/12;i++)
            {
                Vector3 top=markers[0].transform.TransformPoint(markerVertices[i*4]);
                Vector3 ground=markers[0].transform.TransformPoint(markerVertices[i*4+2]);
                float ceilingHeight=MeshHeightAt(ceiling,top);
                Check(Mathf.Abs(top.y-ceilingHeight)<.15f,"anchor attaches to the rendered ceiling upper surface");
                Check(Vector2.Distance(new Vector2(top.x,top.z),new Vector2(ground.x,ground.z))<.001f
                    && Mathf.Abs(ground.y-(terrain.SampleHeight(ground)+terrain.transform.position.y))<.01f,
                    "anchor reaches the actual terrain vertically: top="+top+" ground="+ground+" expected="+(terrain.SampleHeight(ground)+terrain.transform.position.y));
            }
            Check(((GameObject)proxies[2]).GetComponentsInChildren(T("OverlayMeshRenderer")).All(c=>c.GetComponent<Renderer>().sharedMaterial==markerMaterial),
                "road and railway markers share a material");
            Check(proxyRenderers.Length>1,"underground road uses real body and sidewalk models");
            Check(((GameObject)proxies[2]).GetComponentsInChildren<MeshFilter>().Any(filter=>filter.gameObject.name.EndsWith("/track")),"underground rail uses the existing track model");
            Check(proxy.GetComponent<Collider>()!=null,"underground selection collider exists");
            Check(proxy.GetComponent<MeshFilter>().sharedMesh.bounds.center.y<0,"negative absolute elevation is preserved");
            var roadMaterials=proxyRenderers.SelectMany(renderer=>renderer.sharedMaterials).ToArray();
            Check(roadMaterials.All(material=>material!=null && material.shader.name!="CityGenesis/UndergroundGhost"),"underground models retain opaque original materials");
            var setback=Activator.CreateInstance(T("SidewalkSetback"));
            var reference=Static("PathMeshFactory","GeneratePathMesh",road,new List<Vector3>{new Vector3(-70,0,-25),new Vector3(70,0,-25)},setback,setback);
            var referenceGO=(GameObject)reference.GetType().GetProperty("GeneratedObject").GetValue(reference);
            var referenceMaterials=referenceGO.GetComponentsInChildren<MeshRenderer>().SelectMany(renderer=>renderer.sharedMaterials).ToArray();
            var undergroundSurfaceMaterials=proxyRenderers.Where(renderer=>!renderer.name.EndsWith("/wall_underground") && !renderer.name.EndsWith("/ceiling_underground")).SelectMany(renderer=>renderer.sharedMaterials).ToArray();
            Check(undergroundSurfaceMaterials.Length==referenceMaterials.Length && undergroundSurfaceMaterials.SequenceEqual(referenceMaterials),"ground and underground road surfaces share exactly the same materials");
            CheckTunnelParts(masters, modelCache);
            referenceGO.SetActive(false); UnityEngine.Object.Destroy(referenceGO);
            UnityEngine.Object.Destroy((Mesh)reference.GetType().GetProperty("Mesh").GetValue(reference));
            var proxyVisual=proxy.GetComponent(T("VisualModeObject"));
            Call(proxyVisual,"SetHover",true);
            var blue=Resources.Load<Material>("HighlightMaterial");
            Check(blue!=null && proxyRenderers.All(renderer=>renderer.sharedMaterials.All(material=>material==blue)),"hover replaces underground models with existing blue material");
            Check(markers[0].GetComponent<Renderer>().sharedMaterial==markerMaterial,"hover preserves depth marker colors and shared material");
            Call(proxyVisual,"SetHover",false);
            Check(proxyRenderers.SelectMany(renderer=>renderer.sharedMaterials).SequenceEqual(roadMaterials),"leaving hover restores original model materials");

            var input = Add("InputHandler");
            Call(input,"SetModePreset",C("InputModePreset","ALL_NORMAL"));
            var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube); blocker.tag="BuildingObject";
            blocker.transform.position=new Vector3(0,5,-25); blocker.transform.localScale=new Vector3(15,10,15);
            var visual = Add("VisualModeObject",blocker); Set(visual,"objectType",C("InputObjectType","BUILDING"));
            var originalMaterial = blocker.GetComponent<Renderer>().sharedMaterial;
            Call(visual,"ApplyVisualMode",normal);
            Check(blocker.GetComponent<Renderer>().sharedMaterial!=originalMaterial,"visible surface building fades");
            Physics.SyncTransforms();
            var hits = Physics.RaycastAll(new Ray(new Vector3(0,80,-25),Vector3.down),200);
            object[] pick = {hits,default(RaycastHit)};
            Check((bool)Call(input,"TryGetUndergroundHit",pick),"underground path is selectable through terrain and building");
            Check(((RaycastHit)pick[1]).collider.gameObject==proxy,"selection reaches the intended underground path");
            var surfacePath = MakePath(7,221,C("Constants","PATH_STREET"),-25,0);
            Set(surfacePath,"follow_terrain",1);
            var surfaceGO = Build(surfacePath);
            CheckParcelSelection(input, proxy);
            Physics.SyncTransforms();
            hits = Physics.RaycastAll(new Ray(new Vector3(0,80,-25),Vector3.down),200).OrderBy(h=>h.distance).ToArray();
            pick = new object[] {hits,default(RaycastHit)};
            Check((bool)Call(input,"TryGetObjectHit",pick) && ((RaycastHit)pick[1]).collider.gameObject==proxy,
                "underground selection passes through a surface road and building");
            object[] surfaceOnlyPick = {hits.Where(h=>h.collider.gameObject!=proxy).ToArray(),default(RaycastHit)};
            Check((bool)Call(input,"TryGetObjectHit",surfaceOnlyPick)
                && (int)Call(input,"GetObjectType",((RaycastHit)surfaceOnlyPick[1]).collider.gameObject)==terrainType,
                "surface roads and buildings cannot become fallback hover targets when no tunnel is hit");
            Check(!(bool)Call(input,"CanSelectObject",blocker,blocker.transform.position,buildingType)
                && !(bool)Call(input,"CanSelectObject",surfaceGO,new Vector3(0,5,-25),pathType),
                "surface object selection is disabled underground");
            int clickCount = 0;
            Call(input,"SetClickEvent",new Action<int,Vector3>((_,__)=>clickCount++));
            Call(input,"HandleObjectClick",blocker,blocker.transform.position,buildingType);
            Check(clickCount==0,"a queued surface click cannot fire after switching underground");
            Static("UndergroundView","SetActive",false);
            Check((bool)Call(input,"CanSelectObject",blocker,blocker.transform.position,buildingType)
                && (bool)Call(input,"CanSelectObject",surfaceGO,new Vector3(0,5,-25),pathType),
                "normal view restores surface selection");
            Static("UndergroundView","SetActive",true);
            Call(input,"SetModePreset",C("InputModePreset","REMOVE_OBJECTS"));
            Check((bool)Call(input,"TryGetUndergroundHit",pick),"bulldozer can select an underground plan");
            var removal = Add("RemovalManager");
            Check(!(bool)Call(removal,"IsRemovable",pathType,1),"completed roads retain existing demolition rules");
            Set(road,"status",C("Constants","STATUS_PENDING"));
            Check((bool)Call(removal,"IsRemovable",pathType,1),"planned underground roads retain delete eligibility");
            Call(input,"SetModePreset",C("InputModePreset","ROAD_CONSTRUCTION"));
            Check(!(bool)Call(input,"TryGetUndergroundHit",pick),"construction keeps terrain input");

            Static("ObjectVisibilityFilter","SetHidden",null,new[]{1});
            UnityEngine.Object.DestroyImmediate(proxy.GetComponent<MeshCollider>());
            Drain((IEnumerator)Call(pathManager,"SetColliderAsync",proxy,proxy.GetComponent<MeshFilter>().sharedMesh));
            Check(proxyRenderers.All(renderer=>!renderer.enabled) && !proxy.GetComponent<Collider>().enabled,"individual hidden filter affects underground geometry and picking");
            Check(markers.All(c=>!c.GetComponent<Renderer>().enabled),"individual hidden filter also hides depth markers");
            Static("ObjectVisibilityFilter","Clear");
            Call(modes,"SetMode",pathType,hidden);
            UnityEngine.Object.DestroyImmediate(proxy.GetComponent<MeshCollider>());
            Drain((IEnumerator)Call(pathManager,"SetColliderAsync",proxy,proxy.GetComponent<MeshFilter>().sharedMesh));
            Check(proxyRenderers.All(renderer=>!renderer.enabled) && !proxy.GetComponent<Collider>().enabled,"hidden road layer stays hidden after asynchronous collider creation");
            Check(markers.All(c=>!c.GetComponent<Renderer>().enabled),"hidden road layer hides depth markers through the common renderer update");
            Call(modes,"SetMode",pathType,normal);
            Call(visual,"ApplyVisualMode",hidden); Check(!blocker.GetComponent<Renderer>().enabled,"hidden surface building stays hidden");
            Call(visual,"ApplyVisualMode",normal);

            var latePart=GameObject.CreatePrimitive(PrimitiveType.Cube); latePart.transform.SetParent(blocker.transform,false);
            var lateOriginal=latePart.GetComponent<Renderer>().sharedMaterial;
            Set(visual,"useChildRenderers",true); Call(visual,"RefreshMaterials");
            Check(latePart.GetComponent<Renderer>().sharedMaterial!=lateOriginal,"late generated parts also fade");

            var exterior = Add("BuildingExteriorRenderer", blocker);
            var exteriorJobType = T("BuildingExteriorRenderer+Build");
            var exteriorJobs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(exteriorJobType));
            exteriorJobs.Add(Delegate.CreateDelegate(exteriorJobType, typeof(UndergroundQA).GetMethod(nameof(BuildExterior), Flags)));
            Call(exterior,"Configure",exteriorJobs);
            Drain((IEnumerator)Call(exterior,"Generate"));
            Check(blocker.GetComponentsInChildren<MeshRenderer>().All(r=>r.sharedMaterials.All(m=>m.shader.name=="CityGenesis/UndergroundGhost")),
                "asynchronous exterior generation preserves opacity of the whole building without hovering");
            Call(modes,"SetMode",buildingType,C("VisualModeBuilding","DURABILITY"));
            Call(visual,"ApplyVisualMode",C("VisualModeBuilding","DURABILITY"));
            Call(visual,"SetStatus",C("Constants","STATUS_PENDING"));
            Call(visual,"RefreshMaterials");
            Check(blocker.GetComponentsInChildren<MeshRenderer>().All(r=>r.sharedMaterials.All(m=>m.shader.name=="CityGenesis/UndergroundGhost")),
                "material refresh preserves ghosting in data and planned color modes");
            Call(visual,"SetStatus",C("Constants","STATUS_ACTIVE"));
            Call(modes,"SetMode",buildingType,normal); Call(visual,"ApplyVisualMode",normal);
            Check((Material)Static("UndergroundMaterials","Source",blocker.GetComponent<Renderer>().sharedMaterial)==originalMaterial,
                "refresh does not record planned or data color as the original building material");
            Call(visual,"InitializeMaterials");
            Check(blocker.GetComponent<Renderer>().sharedMaterial.shader.name=="CityGenesis/UndergroundGhost",
                "reinitializing source materials does not leave a surface model opaque");

            Set(road,"status",C("Constants","STATUS_ACTIVE"));
            proxy = Build(road,drawClose);
            Check(Field(proxy.GetComponent(T("DrawLodInfo")),"drawLod").Equals(drawClose),"underground paths use normal close LOD metadata");
            Call(pathManager,"CreateOrUpdateFarPath",road,true);
            var farPath = (GameObject)Call(pathManager,"GetPathObject",1);
            Check(Field(farPath.GetComponent(T("DrawLodInfo")),"drawLod").ToString()=="Far"
                && farPath.GetComponentsInChildren<MeshRenderer>().Length>0,"underground paths use normal far LOD generation");
            Check(farPath.GetComponentsInChildren(T("OverlayMeshRenderer")).Length==0,"whole-path far LOD omits depth markers");
            Check(farPath.GetComponentsInChildren<MeshRenderer>().All(r=>r.sharedMaterials.All(m=>m.shader.name!="CityGenesis/UndergroundGhost")),"far tunnel sections remain opaque");
            Call(pathManager,"HideUnloadedPath",1,farPath,new List<int>());
            Check(!farPath.activeSelf,"normal cell retirement hides underground paths");
            proxy = Build(road);
            CheckStaticOverlay(root);
            var longPath=MakePath(10,221,C("Constants","PATH_STREET"),80,-15);
            Set(longPath,"start_point",new List<object>{-160d,-15d,80d});
            Set(longPath,"end_point",new List<object>{160d,-15d,80d});
            CheckDepthMarkerLod(pathManager,Build(longPath),longPath,camera);

            var mixed = MakePath(5,221,C("Constants","PATH_STREET"),60,-14);
            Set(mixed,"end_point",new List<object>{70d,0d,60d});
            var mixedGO = Build(mixed);
            var mixedSections=mixedGO.GetComponentsInChildren(T("PathSection"));
            Check(mixedSections.Any(s=>(bool)Field(s,"IsUnderground")) && mixedSections.Any(s=>!(bool)Field(s,"IsUnderground")),"one path retains both surface and tunnel sections");
            foreach (var section in mixedSections)
                Check(section.GetComponentsInChildren<MeshRenderer>().All(r=>r.sharedMaterials.All(m=>(m.shader.name=="CityGenesis/UndergroundGhost") != (bool)Field(section,"IsUnderground"))),"section opacity follows depth classification");
            var mixedPoints = (List<Vector3>)Call(pathManager,"CalculatePathPoints",mixed,true);
            Call(input,"SetModePreset",C("InputModePreset","ALL_NORMAL"));
            Check((bool)Call(input,"CanSelectObject",mixedGO,mixedPoints[0],pathType)
                && !(bool)Call(input,"CanSelectObject",mixedGO,mixedPoints[mixedPoints.Count-1],pathType),
                "mixed path selection accepts the tunnel portion and rejects the surface portion");

            var construction = MakePath(6,221,C("Constants","PATH_STREET"),-60,-15,"ConstructionPathSummary");
            Set(construction,"status",C("Constants","STATUS_CONSTRUCTING")); Set(construction,"construction_progress",25f);
            var constructionGO = Build(construction);
            var completed=constructionGO.GetComponentsInChildren<Transform>().Single(t=>t.name=="CompletedSurface");
            float maxBuiltX=completed.GetComponentsInChildren<MeshRenderer>().Max(r=>r.bounds.max.x);
            Check(maxBuiltX < -25f && maxBuiltX > -40f,"underground construction builds only the completed quarter");
            Check(completed.GetComponentsInChildren<MeshFilter>().Count(f=>f.name.EndsWith("/wall_underground"))==2,"completed tunnel construction has both walls");
            Check(completed.GetComponentsInChildren<MeshFilter>().Count(f=>f.name.EndsWith("/ceiling_underground"))==1,"completed tunnel construction has its ceiling");
            var dirtMesh=constructionGO.GetComponentsInChildren<MeshFilter>().Single(f=>f.name=="ConstructionDirt").sharedMesh;
            Check(dirtMesh.bounds.center.y < -8f,"unfinished underground soil stays at tunnel elevation");
            Set(construction,"construction_progress",75f);
            constructionGO=Build(construction);
            completed=constructionGO.GetComponentsInChildren<Transform>().Single(t=>t.name=="CompletedSurface");
            Check(completed.GetComponentsInChildren<MeshRenderer>().Max(r=>r.bounds.max.x)>25f,"same-version construction progress updates the common path object");

            var node=Activator.CreateInstance(T("NodeData")); Set(node,"node_id",100); Set(node,"position",new[]{0f,-14f,-25f});
            ((IDictionary)Field(pathManager,"nodeDataCache"))[100]=node;
            Set(road,"start_node_id",100); Set(rail,"start_node_id",100);
            Call(pathManager,"SavePathToCache",road); Call(pathManager,"SavePathToCache",rail);
            var nodeMesh=(Mesh)Static("PathMeshFactory","BuildStripMesh",new List<Vector3>{new Vector3(-2,0,0),new Vector3(2,0,0)},8f);
            var nodeResult=Activator.CreateInstance(T("NodeMeshResult"),new object[]{nodeMesh,roadMaterials[0],null,null,null,null});
            Call(pathManager,"UpdateNodeSlab",100,new Vector3(0,-14,-25),nodeResult,"");
            var nodeGO=(GameObject)((IDictionary)Field(pathManager,"nodeIntersectionMeshes"))[100];
            Call(nodeGO.GetComponent(T("VisualModeObject")),"Start");
            Check(nodeGO.GetComponent<Renderer>().sharedMaterials.All(m=>m.shader.name!="CityGenesis/UndergroundGhost"),"underground junctions keep opaque original materials");
            Vector3 surfaceNodePosition = new Vector3(0,(float)Static("Utils","GetGroundHeight",new Vector3(0,0,-25)),-25);
            Call(pathManager,"UpdateNodeSlab",100,surfaceNodePosition,nodeResult,"");
            var updatedNodeMaterial = new Material(roadMaterials[0]);
            nodeResult=Activator.CreateInstance(T("NodeMeshResult"),new object[]{nodeMesh,updatedNodeMaterial,null,null,null,null});
            Call(pathManager,"UpdateNodeSlab",100,surfaceNodePosition,nodeResult,"");
            Check(nodeGO.GetComponent<Renderer>().sharedMaterial.shader.name=="CityGenesis/UndergroundGhost"
                && (Material)Static("UndergroundMaterials","Source",nodeGO.GetComponent<Renderer>().sharedMaterial)==updatedNodeMaterial,
                "surface junction updates retain the new source material and reapply transparency immediately");
            Call(pathManager,"UpdateNodeSlab",100,new Vector3(0,-14,-25),nodeResult,"");
            Check(nodeGO.GetComponent<Renderer>().sharedMaterial==updatedNodeMaterial,"junction depth changes remove only the ghost material");

            var keep=(HashSet<Vector2Int>)Field(pathManager,"cellsToKeepVisible");
            var cellLods=(IDictionary)Field(pathManager,"cellPathLod");
            foreach (object path in paths.Values)
                foreach (var cell in (IEnumerable<Vector2Int>)Call(pathManager,"GetPathCoveredCellsCached",path))
                { keep.Add(cell); cellLods[cell]=Enum.Parse(T("LodTier"),"Near"); }
            Call(pathManager,"ClearTerrainFollowQueue");
            var beforeNeighbor=(GameObject)Call(pathManager,"GetPathObject",2);
            Call(pathManager,"QueueTrackNeighborRebuild",2);
            Call(pathManager,"FlushPendingTrackNeighborRebuilds",new object[]{null});
            FinishUpdates();
            Check((GameObject)Call(pathManager,"GetPathObject",2)!=beforeNeighbor,"neighbor changes rebuild the underground model in the common registry");
            var beforeCut=(GameObject)Call(pathManager,"GetPathObject",2);
            Call(pathManager,"OnIntersectionCutChanged",rail,true);
            FinishUpdates();
            Check((GameObject)Call(pathManager,"GetPathObject",2)!=beforeCut,"junction cut changes rebuild the same underground model");

            string snapshot=root+"/Temp/path576-underground.json";
            if (File.Exists(snapshot))
            {
                var actual=Newtonsoft.Json.JsonConvert.DeserializeObject(File.ReadAllText(snapshot),T("GeneratedModels.PathSummary"));
                var actualGO=Build(actual);
                var actualPoints=(List<Vector3>)Call(pathManager,"CalculatePathPoints",actual,true);
                Check(actualGO.transform.parent==pathContainer.transform && actualGO.transform.position==Vector3.zero,"path 576 uses the common world container");
                Check(Vector3.Distance(actualGO.GetComponent<MeshCollider>().bounds.center,(actualPoints[0]+actualPoints[actualPoints.Count-1])*.5f)<2f,"path 576 platform and collider preserve saved XZ and Y");
                actualGO.SetActive(false);
            }
            Static("CoastlineStore","SetFromGeoJson",Newtonsoft.Json.Linq.JArray.Parse("[[[20,-100],[100,-100],[100,100],[20,100],[20,-100]]]"));
            var underground=UnityEngine.Object.FindAnyObjectByType(T("UndergroundView"));
            Call(underground,"LateUpdate");
            Check(((Mesh)Field(underground,"shoreMesh")).vertexCount>0,"waterline outline exists");
            File.WriteAllText(root+"/Temp/UndergroundQA/materials.txt",string.Join("\n",proxies.Values.Cast<GameObject>().SelectMany(go=>go.GetComponentsInChildren<MeshRenderer>()).SelectMany(renderer=>renderer.sharedMaterials).Distinct().Select(material=>material.name+": "+material.shader.name+" base="+material.color+" errors="+string.Join("; ",ShaderUtil.GetShaderMessages(material.shader).Select(message=>message.message)))));
            File.WriteAllBytes(root+"/Temp/UndergroundQA/underground.png",(byte[])Static("CameraCaptureUtil","CapturePng",camera,1200,750));
            foreach (string name in new[] { "UndergroundGhost", "UndergroundGround", "UndergroundSoil", "UndergroundShore", "Forest", "OverlayLine" })
                Check(!ShaderUtil.ShaderHasError(Resources.Load<Shader>("Shaders/"+name)),"shader compile: "+name);

            // 同期ハーネス内の再生成にも、次フレームにUnityが呼ぶ登録処理を適用する。
            foreach(var current in proxies.Values.Cast<GameObject>())
                Call(current.GetComponent(T("VisualModeObject")),"Start");
            Call(modes,"SetMode",terrainType,normal);
            Drain((IEnumerator)Call(modes,"NotifyObjectsBatched",pathType,normal));
            Check(proxies.Values.Cast<GameObject>().SelectMany(go=>go.GetComponentsInChildren(T("OverlayMeshRenderer"))).All(c=>!c.GetComponent<Renderer>().enabled),
                "normal view hides depth markers through the common visual notification");
            Check(!nodeGO.GetComponent<Renderer>().enabled,"tunnel junction hides with common visual notification before regeneration");
            FinishUpdates();
            Call(terrainVisual,"ApplyVisualMode",normal); Call(seaVisual,"ApplyVisualMode",normal); Call(visual,"ApplyVisualMode",normal);
            Check(terrain.enabled && sea.GetComponent<Renderer>().enabled,"normal ground and sea are restored");
            Check(blocker.GetComponent<Renderer>().sharedMaterial==originalMaterial && latePart.GetComponent<Renderer>().sharedMaterial==lateOriginal,"original materials survive switching and late generation");
            Check(camera.backgroundColor==Color.gray,"camera background is restored");
            proxy=Build(road);
            Check(proxy.GetComponentsInChildren<MeshRenderer>().Length==0 && proxy.GetComponent<MeshCollider>()==null,"normal view excludes tunnel meshes and colliders through common generation");
            Check(farRenderer.sharedMaterial==farOriginal,"far building original material is restored");
            forestMaterial=(Material)Call(forest,"MaterialFor",farOriginal);
            Check(forestMaterial.GetFloat("_UndergroundAlpha")==1 && forestMaterial.GetFloat("_ZWrite")==1 && forestMaterial.GetShaderPassEnabled("ShadowCaster"),"GPU forest depth and shadows return in normal view");
            Check((Material)Static("UndergroundMaterials","ForSurface",farOriginal)==farOriginal,"instanced props return to original material");
            Call(forest,"Dispose");
            CheckRoadSnapping(camera);
            File.WriteAllText(report,"PASS\n"+checks+" checks\nScreenshot: underground.png");
            EditorApplication.Exit(0);
        }
        catch(Exception error) { File.WriteAllText(report,"FAIL\n"+checks+" checks\n"+error); EditorApplication.Exit(1); }
    }

    private static float MeshHeightAt(MeshFilter filter, Vector3 point)
    {
        var vertices=filter.sharedMesh.vertices; var triangles=filter.sharedMesh.triangles;
        float height=float.NegativeInfinity;
        for(int i=0;i<triangles.Length;i+=3)
        {
            Vector3 a=filter.transform.TransformPoint(vertices[triangles[i]]), b=filter.transform.TransformPoint(vertices[triangles[i+1]]), c=filter.transform.TransformPoint(vertices[triangles[i+2]]);
            float denominator=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
            if(Mathf.Abs(denominator)<.000001f) continue;
            float u=((b.z-c.z)*(point.x-c.x)+(c.x-b.x)*(point.z-c.z))/denominator;
            float v=((c.z-a.z)*(point.x-c.x)+(a.x-c.x)*(point.z-c.z))/denominator;
            if(u>=-.001f && v>=-.001f && u+v<=1.001f) height=Mathf.Max(height,u*a.y+v*b.y+(1-u-v)*c.y);
        }
        return height;
    }

    private static void CheckStaticOverlay(string root)
    {
        var overlay=(Component)Static("OverlayMeshRenderer","CreateStatic","StaticOverlayQA",true,null);
        var camera=new GameObject("StaticOverlayCamera").AddComponent<Camera>();
        var target=new RenderTexture(128,128,24);
        var pixels=new Texture2D(128,128,TextureFormat.RGBA32,false);
        var prior=RenderTexture.active;
        try
        {
            overlay.gameObject.layer=31;
            camera.cullingMask=1<<31; camera.targetTexture=target;
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.black;
            camera.nearClipPlane=.01f; camera.farClipPlane=50; camera.orthographicSize=2;
            Vector3 a=new Vector3(-.4f,-.6f,0), b=new Vector3(-.4f,.6f,0);
            Call(overlay,"AddSolidLine",a,b,Color.white,1.5f);
            Call(overlay,"AddCircle",a,3f,Color.white); Call(overlay,"AddCircle",b,3f,Color.white);
            Call(overlay,"EndStaticFrame");
            var mesh=overlay.GetComponent<MeshFilter>().sharedMesh;
            var original=mesh.vertices;
            foreach(bool orthographic in new[]{false,true})
            {
                camera.orthographic=orthographic;
                camera.transform.position=orthographic ? new Vector3(2,1,-5) : new Vector3(0,0,-5);
                camera.transform.LookAt(Vector3.zero);
                camera.Render(); RenderTexture.active=target;
                pixels.ReadPixels(new Rect(0,0,128,128),0,0); pixels.Apply();
                foreach(Vector3 point in new[]{a,b,(a+b)*.5f})
                {
                    Vector3 screen=camera.WorldToViewportPoint(point)*128;
                    bool white=false;
                    for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++)
                        white|=pixels.GetPixel(Mathf.RoundToInt(screen.x)+x,Mathf.RoundToInt(screen.y)+y).r>.4f;
                    Check(white,"GPU overlay follows camera projection: "+orthographic+" "+point);
                }
                Check(mesh.vertices.SequenceEqual(original),"camera movement leaves static overlay CPU vertices unchanged");
                File.WriteAllBytes(root+"/Temp/UndergroundQA/depth-markers-"+(orthographic ? "orthographic" : "perspective")+".png",pixels.EncodeToPNG());
            }
        }
        finally
        {
            RenderTexture.active=prior; camera.targetTexture=null;
            UnityEngine.Object.DestroyImmediate(overlay.gameObject); UnityEngine.Object.DestroyImmediate(camera.gameObject);
            UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(pixels);
        }
    }

    private static void CheckDepthMarkerLod(Component manager, GameObject pathGO, object path, Camera camera)
    {
        Vector3 position=camera.transform.position;
        var parts=pathGO.GetComponentsInChildren(T("SplinePartLod"),true);
        void Finish(Component part)
        {
            Call(part,"OnDisable");
            T("SplinePartLod").GetField("tickFrame",Flags).SetValue(null,-1);
            var worker=(IEnumerator)Call(part,"Worker");
            for(int i=0;i<100;i++)
            {
                worker.MoveNext();
                if(worker.Current is WaitUntil) break;
            }
            Check((bool)part.GetType().GetProperty("IsInitialRenderReady").GetValue(part),"fixed-section LOD finishes through the existing worker");
        }
        try
        {
            foreach(var part in parts) Finish(part);
            var markers=pathGO.GetComponentsInChildren(T("OverlayMeshRenderer"));
            Check(markers.Length>1,"long tunnel markers belong to the existing fixed LOD sections");
            var original=markers.Select(c=>c.GetComponent<MeshFilter>().sharedMesh.GetInstanceID()).OrderBy(id=>id).ToArray();
            var anchors=markers.SelectMany(c=>{
                var mesh=c.GetComponent<MeshFilter>().sharedMesh; var vertices=mesh.vertices;
                return Enumerable.Range(0,mesh.vertexCount/12).Select(i=>c.transform.TransformPoint(vertices[i*4]));
            }).OrderBy(p=>p.x).ToArray();
            Check(anchors.Length==11 && anchors.Select(p=>Mathf.RoundToInt(p.x)).Distinct().Count()==11,"fixed-section boundaries neither duplicate nor omit regular anchors");
            pathGO.SetActive(false);
            Check(markers.All(c=>!c.gameObject.activeInHierarchy),"path retirement hides attached depth markers");
            pathGO.SetActive(true);
            camera.transform.position=new Vector3(10000,3000,10000);
            foreach(var part in parts) Finish(part);
            Check(pathGO.GetComponentsInChildren(T("OverlayMeshRenderer")).Length==0,"fixed-section far LOD parks depth markers with the ceiling");
            camera.transform.position=position;
            foreach(var part in parts) Finish(part);
            Check(pathGO.GetComponentsInChildren(T("OverlayMeshRenderer")).Select(c=>c.GetComponent<MeshFilter>().sharedMesh.GetInstanceID()).OrderBy(id=>id).SequenceEqual(original),
                "fixed-section cache restores the same marker meshes");
            Set(path,"version",1);
            Set(path,"start_point",new List<object>{-160d,-25d,80d}); Set(path,"end_point",new List<object>{160d,-25d,80d});
            Call(manager,"CreateOrUpdatePath",path,false,false,Enum.Parse(T("DrawLod"),"Normal"),0d,1d);
            var updated=(GameObject)Call(manager,"GetPathObject",Field(path,"path_id"));
            Check(updated!=pathGO,"data version changes rebuild the path through the common cache key");
            foreach(var part in updated.GetComponentsInChildren(T("SplinePartLod"),true)) Finish(part);
            var next=updated.GetComponentsInChildren(T("OverlayMeshRenderer"));
            Check(next.Length>0 && next.All(c=>c.transform.TransformPoint(c.GetComponent<MeshFilter>().sharedMesh.vertices[0]).y<-20),
                "updated path geometry regenerates the marker attachment heights");
            Check(T("PathDrawContext").GetProperty("PartCreated").GetValue(null)==null,"deferred part generation restores the common draw context");
        }
        finally { camera.transform.position=position; }
    }

    private static void CheckTunnelParts(IDictionary masters, IDictionary modelCache)
    {
        var setback = Activator.CreateInstance(T("SidewalkSetback"));
        object MakePath(int variant, int type)
        {
            var path = Activator.CreateInstance(T("GeneratedModels.PathSummary"));
            Set(path,"path_id",90000+variant); Set(path,"path_variant",variant); Set(path,"path_type",type);
            Set(path,"status",C("Constants","STATUS_ACTIVE")); Set(path,"follow_terrain",0);
            Set(path,"start_point",new List<object>{-40d,-15d,-70d}); Set(path,"end_point",new List<object>{40d,-15d,-70d});
            return path;
        }
        GameObject Generate(object path, List<Vector3> points)
        {
            var result = Static("PathMeshFactory","GeneratePathMesh",path,points,setback,setback);
            var go = (GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
            var selection = (Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
            if (selection != null) UnityEngine.Object.DestroyImmediate(selection);
            return go;
        }
        var below = new List<Vector3>{new Vector3(-40,-15,-70),new Vector3(40,-15,-70)};
        var above = new List<Vector3>{new Vector3(-40,5,-70),new Vector3(40,5,-70)};
        MeshFilter[] Walls(GameObject go) => go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.EndsWith("/wall_underground")).ToArray();
        MeshFilter[] Ceilings(GameObject go) => go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.EndsWith("/ceiling_underground")).ToArray();
        bool Outdoor(GameObject go) => go.GetComponentsInChildren<Transform>().Any(t=>t.name.StartsWith("Equipment") || t.name.StartsWith("tree("));

        var road = MakePath(221,C("Constants","PATH_STREET"));
        var roadMaster = masters[221];
        var saved = new Dictionary<string,object>();
        foreach (string field in new[]{"streetlight_type_allowed","street_tree_type_allowed","utility_mode_allowed","wall_width"}) saved[field]=Field(roadMaster,field);
        try
        {
            foreach (string field in new[]{"streetlight_type_allowed","street_tree_type_allowed","utility_mode_allowed"}) Set(roadMaster,field,true);
            Set(roadMaster,"wall_width",null);
            Set(road,"streetlight_type",1); Set(road,"street_tree_type",1); Set(road,"utility_mode",1);
            var underground = Generate(road,below);
            var walls = Walls(underground);
            Check(walls.Length==2,"roads without a ground wall get two underground walls");
            Check(!Outdoor(underground),"tunnels suppress lamps, street trees, utility poles and wires");
            var ceilings = Ceilings(underground);
            Check(ceilings.Length==1,"tunnels get a ceiling between both walls");
            Check(ceilings.All(f=>f.sharedMesh.normals.All(n=>n.y<-.99f)),"ceiling normals face down");
            Check(ceilings.All(f=>{
                var v=f.sharedMesh.vertices; var t=f.sharedMesh.triangles;
                return Enumerable.Range(0,t.Length/3).All(i=>Vector3.Cross(v[t[3*i+1]]-v[t[3*i]],v[t[3*i+2]]-v[t[3*i]]).y<0);
            }),"ceiling winding faces down");
            Check(ceilings.All(f=>Mathf.Abs(f.GetComponent<Renderer>().bounds.center.y-(-15+3))<.1f),"ceiling meets the clearance height");
            CheckCeilingCulling(ceilings[0]);
            Check(walls.All(f=>Mathf.Abs(f.GetComponent<Renderer>().bounds.max.y-(-15+3))<.1f),"default walls reach the clearance height");
            Check(walls.All(f=>f.GetComponent<Renderer>().sharedMaterial.name=="PATH_CONCRETE"),"default walls use shared concrete");
            UnityEngine.Object.DestroyImmediate(underground);
            var ground = Generate(road,above);
            Check(Walls(ground).Length==0 && Ceilings(ground).Length==0 && Outdoor(ground),"ground keeps outdoor equipment and has no tunnel shell");
            UnityEngine.Object.DestroyImmediate(ground);
            var mixed = Generate(road,new List<Vector3>{new Vector3(-40,5,-70),new Vector3(0,-15,-70),new Vector3(40,-15,-70)});
            Check(Walls(mixed).Length==2 && Outdoor(mixed),"mixed paths retain ground equipment and draw tunnel walls");
            Check(Walls(mixed).All(f=>f.GetComponent<Renderer>().bounds.min.x>-40),"tunnel walls stop at the portal boundary");
            Check(Ceilings(mixed).Length==1 && Ceilings(mixed)[0].GetComponent<Renderer>().bounds.min.x>-40,"ceiling stops at the portal boundary");
            UnityEngine.Object.DestroyImmediate(mixed);

            var authored = GameObject.CreatePrimitive(PrimitiveType.Cube); authored.SetActive(false);
            modelCache["Models/Paths/p221/wall"] = authored;
            Set(roadMaster,"wall_width",.3f);
            ground = Generate(road,above);
            Check(ground.GetComponentsInChildren<MeshFilter>().Count(f=>f.name.EndsWith("/wall"))==2,"ground retains the regular wall model");
            UnityEngine.Object.DestroyImmediate(ground);
            underground = Generate(road,below);
            Check(Walls(underground).Length==2 && !underground.GetComponentsInChildren<MeshFilter>().Any(f=>f.name.EndsWith("/wall")),"tunnel wall replaces the regular wall");
            UnityEngine.Object.DestroyImmediate(underground);
            modelCache.Remove("Models/Paths/p221/wall");
            modelCache["Models/Paths/p221/wall_underground"] = authored;
            var close = GameObject.CreatePrimitive(PrimitiveType.Cube); close.SetActive(false);
            modelCache["Models/Paths/p221/wall_underground_close"] = close;
            object[] resolve = {"Models/Paths/p221/wall_underground","Models/Paths/p1/wall_underground",null};
            Check(Static("PathDrawContext","ResolvePrefab",resolve)==authored,"authored underground wall overrides the default");
            Static("PathDrawContext","Begin",Enum.Parse(T("DrawLod"),"Close"),0d,1d);
            try { Check(Static("PathDrawContext","ResolvePrefab",resolve)==close,"close underground wall uses the existing resolution order"); }
            finally { Static("PathDrawContext","End"); }
            modelCache.Remove("Models/Paths/p221/wall_underground"); modelCache.Remove("Models/Paths/p221/wall_underground_close");
            UnityEngine.Object.DestroyImmediate(authored); UnityEngine.Object.DestroyImmediate(close);
        }
        finally { foreach (var field in saved) Set(roadMaster,field.Key,field.Value); }

        var station = Generate(MakePath(461,C("Constants","PATH_TRACK")),below);
        Check(Walls(station).Length==2,"underground stations get walls on both sides");
        Check(Walls(station).Any(f=>Mathf.Abs(f.GetComponent<Renderer>().bounds.center.z+70)>5.4f),"station walls sit outside the platform");
        Check(!station.GetComponentsInChildren<Transform>().Any(t=>t.name.StartsWith("pole(") || t.name.StartsWith("beam(")),"underground railway has no poles or beams");
        Check(station.GetComponentsInChildren<MeshFilter>().Any(f=>f.name.EndsWith("/wire")),"underground railway retains its catenary");
        var stationWalls=Walls(station).Select(f=>f.GetComponent<Renderer>().bounds).ToArray();
        var stationCeilings=Ceilings(station);
        Check(stationCeilings.Length==1 && Mathf.Abs(stationCeilings[0].GetComponent<Renderer>().bounds.min.z-(stationWalls.Min(b=>b.center.z)+.1f))<.1f
            && Mathf.Abs(stationCeilings[0].GetComponent<Renderer>().bounds.max.z-(stationWalls.Max(b=>b.center.z)-.1f))<.1f,"station ceiling covers the asymmetric platform width between both walls");
        var edges = station.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.EndsWith("/platform")).ToArray();
        Check(edges.Length>0 && edges.All(f=>f.sharedMesh.triangles.All(i=>f.transform.TransformPoint(f.sharedMesh.vertices[i]).y<-15+3.31f)),"underground station keeps its platform without a canopy");
        UnityEngine.Object.DestroyImmediate(station);
        foreach (int variant in new[]{451,461,462,502})
            foreach (string part in new[]{"platform","platform_close"})
            {
                var model=(GameObject)modelCache[$"Models/Paths/p{variant}/{part}"];
                var source=model.GetComponent<MeshFilter>().sharedMesh;
                var filtered=(Mesh)Static("PathTunnelParts","WithoutPlatformRoof",source);
                var vertices=filtered.vertices;
                Check(filtered.triangles.Length>0 && filtered.triangles.All(i=>vertices[i].y<=3.2f),$"{variant}/{part} removes canopy and roof supports");
                Check(source.bounds.max.y>4 && filtered.bounds.max.y>1.4f,$"{variant}/{part} leaves the source and platform floor intact");
                if (variant==502) Check(filtered.triangles.Any(i=>vertices[i].y>2.7f),"monorail platform doors remain underground");
            }
    }

    private static void CheckCeilingCulling(MeshFilter ceiling)
    {
        var go=new GameObject("CeilingCullingCamera");
        var camera=go.AddComponent<Camera>();
        var target=new RenderTexture(64,64,24);
        var pixels=new Texture2D(64,64,TextureFormat.RGBA32,false);
        var oldTarget=RenderTexture.active;
        int oldLayer=ceiling.gameObject.layer;
        try
        {
            ceiling.gameObject.layer=31;
            camera.cullingMask=1<<31; camera.orthographic=true; camera.orthographicSize=2;
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.magenta;
            camera.nearClipPlane=.01f; camera.farClipPlane=10; camera.targetTexture=target;
            Vector3 center=ceiling.GetComponent<Renderer>().bounds.center;
            Color Render(float side)
            {
                camera.transform.position=center+Vector3.up*side*2;
                camera.transform.rotation=Quaternion.LookRotation(Vector3.down*side,Vector3.forward);
                camera.Render(); RenderTexture.active=target;
                pixels.ReadPixels(new Rect(0,0,64,64),0,0); pixels.Apply();
                return pixels.GetPixel(32,32);
            }
            Color top=Render(1), bottom=Render(-1);
            Check(top.r>.9f && top.b>.9f && top.g<.1f,"ceiling is culled when viewed from above");
            Check(bottom.r<.9f || bottom.b<.9f || bottom.g>.1f,"ceiling is visible when viewed from inside");
        }
        finally
        {
            ceiling.gameObject.layer=oldLayer; RenderTexture.active=oldTarget;
            UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(pixels);
            target.Release(); UnityEngine.Object.DestroyImmediate(target);
        }
    }

    private static void CheckParcelSelection(Component input, GameObject undergroundPath)
    {
        int parcelType = C("InputObjectType","PARCEL");
        var manager = Add("ParcelManager");
        Set(input,"cachedParcelManager",manager);
        var parcel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        parcel.tag = "ParcelObject";
        parcel.transform.position = new Vector3(0,8,-25);
        parcel.transform.localScale = new Vector3(30,1,30);
        var summary = Activator.CreateInstance(T("GeneratedModels.ParcelSummary"));
        Set(summary,"parcel_id",90001);
        ((IDictionary)Field(manager,"parcelObjectsCache"))[90001] = parcel;
        Call(manager,"RegisterParcelSpatialIndex",summary,new[]{new[]{-15f,-40f},new[]{15f,-40f},new[]{15f,-10f},new[]{-15f,-10f}},new[]{0f,-25f});
        Physics.SyncTransforms();
        var hits = Physics.RaycastAll(new Ray(new Vector3(0,80,-25),Vector3.down),200).OrderBy(h=>h.distance).ToArray();
        Check(hits.Any(h=>h.collider.gameObject==parcel),"surface parcel collider lies in front of the underground path");
        object[] pick = {hits,default(RaycastHit)};
        Check((bool)Call(input,"TryGetObjectHit",pick) && ((RaycastHit)pick[1]).collider.gameObject==undergroundPath,
            "underground path selection passes through a surface parcel");
        object[] surfaceOnly = {hits.Where(h=>h.collider.gameObject!=undergroundPath).ToArray(),default(RaycastHit)};
        Check((bool)Call(input,"TryGetObjectHit",surfaceOnly)
            && (int)Call(input,"GetObjectType",((RaycastHit)surfaceOnly[1]).collider.gameObject)==C("InputObjectType","TERRAIN"),
            "surface parcels cannot become fallback hover targets underground");
        Vector3 point = new Vector3(0,0,-25);
        Check((GameObject)Call(input,"GetSelectableParcelAtPoint",point)==null,
            "terrain hit cannot be remapped to a surface parcel underground");
        int clicks = 0;
        Call(input,"SetClickEvent",new Action<int,Vector3>((_,__)=>clicks++));
        Set(input,"rightClickEventCallback",new Action<int,Vector3>((_,__)=>clicks++));
        Call(input,"HandleObjectClick",parcel,point,parcelType);
        Call(input,"HandleObjectRightClick",parcel,point,parcelType);
        Call(input,"StartDrag",parcel,point,parcelType);
        Check(clicks==0 && !(bool)Field(input,"isDragging"),"queued parcel click and drag cannot fire underground");
        Static("UndergroundView","SetActive",false);
        Check((bool)Call(input,"CanSelectObject",parcel,point,parcelType)
            && (GameObject)Call(input,"GetSelectableParcelAtPoint",point)==parcel,
            "normal view restores parcel ray selection and terrain remapping");
        Static("UndergroundView","SetActive",true);
        parcel.SetActive(false);
        Set(input,"rightClickEventCallback",null);
    }

    private static void BuildExterior(List<CombineInstance> parts, List<Material> materials, List<Mesh> temporary)
    {
        var mesh = new Mesh();
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward };
        mesh.triangles = new[] { 0, 2, 1 };
        mesh.RecalculateNormals();
        parts.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.identity });
        materials.Add(Resources.Load<Material>("BuildingMaterial"));
        temporary.Add(mesh);
    }

    private static void CheckRoadSnapping(Camera camera)
    {
        var manager = Add("PathManager");
        var editor = Add("RoadPlanManager");
        Set(editor,"pathManager",manager); Set(editor,"gameCamera",camera);
        Set(editor,"PathSubtype",221); Set(editor,"PathType",C("Constants","PATH_STREET"));
        Set(editor,"phase",Enum.Parse(T("RoadEditorPhase"),"Placing"));
        Call(editor,"EnsureOverlayRenderer");
        var background = Field(editor,"backgroundRenderer");
        float Ground(float x, float z) => (float)Static("Utils","GetGroundHeight",new Vector3(x,0,z));
        object MakePath(int id, float z, float height, bool followsTerrain = true)
        {
            var path = Activator.CreateInstance(T("GeneratedModels.PathSummary"));
            Set(path,"path_id",id); Set(path,"path_variant",221); Set(path,"path_type",C("Constants","PATH_STREET"));
            Set(path,"status",C("Constants","STATUS_ACTIVE")); Set(path,"follow_terrain",followsTerrain ? 1 : 0);
            Set(path,"start_node_id",id); Set(path,"start_point",new List<object>{-40d,(double)height,(double)z});
            Set(path,"end_point",new List<object>{40d,(double)height,(double)z});
            Call(manager,"SavePathToCache",path);
            Call(manager,"RegisterPathSegments",path,Call(manager,"CalculatePathPoints",path,true));
            var node = Activator.CreateInstance(T("NodeData"));
            Set(node,"node_id",id); Set(node,"type",C("Constants","NODE_ROAD")); Set(node,"position",new[]{-40f,height,z});
            ((IDictionary)Field(manager,"nodeDataCache"))[id] = node;
            Call(Field(manager,"nodeGrid"),"Add",node,Call(node,"GetAbsolutePosition"));
            return path;
        }
        var surface = MakePath(901,85,0);
        var buried = MakePath(902,75,-15);
        Vector3 surfaceStart = new Vector3(-40,Ground(-40,85),85);
        Vector3 buriedStart = new Vector3(-40,Ground(-40,75),75);
        object NearestNode(Vector3 position, int variant = 221)
        {
            object[] args = {position,5f,manager,default(Vector3),variant};
            return Static("TerrainInputHandler","FindNearestNode",args);
        }
        object NearestEdge(Vector3 position, int variant = 221)
        {
            object[] args = {position,5f,manager,default(Vector3),null,-1,variant};
            return (bool)Static("TerrainInputHandler","FindNearestPathSegment",args) ? args[4] : null;
        }
        bool NearestEndpoint(Vector3 position)
        {
            object[] args = {position,5f,manager,default(Vector3),0f,221};
            return (bool)Static("TerrainInputHandler","FindNearestConstructionEndpoint",args);
        }
        Call(editor,"UpdateBackgroundOverlay",camera);
        int normalLines = ((IList)Field(background,"frameLines")).Count;
        Check(normalLines > 0 && ((IList)Field(background,"frameSquares")).Count == 2,"normal road editor shows existing nodes and edges");
        Check(NearestNode(surfaceStart)!=null && NearestEdge(new Vector3(0,0,85))==surface,"normal road editor preserves surface snapping");
        Static("UndergroundView","SetActive",true);
        Call(editor,"UpdateBackgroundOverlay",camera);
        Check(((IList)Field(background,"frameSquares")).Count==1 && ((IList)Field(background,"frameLines")).Count<normalLines,
            "stationary camera removes surface nodes and edges when underground view starts");
        Check(NearestNode(surfaceStart)==null && NearestEdge(new Vector3(0,0,85))==null,"underground editor excludes surface nodes and edges");
        Check(NearestNode(buriedStart)!=null && NearestEdge(new Vector3(0,0,75))==buried,"underground editor retains buried nodes and edges");
        Check(NearestNode(surfaceStart,-1)!=null && NearestEdge(new Vector3(0,0,85),-1)==surface,"generic terrain snapping keeps its existing scope");
        Set(surface,"status",C("Constants","STATUS_PENDING")); Set(buried,"status",C("Constants","STATUS_PENDING"));
        Check(!NearestEndpoint(surfaceStart) && !NearestEndpoint(new Vector3(40,0,85)) && NearestEndpoint(buriedStart),
            "planned surface endpoints are excluded while buried endpoints remain snappable");

        var mixed = Activator.CreateInstance(T("PathSegment"));
        Set(mixed,"path",buried); Set(mixed,"segmentIndex",42);
        Set(mixed,"a",new Vector3(-40,Ground(-40,75),75)); Set(mixed,"b",new Vector3(40,Ground(40,75)-15,75));
        var segmentArray = Array.CreateInstance(T("PathSegment"),1); segmentArray.SetValue(mixed,0);
        var clipped = ((IEnumerable)Static("TerrainInputHandler","GetVisiblePathSnapSegments",segmentArray)).Cast<object>().ToArray();
        Check(clipped.Length>0 && clipped.All(s=>(int)Field(s,"segmentIndex")==42)
            && clipped.Min(s=>((Vector3)Field(s,"a")).x)>-40,"mixed segments retain only the tunnel run and original segment identity");

        foreach (string flag in new[]{"EnableNodeSnapping","EnablePathSnapping"}) T("TerrainInputHandler").GetField(flag).SetValue(null,true);
        T("TerrainInputHandler").GetField("EnableNodeSnapping").SetValue(null,false);
        Set(editor,"LateralOffsets",new[]{-2f,2f});
        object[] offsetSurface = {new Vector3(0,0,83),default(Vector3),0f};
        object[] offsetBuried = {new Vector3(0,0,77),default(Vector3),0f};
        Check(!(bool)Call(editor,"TryOffsetAwareRailSnap",offsetSurface) && (bool)Call(editor,"TryOffsetAwareRailSnap",offsetBuried),
            "offset rail snapping excludes surface paths and retains buried paths");
        object[] surfaceDirection = {surfaceStart,Vector3.right,default(Vector3)};
        object[] buriedDirection = {buriedStart,Vector3.right,default(Vector3)};
        Check(!(bool)Call(editor,"TryGetExistingPathExtensionDir",surfaceDirection) && (bool)Call(editor,"TryGetExistingPathExtensionDir",buriedDirection),
            "endpoint tangent references exclude surface paths");
        foreach (object path in new[]{surface,buried}) Set(path,"path_type",C("Constants","PATH_TRACK"));
        Set(editor,"PathType",C("Constants","PATH_TRACK"));
        object[] surfaceRail = {new Vector3(0,0,85),1f,default(Vector3),default(Vector3),null,false};
        object[] buriedRail = {new Vector3(0,0,75),1f,default(Vector3),default(Vector3),null,false};
        Check(!(bool)Call(editor,"TryProjectRailTarget",surfaceRail) && (bool)Call(editor,"TryProjectRailTarget",buriedRail),
            "analytic rail projection cannot reintroduce a surface snap target");

        Call(manager,"UnregisterPathSegments",902);
        Call(editor,"SetParallelSnapping",true); Call(editor,"SetEndAlignment",true);
        object[] parallel = {new Vector3(-40,0,83),default(Vector3)};
        Check(!(bool)Call(editor,"TryApplyParallelSnap",parallel),"parallel snapping has no surface reference underground");
        Static("UndergroundView","SetActive",false);
        Call(editor,"UpdateBackgroundOverlay",camera);
        Check(((IList)Field(background,"frameSquares")).Count==2 && NearestNode(surfaceStart)!=null,
            "stationary camera restores surface node candidates in normal view");
        Check((bool)Call(editor,"TryApplyParallelSnap",parallel),"normal view restores surface parallel snapping");

        // アンカーの相対高さ0でも、非追従線路は途中の丘に潜る。
        var terrainData = Terrain.activeTerrain.terrainData;
        var hillHeights = terrainData.GetHeights(0,0,65,65);
        for (int z=0;z<65;z++) for (int x=0;x<65;x++)
        {
            float worldX = -100 + x*200f/64, worldZ = -100 + z*200f/64;
            hillHeights[z,x] += .1f*Mathf.Max(0,1-Mathf.Abs(worldX)/20)*Mathf.Max(0,1-Mathf.Abs(worldZ-65)/10);
        }
        terrainData.SetHeights(0,0,hillHeights); Static("Utils","InvalidateHeightCache");
        var hillRail = MakePath(903,65,0,false); Set(hillRail,"path_type",C("Constants","PATH_TRACK"));
        Static("UndergroundView","SetActive",true);
        Check(NearestEdge(new Vector3(0,0,65))==hillRail,"non-following path beneath a hill remains a visible edge snap target");
        object[] hillProjection = {new Vector3(0,0,65),1f,default(Vector3),default(Vector3),null,false};
        Check((bool)Call(editor,"TryProjectRailTarget",hillProjection),"analytic rail projection uses rendered tunnel depth beneath a hill");
        Static("UndergroundView","SetActive",false);
    }
}
