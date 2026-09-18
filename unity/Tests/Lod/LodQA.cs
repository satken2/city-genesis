using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class LodQA
{
    static Assembly source;
    static string root;
    static readonly List<string> results = new List<string>();
    static int checks;
    static LodQaHost host;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Type Type(string name) => source.GetType(name, true);
    static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Instance).Invoke(target, args);
    static object Static(string type, string name, params object[] args) => Type(type).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
    static object Field(object target, string name) => target.GetType().GetField(name, Instance).GetValue(target);
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Instance).SetValue(target, value);
    static void Check(bool pass, string message) { checks++; if (!pass) throw new Exception(message); }
    static object Lod(int value) => Enum.ToObject(Type("DrawLod"), value);

    static LodQA()
    {
        if (SessionState.GetBool("LodQA.Pending", false)) EditorApplication.delayCall += Start;
    }
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        SessionState.SetString("LodQA.Root", args[Array.IndexOf(args, "-lodSourceRoot")+1]);
        SessionState.SetBool("LodQA.Pending", true);
        EditorApplication.EnterPlaymode();
    }
    static void Start()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.delayCall += Start; return; }
        SessionState.SetBool("LodQA.Pending", false);
        root = SessionState.GetString("LodQA.Root", "");
        AppDomain.CurrentDomain.AssemblyResolve += (o,e) => {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { "/Temp/bin/Debug", "/Library/ScriptAssemblies", "/Assets/Plugins" })
                if (File.Exists(root+dir+"/"+name)) return Assembly.LoadFrom(root+dir+"/"+name);
            return null;
        };
        source = Assembly.LoadFrom(root+"/Temp/bin/Debug/Assembly-CSharp.dll");
        host = new GameObject("LodQaHost").AddComponent<LodQaHost>();
        host.Run(Guard(Suite()));
    }
    static IEnumerator Guard(IEnumerator suite)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(suite);
        Exception failure = null;
        while (stack.Count > 0)
        {
            object current = null;
            try
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); (top as IDisposable)?.Dispose(); continue; }
                current = top.Current;
            }
            catch (Exception ex) { failure = ex; break; }
            if (current is IEnumerator child) stack.Push(child); else yield return current;
        }
        File.WriteAllText(root+"/Temp/LodRuntimeQA/result.txt", (failure == null ? "PASS" : "FAIL") + $" {checks} checks\n" + string.Join("\n", results) + (failure != null ? "\n"+failure : ""));
        EditorApplication.Exit(failure == null ? 0 : 1);
    }
    static IEnumerator Suite()
    {
        StableSelection();
        ProjectionAndResidency();
        LargeMapCameraRange();
        yield return OverviewSessionSwitch();
        yield return CacheLifetime();
        yield return PendingRenderWork();
        yield return InterruptedPathResponse();
        yield return TerrainFollowResume();
        yield return TerrainNotificationConsumption();
        yield return MeshBakeLifetime();
        yield return Scheduler();
        yield return SchedulerThroughput();
        yield return AtomicTerrain();
        yield return BuildingBatches();
        yield return BuildingExterior();
        yield return RoadSegments();
        yield return RoadSegmentsCameraMoves();
        PathMaterials();
        yield return RoadPresentation();
        TerrainBenchmark();
    }
    static void LargeMapCameraRange()
    {
        string scene = File.ReadAllText(root + "/Assets/Scenes/GameScene.unity");
        Check(!scene.Contains("350f452c666648a438a4323f94d385c8") && !scene.Contains("357068000"),
            "Game scene still references the origin-shifting component");
        Check(source.GetType("FloatingOrigin") == null, "Origin shifting is still available in the runtime");
        var go = new GameObject("LargeMapTerrain"); go.SetActive(false);
        var manager = go.AddComponent(Type("TerrainManager"));
        var previousMainCamera = Camera.main;
        if (previousMainCamera != null) previousMainCamera.enabled = false;
        var cameraObject = new GameObject("LargeMapCamera") { tag = "MainCamera" };
        var camera = cameraObject.AddComponent<Camera>();
        camera.farClipPlane = 4500f;
        try
        {
            Check(Camera.main == camera, "Large map test camera is not the main camera");
            Call(manager, "UpdateCameraClipDistance");
            Check(camera.farClipPlane == 4500f, "Unloaded map changed the camera range");
            foreach (var size in new[] { new Vector2Int(2000, 2000), new Vector2Int(30000, 24000), new Vector2Int(8000, 60000) })
            {
                var settings = Activator.CreateInstance(Type("TerrainSettingsData"));
                Set(settings, "map_width", size.x); Set(settings, "map_height", size.y);
                Set(manager, "terrainSettings", settings);
                foreach (var position in new[] { new Vector3(0, 150, 0), new Vector3(size.x / 2f, 500, -size.y / 2f), new Vector3(-6000, 20000, 8000) })
                {
                    camera.transform.position = position;
                    float previous = camera.farClipPlane;
                    Call(manager, "UpdateCameraClipDistance");
                    Check(camera.transform.position == position, "Map range adjustment moved the camera");
                    Check(camera.farClipPlane >= previous, "Map range adjustment shrank the configured range");
                    foreach (float x in new[] { -size.x / 2f, size.x / 2f })
                    foreach (float z in new[] { -size.y / 2f, size.y / 2f })
                    foreach (float y in new[] { -200f, 1000f })
                        Check(Vector3.Distance(position, new Vector3(x, y, z)) < camera.farClipPlane,
                            $"Large map corner is outside the far clipping range: map={size}, camera={position}, far={camera.farClipPlane}");
                }
            }
        }
        finally
        {
            Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(go);
            if (previousMainCamera != null) previousMainCamera.enabled = true;
        }
        results.Add("Large maps: fixed world coordinates and full terrain clipping range across map sizes, corners and altitude");
    }

    static IEnumerator OverviewSessionSwitch()
    {
        var go = new GameObject("OverviewSessionQA"); go.SetActive(false);
        var manager = go.AddComponent(Type("TerrainManager"));
        Set(manager, "ultraFarLandShader", Shader.Find("Universal Render Pipeline/Lit"));
        Set(manager, "ultraFarGridStep", 100f);
        object Batch() => Activator.CreateInstance(Type("LoadBatch"), Activator.CreateInstance(Type("LodCellSets")), false);
        void Receive(int width, int height, float value, long revision)
        {
            var settings = Activator.CreateInstance(Type("TerrainSettingsData"));
            Set(settings, "map_width", width); Set(settings, "map_height", height);
            Set(manager, "terrainSettings", settings);
            var heights = new float[5,5];
            for (int z=0;z<5;z++) for (int x=0;x<5;x++) heights[z,x] = value;
            Set(manager, "farTerrainHeights", heights); Set(manager, "overviewDataRevision", revision);
        }
        try
        {
            Receive(1000, 1000, .25f, 0);
            yield return Call(manager, "EnsureOverview", 0L, Batch());
            var first = (GameObject)Field(manager, "ultraFarLandObject");
            Check(first != null && (long)Field(manager, "overviewRevision") == 0, "Initial overview did not finish");
            Call(manager, "ClearAllCells");
            Check((long)Field(manager, "overviewRevision") == -1 && (long)Field(manager, "overviewDataRevision") == -1,
                "Session clear retained the previous map's overview revision");

            // 別マップも版0から始まり、受信完了後・メッシュ公開前にカメラ移動で中断される。
            Receive(2400, 1600, .3f, 0);
            var batch = Batch();
            var interrupted = (IEnumerator)Call(manager, "EnsureOverview", 0L, batch);
            Check(interrupted.MoveNext(), "New map incorrectly reused the previous map's completed revision");
            var building = (IEnumerator)interrupted.Current;
            Check(building.MoveNext(), "Overview did not enter background generation");
            Call(batch, "Cancel");
            yield return building;
            (building as IDisposable)?.Dispose(); (interrupted as IDisposable)?.Dispose();
            Check(Field(manager, "ultraFarLandObject") == null && (long)Field(manager, "overviewRevision") == -1,
                "Canceled overview was published or marked complete");
            yield return Call(manager, "EnsureOverview", 0L, Batch());
            var second = (GameObject)Field(manager, "ultraFarLandObject");
            var mesh = (Mesh)Field(manager, "ultraFarLandMesh");
            Check(second != null && second.activeSelf && mesh.vertexCount > 0, "Interrupted new map overview did not recover");
            Check(Mathf.Abs(mesh.bounds.size.x - 2400f) < 1f && Mathf.Abs(mesh.bounds.size.z - 1600f) < 1f,
                "Overview recovered with the previous map's dimensions");
            yield return Call(manager, "EnsureOverview", 0L, Batch());
            Check(ReferenceEquals(second, Field(manager, "ultraFarLandObject")), "Completed overview regenerated without a revision change");

            // グリッドの標本点を含まない微小な陸地は空メッシュとして完成する。
            Static("CoastlineStore", "SetFromGeoJson", Newtonsoft.Json.Linq.JToken.Parse("[[[1,1],[2,1],[2,2],[1,2],[1,1]]]"));
            Receive(2400, 1600, .3f, 1);
            yield return Call(manager, "EnsureOverview", 1L, Batch());
            Check(Field(manager, "ultraFarLandObject") == null && (long)Field(manager, "overviewRevision") == 1,
                "Empty overview retained old land or failed to complete");
            yield return null;
            Check(second == null && mesh == null && first == null, "Replaced overview objects or meshes leaked");
            Static("CoastlineStore", "Clear");

            Receive(1000, 1000, .25f, 2);
            var pending = (IEnumerator)Call(manager, "EnsureOverview", 2L, Batch());
            Check(pending.MoveNext(), "Updated overview did not start");
            building = (IEnumerator)pending.Current;
            Check(building.MoveNext(), "Updated overview did not start background work");
            Call(manager, "ClearAllCells");
            Static("CoastlineStore", "Clear");
            yield return building;
            (building as IDisposable)?.Dispose(); (pending as IDisposable)?.Dispose();
            Check(Field(manager, "ultraFarLandObject") == null && (long)Field(manager, "overviewRevision") == -1,
                "Previous session's background result reappeared after clear");
        }
        finally
        {
            Call(manager, "ClearAllCells");
            foreach (string name in new[] { "ultraFarLandMat", "ultraFarLandMatDark" })
                if (Field(manager, name) is Material material) Object.Destroy(material);
            Object.Destroy(go);
        }
        results.Add("Overview: same-revision session switch, interrupted generation/resume, map dimensions, empty land and stale-result rejection");
    }

    static Delegate SegmentFactory<T>(GameObject parent, Action built)
    {
        return new Func<T, double, double, GameObject>((lod, from, to) => {
            built();
            var go = new GameObject("QA Segment");
            go.AddComponent<MeshRenderer>();
            go.transform.SetParent(parent.transform, false);
            return go;
        });
    }
    static void PathMaterials()
    {
        var rootObject = new GameObject("MaterialRoad");
        var visual = rootObject.AddComponent(Type("VisualModeObject"));
        Set(visual, "objectType", Type("InputObjectType").GetField("PATH").GetValue(null));
        Set(visual, "useChildRenderers", true);
        Set(visual, "ignoreSelfRenderer", true);
        var shader = Shader.Find("Hidden/InternalErrorShader");
        var planned = new Material(shader);
        var original = new Material(shader);
        Set(visual, "plannedMaterial", planned);
        int plannedStatus = (int)Type("Constants").GetField("STATUS_SCHEDULED_CONSTRUCTION").GetValue(null);
        int activeStatus = (int)Type("Constants").GetField("STATUS_ACTIVE").GetValue(null);
        Call(visual, "SetStatus", plannedStatus);
        MeshRenderer AddPart()
        {
            var part = new GameObject("LatePart");
            part.transform.SetParent(rootObject.transform);
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { original, original };
            return renderer;
        }
        var first = AddPart();
        Call(visual, "RefreshMaterials");
        Check(Array.TrueForAll(first.sharedMaterials, mat => mat == planned), "First delayed part did not receive planned material immediately");
        var second = AddPart();
        Call(visual, "RefreshMaterials");
        Check(Array.TrueForAll(second.sharedMaterials, mat => mat == planned), "Additional part kept its original material");
        first.gameObject.SetActive(false);
        Call(visual, "RefreshMaterials");
        first.gameObject.SetActive(true);
        Call(visual, "SetStatus", activeStatus);
        Check(Array.TrueForAll(first.sharedMaterials, mat => mat == original)
            && Array.TrueForAll(second.sharedMaterials, mat => mat == original), "Planned material contaminated originals across refresh or cache reuse");
        Object.Destroy(rootObject); Object.Destroy(planned); Object.Destroy(original);
        results.Add("Path materials: delayed parts, multiple slots and inactive cache restore passed");
    }
    static IEnumerator RoadPresentation()
    {
        var camera = new GameObject("SwapCamera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,10,0);
        var cache = Activator.CreateInstance(Type("RenderObjectCache"), new object[] { 8L * 1024 * 1024 });
        GameObject Candidate(string name)
        {
            var rootObject = new GameObject(name);
            rootObject.AddComponent<MeshRenderer>();
            var identity = rootObject.AddComponent(Type("RenderCacheIdentity")); Set(identity,"key",name);
            object spline = Static("RoadPath", "BuildSpline", rootObject,
                new List<Vector3> { Vector3.zero, new Vector3(256,0,0), new Vector3(512,0,0) }, false);
            var part = rootObject.AddComponent(Type("SplinePartLod"));
            var factory = typeof(LodQA).GetMethod("SegmentFactory", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(Type("DrawLod")).Invoke(null, new object[] { rootObject, (Action)(() => {}) });
            Call(part,"Configure",spline,8f,0f,0f,false,null,0d,1d,factory);
            return rootObject;
        }
        var old = new GameObject("VisibleRoad");
        Set(old.AddComponent(Type("RenderCacheIdentity")),"key","old");
        var next = Candidate("PendingRoad");
        Static("PathPresentation","Replace",next,old,cache);
        Check(old.activeSelf && next.GetComponent<MeshRenderer>().forceRenderingOff,"Pending road hid its predecessor or exposed overlapping renderers");
        var latest = Candidate("LatestRoad");
        Static("PathPresentation","Replace",latest,next,cache);
        Check(old.activeSelf && !next.activeSelf,"Repeated replacement lost visible fallback");
        var presentation = latest.GetComponent(Type("PathPresentation"));
        int steps=0;
        while ((bool)Field(presentation,"pending") && steps++<80)
        {
            Check(old.activeSelf && latest.GetComponent<MeshRenderer>().forceRenderingOff,"Road replacement exposed a blank or overlapping frame");
            foreach (var renderer in latest.GetComponentsInChildren<Renderer>())
                Check(renderer.forceRenderingOff,"Newly generated segment overlapped visible fallback");
            yield return null;
        }
        Check(!(bool)Field(presentation,"pending"),"Road replacement never committed");
        Check(!old.activeSelf && !latest.GetComponent<MeshRenderer>().forceRenderingOff,"Completed road failed atomic presentation swap");
        foreach (var renderer in latest.GetComponentsInChildren<Renderer>())
            Check(!renderer.forceRenderingOff,"Completed segment remained hidden");
        var canceled = Candidate("CanceledRoad");
        Static("PathPresentation","Replace",canceled,latest,cache);
        canceled.SetActive(false);
        Check(!latest.activeSelf,"Unloaded pending road left fallback visible");
        yield return null;
        Check(latest == null,"Unloaded pending road leaked fallback");
        Object.Destroy(canceled); Call(cache,"Clear"); Object.Destroy(camera.gameObject);
        yield return null;
        results.Add("Road presentation: old road retained during segmented generation; no overlap, repeated replacement and unload cleanup passed");
    }
    static IEnumerator RoadSegments()
    {
        foreach (var existing in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) Object.DestroyImmediate(existing.gameObject);
        var camera = new GameObject("RoadCamera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,10,0);
        var parent = new GameObject("RoadSections");
        object spline = Static("RoadPath", "BuildSpline", parent,
            new List<Vector3> { Vector3.zero, new Vector3(256,0,0), new Vector3(512,0,0) }, false);
        var component = parent.AddComponent(Type("SplinePartLod"));
        int builds = 0;
        var factory = typeof(LodQA).GetMethod("SegmentFactory", BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(Type("DrawLod")).Invoke(null, new object[] { parent, (Action)(() => builds++) });
        Call(component, "Configure", spline, 8f, 0f, 0f, false, null, 0d, 1d, factory);
        var segments = (IList)Field(component, "segments");
        Check(segments.Count == 4, "512m road must have four fixed intervals");
        for (int i = 0; i < 24; i++) yield return null;
        var first = (GameObject)Field(segments[0], "shown");
        Check(first != null, "Near road interval was not generated");
        int initialBuilds = builds;
        for (int i = 0; i < 12; i++) yield return null;
        Check(builds == initialBuilds, "Stationary camera regenerated road intervals");
        parent.SetActive(false);
        camera.transform.position = new Vector3(0,5000,0);
        parent.SetActive(true);
        for (int i = 0; i < 24; i++) yield return null;
        Check(Field(segments[0], "shown") == null && !first.activeSelf, "Road worker failed to resume after parking");
        camera.transform.position = new Vector3(0,10,0);
        for (int i = 0; i < 24; i++) yield return null;
        Check(ReferenceEquals(first, Field(segments[0], "shown")), "Returning camera failed to reuse road interval");
        Check((double)Field(segments[0], "from") == 0d && (double)Field(segments[0], "to") == .25d,
            "Camera moved the road interval boundary");
        Object.Destroy(parent); Object.Destroy(camera.gameObject);
        yield return null;
        results.Add("Road intervals: fixed boundaries, stationary reuse, disable/resume and cache return");
    }
    static void StableSelection()
    {
        var type = Type("StableSample`1").MakeGenericType(typeof(int));
        int[] Select(int capacity, bool reverse) {
            object sample = Activator.CreateInstance(type, capacity);
            for (int i = 0; i < 10000; i++) { int value = reverse ? 9999-i : i; Call(sample, "Add", (ulong)((value*7919)%10000), value); }
            return (int[])Call(sample, "ToArray");
        }
        int[] a = Select(50,false), b = Select(50,true), c = Select(100,false);
        for (int i = 0; i < a.Length; i++) Check(a[i] == b[i] && a[i] == c[i], "Stable thinning changed with traversal order or budget");
        results.Add("Stable thinning: 10,000 candidates; order-independent and nested subsets");
    }
    static IEnumerator RoadSegmentsCameraMoves()
    {
        var camera = new GameObject("MovingRoadCamera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,10,0);
        var parent = new GameObject("MovingRoadSections");
        object spline = Static("RoadPath", "BuildSpline", parent,
            new List<Vector3> { Vector3.zero, new Vector3(256,0,0), new Vector3(512,0,0) }, false);
        var component = parent.AddComponent(Type("SplinePartLod"));
        int builds = 0;
        var factory = typeof(LodQA).GetMethod("SegmentFactory", BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(Type("DrawLod")).Invoke(null, new object[] { parent, (Action)(() => {
                builds++;
                camera.transform.position = new Vector3(0,5000,0);
            }) });
        Call(component,"Configure",spline,8f,0f,0f,false,null,0d,1d,factory);
        for (int i=0;i<30;i++) yield return null;
        Check(builds == 1,"Camera moved away during generation but obsolete detailed road intervals were still built");
        foreach (var segment in (IList)Field(component,"segments"))
            Check((int)Field(segment,"lod") == 1,"Road interval failed to converge to current camera LOD");
        Object.Destroy(parent); Object.Destroy(camera.gameObject);
        yield return null;
        results.Add("Moving road generation: pending intervals use current camera and avoid obsolete detail builds");
    }
    static void ProjectionAndResidency()
    {
        var camera = new GameObject("Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,600,-600); camera.transform.rotation = Quaternion.Euler(45,0,0);
        object state = Activator.CreateInstance(Type("LodViewState"));
        Check((bool)Call(state,"Update",camera,4f), "Initial view not evaluated");
        Check(!(bool)Call(state,"Update",camera,4f), "Stationary view evaluated repeatedly");
        camera.fieldOfView = 35;
        Check((bool)Call(state,"Update",camera,4f), "FOV-only change was ignored");
        object distanceState = Activator.CreateInstance(Type("LodViewState"));
        Check((bool)Call(distanceState,"UpdatePosition",camera,4f),"Initial distance LOD not evaluated");
        camera.transform.rotation = Quaternion.Euler(30,90,0); camera.fieldOfView = 45;
        Check(!(bool)Call(distanceState,"UpdatePosition",camera,4f),"Rotation/FOV triggered distance-only LOD scan");
        camera.transform.position += Vector3.right * 3f;
        Check(!(bool)Call(distanceState,"UpdatePosition",camera,4f),"Subthreshold movement triggered distance LOD scan");
        camera.transform.position += Vector3.right;
        Check((bool)Call(distanceState,"UpdatePosition",camera,4f),"Accumulated movement was ignored by distance LOD");
        camera.transform.rotation = Quaternion.Euler(45,0,0);
        object normal = Static("DrawLodEvaluator","EvaluateBySize",10f,500f,50f,Lod(1),true,true);
        Check((int)normal == 2, "Orthographic distance incorrectly alters projected size");
        object sets = Static("LodConfig","CalculateCellSets",Vector2Int.zero,Vector3.zero,camera.transform.position,camera.transform.forward,null,500f,100000,100000,new HashSet<Vector2Int>());
        var cells = (List<Vector2Int>)Field(sets,"farCells");
        Check(cells.Count > 0 && cells.Count <= 81,"Residency budget exceeded");
        Check(cells.Count == ((List<Vector2Int>)Field(sets,"dynamicCells")).Count,"Ownership still changes terrain detail");
        results.Add($"View: zoom-only and orthographic decisions passed; {cells.Count}/81 resident cells");
    }
    static IEnumerator CacheLifetime()
    {
        object cache = Activator.CreateInstance(Type("RenderObjectCache"), 16384L);
        GameObject Create(string key) {
            var go = new GameObject(key); var identity = go.AddComponent(Type("RenderCacheIdentity")); Set(identity,"key",key); return go;
        }
        var a = Create("a"); Call(cache,"Park",a);
        Check(ReferenceEquals(Call(cache,"Take","a"),a),"Cache failed to reuse exact object");
        Call(cache,"Park",a);
        for (int i = 0; i < 6; i++) Call(cache,"Park",Create("b"+i));
        yield return null;
        Check(a == null,"LRU did not destroy evicted object");
        Check((long)cache.GetType().GetProperty("Bytes").GetValue(cache) <= 16384,"Cache byte budget exceeded");
        Call(cache,"Clear"); yield return null;
        Check((long)Type("RenderObjectCache").GetProperty("TotalBytes").GetValue(null) == 0,"Global cache accounting leaked");
        results.Add("Cache: exact reuse, LRU eviction, destruction and global byte accounting passed");
    }
    static IEnumerator PendingRenderWork()
    {
        var go = new GameObject("PendingBuildingQA"); go.SetActive(false);
        var manager = go.AddComponent(Type("BuildingManager"));
        var data = Activator.CreateInstance(Type("GeneratedModels.BuildingSummary"));
        Set(data,"agent_id",71001); Set(data,"center_point",new List<object> { 100f,100f });
        var request = Activator.CreateInstance(Type("BuildingManager").GetNestedType("DeferredDetailRequest",BindingFlags.NonPublic),true);
        Set(request,"data",data); Set(request,"drawLod",Lod(2));
        var queue = (IDictionary)Field(manager,"deferredDetailBuildings"); queue[71001] = request;
        ((IDictionary)Field(manager,"buildingDataCache"))[71001] = data;
        var worker = (Coroutine)Static("RenderWorkScheduler","Start",host,Call(manager,"DetailGenerationWorker"),"QA.PendingBuilding");
        for(int i=0;i<10;i++) yield return null;
        Check(queue.Count == 1,"Building request lost before its cell finished loading");
        Static("RenderWorkScheduler","Stop",host,worker);
        ((HashSet<Vector2Int>)Field(manager,"loadedCells")).Add(Vector2Int.zero);
        Check((bool)Call(manager,"IsDeferredCellReady",data),"Loaded cell did not release pending building");
        foreach (var existing in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) Object.DestroyImmediate(existing.gameObject);
        var camera = new GameObject("DeferredBuildingCamera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,5000,0);
        var latest = Activator.CreateInstance(Type("GeneratedModels.BuildingSummary"));
        Set(latest,"agent_id",71001); Set(latest,"center_point",new List<object> {100f,100f}); Set(latest,"version",2);
        ((IDictionary)Field(manager,"buildingDataCache"))[71001] = latest;
        ((IDictionary)Field(manager,"buildingBoundsRadius"))[71001] = 3f;
        var far = new GameObject("DeferredFarBuilding"); far.transform.position = new Vector3(100,0,100);
        Set(Static("DrawLodInfo","Ensure",far),"drawLod",Lod(1));
        Set(far.AddComponent(Type("RenderCacheIdentity")),"key",Call(manager,"BuildingRenderKey",latest,Lod(1)));
        ((IDictionary)Field(manager,"buildingObjectsCache"))[71001] = far;
        Set(manager,"maxDetailedBuildingObjects",0);
        worker = (Coroutine)Static("RenderWorkScheduler","Start",host,Call(manager,"DetailGenerationWorker"),"QA.StaleBuilding");
        for(int i=0;i<10;i++) yield return null;
        Check(queue.Count == 0,"Obsolete detail request remained queued after moving away");
        Check(ReferenceEquals(((IDictionary)Field(manager,"buildingObjectsCache"))[71001],far),"Obsolete detail request replaced an already-correct far object");
        Check((int)((IDictionary)Field(manager,"buildingVersionCache"))[71001] == 2,"Deferred generation used stale building data");
        Static("RenderWorkScheduler","Stop",host,worker);
        Object.Destroy(far); Object.Destroy(camera.gameObject);
        Object.Destroy(go);

        go = new GameObject("PendingPathQA"); go.SetActive(false);
        manager = go.AddComponent(Type("PathManager"));
        var path = Activator.CreateInstance(Type("GeneratedModels.PathSummary"));
        Set(path,"path_id",72001); Set(path,"version",3); Set(path,"follow_terrain",true);
        ((IDictionary)Field(manager,"pathVersionCache"))[72001] = 3;
        Check(!(bool)Call(manager,"HasPathRepresentation",path),"Received version hid a road with no generated object");
        var representation = new GameObject("Path_72001");
        ((IDictionary)Field(manager,"pathObjectsCache"))[72001] = representation;
        Check((bool)Call(manager,"HasPathRepresentation",path),"Completed road was not recognized");
        var emptyCells = new List<Vector2Int>(); var emptySet = new HashSet<Vector2Int>();
        var lodUpdates = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Vector2Int),Type("LodTier")));
        var batch = (IEnumerator)Call(manager,"LoadMergedPathsAndNodesAndApplyCells",emptyCells,emptySet,emptySet,emptyCells,lodUpdates,emptyCells,emptyCells);
        Check(batch.MoveNext() && (bool)Field(manager,"isLodPathBatchActive"),"Road batch did not enter its loading state");
        (batch as IDisposable)?.Dispose();
        Check(!(bool)Field(manager,"isLodPathBatchActive"),"Canceled road batch left terrain follow-up blocked");
        Object.Destroy(representation); Object.Destroy(go);
        yield return null;
        results.Add("Pending render work: unloaded-cell building request retained; received-only road version cannot skip rendering");
    }
    static IEnumerator InterruptedPathResponse()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        int status = (int)Type("Constants").GetField("STATUS_PENDING").GetRawConstantValue();
        string json = "{\"paths\":[{\"path_id\":73001,\"version\":4,\"status\":"+status+",\"follow_terrain\":true,\"start_point\":[10,0,10],\"end_point\":[30,0,10]}],\"next_cursor\":null}";
        var server = System.Threading.Tasks.Task.Run(async () => {
            for(int i=0;i<2;i++) using(var client = await listener.AcceptTcpClientAsync())
            {
                var stream = client.GetStream(); var buffer = new byte[4096]; string headers = "";
                while (!headers.Contains("\r\n\r\n")) {
                    int count = await stream.ReadAsync(buffer,0,buffer.Length);
                    if(count == 0) throw new Exception("QA client closed before request");
                    headers += System.Text.Encoding.ASCII.GetString(buffer,0,count);
                }
                var body = System.Text.Encoding.UTF8.GetBytes(json);
                var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response,0,response.Length); await stream.WriteAsync(body,0,body.Length);
            }
        });
        var baseUrl = Type("ApiClient").GetField("BASE_URL"); var previous = baseUrl.GetValue(null);
        var go = new GameObject("InterruptedRoadQA"); go.SetActive(false);
        var manager = go.AddComponent(Type("PathManager"));
        baseUrl.SetValue(null,"http://127.0.0.1:"+port);
        try
        {
            for(int attempt=0;attempt<2;attempt++)
            {
                Set(manager,"isLodPathBatchActive",true);
                var cells = new List<Vector2Int> { Vector2Int.zero }; var set = new HashSet<Vector2Int>(cells);
                var load = (IEnumerator)Call(manager,"LoadMergedPaths",cells,set,set,null,null,null,null);
                try
                {
                    Check(load.MoveNext(),"Road did not request its API page");
                    yield return load.Current;
                    Check(load.MoveNext(),"Pending road was skipped on retry");
                    Check(((IDictionary)Field(manager,"pathDataCache")).Contains(73001),"Road response was not received");
                    Check(!((IDictionary)Field(manager,"pathVersionCache")).Contains(73001),"Road version committed before mesh generation");
                }
                finally { (load as IDisposable)?.Dispose(); }
            }
        }
        finally { baseUrl.SetValue(null,previous); listener.Stop(); Object.Destroy(go); }
        Check(server.IsCompleted && !server.IsFaulted,"Road fixture did not complete both requests");
        results.Add("Road HTTP regression: pending road received twice; interruption before generation leaves version uncommitted");
    }
    static IEnumerator TerrainFollowResume()
    {
        foreach (var existing in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) Object.DestroyImmediate(existing.gameObject);
        var camera = new GameObject("FollowCamera").AddComponent<Camera>(); camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,5000,0);
        var go = new GameObject("FollowPaths"); go.SetActive(false);
        var manager = go.AddComponent(Type("PathManager"));
        object Path(int id, float x, int version = 1)
        {
            var p = Activator.CreateInstance(Type("GeneratedModels.PathSummary"));
            Set(p,"path_id",id); Set(p,"version",version); Set(p,"follow_terrain",true);
            Set(p,"start_node_id",id+1000);
            var node = Activator.CreateInstance(Type("NodeData"));
            Set(node,"node_id",id+1000); Set(node,"position",new float[] {x,0f,10f});
            Set(node,"type",1 << (int)Type("Constants").GetField("INFRA_BIT_BASE").GetRawConstantValue());
            ((IDictionary)Field(manager,"nodeDataCache"))[id+1000] = node;
            Set(p,"status",(int)Type("Constants").GetField("STATUS_ACTIVE").GetRawConstantValue());
            Set(p,"start_point",new List<object> {x,0f,10f});
            Set(p,"end_point",new List<object> {x+20f,0f,10f});
            Call(manager,"SavePathToCache",p); Call(manager,"RegisterPathCells",p);
            return p;
        }
        float size = (float)Type("TerrainManager").GetProperty("CellSize").GetValue(null);
        var firstPath = Path(74001,10f);
        var secondPath = Path(74002,size*10+10f);
        var cells = new List<Vector2Int> {Vector2Int.zero,new Vector2Int(10,0)};
        var completed = (IDictionary)Field(manager,"terrainFollowCompletedPaths");
        var revisions = (IDictionary)Field(manager,"geometryRevisions");
        var objects = (IDictionary)Field(manager,"pathObjectsCache");
        var interrupted = (IEnumerator)Call(manager,"RebuildPathShapesForCellsCoroutine",cells);
        Check(interrupted.MoveNext() && completed.Count == 1,"Follow-up did not checkpoint its first road before yielding");
        var first = objects[74001]; int revision = (int)revisions[74001];
        (interrupted as IDisposable)?.Dispose();
        yield return (IEnumerator)Call(manager,"RebuildPathShapesForCellsCoroutine",cells);
        Check(completed.Count == 2,"Resumed follow-up lost its unfinished road");
        Check(ReferenceEquals(first,objects[74001]) && (int)revisions[74001] == revision,"Resumed follow-up regenerated its completed road");
        var completedNodes = (HashSet<int>)Field(manager,"terrainFollowCompletedNodes");
        Check(completedNodes.Count == 2,"Resumed road phase lost its pending intersections");
        yield return (IEnumerator)Call(manager,"RebuildPathShapesForCellsCoroutine",cells);
        Check(completedNodes.Count == 2 && (int)revisions[74001] == revision,"Repeated resume failed to preserve completed intersections and roads");

        ((GameObject)first).SetActive(false);
        var set = new HashSet<Vector2Int>(cells);
        yield return (IEnumerator)Call(manager,"RebuildCachedPathsForCells",cells,set,set,null);
        Check(ReferenceEquals(first,objects[74001]) && ((GameObject)first).activeSelf
            && (int)revisions[74001] == revision,"Cell re-entry invalidated an unchanged road's cached geometry");

        Call(manager,"BeginDeferredTerrainTilePathRebuilds");
        Call(manager,"OnTerrainTileReadyHandler",new Vector2Int(10,0),null);
        Check(completed.Contains(74001) && !completed.Contains(74002),"New terrain invalidated unrelated progress or kept affected progress");
        int secondRevision = (int)revisions[74002];
        yield return (IEnumerator)Call(manager,"RebuildPathShapesForCellsCoroutine",cells);
        Check((int)revisions[74002] > secondRevision && (int)revisions[74001] == revision,"Terrain change did not selectively rebuild affected roads");
        Path(74002,size*10+10f,2);
        Check(!completed.Contains(74002) && completed.Contains(74001),"Data change did not invalidate the affected road checkpoint");

        var nodeProgress = (HashSet<int>)Field(manager,"terrainFollowCompletedNodes");
        Set(firstPath,"start_node_id",75001); nodeProgress.Add(75001); nodeProgress.Add(75002);
        Call(manager,"OnTerrainTileReadyHandler",new Vector2Int(1,0),null);
        Check(!completed.Contains(74001) && !nodeProgress.Contains(75001) && nodeProgress.Contains(75002),"Neighbor terrain did not invalidate the related road and intersection only");
        Call(manager,"ClearTerrainFollowProgress");
        Check(completed.Count == 0 && nodeProgress.Count == 0,"Forced follow-up retained old checkpoints");
        Call(manager,"ClearAllCells");
        Object.Destroy(go); Object.Destroy(camera.gameObject);
        yield return null;
        results.Add("Terrain follow-up: partial cancellation resumes remaining roads; re-entry reuses geometry; new terrain/data selectively invalidates progress");
    }

    static IEnumerator TerrainNotificationConsumption()
    {
        var go = new GameObject("FollowNotificationQA"); go.SetActive(false);
        var loader = go.AddComponent(Type("CellDataLoader"));
        var paths = go.AddComponent(Type("PathManager"));
        var buildings = go.AddComponent(Type("BuildingManager"));
        Set(loader,"pathManager",paths); Set(loader,"buildingManager",buildings);
        var cells = new List<Vector2Int> {Vector2Int.zero};
        ((HashSet<Vector2Int>)Field(paths,"deferredTerrainReadyCells")).Add(Vector2Int.zero);
        ((HashSet<Vector2Int>)Field(buildings,"deferredTerrainReadyCells")).Add(Vector2Int.zero);
        var sets = Activator.CreateInstance(Type("LodCellSets"));
        Set(sets,"farCells",cells); Set(sets,"nearCells",new List<Vector2Int>()); Set(sets,"dynamicCells",new List<Vector2Int>());
        Set(loader,"pendingCellSets",sets);
        Call(loader,"CollectDeferredTerrainNotifications");
        Check(((HashSet<Vector2Int>)Field(loader,"pendingFollowUpCells")).Count == 1,"Duplicate manager notifications were not merged before follow-up");
        Check((bool)Field(paths,"deferTerrainTilePathRebuilds") && (bool)Field(buildings,"deferTerrainTileBuildingRebuilds"),"Collecting notifications ended the terrain follow-up barrier");
        yield return (IEnumerator)Call(loader,"TerrainFollowUpCoroutine");
        Check(((HashSet<Vector2Int>)Field(loader,"pendingFollowUpCells")).Count == 0
            && Field(loader,"terrainFollowUpCoroutine") == null,"Already consumed notifications scheduled a second follow-up pass");
        Check(((HashSet<Vector2Int>)Call(paths,"EndDeferredTerrainTilePathRebuilds")).Count == 0
            && ((HashSet<Vector2Int>)Call(buildings,"EndDeferredTerrainTileBuildingRebuilds")).Count == 0,"Processed notifications remained in managers");
        Object.Destroy(go);
        yield return null;
        results.Add("Terrain notifications: duplicated path/building ready cells consumed before work; no second pass after completion");
    }

    static IEnumerator MeshBakeLifetime()
    {
        var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
        Object.Destroy(primitive);
        var baked = (System.Threading.Tasks.Task)Static("MeshBakeService", "Bake", mesh);
        while (!baked.IsCompleted) yield return null;
        Check(!baked.IsFaulted, "Native mesh bake failed");
        var service = Type("MeshBakeService").GetField("instance", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var tasks = (IDictionary)Field(service,"tasks");
        var pending = new System.Threading.Tasks.TaskCompletionSource<bool>();
        tasks[mesh] = pending.Task;
        Static("MeshBakeService","Release",mesh);
        yield return null; yield return null;
        Check(mesh != null,"Mesh was destroyed while a native operation still owned it");
        pending.SetResult(true);
        yield return null; yield return null;
        Check(mesh == null,"Retired mesh remained after native operation completed");
        results.Add("Mesh lifetime: native bake passed; pending work retains and then releases retired mesh");
    }
    static IEnumerator Scheduler()
    {
        Static("RenderWorkScheduler","Reset");
        int completed = 0; var frames = new HashSet<int>(); bool disposed = false;
        IEnumerator Job() {
            for (int i = 0; i < 4; i++) {
                frames.Add(Time.frameCount); var timer = Stopwatch.StartNew();
                while (timer.Elapsed.TotalMilliseconds < 1.1) { }
                yield return null;
            }
            completed++;
        }
        for (int i = 0; i < 8; i++) Static("RenderWorkScheduler","Start",host,Job(),"QA");
        float end = Time.realtimeSinceStartup+10;
        while (completed < 8 && Time.realtimeSinceStartup < end) yield return null;
        Check(completed == 8,"Global scheduler starved a worker");
        Check(frames.Count >= 8,"Scheduler failed to spread aggregate work");
        IEnumerator Cancelable() { try { while (true) yield return null; } finally { disposed = true; } }
        Coroutine running = (Coroutine)Static("RenderWorkScheduler","Start",host,Cancelable(),"QA");
        for (int i = 0; i < 4; i++) yield return null;
        Static("RenderWorkScheduler","Stop",host,running); yield return null;
        Check(disposed,"Cancellation did not dispose nested work");
        Check((int)Type("RenderWorkScheduler").GetProperty("WaitingCount").GetValue(null) == 0,"Canceled scheduler ticket leaked");
        results.Add($"Scheduler: 8 workers / 32 steps across {frames.Count} frames; cancellation passed");
    }
    static IEnumerator SchedulerThroughput()
    {
        Static("RenderWorkScheduler", "Reset");
        object resume = Type("RenderWorkScheduler").GetField("Continue").GetValue(null);
        int oldFps = Application.targetFrameRate;
        Application.targetFrameRate = 60;
        int completed = 0;
        var progress = new int[4];
        var frames = new HashSet<int>();
        double maxWork = 0;
        int previousFrame = -1;
        double frameWork = 0;
        IEnumerator Work(int worker)
        {
            for (int i = 0; i < 220; i++)
            {
                if (previousFrame != Time.frameCount) { previousFrame = Time.frameCount; frameWork = 0; }
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalMilliseconds < .1) { }
                frameWork += watch.Elapsed.TotalMilliseconds;
                maxWork = Math.Max(maxWork, frameWork);
                frames.Add(Time.frameCount);
                progress[worker]++;
                yield return resume;
            }
            Check(Array.TrueForAll(progress, count => count > 0), "Ready worker starved behind cooperative work");
            completed++;
        }
        for (int i = 0; i < 4; i++) Static("RenderWorkScheduler", "Start", host, Work(i), "QA.Throughput");
        float deadline = Time.realtimeSinceStartup + 15;
        while (completed < 4 && Time.realtimeSinceStartup < deadline) yield return null;
        Check(completed == 4, "Cooperative work failed to finish");
        Check(frames.Count < 100, "Small items still pay one frame each");
        Check(frames.Count >= 20, "Cooperative work ignored the shared frame budget");
        Check(maxWork < 8, "Small cooperative steps exceeded frame budget excessively");

        bool resumed = false, nestedDisposed = false;
        int startFrame = 0, afterNull = 0;
        IEnumerator NativeWait()
        {
            startFrame = Time.frameCount;
            yield return null;
            afterNull = Time.frameCount;
            yield return new WaitUntil(() => resumed);
            yield return new WaitForSecondsRealtime(.05f);
            completed++;
        }
        Static("RenderWorkScheduler", "Start", host, NativeWait(), "QA.NativeWait");
        for (int i = 0; i < 5; i++) yield return null;
        Check(afterNull > startFrame, "Null yield failed to wait for next frame");
        Check(completed == 4, "CustomYieldInstruction resumed before its condition");
        resumed = true;
        deadline = Time.realtimeSinceStartup + 2;
        while (completed < 5 && Time.realtimeSinceStartup < deadline) yield return null;
        Check(completed == 5, "Native wait failed to resume");

        IEnumerator Child()
        {
            try { yield return new WaitForSecondsRealtime(30); }
            finally { nestedDisposed = true; }
        }
        IEnumerator Parent() { yield return Child(); }
        var handle = (Coroutine)Static("RenderWorkScheduler", "Start", host, Parent(), "QA.NestedCancel");
        for (int i = 0; i < 3; i++) yield return null;
        Static("RenderWorkScheduler", "Stop", host, handle);
        Check(nestedDisposed, "Cancel during native wait leaked nested iterator");
        Check((int)Type("RenderWorkScheduler").GetProperty("WaitingCount").GetValue(null) == 0, "Native wait cancellation left queue entries");
        bool selfDisposed = false;
        Coroutine selfHandle = null;
        IEnumerator SelfCancel()
        {
            try
            {
                Static("RenderWorkScheduler", "Stop", host, selfHandle);
                yield return resume;
            }
            finally { selfDisposed = true; }
        }
        selfHandle = (Coroutine)Static("RenderWorkScheduler", "Start", host, SelfCancel(), "QA.SelfCancel");
        for (int i = 0; i < 3; i++) yield return null;
        Check(selfDisposed, "Self cancellation did not release the executing iterator");
        Check((int)Type("RenderWorkScheduler").GetProperty("WaitingCount").GetValue(null) == 0, "Self cancellation left queue entries");
        Application.targetFrameRate = oldFps;
        results.Add($"Scheduler throughput: 880 items in {frames.Count} frames, max synthetic CPU/frame={maxWork:F2}ms; fair progress, native waits and nested cancellation passed");
    }
    static IEnumerator AtomicTerrain()
    {
        var go = new GameObject("TerrainManager"); go.SetActive(false);
        var manager = go.AddComponent(Type("TerrainManager"));
        Set(manager,"terrainSize",new Vector3(500,1200,500));
        var requestType = Type("TerrainManager").GetNestedType("HeightStreamRequest",BindingFlags.NonPublic);
        var pending = (IDictionary)Field(manager,"pendingStreamRequests");
        var tiles = (IDictionary)Field(manager,"terrainTiles");
        object Request(float height) {
            var request = Activator.CreateInstance(requestType,true);
            var heights = new float[513,513];
            for (int z=0;z<513;z++) for(int x=0;x<513;x++) heights[z,x]=height;
            Set(request,"heights",heights); return request;
        }
        Coroutine worker = (Coroutine)Static("RenderWorkScheduler","Start",host,Call(manager,"HeightStreamWorker"),"QA.Terrain");
        object first = Request(.1f); pending[Vector2Int.zero] = first;
        int steps=0;
        while (!(bool)Field(first,"done") && steps++<180) yield return null;
        Check((bool)Field(first,"succeeded"),"Initial terrain did not commit");
        var old = (Terrain)tiles[Vector2Int.zero]; var oldData = old.terrainData;
        var oldLayer = oldData.terrainLayers[0];
        float oldHeight = oldData.GetHeight(0,0);
        object second = Request(.2f); pending[Vector2Int.zero] = second;
        steps=0;
        while (!(bool)Field(second,"done") && steps++<180)
        {
            Check(ReferenceEquals(((Terrain)tiles[Vector2Int.zero]).terrainData,oldData), "Partial terrain exposed before commit");
            Check(oldData.GetHeight(0,0) == oldHeight, "Staged update modified visible terrain heights");
            yield return null;
        }
        Check((bool)Field(second,"succeeded"),"Updated terrain did not commit");
        Check(((Terrain)tiles[Vector2Int.zero]).terrainData.GetHeight(0,0)>oldHeight,"Updated surface not visible");
        yield return null;
        Check(oldLayer == null, "Replaced tile leaked its generated TerrainLayer");
        object third=Request(.3f); pending[new Vector2Int(1,0)]=third;
        yield return null; yield return null;
        Call(manager,"ClearAllCells");
        for(int i=0;i<15;i++) yield return null;
        Check(tiles.Count==0,"Canceled terrain reappeared after session clear");
        Static("RenderWorkScheduler","Stop",host,worker); Object.Destroy(go);
        results.Add("Terrain: staged update stayed invisible, atomic commit and clear cancellation passed");
    }
    static IEnumerator BuildingBatches()
    {
        Check(ReferenceEquals(Static("BuildingManager","GetFarBuildingMaterial"), Static("BuildingManager","GetFarBuildingMaterial")),
            "Far material must be shared across building roots for batching");
        var objects = new List<GameObject>();
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
        for(int i=0;i<10;i++) {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.transform.position=new Vector3(10+i*4,4,10); objects.Add(go);
            var renderer=go.GetComponent<MeshRenderer>(); renderer.sharedMaterial=material;
            var member=go.AddComponent(Type("FarBuildingMember")); Call(member,"Configure",renderer,go.GetComponent<MeshFilter>().sharedMesh);
        }
        for(int i=0;i<12;i++) yield return null;
        Check(objects[0].GetComponent<MeshRenderer>().forceRenderingOff,"Far buildings were not batched");
        objects[0].SetActive(false);
        Check(!objects[1].GetComponent<MeshRenderer>().forceRenderingOff,"Removing a member left stale combined geometry");
        for(int i=0;i<12;i++) yield return null;
        Check(objects[1].GetComponent<MeshRenderer>().forceRenderingOff,"Changed far batch did not rebuild");
        var changedRenderer = objects[1].GetComponent<MeshRenderer>();
        var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", Color.red);
        changedRenderer.SetPropertyBlock(block);
        Call(objects[1].GetComponent(Type("FarBuildingMember")), "LateUpdate");
        Check(!changedRenderer.forceRenderingOff, "Per-building display override left an incorrect summary");
        for(int i=0;i<12;i++) yield return null;
        Check(!changedRenderer.forceRenderingOff, "Property-block renderer was incorrectly batched");
        foreach(var go in objects) Object.Destroy(go); Object.Destroy(material);
        yield return null;
        results.Add("Building summary: 10 roots combined; removal falls back immediately and rebuilds");
    }
    static Material exteriorMaterial;
    static void ExteriorJob(List<CombineInstance> meshes, List<Material> materials, List<Mesh> temporary)
    {
        var mesh = new Mesh(); mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.forward }; mesh.triangles = new[] { 0,2,1 };
        temporary.Add(mesh);
        meshes.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.identity });
        meshes.Add(new CombineInstance { mesh = mesh, transform = Matrix4x4.Translate(Vector3.right*2) });
        materials.Add(exteriorMaterial); materials.Add(exteriorMaterial);
    }
    static IEnumerator BuildingExterior()
    {
        var parent = new GameObject("ExteriorQA");
        var component = parent.AddComponent(Type("BuildingExteriorRenderer"));
        var buildType = Type("BuildingExteriorRenderer").GetNestedType("Build");
        var jobs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(buildType));
        var job = Delegate.CreateDelegate(buildType, typeof(LodQA).GetMethod("ExteriorJob", BindingFlags.Static | BindingFlags.NonPublic));
        jobs.Add(job); jobs.Add(job);
        exteriorMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        Call(component, "Configure", jobs);
        int frames = 0;
        while ((int)Field(component,"completed") == 0 && frames++ < 60) yield return null;
        Check((int)Field(component,"completed") == 1, "Exterior jobs did not yield between buildings");
        parent.SetActive(false);
        for(int i=0;i<4;i++) yield return null;
        Check((int)Field(component,"completed") == 1,"Parked exterior continued generating");
        parent.SetActive(true);
        frames = 0;
        while ((int)Field(component,"completed") != 2 && frames++ < 60) yield return null;
        var parts = parent.GetComponentsInChildren<MeshFilter>();
        Check(parts.Length == 2 && parts[0].sharedMesh.subMeshCount == 1,"Exterior resume or material merge failed");
        var owned = parts[0].sharedMesh;
        Object.Destroy(parent); Object.Destroy(exteriorMaterial);
        yield return null; yield return null;
        Check(owned == null,"Exterior generated mesh leaked");
        results.Add("Building exterior: deferred jobs, material merge, park/resume and mesh release passed");
    }
    static void TerrainBenchmark()
    {
        var data=new TerrainData { heightmapResolution=513, size=new Vector3(500,1200,500), alphamapResolution=16 };
        var heights=new float[513,513];
        for(int z=0;z<513;z++) for(int x=0;x<513;x++) heights[z,x]=.2f+.03f*Mathf.Sin(x*.03f)*Mathf.Cos(z*.02f);
        var timer=Stopwatch.StartNew(); data.SetHeightsDelayLOD(0,0,heights); data.SyncHeightmap(); timer.Stop();
        double terrainMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var vertices=new Vector3[513*513]; var indices=new int[512*512*6];
        for(int z=0;z<513;z++) for(int x=0;x<513;x++) vertices[z*513+x]=new Vector3(x*500f/512,heights[z,x]*1200,z*500f/512);
        int at=0;
        for(int z=0;z<512;z++) for(int x=0;x<512;x++) { int i=z*513+x; indices[at++]=i; indices[at++]=i+513; indices[at++]=i+1; indices[at++]=i+1; indices[at++]=i+513; indices[at++]=i+514; }
        var mesh=new Mesh { indexFormat=UnityEngine.Rendering.IndexFormat.UInt32 }; mesh.vertices=vertices; mesh.triangles=indices; mesh.RecalculateNormals();
        timer.Stop();
        results.Add($"CPU prototype (single tile, no GPU FPS claim): native height upload+sync {terrainMs:F3}ms; full-grid custom mesh {timer.Elapsed.TotalMilliseconds:F3}ms, {mesh.vertexCount} vertices / {indices.Length/3} triangles");
        Object.Destroy(data); Object.Destroy(mesh);
    }
}
