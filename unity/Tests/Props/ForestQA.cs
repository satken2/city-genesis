using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static partial class PropsQA
{
    struct GpuPoint { public Vector4 positionScale, rotationVisible; }
    static Terrain forestTerrain;
    static void GenerateForests()
    {
        var renderer = Field(manager,"forestRenderer");
        foreach (var chunk in Chunks.Values) foreach (var patch in (IList)Field(chunk,"forests"))
            Check((bool)renderer.GetType().GetMethod("Generate").Invoke(renderer,new object[]{patch,forestTerrain,Vector3.zero}),"GPU generation unavailable");
    }

    static void CheckGpuForest()
    {
        CheckForestFog();
        CheckForestOverview();
        var shader = Resources.Load<Shader>("Shaders/Forest");
        Check(shader != null && shader.isSupported && !ShaderUtil.ShaderHasError(shader),"forest shader compilation failed");
        var data = new TerrainData { heightmapResolution=33, size=new Vector3(500,100,500) };
        var heights = new float[33,33];
        for (int z=0;z<33;z++) for(int x=0;x<33;x++) heights[z,x] = .1f + x/32f*.3f + z/32f*.2f;
        data.SetHeights(0,0,heights);
        forestTerrain=Terrain.CreateTerrainGameObject(data).GetComponent<Terrain>();
        forestTerrain.drawHeightmap=false;
        var timer = new Stopwatch();
        foreach (int count in new[]{10000,50000})
        {
            Set(manager,"useGpuForests",false);
            timer.Restart(); Apply(Snapshot(count,true,count),true); timer.Stop();
            double cpu = timer.Elapsed.TotalMilliseconds;
            var expected = Points();
            Set(manager,"useGpuForests",true);
            timer.Restart(); Apply(Snapshot(count,true,count),true); timer.Stop();
            Check((int)managerType.GetProperty("ResidentCandidateCount").GetValue(manager)==0,"GPU forest retained CPU candidates");
            Check((int)managerType.GetProperty("ForestCandidateCount").GetValue(manager)>0,"GPU forest is empty");
            results.Add($"GPU forest {count}: CPU snapshot {timer.Elapsed.TotalMilliseconds:F2}ms vs fallback {cpu:F2}ms; GPU dispatch/readback excluded");
            timer.Restart(); GenerateForests(); timer.Stop();
            results.Add($"GPU forest {count}: dispatch submission CPU {timer.Elapsed.TotalMilliseconds:F2}ms (GPU completion excluded)");
            int valid=0;
            var sampleMethod=asm.GetType("PropGeometry").GetMethod("Sample");
            foreach(var chunk in Chunks.Values) foreach(var patch in (IList)Field(chunk,"forests"))
            {
                int n=(int)Field(patch,"count"), width=(int)Field(patch,"gridWidth");
                var buffer=(GraphicsBuffer)Field(patch,"points");
                var points=new GpuPoint[n]; buffer.GetData(points);
                for(int i=0;i<n;i++)
                {
                    var sample=sampleMethod.Invoke(null,new object[]{(int)Field(patch,"startX")+i%width,(int)Field(patch,"startZ")+i/width,(float)Field(patch,"spacing"),(int)Field(patch,"seed")});
                    Vector2 xy=(Vector2)Field(sample,"position");
                    Check(Vector2.Distance(xy,new Vector2(points[i].positionScale.x,points[i].positionScale.z))<.0002f,"GPU/CPU seed position mismatch");
                    Check(Mathf.Abs(points[i].positionScale.w-(float)Field(sample,"scale"))<.00001f,"GPU scale mismatch");
                    bool inside=((Rect)Field(patch,"rect")).Contains(xy);
                    bool visible=inside && expected.TryGetValue(xy,out bool allowed) && allowed;
                    Check((points[i].rotationVisible.y>.5f)==visible,"GPU polygon/obstacle mismatch");
                    float height=10+Mathf.Clamp(xy.x,0,500)*.06f+Mathf.Clamp(xy.y,0,500)*.04f;
                    Check(Mathf.Abs(points[i].positionScale.y-height)<.015f,$"GPU terrain height mismatch at {xy}: {points[i].positionScale.y} expected {height}");
                    if(visible)valid++;
                }
            }
            Check(valid==expected.Count(p=>p.Value),"GPU duplicated/lost candidates at chunk boundaries");
            object first=Chunks.Values.Cast<object>().First(c=>((IList)Field(c,"forests")).Count>0);
            var old=((IList)Field(first,"forests"))[0];
            Apply(Snapshot(count,true,count));
            Check(Chunks.Values.Cast<object>().Any(c=>((IList)Field(c,"forests")).Contains(old)),"unchanged forest recreated buffers");
            managerType.GetMethod("HideSource").Invoke(manager,new object[]{true,1});
            Check(Chunks.Values.Cast<object>().All(c=>((IList)Field(c,"forests")).Cast<object>().All(p=>(bool)Field(p,"hidden"))),"scatter deletion did not hide GPU forest");
        }
        CheckScatterOptions();
        CheckGpuHolesAndEdits();
        var zero=new float[33,33]; data.SetHeights(0,0,zero);
        Set(manager,"useGpuForests",false); Apply(Snapshot(10000,true),true); Build(false);
    }

    static void CheckScatterOptions()
    {
        var sampleMethod = asm.GetType("PropGeometry").GetMethod("Sample");
        foreach (bool water in new[] { false, true })
        {
            HashSet<Vector2> previous = null;
            foreach (int fade in new[] { 0, 40, 100 })
            {
                string snapshot = Snapshot(10000, true).Replace("\"seed\":7", "\"seed\":7,\"edge_fade_width\":" + fade + ",\"allow_water\":" + (water ? "true" : "false"));
                snapshot = snapshot.Replace("\"obstacles\":[", "\"obstacles\":[{\"key\":\"water:0\",\"signature\":\"water\",\"polygon\":[[0,0],[150,0],[150,500],[0,500]]},");
                Set(manager, "useGpuForests", false); Apply(snapshot, true);
                var expected = Points();
                var visible = new HashSet<Vector2>(expected.Where(p => p.Value).Select(p => p.Key));
                Check(visible.Count > 0, "fade removed all interior trees");
                Check(visible.Any(p => p.x < 140) == water, "water placement permission mismatch");
                Check(!visible.Any(p => p.x > 220 && p.x < 240), "water permission disabled road exclusion");
                if (previous != null) Check(visible.IsProperSubsetOf(previous), "fade did not monotonically thin fixed candidates");
                previous = visible;
                Set(manager, "useGpuForests", true); Apply(snapshot, true); GenerateForests();
                int count = 0;
                foreach (var chunk in Chunks.Values) foreach (var patch in (IList)Field(chunk, "forests"))
                {
                    int n = (int)Field(patch, "count"), width = (int)Field(patch, "gridWidth");
                    var gpu = new GpuPoint[n]; ((GraphicsBuffer)Field(patch, "points")).GetData(gpu);
                    for (int i = 0; i < n; i++)
                    {
                        var sample = sampleMethod.Invoke(null, new object[] { (int)Field(patch, "startX") + i % width, (int)Field(patch, "startZ") + i / width, (float)Field(patch, "spacing"), (int)Field(patch, "seed") });
                        var xy = (Vector2)Field(sample, "position");
                        bool allowed = ((Rect)Field(patch, "rect")).Contains(xy) && visible.Contains(xy);
                        Check((gpu[i].rotationVisible.y > .5f) == allowed, "CPU/GPU fade or water mismatch");
                        if (allowed) count++;
                    }
                }
                Check(count == visible.Count, "GPU fade candidate count mismatch");
            }
        }
        results.Add("Scatter options: fade widths 0/40/100, stable thinning, water toggle, road exclusion and CPU/GPU agreement passed");
    }

    static void CheckForestFog()
    {
        var savedPosition=camera.transform.position; var savedRotation=camera.transform.rotation;
        bool savedOrtho=camera.orthographic; float savedFar=camera.farClipPlane, savedSize=camera.orthographicSize;
        var plane=GameObject.CreatePrimitive(PrimitiveType.Plane);
        plane.transform.position=new Vector3(250,2000,250); plane.transform.localScale=Vector3.one*100;
        var lit=new Material(Shader.Find("Universal Render Pipeline/Lit"));
        var forest=new Material(Resources.Load<Shader>("Shaders/Forest"));
        foreach(var material in new[]{lit,forest}) { material.SetColor("_BaseColor",new Color(.3f,.4f,.2f)); material.SetFloat("_Smoothness",.1f); }
        plane.GetComponent<Renderer>().sharedMaterial=lit;
        var mesh=plane.GetComponent<MeshFilter>().sharedMesh;
        using(var buffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,32))
        {
            buffer.SetData(new[]{new GpuPoint{positionScale=new Vector4(250,2000,250,1),rotationVisible=new Vector4(0,1,.5f,.5f)}});
            var props=new MaterialPropertyBlock(); props.SetBuffer("_ForestPoints",buffer);
            props.SetMatrix("_PartLocal",Matrix4x4.Scale(Vector3.one*100)); props.SetMatrix("_PartNormal",Matrix4x4.identity);
            props.SetInt("_ForestLod",1); props.SetFloat("_DetailDistance",100); props.SetFloat("_MaxDistance",1500);
            camera.transform.position=new Vector3(250,2500,250); camera.transform.rotation=Quaternion.Euler(90,0,0);
            camera.farClipPlane=30000; camera.orthographicSize=100;
            props.SetVector("_ForestCamera",camera.transform.position);
            camera.targetTexture=new RenderTexture(128,128,24);
            var sample=new Texture2D(128,128,TextureFormat.RGB24,false);
            Color Read() { camera.Render(); RenderTexture.active=camera.targetTexture; sample.ReadPixels(new Rect(0,0,128,128),0,0); sample.Apply(); return sample.GetPixel(64,64); }
            RenderSettings.fog=true; RenderSettings.fogMode=FogMode.Linear;
            RenderSettings.fogStartDistance=0; RenderSettings.fogEndDistance=600; RenderSettings.fogColor=new Color(.7f,.75f,.8f);
            foreach(bool ortho in new[]{false,true})
            {
                camera.orthographic=ortho;
                plane.SetActive(true); Color reference=Read(); plane.SetActive(false);
                Graphics.RenderMeshPrimitives(new RenderParams(forest){camera=camera,matProps=props,worldBounds=new Bounds(plane.transform.position,Vector3.one*1000)},mesh,0,1);
                Color actual=Read();
                float error=Mathf.Max(Mathf.Abs(actual.r-reference.r),Mathf.Max(Mathf.Abs(actual.g-reference.g),Mathf.Abs(actual.b-reference.b)))*255;
                results.Add($"Fog parity ortho={ortho}: max RGB error={error:F2}/255; lit={reference}; forest={actual}");
                Check(error<4,"Forest fog differs from URP Lit: "+error+" ortho="+ortho);
            }
            RenderTexture.active=null; UnityEngine.Object.DestroyImmediate(camera.targetTexture); camera.targetTexture=null;
            UnityEngine.Object.DestroyImmediate(sample);
        }
        UnityEngine.Object.DestroyImmediate(plane); UnityEngine.Object.DestroyImmediate(lit); UnityEngine.Object.DestroyImmediate(forest);
        RenderSettings.fog=false; camera.orthographic=savedOrtho; camera.farClipPlane=savedFar; camera.orthographicSize=savedSize;
        camera.transform.SetPositionAndRotation(savedPosition,savedRotation);
    }

    static void CheckForestOverview()
    {
        var responseType = asm.GetType("ForestOverviewResponse");
        var type = asm.GetType("ForestOverview");
        var converter = AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert");
        object Parse(string json) => converter.GetMethod("DeserializeObject",new[]{typeof(string),typeof(Type)}).Invoke(null,new object[]{json,responseType});
        var response = Parse(Snapshot(50000,true).TrimEnd('}') + ",\"coastline\":{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[400,0],[400,500],[0,500],[0,0]],[[50,50],[100,50],[100,100],[50,100],[50,50]]]}}");
        var raster = type.GetMethod("Build").Invoke(null,new object[]{response,System.Threading.CancellationToken.None});
        var bounds = (Rect)Field(raster,"bounds");
        int width=(int)Field(raster,"width"), height=(int)Field(raster,"height");
        var pixels=(Color32[])Field(raster,"pixels");
        byte At(float x,float z) => pixels[Mathf.FloorToInt((z-bounds.yMin)/bounds.height*height)*width+Mathf.FloorToInt((x-bounds.xMin)/bounds.width*width)].r;
        Check(At(150,150)>200,"global forest missing away from camera");
        Check(At(230,150)==0,"forest overview covered road");
        Check(At(75,75)==0,"forest overview covered lake hole");
        Check(At(450,150)==0,"forest overview covered sea");
        Check(width<=2048 && height<=2048,"forest mask exceeded texture budget");
        var scatter = ((IList)Field(response, "scatters"))[0];
        Set(scatter, "allow_water", true); Set(scatter, "edge_fade_width", 100f);
        var softened = type.GetMethod("Build").Invoke(null, new object[] { response, System.Threading.CancellationToken.None });
        var softenedPixels = (Color32[])Field(softened, "pixels");
        byte SoftAt(float x, float z) => softenedPixels[Mathf.FloorToInt((z-bounds.yMin)/bounds.height*height)*width+Mathf.FloorToInt((x-bounds.xMin)/bounds.width*width)].r;
        Check(SoftAt(75,75) > 0 && SoftAt(450,150) > 0, "overview water permission ignored");
        Check(SoftAt(230,150) == 0, "overview water permission covered road");
        Check(SoftAt(2,250) < SoftAt(30,250) && SoftAt(30,250) < SoftAt(150,250), "overview edge did not fade");
        using(var cancel=new System.Threading.CancellationTokenSource())
        {
            cancel.Cancel(); bool canceled=false;
            try { type.GetMethod("Build").Invoke(null,new object[]{response,cancel.Token}); }
            catch(System.Reflection.TargetInvocationException e) { canceled=e.InnerException is OperationCanceledException; }
            Check(canceled,"overview ignored session cancellation");
        }
        var overview=Activator.CreateInstance(type);
        type.GetMethod("Publish").Invoke(overview,new[]{raster});
        Check(Shader.GetGlobalFloat("_ForestOverviewEnabled")==1,"overview not published");
        ((IDisposable)overview).Dispose();
        Check(Shader.GetGlobalFloat("_ForestOverviewEnabled")==0,"overview leaked across sessions");
        results.Add("Forest overview: global coverage, road/lake/sea exclusions, texture budget and cancellation passed");
    }

    static void CheckGpuHolesAndEdits()
    {
        var previousCells=cells;
        cells=new List<Vector2Int>{new Vector2Int(-1,-1)};
        var loaded=(HashSet<Vector2Int>)managerType.BaseType.GetField("loadedCells",flags).GetValue(manager);
        loaded.Clear(); loaded.UnionWith(cells);
        forestTerrain.transform.position=new Vector3(-500,0,-500);
        var heights=new float[33,33];
        for(int z=0;z<33;z++)for(int x=0;x<33;x++)heights[z,x]=.25f;
        forestTerrain.terrainData.SetHeights(0,0,heights);
        string snapshot="{\"scatters\":[{\"prop_scatter_id\":8,\"prop_type\":2,\"version\":1,\"density\":400,\"seed\":-17,\"region\":[[[[-500,-500],[-60,-500],[-60,-60],[-500,-60]],[[-420,-420],[-350,-420],[-350,-350],[-420,-350]]],[[[-40,-40],[0,-40],[0,0],[-40,0]]]]}],\"instances\":[],\"obstacles\":[{\"key\":\"water:0\",\"signature\":\"water\",\"polygon\":[[-480,-480],[-100,-480],[-100,-100],[-480,-100]],\"holes\":[[[-300,-300],[-200,-300],[-200,-200],[-300,-200]]]}]}";
        Set(manager,"useGpuForests",false);Apply(snapshot,true);var expected=Points();
        Set(manager,"useGpuForests",true);Apply(snapshot,true);GenerateForests();
        var sampleMethod=asm.GetType("PropGeometry").GetMethod("Sample");
        var found=new HashSet<Vector2>();
        Vector3 pickPosition=Vector3.zero;
        foreach(var chunk in Chunks.Values)foreach(var patch in (IList)Field(chunk,"forests"))
        {
            int n=(int)Field(patch,"count"), width=(int)Field(patch,"gridWidth");
            var gpu=new GpuPoint[n];((GraphicsBuffer)Field(patch,"points")).GetData(gpu);
            for(int i=0;i<n;i++)
            {
                var sample=sampleMethod.Invoke(null,new object[]{(int)Field(patch,"startX")+i%width,(int)Field(patch,"startZ")+i/width,(float)Field(patch,"spacing"),(int)Field(patch,"seed")});
                var xy=(Vector2)Field(sample,"position");
                bool visible=((Rect)Field(patch,"rect")).Contains(xy) && expected.TryGetValue(xy,out bool allowed) && allowed;
                Check((gpu[i].rotationVisible.y>.5f)==visible,"negative coords / holes / island mismatch");
                if(!visible)continue;
                Check(found.Add(xy),"GPU duplicate across chunks");
                Check(Mathf.Abs(gpu[i].positionScale.y-25)<.015f,"negative tile height mismatch");
                pickPosition=new Vector3(xy.x,25,xy.y);
            }
        }
        Check(found.SetEquals(expected.Where(x=>x.Value).Select(x=>x.Key)),"GPU hole/multipolygon missing visible tree");
        camera.transform.position=pickPosition+Vector3.up*30;
        var pickArgs=new object[]{new Ray(camera.transform.position,Vector3.down),100f,null};
        Check((bool)managerType.GetMethod("TryPick").Invoke(manager,pickArgs),"GPU forest cannot be picked");
        Check((bool)Field(pickArgs[2],"scatter") && (int)Field(pickArgs[2],"id")==8,"GPU pick wrong source");
        var first=Chunks.Values.Cast<object>().Where(c=>((IList)Field(Field(c,"spec"),"obstacles")).Count>0)
            .SelectMany(c=>((IList)Field(c,"forests")).Cast<object>()).First();
        var oldBuffer=(GraphicsBuffer)Field(first,"points");
        for(int z=0;z<33;z++)for(int x=0;x<33;x++)heights[z,x]=.4f;
        forestTerrain.terrainData.SetHeights(0,0,heights);
        Drain((IEnumerator)managerType.GetMethod("AdjustPropHeightsForCellsCoroutine").Invoke(manager,new object[]{cells}));
        Check((bool)Field(first,"dirty"),"terrain edit did not invalidate GPU patch");GenerateForests();
        var updated=new GpuPoint[(int)Field(first,"count")];oldBuffer.GetData(updated);
        Check(updated.All(p=>Mathf.Abs(p.positionScale.y-40)<.015f),"GPU forest did not follow terrain edit");
        Apply(snapshot.Replace("\"signature\":\"water\"","\"signature\":\"changed\""));
        Check(!oldBuffer.IsValid(),"replaced GPU buffer leaked");
        GenerateForests();
        var preserved=Chunks.Values.Cast<object>().SelectMany(c=>((IList)Field(c,"forests")).Cast<object>()).First();
        Drain((IEnumerator)managerType.GetMethod("ApplySnapshot",flags).Invoke(manager,new[]{Json(snapshot),cells,(object)true,999,null}));
        Check(Chunks.Values.Cast<object>().Any(c=>((IList)Field(c,"forests")).Contains(preserved)),"stale load replaced GPU forest");
        cells=previousCells;loaded.Clear();loaded.UnionWith(cells);forestTerrain.transform.position=Vector3.zero;
        results.Add("GPU forest: negative coordinates, multipolygon holes, water islands, terrain edits, picking, unchanged reuse, buffer release and stale-load rejection passed");
    }

    static void SetupGpuRender()
    {
        if (renderStage==8)
        {
            var heights=new float[33,33];
            for(int z=0;z<33;z++)for(int x=0;x<33;x++)
                heights[z,x]=.15f + .22f*Mathf.Pow(Mathf.Sin(x*.14f),2) + .3f*Mathf.Pow(Mathf.Sin(z*.11f),2);
            forestTerrain.terrainData.SetHeights(0,0,heights);
            var tex=new Texture2D(1,1);tex.SetPixel(0,0,new Color(.29f,.35f,.18f));tex.Apply();
            forestTerrain.terrainData.terrainLayers=new[]{new TerrainLayer{diffuseTexture=tex,tileSize=Vector2.one*20}};
            forestTerrain.materialTemplate=new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
            forestTerrain.drawHeightmap=true;
        }
        Set(manager,"useGpuForests",true);
        Apply(Snapshot(10000,true,10000).Replace("\"prop_type\":1","\"prop_type\":2"),true);
        // 本編ワーカーが所属タイルを解決してGPU生成する経路も確認する。
        var tm=new GameObject("Forest QA TerrainManager").AddComponent(asm.GetType("TerrainManager"));
        ((IDictionary)Field(tm,"terrainTiles"))[Vector2Int.zero]=forestTerrain;
        Set(manager,"terrainManager",tm);
        var worker=(IEnumerator)managerType.GetMethod("DrawWorker",flags).Invoke(manager,null);
        for(int i=0;i<128;i++)
        {
            Check(worker.MoveNext(),"forest worker stopped");
            if(worker.Current is IEnumerator child)Drain(child);
            if(Chunks.Values.Cast<object>().All(c=>((IList)Field(c,"forests")).Cast<object>().All(p=>(bool)Field(p,"ready"))))break;
        }
        Check(Chunks.Values.Cast<object>().All(c=>((IList)Field(c,"forests")).Cast<object>().All(p=>(bool)Field(p,"ready"))),"worker did not generate forest");
        (worker as IDisposable)?.Dispose();
        ((IDictionary)Field(tm,"terrainTiles")).Clear();
        UnityEngine.Object.DestroyImmediate(tm.gameObject);
        Build(false);
        camera.transform.position = renderStage==6 ? new Vector3(250,28,150) : renderStage==7 ? new Vector3(250,200,-280) : new Vector3(250,110,-100);
        camera.transform.LookAt(new Vector3(250,renderStage>=8?40:0,320));
        if(renderStage==9)
        {
            var broken=Resources.Load<Shader>("Shaders/ForestWithoutCluster");
            Check(broken!=null && !ShaderUtil.ShaderHasError(broken),"missing-cluster regression shader failed");
            foreach(Material material in ((IDictionary)Field(Field(manager,"forestRenderer"),"materials")).Values)
                material.shader=broken;
        }
    }

    static void CheckForestSunlight(Texture2D missingCluster)
    {
        var correct=new Texture2D(2,2);
        correct.LoadImage(System.IO.File.ReadAllBytes(output+"/gpu-forest-mountain.png"));
        var a=correct.GetPixels32();var b=missingCluster.GetPixels32();
        double lit=0,unlit=0;int samples=0;
        for(int i=0;i<a.Length*0.6;i++)
        {
            if(a[i].g<=a[i].r*1.1 || a[i].g<=a[i].b*1.05)continue;
            lit+=a[i].r*.2126+a[i].g*.7152+a[i].b*.0722;
            unlit+=b[i].r*.2126+b[i].g*.7152+b[i].b*.0722;
            samples++;
        }
        Check(samples>10000,"sunlight comparison has too few forest pixels");
        results.Add($"Forward+ sunlight: forest luminance {lit/samples:F2}, missing cluster variant {unlit/samples:F2}");
        Check(lit>unlit*1.15,"Forward+ forest lost directional lighting");
        UnityEngine.Object.DestroyImmediate(correct);
    }

    static void SetupForestBoundaryRender()
    {
        foreach(Material material in ((IDictionary)Field(Field(manager,"forestRenderer"),"materials")).Values)
            material.shader=Resources.Load<Shader>("Shaders/Forest");
        var type=asm.GetType("ForestOverview");
        var overview=Activator.CreateInstance(type);
        var converter=AppDomain.CurrentDomain.GetAssemblies().First(a=>a.GetName().Name=="Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert");
        var response=converter.GetMethod("DeserializeObject",new[]{typeof(string),typeof(Type)}).Invoke(null,new object[]{Snapshot(10000,true).Replace("\"prop_type\":1","\"prop_type\":2"),asm.GetType("ForestOverviewResponse")});
        var raster=type.GetMethod("Build").Invoke(null,new object[]{response,System.Threading.CancellationToken.None});
        type.GetMethod("Publish").Invoke(overview,new[]{raster});
        var model=managerType.GetMethod("Model",flags).Invoke(manager,new object[]{2});
        type.GetMethod("SetLeafColor").Invoke(overview,new[]{(object)2,model});
        results.Add("Overview broadleaf linear albedo="+Shader.GetGlobalVector("_ForestLeafColor"));
        var rects=new List<Rect>();
        for(int z=0;z<8;z++)for(int x=0;x<4;x++)rects.Add(new Rect(x*62.5f,z*62.5f,62.5f,62.5f));
        type.GetMethod("UpdateDetail").Invoke(overview,new object[]{rects,62.5f});
        var ground=new Material(Shader.Find("Custom/DepthOffsetTerrainLit"));
        ground.SetFloat("_ProceduralGroundBlend",1);
        ground.SetFloat("_ProceduralGroundScale",40);
        ground.SetColor("_TerrainGroundColorA",new Color(.38f,.47f,.26f));
        ground.SetColor("_TerrainGroundColorB",new Color(.38f,.47f,.26f));
        ground.SetColor("_TerrainGroundColorC",new Color(.38f,.47f,.26f));
        forestTerrain.materialTemplate=ground;
        Check(!ShaderUtil.ShaderHasError(ground.shader),"overview ground shader failed");
        camera.transform.position=new Vector3(250,380,-220);
        camera.transform.LookAt(new Vector3(250,40,250));
    }
}
