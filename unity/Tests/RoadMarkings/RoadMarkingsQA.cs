using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Render and inspect the main road markings in an isolated URP project.</summary>
public static class RoadMarkingsQA
{
    static Assembly assembly;
    static string root, source;
    static Camera camera;
    static int step;
    static Component surface;
    static GameObject plane;
    static readonly List<GameObject> roads = new();
    static readonly List<string> report = new();
    static Type Type(string name) => assembly.GetType(name, true);
    static object Call(string type, string name, params object[] args) => Type(type).GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
    static object Method(object target, string name, params object[] args) => target.GetType().GetMethod(name).Invoke(target, args);
    static object Property(string type, string name) => Type(type).GetProperty(name).GetValue(null);
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        root = Path.GetDirectoryName(Application.dataPath); source = File.ReadAllText(root + "/source.txt").Trim();
        var references = File.ReadAllLines(root + "/references.txt");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            string path = references.FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase) && File.Exists(p));
            return path == null ? null : Assembly.LoadFrom(path);
        };
        try
        {
            assembly = Assembly.LoadFrom(source + "/Temp/bin/Debug/Assembly-CSharp.dll");
            GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.gray;
            var sun = new GameObject("Sun").AddComponent<Light>(); sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(60, 0, 0);
            camera = new GameObject("Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 70, 0); camera.transform.rotation = Quaternion.Euler(90, 0, 0);
            camera.orthographic = true; camera.orthographicSize = 36; camera.farClipPlane = 1500;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.targetTexture = new RenderTexture(768, 768, 24);
            CheckRules();
            object plan = Activator.CreateInstance(Type("RoadMarkingPlan"));
            Method(plan, "Strip", new Vector2(-28, 3), new Vector2(28, 3), .5f);
            Method(plan, "Strip", new Vector2(-13, -25), new Vector2(-13, 17), .5f);
            Method(plan, "Arrow", new Vector2(8, 10), Vector2.up, Enum.ToObject(Type("RoadMarkingTurns"), 7), 2f);
            for (int turns = 1; turns <= 7; turns++)
                Method(plan, "Arrow", new Vector2((turns - 4) * 8, 22), Vector2.up, Enum.ToObject(Type("RoadMarkingTurns"), turns), 1.5f);
            for (int i = -20; i <= 20; i += 2) Method(plan, "Strip", new Vector2(i, -15), new Vector2(i, -10), .8f);
            plane = CreateSurface("Flat", plan, Vector3.zero);
            surface = plane.GetComponent(Type("RoadMarkingSurface"));
            EditorApplication.update += Tick;
        }
        catch (Exception e) { Finish(e); }
    }

    static GameObject CreateSurface(string name, object plan, Vector3 origin)
    {
        var go = new GameObject(name); go.transform.position = origin;
        var mesh = new Mesh();
        mesh.vertices = new[] { new Vector3(-32,0,-32), new Vector3(-32,0,32), new Vector3(32,0,32), new Vector3(32,0,-32) };
        mesh.triangles = new[] { 0,1,2,0,2,3 }; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        mesh.SetUVs(3, mesh.vertices.Select(p => new Vector4(p.x,p.z,1,0)).ToList());
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit")); material.SetColor("_BaseColor",new Color(.12f,.12f,.12f));
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        var s = go.AddComponent(Type("RoadMarkingSurface")); Method(s,"Configure",plan,null);
        return go;
    }

    static void CheckRules()
    {
        Require((int)Call("RoadMarkingRules","Turn", Vector2.up, Vector2.left) == 1,"Left turn classification");
        Require((int)Call("RoadMarkingRules","Turn", Vector2.up, Vector2.right) == 4,"Right turn classification");
        Require((int)Call("RoadMarkingRules","Turn", Vector2.up, Vector2.up) == 2,"Straight classification");
        object all = Enum.ToObject(Type("RoadMarkingTurns"),7);
        int left = (int)Type("Constants").GetField("TRAFFIC_HAND_LEFT").GetValue(null);
        int right = (int)Type("Constants").GetField("TRAFFIC_HAND_RIGHT").GetValue(null);
        Require((int)Call("RoadMarkingRules","ForLane",all,1,2,left) == 4,"Left-hand inner right-turn lane");
        Require((int)Call("RoadMarkingRules","ForLane",all,1,2,right) == 1,"Right-hand inner left-turn lane");
        long bytes = (long)Call("RoadMarkingAtlas","TextureBytes",1024,1,1);
        Require(bytes == 1398101,"Mip chain byte accounting");
        report.Add("PASS: turn rules, traffic hand, exact mip byte accounting");
    }

    static void Tick()
    {
        try
        {
            // In Editor verification, the game's frameCount does not advance, so update the budget as a separate update frame.
            var atlas = Type("RoadMarkingAtlas").GetField("instance",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
            atlas.GetType().GetField("frame",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(atlas,-1);
            Call("RoadMarkingAtlas","Refresh",camera);
            if (++step < 12) return;
            if (step == 12)
            {
                var pixels = Capture("flat.png");
                var visual=plane.AddComponent(Type("VisualModeObject"));Method(visual,"InitializeMaterials");
                plane.GetComponent<Renderer>().SetPropertyBlock(null);
                visual.GetType().GetMethod("RestoreOriginalMaterials",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(visual,null);
                var restored=Capture("restored.png");
                Require(pixels.SequenceEqual(restored),"Visual mode restores marking properties");
                int light = pixels.Count(p=>p.r>140 && p.g>140 && p.b>100), dark=pixels.Count(p=>p.r<130 && p.g<130 && p.b<130);
                Require(light>1000 && dark>1000,$"Paint/asphalt separation: light={light}, dark={dark}");
                Require(!pixels.Any(p=>p.r>240 && p.b>240 && p.g<20),"Error shader pixels");
                report.Add($"PASS: direct surface paint; light={light}, dark={dark}");
                CheckArrowImages();
                long allocated=(long)Property("RoadMarkingAtlas","ReservedBytes");
                Require(allocated+(long)Property("RoadMarkingAtlas","TableBytes") <= 128L*1024*1024,"Memory cap");
                // Normal MonoBehaviour notifications do not run in EditMode, so invoke the same callback explicitly.
                surface.SendMessage("OnDisable"); plane.SetActive(false); plane.SetActive(true); surface.SendMessage("OnEnable");
            }
            if (step == 16)
            {
                Require((int)Property("RoadMarkingAtlas","CacheHits")>0,"Disable/enable cache reuse");
                plane.transform.rotation = Quaternion.Euler(0,0,12);
                Capture("slope.png");
                Method(surface,"ApplyProperties");
                camera.transform.position = new Vector3(0,650,0); camera.orthographicSize=36;
                var pixels=Capture("far.png");
                Require(pixels.Count(p=>p.r>160&&p.g>160)<100,"Far marking fade");
                camera.transform.position = new Vector3(0,70,0);
                report.Add("PASS: slope, far fade, cached re-entry");
                CheckRoad();
            }
            if (step == 22)
            {
                CheckRoadSeams();
                CheckJunction();
            }
            if (step == 42)
            {
                var junctionPixels = Capture("junction.png");
                Require(junctionPixels.Count(p=>p.r>140 && p.g>140 && p.b>100)>1000,"Generated junction paint");
                CheckCapacity();
                CheckLod();
                foreach(string name in new[]{"Custom/RoadSurface","Hidden/RoadMarkingBake"})
                    foreach(var message in ShaderUtil.GetShaderMessages(Shader.Find(name)))
                        Require(message.severity.ToString()!="Error",name+": "+message.message);
                Call("RoadMarkingAtlas","Clear");
                Require((long)Property("RoadMarkingAtlas","ReservedBytes")==0 && (long)Property("RoadMarkingAtlas","TableBytes")==0,"Session cleanup");
                report.Add("PASS: shader compilation and session resource release");
                Finish(null);
            }
        }
        catch(Exception e){Finish(e);}
    }

    static void CheckArrowImages()
    {
        var target = camera.targetTexture;
        camera.targetTexture = new RenderTexture(1344, 192, 24);
        camera.aspect = 7; camera.orthographicSize = 4; camera.transform.position = new Vector3(0, 70, 22);
        var pixels = Capture("arrows.png");
        for (int turns = 1; turns <= 7; turns++)
        {
            // Enclose each arrow on its own and inspect paint connectivity and closed holes without including neighboring markings.
            const int width = 96, height = 144;
            int left = (turns - 1) * 192 + 48, bottom = 24;
            var paint = new bool[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                var p = pixels[(bottom + y) * 1344 + left + x];
                paint[y * width + x] = p.r > 140 && p.g > 140 && p.b > 100;
            }
            var seen = new bool[paint.Length];
            int components = 0, holes = 0;
            var queue = new Queue<int>();
            for (int i = 0; i < paint.Length; i++)
            {
                if (seen[i]) continue;
                bool white = paint[i], border = false;
                queue.Enqueue(i); seen[i] = true;
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue(), x = current % width, y = current / width;
                    border |= x == 0 || y == 0 || x == width - 1 || y == height - 1;
                    foreach (int neighbor in new[] { x > 0 ? current - 1 : -1, x + 1 < width ? current + 1 : -1,
                        y > 0 ? current - width : -1, y + 1 < height ? current + width : -1 })
                        if (neighbor >= 0 && !seen[neighbor] && paint[neighbor] == white)
                        { seen[neighbor] = true; queue.Enqueue(neighbor); }
                }
                if (white) components++; else if (!border) holes++;
            }
            Require(components == 1 && holes == 0, $"Arrow {turns}: connected paint={components}, holes={holes}");
            if ((turns & 2) == 0)
                Require(!paint.Skip(100 * width).Any(p => p), $"Turn-only arrow {turns} has no forward stub");
        }
        UnityEngine.Object.DestroyImmediate(camera.targetTexture);
        camera.targetTexture = target; camera.ResetAspect(); camera.orthographicSize = 36;
        camera.transform.position = new Vector3(0, 70, 0);
        camera.orthographicSize = 4; camera.transform.position = new Vector3(8, 70, 10);
        Capture("arrow-close.png");
        camera.orthographicSize = 36; camera.transform.position = new Vector3(0, 70, 0);
        report.Add("PASS: all seven arrow masks have connected heads, no enclosed holes, no turn-only forward stub");
    }

    static void CheckRoadSeams()
    {
        var filters = roads.SelectMany(r => r.GetComponentsInChildren<MeshFilter>())
            .Where(f => f.GetComponent(Type("RoadMarkingSurface")) != null).ToArray();
        var originals = filters.Select(f => f.sharedMesh).ToArray();
        var references = originals.Select(m => UnityEngine.Object.Instantiate(m)).ToArray();
        int removed = 0;
        for (int f = 0; f < filters.Length; f++)
        {
            var vertices = references[f].vertices;
            for (int s = 0; s < references[f].subMeshCount; s++)
            {
                var triangles = references[f].GetTriangles(s); var kept = new List<int>();
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                    var normal = filters[f].transform.TransformDirection(Vector3.Cross(b - a, c - a).normalized);
                    var center = filters[f].transform.TransformPoint((a + b + c) / 3f);
                    // Keep the end faces on the outer perimeter and remove from the reference image only the internal end faces of repeating components that should be hidden.
                    if (Mathf.Abs(normal.z) > .7f && Mathf.Abs(normal.y) < .7f && Mathf.Abs(center.z) < 65f)
                    { removed++; continue; }
                    kept.Add(triangles[t]); kept.Add(triangles[t + 1]); kept.Add(triangles[t + 2]);
                }
                references[f].SetTriangles(kept, s);
            }
        }
        Require(removed == 0, "Road generation omits repeated internal cap faces");
        var position = camera.transform.position; var rotation = camera.transform.rotation;
        foreach (var view in new[] { ("road", position), ("road-oblique", new Vector3(80, 95, -90)), ("road-low", new Vector3(-70, 50, -100)) })
        {
            camera.transform.position = view.Item2;
            if (view.Item1 == "road") camera.transform.rotation = rotation; else camera.transform.LookAt(new Vector3(0, 4, 0));
            for (int f = 0; f < filters.Length; f++) filters[f].sharedMesh = originals[f];
            var actual = Capture(view.Item1 + ".png");
            Require(actual.Count(p => p.r > 140 && p.g > 140 && p.b > 100) > 100, view.Item1 + " contains painted road");
            for (int f = 0; f < filters.Length; f++) filters[f].sharedMesh = references[f];
            var reference = Capture(view.Item1 + "-surface-reference.png");
            int changed = actual.Zip(reference, (a, b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b))).Count(d => d > 10);
            Require(changed <= 32, $"{view.Item1}: hidden caps affect {changed} pixels");
        }
        for (int f = 0; f < filters.Length; f++) { filters[f].sharedMesh = originals[f]; UnityEngine.Object.DestroyImmediate(references[f]); }
        camera.transform.SetPositionAndRotation(position, rotation);
        report.Add("PASS: no internal FBX cap faces or surface seams from top, oblique and low views");
    }

    static void CheckRoad()
    {
        plane.SetActive(false);
        var masterType=Type("MasterDataStore");var master=new GameObject("Master").AddComponent(masterType);
        masterType.GetField("<Instance>k__BackingField",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,master);
        var dictionary=(IDictionary)masterType.GetField("pathMasterById",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(master);
        var item=Activator.CreateInstance(Type("PathMasterItem"));
        foreach(var field in new[]{("width",(object)16f),("sidewalk_width",(object)2f),("side_width",(object).5f),("lanes",(object)4),("follow_terrain",(object)false)}) item.GetType().GetField(field.Item1).SetValue(item,field.Item2);
        dictionary.Add(101,item);
        var modelType=Type("ModelManager");var model=new GameObject("Models").AddComponent(modelType);
        modelType.GetField("<Instance>k__BackingField",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,model);
        var models=(IDictionary)modelType.GetField("_modelCache",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(model);
        foreach(string part in new[]{"body","line","sidewalk"})
        {
            string file=Directory.GetFiles(Application.dataPath+"/Paths/p1",part+".fbx",SearchOption.AllDirectories).FirstOrDefault();
            if(file!=null) models.Add("Models/Paths/p101/"+part,AssetDatabase.LoadAssetAtPath<GameObject>("Assets"+file.Substring(Application.dataPath.Length).Replace('\\','/')));
        }
        var path=Activator.CreateInstance(Type("GeneratedModels.PathSummary"));
        foreach(var field in new[]{("path_id",19),("path_variant",101),("path_type",1),("start_node_id",1),("end_node_id",2)}) path.GetType().GetField(field.Item1).SetValue(path,field.Item2);
        path.GetType().GetField("start_point").SetValue(path,new List<object>{0f,0f,-70f});
        path.GetType().GetField("end_point").SetValue(path,new List<object>{0f,0f,70f});
        var generator=Activator.CreateInstance(Type("RoadPath"));
        var setback=Activator.CreateInstance(Type("SidewalkSetback"));
        var result=Method(generator,"Generate",path,new List<Vector3>{new Vector3(0,0,-70),new Vector3(0,8,0),new Vector3(0,0,70)},setback,setback);
        var road=(GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        roads.Add(road);
        Require(road.GetComponentsInChildren<Transform>().All(t=>!t.name.EndsWith("/line")),"No separate line meshes");
        Require(road.GetComponentsInChildren(Type("RoadMarkingSurface")).Length>0,"Road surface hook");
        camera.transform.position=new Vector3(0,120,0);camera.orthographicSize=80;
        report.Add("PASS: real road generator with FBX body, slope, no line objects");
    }

    static void CheckJunction()
    {
        foreach(var road in roads) road.SetActive(false);
        var pathType=Type("GeneratedModels.PathSummary");
        var paths=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pathType));
        var ends=new[]{new Vector3(0,0,90),new Vector3(90,0,0),new Vector3(0,0,-90),new Vector3(-90,0,0)};
        for(int i=0;i<4;i++)
        {
            var path=Activator.CreateInstance(pathType);
            foreach(var field in new[]{("path_id",i+31),("path_variant",101),("path_type",1),("start_node_id",1),("end_node_id",i+2),("status",(int)Type("Constants").GetField("STATUS_ACTIVE").GetValue(null))}) pathType.GetField(field.Item1).SetValue(path,field.Item2);
            pathType.GetField("width").SetValue(path,16f);
            pathType.GetField("start_point").SetValue(path,new List<object>{0f,0f,0f});
            pathType.GetField("end_point").SetValue(path,new List<object>{ends[i].x,0f,ends[i].z});
            paths.Add(path);
        }
        var provider=Type("RoadMarkingRules").GetField("ConnectedPaths");
        var node=Expression.Parameter(typeof(int));
        var empty=Expression.New(paths.GetType());
        provider.SetValue(null,Expression.Lambda(provider.FieldType,Expression.Condition(Expression.Equal(node,Expression.Constant(1)),Expression.Constant(paths,paths.GetType()),empty),node).Compile());
        Type("RoadMarkingRules").GetField("IsApproachEndpoint").SetValue(null,null);
        var generator=Activator.CreateInstance(Type("RoadIntersectionMeshGenerator"));
        var result=Method(generator,"Generate",1,Vector3.zero,paths,1f);
        Require(result!=null,"Junction generator result");
        var mesh=(Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
        var go=new GameObject("Junction");go.AddComponent<MeshFilter>().sharedMesh=mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial=new Material(Shader.Find("Universal Render Pipeline/Lit"));
        go.GetComponent<MeshRenderer>().sharedMaterial.SetColor("_BaseColor",new Color(.12f,.12f,.12f));
        object plan=result.GetType().GetProperty("Markings").GetValue(result);
        Method(go.AddComponent(Type("RoadMarkingSurface")),"Configure",plan,null);roads.Add(go);
        var connections=(IDictionary)result.GetType().GetProperty("ConnectionPoints").GetValue(result);
        var roadGenerator=Activator.CreateInstance(Type("RoadPath"));var setback=Activator.CreateInstance(Type("SidewalkSetback"));
        for(int i=0;i<4;i++)
        {
            var start=(Vector3)connections[i+31];
            var roadResult=Method(roadGenerator,"Generate",paths[i],new List<Vector3>{start,ends[i]},setback,setback);
            var road=(GameObject)roadResult.GetType().GetProperty("GeneratedObject").GetValue(roadResult);roads.Add(road);
            var context=road.GetComponentInChildren(Type("RoadMarkingContext"));
            var markingPlan=context.GetType().GetProperty("Plan").GetValue(context);
            Require(!(bool)markingPlan.GetType().GetProperty("Empty").GetValue(markingPlan),"Approach crosswalk/arrow plan");
        }
        var signature=(string)Call("RoadMarkingRules","JunctionSignature",1,paths);
        pathType.GetField("travel_direction").SetValue(paths[1],1);
        Require(signature!=(string)Call("RoadMarkingRules","JunctionSignature",1,paths),"Direction change invalidates approaches");
        camera.transform.position=new Vector3(0,110,0);camera.orthographicSize=55;
        report.Add("PASS: four-way junction, approach masks and topology invalidation");
    }

    static void CheckCapacity()
    {
        foreach(var road in roads)
        {
            foreach(Component s in road.GetComponentsInChildren(Type("RoadMarkingSurface"),true)) s.SendMessage("OnDisable");
            road.SetActive(false);
        }
        var active=new List<Component>();
        for(int i=0;i<40;i++)
        {
            var plan=Activator.CreateInstance(Type("RoadMarkingPlan"));
            Method(plan,"Strip",new Vector2(1+i*.01f,1),new Vector2(20,20),.2f);
            active.Add(CreateSurface("Density"+i,plan,Vector3.zero).GetComponent(Type("RoadMarkingSurface")));
        }
        camera.orthographicSize=12;camera.transform.position=new Vector3(0,50,0);
        var atlas=Type("RoadMarkingAtlas").GetField("instance",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        var frame=atlas.GetType().GetField("frame",BindingFlags.NonPublic|BindingFlags.Instance);
        for(int i=0;i<70;i++) { frame.SetValue(atlas,-1);Call("RoadMarkingAtlas","Refresh",camera); }
        int bakes=(int)Property("RoadMarkingAtlas","BakedPages");
        for(int i=0;i<5;i++) { frame.SetValue(atlas,-1);Call("RoadMarkingAtlas","Refresh",camera); }
        Require((int)Property("RoadMarkingAtlas","BakedPages")==bakes,"No continuous rebaking under high-tier overflow");
        foreach(var s in active) Require(((IList)s.GetType().GetField("Tiles").GetValue(s)).Count>0,"Visible mask tiles");
        long memory=(long)Property("RoadMarkingAtlas","ReservedBytes")+(long)Property("RoadMarkingAtlas","TableBytes");
        Require(memory<=128L*1024*1024,"High density memory cap");
        report.Add($"PASS: 40 overlapping unique masks, high-tier overflow/reuse; reserved={memory} bytes");
        var oldHighTiles=new List<object>();
        foreach(var s in active)
        {
            foreach(var tile in (IList)s.GetType().GetField("Tiles").GetValue(s))
            {
                var page=tile.GetType().GetField("Page").GetValue(tile);
                Require(page!=null,"Lower-tier fallback provides every visible tile");
                if((int)page.GetType().GetField("Tier").GetValue(page)==2) oldHighTiles.Add(tile);
            }
            s.transform.position=new Vector3(10000,0,0);
        }
        int evictions=(int)Property("RoadMarkingAtlas","Evictions");
        for(int i=0;i<26;i++)
        {
            var plan=Activator.CreateInstance(Type("RoadMarkingPlan"));
            Method(plan,"Strip",new Vector2(3+i*.013f,2),new Vector2(21,20),.3f);
            CreateSurface("Replacement"+i,plan,Vector3.zero);
        }
        for(int i=0;i<40;i++){frame.SetValue(atlas,-1);Call("RoadMarkingAtlas","Refresh",camera);}
        Require((int)Property("RoadMarkingAtlas","Evictions")>evictions,"Unseen live pages can be evicted");
        Require(oldHighTiles.All(t=>t.GetType().GetField("Page").GetValue(t)==null),"Eviction clears live references before slot reuse");
        report.Add("PASS: live offscreen eviction invalidates old page references");
    }

    static void CheckLod()
    {
        Type("RoadMarkingRules").GetField("ConnectedPaths").SetValue(null,null);
        var master=Property("MasterDataStore","Instance");
        var dictionary=(IDictionary)master.GetType().GetField("pathMasterById",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(master);
        var item=Activator.CreateInstance(Type("PathMasterItem"));
        foreach(var field in new[]{("width",(object)24f),("sidewalk_width",(object)3f),("side_width",(object).5f),("lanes",(object)4),("follow_terrain",(object)false),("body_material",(object)"PATH_ASPHALT"),("type",(object)1)}) item.GetType().GetField(field.Item1).SetValue(item,field.Item2);
        dictionary.Add(231,item);
        var model=Property("ModelManager","Instance");
        var models=(IDictionary)model.GetType().GetField("_modelCache",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(model);
        foreach(string part in new[]{"body","sidewalk"}) models.Add("Models/Paths/p1/"+part,models["Models/Paths/p101/"+part]);
        var prefab=(GameObject)models["Models/Paths/p1/body"];
        Require(prefab.GetComponent<Renderer>().sharedMaterial.name=="PATH_CONCRETE","Real street FBX has concrete as its first material");
        var path=Activator.CreateInstance(Type("GeneratedModels.PathSummary"));
        foreach(var field in new[]{("path_id",900),("path_variant",231),("path_type",1),("start_node_id",901),("end_node_id",902)}) path.GetType().GetField(field.Item1).SetValue(path,field.Item2);
        path.GetType().GetField("start_point").SetValue(path,new List<object>{0f,0f,0f});
        path.GetType().GetField("end_point").SetValue(path,new List<object>{0f,20f,400f});
        var setback=Activator.CreateInstance(Type("SidewalkSetback"));
        Type("PathDrawContext").GetProperty("PathVariant").SetValue(null,231);
        var result=Method(Activator.CreateInstance(Type("RoadPath")),"Generate",path,new List<Vector3>{Vector3.zero,new Vector3(0,20,400)},setback,setback);
        var road=(GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        var lod=road.GetComponentsInChildren(Type("SplinePartLod")).First(c=>(bool)c.GetType().GetField("body",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(c));
        var factory=(Delegate)lod.GetType().GetField("build",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(lod);
        var close=(GameObject)factory.DynamicInvoke(Enum.Parse(Type("DrawLod"),"Close"),0d,.25d);
        var normal=(GameObject)factory.DynamicInvoke(Enum.Parse(Type("DrawLod"),"Normal"),0d,.25d);
        Vector2 Range(GameObject go)
        {
            var coordinates=new List<Vector4>();go.GetComponent<MeshFilter>().sharedMesh.GetUVs(3,coordinates);
            var top=coordinates.Where(p=>p.z>.99f).ToList();Require(top.Count>0,"Top surface marking coordinates");
            return new Vector2(top.Min(p=>p.x),top.Max(p=>p.x));
        }
        Require(Vector2.Distance(Range(close),Range(normal))<.01f,"Close/Normal share distance range");
        var second=(GameObject)factory.DynamicInvoke(Enum.Parse(Type("DrawLod"),"Normal"),.25d,.5d);
        Require(Math.Abs(Range(normal).y-Range(second).x)<.01f,"Fixed LOD boundary has continuous dash distance");
        var segments=(IList)lod.GetType().GetField("segments",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(lod);
        var far=(GameObject)lod.GetType().GetMethod("BuildFar",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(lod,new[]{segments[0]});
        Require(far.GetComponent(Type("RoadMarkingSurface"))==null,"Far segment omits markings");
        Material FarMaterial(Component part)
        {
            ((IDictionary)Type("PathFarModel").GetField("tiles",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null)).Clear();
            var sections=(IList)part.GetType().GetField("segments",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(part);
            var segment=(GameObject)part.GetType().GetMethod("BuildFar",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(part,new[]{sections[0]});
            return segment.GetComponent<Renderer>().sharedMaterial;
        }
        Color ColorOf(string name)=>((Material)Call("RoadPath","GetOrCreateRoadMaterialByName",name)).GetColor("_BaseColor");
        Require(far.GetComponent<Renderer>().sharedMaterial.GetColor("_BaseColor")==ColorOf("PATH_ASPHALT"),"Four-lane Far road uses master asphalt rather than first FBX material");
        foreach(var detail in new[]{close,normal})
            Require(detail.GetComponent<Renderer>().sharedMaterials.Select(m=>m.name).SequenceEqual(new[]{"PATH_CONCRETE","PATH_ASPHALT"}.Select(n=>n+" (road surface)")),"Close/Normal preserve both FBX submesh materials");
        foreach(string name in new[]{"PATH_CONCRETE",null,"MISSING_ROAD_MATERIAL"})
        {
            item.GetType().GetField("body_material").SetValue(item,name);
            Type("PathDrawContext").GetProperty("PathVariant").SetValue(null,231); // Direct section builds above end the draw context and reset it to 0.
            var alternate=Method(Activator.CreateInstance(Type("RoadPath")),"Generate",path,new List<Vector3>{Vector3.zero,new Vector3(0,20,400)},setback,setback);
            var alternateRoad=(GameObject)alternate.GetType().GetProperty("GeneratedObject").GetValue(alternate);
            var alternateLod=alternateRoad.GetComponentsInChildren(Type("SplinePartLod")).First(c=>(bool)c.GetType().GetProperty("IsBody").GetValue(c));
            Require(FarMaterial(alternateLod).GetColor("_BaseColor")==ColorOf("PATH_CONCRETE"),"Master concrete and absent/unresolved master material follow the common resolution rule");
            alternateRoad.SetActive(false);
        }
        Type("PathDrawContext").GetProperty("PathVariant").SetValue(null,0);
        foreach(var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)) renderer.enabled=renderer.gameObject==far;
        camera.transform.position=new Vector3(0,90,50);camera.transform.rotation=Quaternion.Euler(90,0,0);camera.orthographicSize=60;
        Capture("four-lane-far-asphalt.png");
        report.Add("PASS: four-lane Far asphalt, configurable concrete, missing-material fallback and unchanged detail materials");
        report.Add("PASS: 400m graded road, Close/Normal coordinate continuity, fixed LOD boundary, Far omission");
    }

    static Color32[] Capture(string name)
    {
        camera.Render();RenderTexture.active=camera.targetTexture;
        var image=new Texture2D(camera.targetTexture.width,camera.targetTexture.height,TextureFormat.RGB24,false);
        image.ReadPixels(new Rect(0,0,image.width,image.height),0,0);image.Apply();
        File.WriteAllBytes(root+"/"+name,image.EncodeToPNG());var pixels=image.GetPixels32();UnityEngine.Object.DestroyImmediate(image);RenderTexture.active=null;return pixels;
    }
    static void Finish(Exception error)
    {
        EditorApplication.update-=Tick;
        File.WriteAllText(root+"/result.txt",(error==null?"PASS\n":"FAIL\n"+error+"\n")+string.Join("\n",report));
        EditorApplication.Exit(error==null?0:1);
    }
}
