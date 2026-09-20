using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;

// 独立したUnityプロジェクトで、本編のアセンブリと実FBXを検証する。
public static partial class PropsQA
{
    static Assembly asm;
    static Type managerType;
    static object manager;
    static string root, output;
    static Camera camera;
    static int checks;
    static readonly List<string> results = new List<string>();
    static BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static object Field(object o, string name) => o.GetType().GetField(name, flags).GetValue(o);
    static void Set(object o, string name, object value) => o.GetType().GetField(name, flags).SetValue(o, value);
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Drain(IEnumerator routine)
    {
        while (routine.MoveNext()) if (routine.Current is IEnumerator child) Drain(child);
    }
    static object Json(string text)
    {
        var json = asm.GetType("PropListResponse");
        var converter = AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert");
        return converter.GetMethod("DeserializeObject",new[]{typeof(string),typeof(Type)}).Invoke(null,new object[]{text,json});
    }
    static List<Vector2Int> cells = new List<Vector2Int>{new Vector2Int(0,0)};
    static string Snapshot(int count, bool road, int version=0)
    {
        return "{\"scatters\":[{\"prop_scatter_id\":1,\"prop_type\":1,\"version\":"+version+",\"density\":"+(count/25f).ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"seed\":7,\"region\":[[[[0,0],[500,0],[500,500],[0,500],[0,0]]]]}],\"instances\":[],\"obstacles\":"+
            (road?"[{\"key\":\"path:1\",\"signature\":\"road\",\"polygon\":[[220,0],[240,0],[240,500],[220,500]]}]":"[]")+"}";
    }
    static void Apply(string json, bool force=false)
    {
        Drain((IEnumerator)managerType.GetMethod("ApplySnapshot",flags).Invoke(manager,new[]{Json(json),cells,(object)force,0,null}));
    }
    static IDictionary Chunks => (IDictionary)Field(manager,"chunks");
    static Dictionary<Vector2, bool> Points()
    {
        var result = new Dictionary<Vector2,bool>();
        foreach(var chunk in Chunks.Values) foreach(var p in (Array)Field(chunk,"points"))
        {
            var pos=(Vector3)Field(p,"position");
            Check(!result.ContainsKey(new Vector2(pos.x,pos.z)),"duplicate boundary point");
            result.Add(new Vector2(pos.x,pos.z),(bool)Field(p,"visible"));
        }
        return result;
    }
    static void Build(bool far)
    {
        foreach(var chunk in Chunks.Values)
            Drain((IEnumerator)managerType.GetMethod("RebuildBatches",flags).Invoke(manager,new object[]{chunk,camera,far,camera.orthographic ? camera.orthographicSize : Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f)}));
    }
    public static void Run()
    {
        var args=Environment.GetCommandLineArgs(); root=args[Array.IndexOf(args,"-propsSourceRoot")+1];
        output=root+"/Temp/PropsUnityQA";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{
                string name=new AssemblyName(e.Name).Name+".dll";
                foreach(string dir in new[]{"Temp/bin/Debug","Library/ScriptAssemblies","Assets/Plugins"})
                {string path=Path.Combine(root,dir,name);if(File.Exists(path))return Assembly.LoadFrom(path);}return null;
            };
            asm=Assembly.LoadFrom(root+"/Temp/bin/Debug/Assembly-CSharp.dll");
            var modelType=asm.GetType("ModelManager");
            var models=new GameObject("Models").AddComponent(modelType);
            modelType.GetField("<Instance>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,models);
            var cache=(IDictionary)Field(models,"_modelCache");
            ModelImportSettings.EnableReadWriteAll();
            ModelPrefabGenerator.SyncAll();
            var rendererData=AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
            Check(rendererData.renderingMode==RenderingMode.ForwardPlus,"QA must use the production Forward+ renderer");
            var pipeline=AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            GraphicsSettings.defaultRenderPipeline=pipeline; QualitySettings.renderPipeline=pipeline;
            var previewSource = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var preview = (Material)asm.GetType("PropPlacementManager").GetMethod("CreatePreviewMaterial", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[]{previewSource});
            Check(preview.enableInstancing && preview.GetFloat("_Surface") == 1f && preview.GetFloat("_ZWrite") == 0f && preview.renderQueue == 3000, "transparent instanced preview state");
            Check(File.ReadAllText(root + "/ProjectSettings/GraphicsSettings.asset").Contains("m_InstancingStripping: 2"), "player instancing variants retained");
            UnityEngine.Object.DestroyImmediate(preview); UnityEngine.Object.DestroyImmediate(previewSource);
            foreach(string id in new[]{"1","2","1_far","2_far","101","102","103","104"})
            {
                var model=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Props/"+id+".fbx");
                Check(model!=null,"missing FBX");
                Check(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/Props/"+id+".prefab")!=null,"missing generated wrapper "+id);
                var instance=UnityEngine.Object.Instantiate(model); instance.SetActive(false);
                foreach(var r in instance.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mats=r.sharedMaterials;
                    for(int i=0;i<mats.Length;i++)
                    {
                        var source=mats[i]; var mat=new Material(Shader.Find("Universal Render Pipeline/Lit"));
                        if(source!=null && source.HasProperty("_Color")) mat.SetColor("_BaseColor",source.color);
                        if(source!=null)
                        {
                            mat.SetTexture("_BaseMap",source.mainTexture);
                            if(source.HasProperty("_Glossiness")) mat.SetFloat("_Smoothness",source.GetFloat("_Glossiness"));
                            if(source.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness",source.GetFloat("_Smoothness"));
                            if(source.HasProperty("_Metallic")) mat.SetFloat("_Metallic",source.GetFloat("_Metallic"));
                            if(source.name.StartsWith("PROP_PINE_GREEN") || source.name.StartsWith("PROP_LEAF_GREEN"))
                            {
                                Check(source.mainTexture!=null,"missing foliage color texture: "+id);
                                Check(mat.GetFloat("_Smoothness")<=.15f,"foliage too glossy: "+id);
                            }
                        }
                        mats[i]=mat;
                    }
                    r.sharedMaterials=mats;
                }
                cache["Models/Props/"+id]=instance;
            }
            camera=new GameObject("Camera").AddComponent<Camera>();camera.tag="MainCamera";
            camera.transform.position=new Vector3(250,380,-220);camera.transform.LookAt(new Vector3(250,0,250));
            camera.farClipPlane=3000; camera.fieldOfView=60; camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(.45f,.6f,.75f);
            var sun=new GameObject("Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(50,-30,0);
            var ground=GameObject.CreatePrimitive(PrimitiveType.Plane);ground.transform.position=new Vector3(250,-.05f,250);ground.transform.localScale=Vector3.one*50;
            var groundMat=new Material(Shader.Find("Universal Render Pipeline/Lit"));groundMat.SetColor("_BaseColor",new Color(.29f,.35f,.18f));ground.GetComponent<Renderer>().sharedMaterial=groundMat;
            managerType=asm.GetType("PropManager");manager=new GameObject("Props").AddComponent(managerType);
            Set(manager,"useGpuForests",false);
            // 編集モードではAwakeに依存せず、ロード対象を明示する。
            var loaded=(HashSet<Vector2Int>)managerType.BaseType.GetField("loadedCells",flags).GetValue(manager);loaded.UnionWith(cells);
            var dense=GameObject.CreatePrimitive(PrimitiveType.Sphere);dense.SetActive(false);
            dense.GetComponent<Renderer>().sharedMaterial=new Material(Shader.Find("Universal Render Pipeline/Lit"));
            cache["Models/Props/991"]=dense;
            CheckGeometry();
            CheckManualPlacement();
            foreach(int count in new[]{1000,10000,50000})
            {
                var watch=Stopwatch.StartNew();Apply(Snapshot(count,false,count),true);watch.Stop();
                var points=Points();Check(Math.Abs(points.Count-count)<count*.05f,"density count "+points.Count);
                double generate=watch.Elapsed.TotalMilliseconds;
                watch.Restart();Build(true);watch.Stop();
                int batchCount=Chunks.Values.Cast<object>().Sum(c=>((IList)Field(c,"batches")).Count);
                Check(batchCount>0,"no far batches");
                results.Add($"{count}: actual={points.Count}, generationCPUms={generate:F2}, farBatchCPUms={watch.Elapsed.TotalMilliseconds:F2}, batches={batchCount}");
            }
            var original=Points();
            int normalBudget = (int)Field(manager,"maxResidentCandidates");
            Set(manager,"maxResidentCandidates",20000);
            Apply(Snapshot(50000,false,50000));
            var limited = Points();
            Check(limited.Count <= 20000 && limited.Count > 19000, "resident candidate budget not enforced");
            Check(limited.Keys.All(original.ContainsKey), "budget changed scatter positions");
            Check(Chunks.Values.Cast<object>().All(c=>((Array)Field(c,"points")).Length>0), "budget dropped a whole forest chunk");
            Set(manager,"maxResidentCandidates",10000);
            Apply(Snapshot(50000,false,50000));
            Check(Points().Keys.All(limited.ContainsKey), "reduced budget was not a nested subset");
            Set(manager,"maxResidentCandidates",normalBudget);
            Apply(Snapshot(50000,false,50000));
            Apply(Snapshot(50000,false,50000));
            Check((int)managerType.GetProperty("LastRegeneratedChunks").GetValue(manager)==0,"unchanged snapshot regenerated");
            Apply(Snapshot(50000,true,50000));var blocked=Points();
            Check(original.Keys.All(blocked.ContainsKey),"obstacle moved candidates");
            Check(blocked.Values.Any(v=>!v),"road did not hide props");
            Check((int)managerType.GetProperty("LastRegeneratedChunks").GetValue(manager)==0,"road regenerated candidates");
            Check((int)managerType.GetProperty("LastExclusionUpdatedChunks").GetValue(manager)<Chunks.Count,"road updated whole forest");
            Apply(Snapshot(50000,false,50000));Check(Points().All(p=>p.Value),"removed road did not restore props");
            string waterSnapshot = Snapshot(50000,false,50000).Replace("\"obstacles\":[]",
                "\"obstacles\":[{\"key\":\"water:0\",\"signature\":\"coast\",\"polygon\":[[-8,-8],[508,-8],[508,508],[-8,508]],\"holes\":[[[100,100],[400,100],[400,400],[100,400]]]}]");
            Apply(waterSnapshot);
            var coastPoints = Points();
            Check(coastPoints.Where(p=>p.Key.x<90).All(p=>!p.Value), "ocean trees remained visible");
            Check(coastPoints.Where(p=>p.Key.x>110 && p.Key.x<390 && p.Key.y>110 && p.Key.y<390).All(p=>p.Value), "island trees disappeared");
            Check((int)managerType.GetProperty("LastRegeneratedChunks").GetValue(manager)==0,"coast regenerated candidates");
            Apply(Snapshot(50000,false,50000));
            Check(Points().All(p=>p.Value),"coast removal did not restore trees");
            var geom=asm.GetType("PropGeometry");
            var sample=geom.GetMethod("Sample");
            Check(sample.Invoke(null,new object[]{-4,12,5f,7}).Equals(sample.Invoke(null,new object[]{-4,12,5f,7})),"unstable sample");
            var before=original.Keys.ToHashSet();
            managerType.GetMethod("ClearAllCells").Invoke(manager,null);loaded.UnionWith(cells);
            Set(manager,"generation",0);Set(manager,"subdivisions",4);
            Apply(Snapshot(50000,false,50000));Check(before.SetEquals(Points().Keys),"partition changed points");
            Set(manager,"subdivisions",8);
            managerType.GetMethod("ClearAllCells").Invoke(manager,null);Set(manager,"generation",0);loaded.UnionWith(cells);
            Apply(Snapshot(10000,true,1));Build(false);
            managerType.GetMethod("Model",flags).Invoke(manager,new object[]{2});
            managerType.GetMethod("Model",flags).Invoke(manager,new object[]{991});
            CheckModels();
            CheckForestLod();
            CheckDistanceHidden();
            var existing=Points();
            Drain((IEnumerator)managerType.GetMethod("ApplySnapshot",flags).Invoke(manager,new[]{Json(Snapshot(1000,false)),cells,(object)true,999,null}));
            Check(existing.Keys.ToHashSet().SetEquals(Points().Keys),"stale generation committed");
            var batchType=asm.GetType("LoadBatch");
            var canceled=Activator.CreateInstance(batchType,new object[]{Activator.CreateInstance(asm.GetType("LodCellSets")),false});
            batchType.GetMethod("Cancel").Invoke(canceled,null);
            Drain((IEnumerator)managerType.GetMethod("ApplySnapshot",flags).Invoke(manager,new[]{Json(Snapshot(1000,false)),cells,(object)true,0,canceled}));
            Check(existing.Keys.ToHashSet().SetEquals(Points().Keys),"canceled batch committed");
            var terrainData=new TerrainData { heightmapResolution=33, size=new Vector3(500,100,500) };
            var heights=new float[33,33];for(int y=0;y<33;y++)for(int x=0;x<33;x++)heights[y,x]=.2f;
            terrainData.SetHeights(0,0,heights);
            var terrain=Terrain.CreateTerrainGameObject(terrainData);
            Drain((IEnumerator)managerType.GetMethod("AdjustPropHeightsForCellsCoroutine").Invoke(manager,new object[]{cells}));
            foreach(var chunk in Chunks.Values) foreach(var point in (Array)Field(chunk,"points"))
                Check(Mathf.Abs(((Vector3)Field(point,"position")).y-20)<.01f,"terrain follow height");
            UnityEngine.Object.DestroyImmediate(terrain);UnityEngine.Object.DestroyImmediate(terrainData);
            Drain((IEnumerator)managerType.GetMethod("AdjustPropHeightsForCellsCoroutine").Invoke(manager,new object[]{cells}));
            // 描画はEditorのフレームを分け、同一フレームの描画登録を累積させない。
            CheckGpuForest();
            EditorApplication.update += RenderTick;
        }
        catch(Exception ex){File.WriteAllText(output+"/result.txt",ex.ToString());Debug.LogException(ex);EditorApplication.Exit(1);}
    }
    static void CheckGeometry()
    {
        var g=asm.GetType("PropGeometry");var inRing=g.GetMethod("InRing");var blocked=g.GetMethod("Blocked");
        var ring=new Vector2[]{new Vector2(0,0),new Vector2(10,0),new Vector2(10,10),new Vector2(0,10)};
        Check((bool)inRing.Invoke(null,new object[]{new Vector2(0,5),ring}),"boundary not covered");
        Check((bool)blocked.Invoke(null,new object[]{new Vector2(-.5f,5),1f,ring,new Rect(0,0,10,10)}),"clearance missing");
        Check(!(bool)blocked.Invoke(null,new object[]{new Vector2(-2,5),1f,ring,new Rect(0,0,10,10)}),"clearance too wide");
        var withHoles = g.GetMethod("BlockedWithHoles");
        var holes = new List<Vector2[]> {new[]{new Vector2(2,2),new Vector2(8,2),new Vector2(8,8),new Vector2(2,8)}};
        Check(!(bool)withHoles.Invoke(null,new object[]{new Vector2(5,5),.5f,ring,new Rect(0,0,10,10),holes}),"island interior blocked");
        Check((bool)withHoles.Invoke(null,new object[]{new Vector2(2,5),.5f,ring,new Rect(0,0,10,10),holes}),"shore boundary allowed trees");
        Check((bool)withHoles.Invoke(null,new object[]{new Vector2(2.3f,5),.5f,ring,new Rect(0,0,10,10),holes}),"shore clearance missing");
        // 穴と複数パートを含む同じregionを異なる小区画に分けて生成する。
        var response=Json("{\"scatters\":[{\"region\":[[[[0,0],[20,0],[20,20],[0,20]],[[5,5],[15,5],[15,15],[5,15]]],[[[30,0],[40,0],[40,10],[30,10]]]]}]} ");
        var scatter=((IList)Field(response,"scatters"))[0];
        var regions=g.GetMethod("Parse").Invoke(null,new[]{Field(scatter,"region")});
        var values=(IEnumerable)g.GetMethod("Scatter").Invoke(null,new object[]{new Rect(0,0,40,20),regions,10000f,5});
        int count=0;foreach(var p in values){var xy=(Vector2)Field(p,"position");Check(!(xy.x>5 && xy.x<15 && xy.y>5 && xy.y<15),"hole has tree");count++;}
        Check(count==400,"hole/multipolygon count="+count);
    }
    static void CheckModels()
    {
        var models=(IDictionary)Field(manager,"models");
        foreach(var m in models.Values)
        {
            var normal=(IList)Field(m,"normal");var far=(IList)Field(m,"far");Check(far.Count>0,"missing far representation");
            foreach(var p in far)
            {
                var mesh=(Mesh)Field(p,"mesh");Check(mesh.vertexCount<=200 || normal.Cast<object>().Any(n=>ReferenceEquals(Field(n,"mesh"),mesh)),"far mesh not simplified");
                var b=mesh.bounds;Check(b.size.sqrMagnitude>0,"empty envelope");
                Check(((Material)Field(p,"material")).enableInstancing,"instancing disabled");
            }
            results.Add($"model normalParts={normal.Count}, normalVertices={normal.Cast<object>().Sum(p=>((Mesh)Field(p,"mesh")).vertexCount)}, farParts={far.Count}, farVertices={far.Cast<object>().Sum(p=>((Mesh)Field(p,"mesh")).vertexCount)}");
            Check(far.Cast<object>().Sum(p=>((Mesh)Field(p,"mesh")).vertexCount) < normal.Cast<object>().Sum(p=>((Mesh)Field(p,"mesh")).vertexCount), "far model did not reduce vertices");
        }
    }
    static int renderStage, renderFrame;
    static void CheckForestLod()
    {
        int expected = Points().Count(p=>p.Value);
        camera.orthographic = true;
        foreach (float size in new[]{300f,1000f,100000f,300f})
        {
            camera.orthographicSize=size; Build(true);
            int actual=Chunks.Values.Cast<object>().Sum(c=>((IList)Field(c,"batches")).Cast<object>()
                .Sum(b=>((Array)Field(b,"matrices")).Length));
            Check(actual==expected,"LOD thinned forest: "+size+" "+actual+"/"+expected);
            foreach(var c in Chunks.Values) foreach(var p in (Array)Field(c,"points"))
                if((bool)Field(p,"visible")) Check((bool)Field(p,"distant")== (size>300f),"distant crown selection");
        }
        camera.orthographic=false;
    }
    // カメラからの実距離でHiddenになり、戻ると同じ本数が復元されること。区画(0〜500m)の手前1,800mは進入閾値1,500mより遠い。
    static void CheckDistanceHidden()
    {
        int Drawn()=>Chunks.Values.Cast<object>().Sum(c=>((IList)Field(c,"batches")).Cast<object>().Sum(b=>((Array)Field(b,"matrices")).Length));
        int expected=Drawn();
        Check(expected>0,"distance test needs drawn props");
        var savedPosition=camera.transform.position; var savedRotation=camera.transform.rotation;
        camera.transform.position=new Vector3(250,380,-1800);camera.transform.LookAt(new Vector3(250,0,250));
        Build(true);
        Check(Drawn()==0,"props beyond hide distance still drawn");
        foreach(var c in Chunks.Values)
        {
            Check((bool)Field(c,"hidden"),"chunk beyond hide distance not flagged");
            foreach(var p in (Array)Field(c,"points"))
                if((bool)Field(p,"visible")) Check(Convert.ToInt32(Field(p,"lod"))==0,"prop beyond hide distance not Hidden");
        }
        camera.transform.position=savedPosition;camera.transform.rotation=savedRotation;
        Build(true);
        Check(Drawn()==expected,"props not restored within hide distance");
        foreach(var c in Chunks.Values) Check(!(bool)Field(c,"hidden"),"chunk within hide distance still flagged");
    }
    static void CheckManualPlacement()
    {
        foreach(string key in new[]{"parcel:1","path:1","water:0"})
        {
            string json=Snapshot(1000,false).Replace("\"instances\":[]",
                "\"instances\":[{\"prop_instance_id\":1,\"prop_type\":1,\"x\":250,\"z\":250,\"scale\":1}]")
                .Replace("\"obstacles\":[]", "\"obstacles\":[{\"key\":\""+key+"\",\"signature\":\"blocked\",\"polygon\":[[-8,-8],[508,-8],[508,508],[-8,508]]}]");
            Apply(json,true);
            int manual=0, hidden=0;
            foreach(var c in Chunks.Values) foreach(var p in (Array)Field(c,"points"))
            {
                if((bool)Field(p,"explicitInstance")) {manual++;Check((bool)Field(p,"visible"),"manual tree hidden by "+key);}
                else {hidden++;Check(!(bool)Field(p,"visible"),"scatter ignored "+key);}
            }
            Check(manual==1 && hidden>900,"manual/scatter test missing candidates");
        }
        var placementType=asm.GetType("PropPlacementManager");
        var placement=new GameObject("ManualPreviewQA").AddComponent(placementType);
        Set(placement,"propType",1);
        Set(placement,"active",true);
        var ghost=(GameObject)placementType.GetMethod("InstantiateProp",flags).Invoke(placement,new object[]{new Vector3(250,0,250),0f,1f,true});
        Check(ghost!=null && ghost.activeSelf,"manual ghost hidden inside water");
        UnityEngine.Object.DestroyImmediate(ghost);
        var hover=placementType.GetMethod("OnTerrainHover");
        hover.Invoke(placement,new object[]{new Vector3(250,0,250)});
        ghost=(GameObject)Field(placement,"hoverGhost");
        ghost.SetActive(false);
        hover.Invoke(placement,new object[]{new Vector3(251,0,251)});
        Check(ghost.activeSelf,"moving manual ghost hidden inside water");
        placementType.GetMethod("OnChangeSettingsFromIpc").Invoke(placement,new object[]{new Dictionary<string,object>{{"height",5.5f}}});
        Check(Mathf.Abs(ghost.transform.position.y-5.5f)<.001f,"height setting did not move stationary ghost");
        placementType.GetMethod("OnTerrainLeftClick").Invoke(placement,new object[]{new Vector3(251,0,251)});
        var placed=(IList)Field(placement,"placedInstances");
        Check(Mathf.Abs((float)placed[0].GetType().GetField("Item2").GetValue(placed[0])-5.5f)<.001f,"placed height missing from payload");
        placementType.GetMethod("OnChangeSettingsFromIpc").Invoke(placement,new object[]{new Dictionary<string,object>{{"height",-2.25f}}});
        Check(Mathf.Abs(ghost.transform.position.y+2.25f)<.001f,"negative height not applied");
        Check(Mathf.Abs(((GameObject)((IList)Field(placement,"placedObjects"))[0]).transform.position.y-5.5f)<.001f,"setting moved previously placed instance");
        foreach(GameObject go in (IList)Field(placement,"placedObjects")) UnityEngine.Object.DestroyImmediate(go);
        UnityEngine.Object.DestroyImmediate(ghost);
        UnityEngine.Object.DestroyImmediate(placement.gameObject);
        string elevated="{\"instances\":[{\"prop_instance_id\":9,\"prop_type\":1,\"x\":250,\"y\":5.5,\"z\":250,\"scale\":1,\"version\":0}],\"scatters\":[],\"obstacles\":[]}";
        Apply(elevated,true);
        foreach(var c in Chunks.Values) foreach(var p in (Array)Field(c,"points"))
            Check(Mathf.Abs(((Vector3)Field(p,"position")).y-5.5f)<.001f,"saved height lost");
        var terrainData=new TerrainData {heightmapResolution=33,size=new Vector3(500,100,500)};
        var heights=new float[33,33];for(int z=0;z<33;z++)for(int x=0;x<33;x++)heights[z,x]=.2f;
        terrainData.SetHeights(0,0,heights);
        var terrain=Terrain.CreateTerrainGameObject(terrainData);
        asm.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null,null);
        Drain((IEnumerator)managerType.GetMethod("AdjustPropHeightsForCellsCoroutine").Invoke(manager,new object[]{cells}));
        foreach(var c in Chunks.Values) foreach(var p in (Array)Field(c,"points"))
            Check(Mathf.Abs(((Vector3)Field(p,"position")).y-25.5f)<.001f,"terrain edit discarded relative height");
        Apply(elevated.Replace("5.5","-2.25").Replace("\"version\":0","\"version\":1"));
        foreach(var c in Chunks.Values) foreach(var p in (Array)Field(c,"points"))
            Check(Mathf.Abs(((Vector3)Field(p,"position")).y-17.75f)<.001f,"height version update lost");
        UnityEngine.Object.DestroyImmediate(terrain);UnityEngine.Object.DestroyImmediate(terrainData);
        asm.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null,null);
    }
    static readonly Stopwatch renderTimer = new Stopwatch();
    static double cpuTotal;
    static void RenderTick()
    {
        try
        {
            if (renderFrame == 0)
            {
                if (renderStage < 2)
                {
                    camera.transform.position=new Vector3(250,24,160);
                    camera.transform.LookAt(new Vector3(250,0,250));
                    Build(renderStage==1);
                }
                else if (renderStage == 2)
                {
                    camera.transform.position=new Vector3(250,380,-220);
                    camera.transform.LookAt(new Vector3(250,0,250));
                    Apply(Snapshot(50000,true,50000));Build(true);
                }
                else if (renderStage < 5)
                {
                    int treeType = renderStage == 3 ? 1 : 2;
                    string forest = "{\"scatters\":[{\"prop_scatter_id\":2,\"prop_type\":"+treeType+",\"version\":0,\"density\":400,\"seed\":7,\"region\":[[[[210,210],[290,210],[290,290],[210,290]]]]}],\"instances\":[],\"obstacles\":[{\"key\":\"parcel:1\",\"signature\":\"parcel\",\"polygon\":[[240,230],[260,230],[260,250],[240,250]],\"floor_variant\":0}]}";
                    Apply(forest, true);
                    var points = Points();
                    Check(points.Any(p=>!p.Value && p.Key.x>240 && p.Key.x<260 && p.Key.y>230 && p.Key.y<250), "parcel did not hide trees");
                    Check(points.Where(p=>p.Key.x>240 && p.Key.x<260 && p.Key.y>230 && p.Key.y<250).All(p=>!p.Value), "tree remained inside parcel");
                    camera.transform.position=new Vector3(310,65,165);
                    camera.transform.LookAt(new Vector3(250,0,250));
                    Build(false);
                }
                else if (renderStage == 5)
                {
                    Apply("{\"instances\":[{\"prop_instance_id\":101,\"prop_type\":101,\"x\":241,\"y\":1.5,\"z\":250,\"scale\":1},{\"prop_instance_id\":102,\"prop_type\":102,\"x\":246,\"y\":1.5,\"z\":250,\"scale\":1},{\"prop_instance_id\":103,\"prop_type\":103,\"x\":252,\"y\":1.5,\"z\":250,\"scale\":1},{\"prop_instance_id\":104,\"prop_type\":104,\"x\":258,\"y\":1.5,\"z\":250,\"scale\":1}],\"scatters\":[],\"obstacles\":[]}",true);
                    var slab=GameObject.CreatePrimitive(PrimitiveType.Cube);
                    foreach(int id in new[]{101,102,103,104})
                    {
                        var model=managerType.GetMethod("Model",flags).Invoke(manager,new object[]{id});
                        var bounds=(Bounds)Field(model,"bounds");
                        Check(Mathf.Abs(bounds.min.y)<.01f && Mathf.Abs(bounds.max.y-(id<103?6.52f:1.13f))<.01f,"stair prop height/origin mismatch "+id+": "+bounds);
                        results.Add("station prop "+id+" bounds="+bounds);
                    }
                    slab.transform.position=new Vector3(250,1.25f,250);slab.transform.localScale=new Vector3(26,.5f,12);
                    slab.GetComponent<Renderer>().sharedMaterial=new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    camera.transform.position=new Vector3(267,17,268);camera.transform.LookAt(new Vector3(250,3,250));
                    Build(false);
                }
                else SetupGpuRender();
                if (renderStage == 10) SetupForestBoundaryRender();
                camera.targetTexture=new RenderTexture(1200,850,24);
                cpuTotal=0;
            }
            renderTimer.Restart();
            managerType.GetMethod("LateUpdate",flags).Invoke(manager,null);camera.Render();
            renderTimer.Stop();
            if(renderFrame>=10)cpuTotal+=renderTimer.Elapsed.TotalMilliseconds;
            renderFrame++;
            if(renderFrame<30)return;
            string filename=renderStage==0?"preview-near.png":renderStage==1?"preview-far.png":renderStage==2?"preview-50000.png":renderStage==3?"forest-conifer.png":renderStage==4?"forest-broadleaf.png":renderStage==5?"station-stair-props.png":renderStage==6?"gpu-forest-near.png":renderStage==7?"gpu-forest-far.png":renderStage==8?"gpu-forest-mountain.png":"gpu-forest-mountain-no-cluster.png";
            if (renderStage == 10) filename="gpu-forest-overview-boundary.png";
            results.Add(filename+" warmed submit+Camera.Render CPUms/frame="+(cpuTotal/20).ToString("F2")+
                ", submittedBatches="+managerType.GetProperty("SubmittedBatchCount").GetValue(manager)+
                ", submeshInstances="+managerType.GetProperty("SubmittedInstanceCount").GetValue(manager)+
                ", forestBatches="+managerType.GetProperty("SubmittedForestBatches").GetValue(manager));
            Check((int)managerType.GetProperty(renderStage<6?"SubmittedBatchCount":"SubmittedForestBatches").GetValue(manager)>0,"no GPU submission");
            var rt=camera.targetTexture;RenderTexture.active=rt;
            var tex=new Texture2D(1200,850,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1200,850),0,0);tex.Apply();
            File.WriteAllBytes(output+"/"+filename,tex.EncodeToPNG());RenderTexture.active=null;camera.targetTexture=null;
            Check(tex.GetPixels32().Count(c=>c.r>230 && c.g<30 && c.b>230)<1000,"shader error pixels in "+filename);
            if(renderStage==9)CheckForestSunlight(tex);
            UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(rt);
            renderFrame=0;renderStage++;
            if(renderStage<11)return;
            EditorApplication.update-=RenderTick;
            var buffers=Chunks.Values.Cast<object>().SelectMany(c=>((IList)Field(c,"forests")).Cast<object>()).Select(p=>(GraphicsBuffer)Field(p,"points")).ToArray();
            managerType.GetMethod("ClearAllCells").Invoke(manager,null);
            Check(buffers.All(b=>!b.IsValid()),"session clear leaked GPU forest buffers");
            results.Add("PASS checks="+checks+"; actual FBX with URP Lit (fallback) / Forest.shader (GPU); CPU timings exclude GPU completion");
            File.WriteAllLines(output+"/result.txt",results);EditorApplication.Exit(0);
        }
        catch(Exception ex)
        {EditorApplication.update-=RenderTick;File.WriteAllText(output+"/result.txt",ex.ToString());Debug.LogException(ex);EditorApplication.Exit(1);}
    }
}
