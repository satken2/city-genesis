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
public static class PropsQA
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
            Drain((IEnumerator)managerType.GetMethod("RebuildBatches",flags).Invoke(manager,new object[]{chunk,camera,far,Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f)}));
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
            var rendererData=ScriptableObject.CreateInstance<UniversalRendererData>();
            var pipeline=UniversalRenderPipelineAsset.Create(rendererData);
            GraphicsSettings.defaultRenderPipeline=pipeline; QualitySettings.renderPipeline=pipeline;
            var previewSource = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var preview = (Material)asm.GetType("PropPlacementManager").GetMethod("CreatePreviewMaterial", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[]{previewSource});
            Check(preview.enableInstancing && preview.GetFloat("_Surface") == 1f && preview.GetFloat("_ZWrite") == 0f && preview.renderQueue == 3000, "transparent instanced preview state");
            Check(File.ReadAllText(root + "/ProjectSettings/GraphicsSettings.asset").Contains("m_InstancingStripping: 2"), "player instancing variants retained");
            UnityEngine.Object.DestroyImmediate(preview); UnityEngine.Object.DestroyImmediate(previewSource);
            foreach(int id in new[]{1,2})
            {
                var model=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Props/"+id+".fbx");
                Check(model!=null,"missing FBX");
                var instance=UnityEngine.Object.Instantiate(model); instance.SetActive(false);
                foreach(var r in instance.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mats=r.sharedMaterials;
                    for(int i=0;i<mats.Length;i++)
                    {
                        var source=mats[i]; var mat=new Material(Shader.Find("Universal Render Pipeline/Lit"));
                        if(source!=null && source.HasProperty("_Color")) mat.SetColor("_BaseColor",source.color);
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
            // 編集モードではAwakeに依存せず、ロード対象を明示する。
            var loaded=(HashSet<Vector2Int>)managerType.BaseType.GetField("loadedCells",flags).GetValue(manager);loaded.UnionWith(cells);
            var dense=GameObject.CreatePrimitive(PrimitiveType.Sphere);dense.SetActive(false);
            dense.GetComponent<Renderer>().sharedMaterial=new Material(Shader.Find("Universal Render Pipeline/Lit"));
            cache["Models/Props/991"]=dense;
            CheckGeometry();
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
            Apply(Snapshot(50000,false,50000));
            Check((int)managerType.GetProperty("LastRegeneratedChunks").GetValue(manager)==0,"unchanged snapshot regenerated");
            Apply(Snapshot(50000,true,50000));var blocked=Points();
            Check(original.Keys.All(blocked.ContainsKey),"obstacle moved candidates");
            Check(blocked.Values.Any(v=>!v),"road did not hide props");
            Check((int)managerType.GetProperty("LastRegeneratedChunks").GetValue(manager)==0,"road regenerated candidates");
            Check((int)managerType.GetProperty("LastExclusionUpdatedChunks").GetValue(manager)<Chunks.Count,"road updated whole forest");
            Apply(Snapshot(50000,false,50000));Check(Points().All(p=>p.Value),"removed road did not restore props");
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
                var mesh=(Mesh)Field(p,"mesh");Check(mesh.vertexCount<=128 || normal.Cast<object>().Any(n=>ReferenceEquals(Field(n,"mesh"),mesh)),"far mesh not simplified");
                var b=mesh.bounds;Check(b.size.sqrMagnitude>0,"empty envelope");
                Check(((Material)Field(p,"material")).enableInstancing,"instancing disabled");
            }
            results.Add($"model normalParts={normal.Count}, normalVertices={normal.Cast<object>().Sum(p=>((Mesh)Field(p,"mesh")).vertexCount)}, farParts={far.Count}, farVertices={far.Cast<object>().Sum(p=>((Mesh)Field(p,"mesh")).vertexCount)}");
        }
    }
    static int renderStage, renderFrame;
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
                else
                {
                    camera.transform.position=new Vector3(250,380,-220);
                    camera.transform.LookAt(new Vector3(250,0,250));
                    Apply(Snapshot(50000,true,50000));Build(true);
                }
                camera.targetTexture=new RenderTexture(1200,850,24);
                cpuTotal=0;
            }
            renderTimer.Restart();
            managerType.GetMethod("LateUpdate",flags).Invoke(manager,null);camera.Render();
            renderTimer.Stop();
            if(renderFrame>=10)cpuTotal+=renderTimer.Elapsed.TotalMilliseconds;
            renderFrame++;
            if(renderFrame<30)return;
            string filename=renderStage==0?"preview-near.png":renderStage==1?"preview-far.png":"preview-50000.png";
            results.Add(filename+" warmed submit+Camera.Render CPUms/frame="+(cpuTotal/20).ToString("F2")+
                ", submittedBatches="+managerType.GetProperty("SubmittedBatchCount").GetValue(manager)+
                ", submeshInstances="+managerType.GetProperty("SubmittedInstanceCount").GetValue(manager));
            Check((int)managerType.GetProperty("SubmittedBatchCount").GetValue(manager)>0,"no GPU submission");
            var rt=camera.targetTexture;RenderTexture.active=rt;
            var tex=new Texture2D(1200,850,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1200,850),0,0);tex.Apply();
            File.WriteAllBytes(output+"/"+filename,tex.EncodeToPNG());RenderTexture.active=null;camera.targetTexture=null;
            UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(rt);
            renderFrame=0;renderStage++;
            if(renderStage<3)return;
            EditorApplication.update-=RenderTick;
            results.Add("PASS checks="+checks+"; screenshot rendering uses URP Lit materials on actual FBX geometry; CPU timings exclude GPU completion");
            File.WriteAllLines(output+"/result.txt",results);EditorApplication.Exit(0);
        }
        catch(Exception ex)
        {EditorApplication.update-=RenderTick;File.WriteAllText(output+"/result.txt",ex.ToString());Debug.LogException(ex);EditorApplication.Exit(1);}
    }
}
