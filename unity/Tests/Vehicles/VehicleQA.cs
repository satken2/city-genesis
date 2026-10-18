using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public static partial class VehicleQA
{
    static Assembly game;
    static readonly List<string> accessTimings = new List<string>();
    static string source;
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static object Get(object o, string name) => o.GetType().GetField(name, Flags).GetValue(o);
    static void Set(object o, string name, object value) => o.GetType().GetField(name, Flags).SetValue(o, value);
    static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Flags).Invoke(o, args);
    static object New(string type, params object[] args) => Activator.CreateInstance(game.GetType(type), args);
    static float Property(object o, string name) => Convert.ToSingle(o.GetType().GetProperty(name).GetValue(o));
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Near(float a, float b, float tolerance, string message) => Check(Mathf.Abs(a-b) <= tolerance, message + $" ({a} vs {b})");
    static object Line(params Vector3[] points) => New("VehiclePolyline", new object[] { points });
    static void Sample(object line, float s, bool forward, out Vector3 p, out Vector3 t)
    {
        object[] args = { s, forward, null, null };
        line.GetType().GetMethod("Sample").Invoke(line,args); p=(Vector3)args[2];t=(Vector3)args[3];
    }
    public static void Run()
    {
        string result = Path.Combine(Directory.GetCurrentDirectory(), "result.txt");
        try
        {
            var args=Environment.GetCommandLineArgs(); source=args[Array.IndexOf(args,"-vehicleSourceRoot")+1];
            AppDomain.CurrentDomain.AssemblyResolve += (o,e) =>
            {
                string name=new AssemblyName(e.Name).Name+".dll";
                foreach(string dir in new[]{"Temp/bin/Debug","Library/ScriptAssemblies","Assets/Plugins"})
                { string file=Path.Combine(source,dir,name); if(File.Exists(file))return Assembly.LoadFrom(file); }
                return null;
            };
            ModelImportSettings.EnableReadWriteAll();
            ModelPrefabGenerator.SyncAll();
            game=Assembly.LoadFrom(Path.Combine(source,"Temp/bin/Debug/Assembly-CSharp.dll"));
            if (Array.IndexOf(args, "-parkingOnly") >= 0)
            {
                StreetParking();
                File.WriteAllText(result, "PASS\nStreet parking: actual models, mix, counts, clearance, curves, grade, directions, lifecycle and API data\n" + string.Join("\n", accessTimings));
                EditorApplication.Exit(0); return;
            }
            if (Array.IndexOf(args, "-pedestrianModelsOnly") >= 0)
            {
                PedestrianModels(); PedestrianPoses(); accessTimings.Add(PedestrianDrawTiming());
                File.WriteAllText(result, "PASS\nPedestrian FBX topology, normals, budgets, scale, ground, walking poses and draw timing\n" + string.Join("\n", accessTimings));
                EditorApplication.Exit(0);
                return;
            }
            if (Array.IndexOf(args,"-transitOnly")>=0)
            {
                Geometry(); MotionContract(); TransitModels(); TransitPlacement(); TransitConsistGeometry(); TransitHighlightSelection();
                File.WriteAllText(result,"PASS\nPublic transport: motion, models, turnback, shared curved geometry, missing paths, vehicle selection and occlusion");
                EditorApplication.Exit(0); return;
            }
            if (Array.IndexOf(args,"-driveOnly")>=0)
            {
                Geometry(); Junction(); MotionContract(); TransitPlacement(); DriveCamera();
                File.WriteAllText(result,"PASS\nDrive: branch selection, one-way restrictions, protected lifetime, watched NPC car/train, transit start/turnback/loading, first/third-person camera, view handover and input cleanup");
                EditorApplication.Exit(0); return;
            }
            StreetParking();
            Geometry(); Budget(); PopulationCoverage(); Junction(); Congestion(); RoadsideExit(); MotionContract(); TransitModels(); TransitPlacement(); TransitConsistGeometry(); TransitHighlightSelection(); DriveCamera();
            PedestrianBands(); PedestrianModels(); PedestrianPoses(); accessTimings.Add(PedestrianDrawTiming());
            var report = new List<string>();
            foreach(int split in new[]{1,5}) report.Add(PedestrianNetwork(split,12,1200,false,true));
            // 端数枠は入口のある網で測る(閉じた網は補充を止めて既存の歩行者を残すので平均が下がらない)。入口を過ぎた歩行者は
            // 1周(遅い人で約970秒)してから入り、その超過を端数枠の繰り越しが後で相殺するので、平均が収まるまで長く歩かせる。
            report.Add(PedestrianNetwork(5,.2f,30000,false,true));
            // 歩行者は1.1m/sなので、入口のある1辺へ戻るまで800mの環状路で最大約730秒かかる。
            report.Add(PedestrianNetwork(5,12,2400,true,true));
            report.Add(PedestrianNetwork(5,12,600,true,false));
            foreach(int split in new[]{1,5,20}) report.Add(Network(split,12,1200,false));
            report.Add(Network(5,.2f,1200,false));
            report.Add(Network(5,12,1200,true));
            report.Add(Network(5,12,120,true,4,false));
            report.Add(Network(200,12,120,true));
            report.Add(Network(5,12,120,false,60));
            File.WriteAllText(result,"PASS\nGeometry, directions, lane boundaries, fractional budget\n"+string.Join("\n",report)+"\n"+string.Join("\n",accessTimings));
            EditorApplication.Exit(0);
        }
        catch(Exception ex) { File.WriteAllText(result,"FAIL\n"+ex); EditorApplication.Exit(1); }
    }
    static void Geometry()
    {
        var line=Line(new Vector3(-20,0,0),new Vector3(20,0,0));
        var intervals=(IList)Call(line,"InsideIntervals",Vector3.zero,10f);
        Check(intervals.Count==1,"Outside endpoints crossing circle missed");
        var interval=(Vector2)intervals[0]; Near(interval.x,10,.001f,"Circle entry");Near(interval.y,30,.001f,"Circle exit");
        var reentry=Line(new Vector3(-20,0,0),new Vector3(20,0,0),new Vector3(-20,0,0));
        Check(((IList)Call(reentry,"InsideIntervals",Vector3.zero,10f)).Count==2,"Reentry merged across outside section");
        foreach(float angle in new[]{0f,45f,90f,135f,180f})
        {
            Vector3 t0=Vector3.forward,t1=Quaternion.Euler(0,angle,0)*t0;
            Vector3 p0=new Vector3(0,2,-10),p1=new Vector3(8,3,0);
            var curve=game.GetType("VehiclePolyline").GetMethod("Connector").Invoke(null,new object[]{p0,t0,p1,t1,.4f});
            Sample(curve,0,true,out var start,out var startT);
            Sample(curve,Property(curve,"Length"),true,out var end,out var endT);
            Near(Vector3.Distance(start,p0),0,.001f,"Connector start"); Near(Vector3.Distance(end,p1),0,.001f,"Connector end");
            Check(Vector3.Angle(startT,t0)<12 && Vector3.Angle(endT,t1)<12,"Connector tangency");
        }
        var v=New("Vehicle"); Set(v,"speed",10f);Call(v,"SetPath",1,Line(Vector3.zero,Vector3.forward*5),true,-1f);
        Near((float)Call(v,"Advance",1f),.5f,.001f,"Residual time lost");
        foreach(int lanes in new[]{0,1,2,3,4,6}) foreach(int direction in new[]{0,1,2}) foreach(int hand in new[]{0,1})
        {
            var layout=New("RoadLaneLayout",24f,3f,.5f,0f,lanes,direction);
            int count=(int)Property(layout,"Count"); float outer=Property(layout,"Outer");
            for(int i=0;i<count;i++) Check(Mathf.Abs((float)Call(layout,"Center",i,hand))<outer,"Lane outside carriageway");
        }
        var four=New("RoadLaneLayout",24f,3f,.5f,0f,4,0);
        Near((float)Call(four,"Center",0,0),-6.5625f,.001f,"Outer lane");
        Near((float)Call(four,"Center",1,0),-2.1875f,.001f,"Inner lane");
        foreach(int dir in new[]{0,1,2}) foreach(bool fwd in new[]{false,true})
        {
            bool actual=(bool)game.GetType("VehicleManager").GetMethod("DirectionAllowsEntry").Invoke(null,new object[]{dir,fwd});
            Check(actual==(dir==0 || dir==(fwd?1:2)),"One-way entry");
        }
        RoadVehicleGeometry();
    }
    static void Budget()
    {
        foreach(float target in new[]{0f,.2f,.7f,1f,1.2f,12.3f,300f})
        {
            var budget=New("VehiclePopulationBudget");Call(budget,"SetTarget",target);
            double total=0;
            for(int i=0;i<1000;i++) { total+=Property(budget,"Desired");Call(budget,"Advance",12f); }
            Near((float)(total/1000),target,.002f,"Fractional budget bias");
            Call(budget,"SetTarget",0f);Near(Property(budget,"Desired"),0,0,"Budget reduction");
        }
    }
    static void PopulationCoverage()
    {
        foreach (bool pedestrian in new[] { false, true })
        {
            UnityEngine.Random.InitState(7301);
            string kind = pedestrian ? "Pedestrian" : "Vehicle";
            var master = new GameObject(kind + "CoverageMaster").AddComponent(game.GetType("MasterDataStore"));
            game.GetType("MasterDataStore").GetField("<Instance>k__BackingField", Flags).SetValue(null, master);
            ((IDictionary)Get(master, "pathMasterById"))[0] = New("PathMasterItem");
            var paths = new GameObject(kind + "CoveragePaths").AddComponent(game.GetType("PathManager"));
            var manager = new GameObject(kind + "Coverage").AddComponent(game.GetType(kind + "Manager"));
            Set(manager, "pathManager", paths); Set(manager, "renderRadius", 100f); Set(manager, "despawnMargin", 0f);
            if (!pedestrian) { Set(manager, "motionInFlight", true); Set(manager, "spawnBudgetMs", 1000f); }
            var constants = game.GetType("Constants");
            int street = (int)constants.GetField("PATH_STREET").GetRawConstantValue();
            int status = (int)constants.GetField("STATUS_ACTIVE").GetRawConstantValue();
            var segmentType = manager.GetType().GetNestedType(pedestrian ? "Walk" : "Road", BindingFlags.NonPublic);
            var active = (IList)Get(manager, "active");
            var data = new List<object>(); var lines = new List<object>();
            float speed = (float)Get(manager, pedestrian ? "walkSpeed" : "baseSpeed");
            float traffic = 20f * (float)Get(manager, "activeHoursPerDay") * 3600f * speed / 200f;
            for (int i = 0; i < 2; i++)
            {
                var path = New("GeneratedModels.PathSummary");
                Set(path, "path_id", i + 1); Set(path, "start_node_id", 10 + i * 2); Set(path, "end_node_id", 11 + i * 2);
                Set(path, "path_type", street); Set(path, "status", status);
                Set(path, "path_traffic", new Dictionary<string, float> { { pedestrian ? "0" : "10", traffic } });
                Call(paths, "SavePathToCache", path); data.Add(path);
                lines.Add(Line(new Vector3(-1000, 0, i * 20), new Vector3(1000, 0, i * 20)));
            }
            Action<float, int> commit = (x, count) =>
            {
                Vector3 center = new Vector3(x, 0, 0);
                var snapshot = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(int), segmentType));
                for (int i = 0; i < count; i++)
                {
                    var segment = Activator.CreateInstance(segmentType);
                    var intervals = (List<Vector2>)Call(lines[i], "InsideIntervals", center, 100f);
                    float length = 0; foreach (var interval in intervals) length += interval.y - interval.x;
                    Set(segment, "data", data[i]); Set(segment, "line", lines[i]); Set(segment, "intervals", intervals);
                    Set(segment, "insideLength", length); Set(segment, "scanCenter", center); Set(segment, "scanRadius", 100f);
                    if (pedestrian) Set(segment, "centered", true);
                    else Set(segment, "lanes", New("RoadLaneLayout", 24f, 0f, 0f, 0f, 4, 0));
                    snapshot.Add(i + 1, segment);
                }
                Call(manager, pedestrian ? "CommitWalks" : "CommitRoads", snapshot, center);
                if (!pedestrian) while ((bool)Get(manager, "seeding")) Call(manager, "FillNewCoverage");
            };
            Func<Dictionary<object, float>> positions = () =>
            {
                var result = new Dictionary<object, float>();
                foreach (var item in active) result.Add(item, (float)Get(item, "s"));
                return result;
            };
            Func<Dictionary<object, float>, int> additions = before =>
            {
                int added = 0;
                foreach (var item in active)
                    if (before.TryGetValue(item, out float s)) Near((float)Get(item, "s"), s, .001f, kind + " overlap moved during seeding");
                    else
                    {
                        added++;
                        Check((float)Get(item, "age") >= (float)Get(manager, "fadeDuration"), kind + " new area fades in from empty");
                        if (!pedestrian)
                        {
                            Near(((GameObject)Get(item, "go")).transform.localScale.x, 1f, .001f, "Seeded car is invisible");
                            Near((float)Get(item, "spawnBlendRemaining"), 0, 0, "Seeded car starts on the shoulder");
                        }
                    }
                return added;
            };
            game.GetType("TrafficClock").GetMethod("SetPaused").Invoke(null, new object[] { true });
            commit(0, 1);
            Check(active.Count >= 19 && active.Count <= 20, kind + " initial density waits for inflow");
            var before = positions(); commit(0, 1);
            Check(additions(before) == 0, kind + " stationary view keeps spawning");
            before = positions(); commit(50, 1);
            Check(additions(before) >= 4, kind + " ordinary pan leaves new coverage empty");
            foreach (var item in active)
                if (!before.ContainsKey(item)) Check((float)Get(item, "s") >= 1100f, kind + " pan seeds the old overlap");
            int smallPanAdds = 0;
            for (int i = 1; i <= 10; i++) { before = positions(); commit(50 + i * 5, 1); smallPanAdds += additions(before); }
            Check(smallPanAdds >= 4 && smallPanAdds <= 6, kind + " small pans lose fractional density");
            commit(600, 1);
            Check(active.Count >= 19 && active.Count <= 21, kind + " jump retains offscreen population or fills slowly");
            var quarters = new HashSet<int>();
            foreach (var item in active) quarters.Add(Mathf.Clamp((int)(((float)Get(item, "s") - 1500f) / 50f), 0, 3));
            Check(quarters.Count == 4, kind + " new area is clustered at its boundary");
            before = positions(); commit(600, 2);
            Check(additions(before) >= 18, kind + " late-loaded road waits for an entrance");
            before = positions(); commit(600, 2);
            Check(additions(before) == 0, kind + " repeated loaded snapshot duplicates population");
            Call(manager, pedestrian ? "ClearPedestrians" : "ClearVehicles");
            Set(manager, pedestrian ? "maxActivePedestrians" : "maxActiveVehicles", 5);
            if (!pedestrian) Set(manager, "motionInFlight", true);
            commit(0, 2);
            Check(active.Count <= 5, kind + " coverage bypasses the hard cap");
            Call(manager, pedestrian ? "ClearPedestrians" : "ClearVehicles");
            game.GetType("TrafficClock").GetMethod("SetPaused").Invoke(null, new object[] { false });
            UnityEngine.Object.DestroyImmediate(manager.gameObject); UnityEngine.Object.DestroyImmediate(paths.gameObject);
            UnityEngine.Object.DestroyImmediate(master.gameObject);
            accessTimings.Add(kind + " coverage: initial, paused, stationary, pan, small pans, jump, streaming, cap");
        }
    }
    static void TransitPlacement()
    {
        var models=new GameObject("PlacementModels").AddComponent(game.GetType("ModelManager"));
        game.GetType("ModelManager").GetProperty("Instance").SetValue(null,models);
        var manager=new GameObject("TransitPlacement").AddComponent(game.GetType("TransitVehicleManager"));
        var stepType=game.GetType("GeneratedModels.TransitMotionStep");
        var journeyType=game.GetType("GeneratedModels.TransitJourney");
        var journeys=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(journeyType));
        foreach(bool returning in new[]{false,true})
        {
            var steps=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(stepType));
            var step=Activator.CreateInstance(stepType);
            Set(step,"path_id",1);Set(step,"from_fraction",returning?.96f:.04f);Set(step,"to_fraction",returning?.04f:.96f);
            Set(step,"length_m",460f);Set(step,"start_speed_mps",10f);Set(step,"end_speed_mps",10f);
            Set(step,"duration_seconds",46.0);Set(step,"dwell_seconds",4.0);steps.Add(step);
            var journey=Activator.CreateInstance(journeyType);Set(journey,"steps",steps);Set(journey,"turnback",true);Set(journey,"reversed",returning);journeys.Add(journey);
        }
        var data=New("GeneratedModels.TransitLineSummary");
        Set(data,"line_agent_id",123);Set(data,"path_variant",1002);Set(data,"cars_per_train",2);Set(data,"vehicle_count",2);Set(data,"vehicle_model",2);
        Set(data,"length_per_vehicle",20f);Set(data,"status",(int)game.GetType("Constants").GetField("STATUS_ACTIVE").GetRawConstantValue());Set(data,"journeys",journeys);
        var sourceList=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(data.GetType()));sourceList.Add(data);
        Call(manager,"RebuildLines",sourceList);
        var lines=(IList)Get(manager,"lines");Check(lines.Count==1,"Operating transit line missing");
        var path=New("GeneratedModels.PathSummary");Set(path,"path_id",1);Set(path,"travel_direction",0);
        var routeGeometry=New("TransitRouteGeometry",new object[]{null});
        Set(manager,"routeGeometry",routeGeometry);
        var g=Activator.CreateInstance(routeGeometry.GetType().GetNestedType("Geometry",BindingFlags.NonPublic));
        Set(g,"data",path);Set(g,"full",Line(Vector3.zero,Vector3.forward*500));Set(g,"trimStart",0f);Set(g,"trimEnd",500f);Set(g,"rail",true);
        ((IDictionary)Get(routeGeometry,"geometry")).Add(1,g);
        var previous=new Dictionary<string,Vector3>();
        foreach(double time in new[]{0.0,25.0,46.0,49.9,50.0,75.0,99.9,100.0})
        {
            Get(manager,"used").GetType().GetMethod("Clear").Invoke(Get(manager,"used"),null);
            Set(manager,"clock",time);Call(manager,"DrawLine",lines[0],Vector3.forward*250);Call(manager,"HideUnused",false);
            var visible=(IDictionary)Get(manager,"visible");Check(visible.Count==2,"Consist disappears at terminal/time "+time);
            var positions=new List<Vector3>();
            foreach(DictionaryEntry entry in visible) Check(((GameObject)entry.Value).name == "20:2","Consist ignores the line's vehicle model or mixes at turnback");
            foreach(DictionaryEntry entry in visible)
            {
                var position=((GameObject)entry.Value).transform.position;positions.Add(position);
                Check(position.z>=0 && position.z<=500,"Car leaves platform/path");
                if(time==50.0)Near(Vector3.Distance(previous[(string)entry.Key],position),0,.01f,"Turnback teleports physical car");
                previous[(string)entry.Key]=position;
            }
            Near(Vector3.Distance(positions[0],positions[1]),20,.01f,"Consist spacing incorrect");
        }
        var motion=Get(lines[0],"motion");
        foreach (bool forward in new[] { true, false })
        {
            object[] timeArgs={motion,1,.5f,forward,0.0};
            Check((bool)Call(manager,"TryFindDriveTime",timeArgs),"Selected position cannot be mapped onto route clock");
            Near(Convert.ToSingle(timeArgs[4]),forward?23:73,.001f,"Route start confuses outward and return journey");
        }
        object[] invalidStart={motion,999,.5f,true,0.0};
        Check(!(bool)Call(manager,"TryFindDriveTime",invalidStart),"Ride accepts a track outside its route");
        manager.GetType().GetProperty("DrivenLineId").SetValue(manager,123);
        Set(manager,"clock",49.999); Call(manager,"DrawLine",lines[0],Vector3.zero);
        Vector3 cab=(Vector3)manager.GetType().GetProperty("DrivePosition").GetValue(manager);
        Vector3 heading=(Vector3)manager.GetType().GetProperty("DriveDirection").GetValue(manager);
        Set(manager,"clock",50.0); Call(manager,"DrawLine",lines[0],Vector3.zero);
        Near(Vector3.Distance(cab,(Vector3)manager.GetType().GetProperty("DrivePosition").GetValue(manager)),20,.02f,"Ride does not move to the new leading car at turnback");
        Check(Vector3.Dot(heading,(Vector3)manager.GetType().GetProperty("DriveDirection").GetValue(manager))<0,"Ride keeps the old heading after turnback");
        cab=(Vector3)manager.GetType().GetProperty("DrivePosition").GetValue(manager);
        ((IDictionary)Get(routeGeometry,"geometry")).Clear();
        Set(manager,"driveDelta",.5f); Set(manager,"clock",50.5); Call(manager,"DrawLine",lines[0],Vector3.zero);
        Check((bool)manager.GetType().GetProperty("DriveWaiting").GetValue(manager),"Ride does not wait for missing track geometry");
        Near(Convert.ToSingle(Get(manager,"driveOffset")),-.5f,.001f,"Ride clock runs through unloaded track");
        Near(Vector3.Distance(cab,(Vector3)manager.GetType().GetProperty("DrivePosition").GetValue(manager)),0,.02f,"Missing geometry moves the ride camera");
        ((IDictionary)Get(routeGeometry,"geometry")).Add(1,g);
        Call(manager,"StopDrive");
        Set(manager,"clock",25.0);
        Check(!(bool)Call(manager,"Watch",123,1),"Watching accepts a train the line does not run");
        Check((bool)Call(manager,"Watch",123,0),"Running train cannot be watched");
        Near(Convert.ToSingle(Get(manager,"driveOffset")),0,.0001f,"Watching shifts the train clock");
        Vector3 watchStart=(Vector3)manager.GetType().GetProperty("DrivePosition").GetValue(manager);
        Call(manager,"DrawLine",lines[0],Vector3.zero);
        Near(Vector3.Distance(watchStart,(Vector3)manager.GetType().GetProperty("DrivePosition").GetValue(manager)),0,.02f,"Watched train jumps when the ride starts");
        Near(Property(manager,"DriveCarLength"),20,.001f,"Ride does not know the leading car length");
        Call(manager,"StopDrive");
        // ハイライトも同じ補間を使い、パス途中の停止中心と曲線の高さを維持する。
        var curved=Line(Vector3.zero,new Vector3(100,25,100),new Vector3(0,50,300),Vector3.forward*500);
        Set(g,"full",curved); Set(g,"trimEnd",Property(curved,"Length"));
        object[] sampleArgs={lines[0],230.0,230.0,true,null,null};
        Check((bool)Call(routeGeometry,"Sample",sampleArgs),"Shared route rejects loaded curve");
        Sample(curved,Property(curved,"Length")*.5f,true,out var expected,out _);
        expected.y+=(float)game.GetType("RoadPartHeights").GetField("TRACK_ABOVE_SPLINE").GetRawConstantValue()+.2f;
        Near(Vector3.Distance((Vector3)sampleArgs[4],expected),0,.01f,"Highlight cuts across curved/elevated track");
        // 跨座式モノレールの車両は、鉄道のレール頭頂の代わりに軌道桁の天端(軌道パーツの原点から1.08m)に接地する。
        Set(path,"path_type",(int)game.GetType("Constants").GetField("PATH_MONORAIL").GetRawConstantValue());
        Check((bool)Call(routeGeometry,"Sample",sampleArgs),"Monorail route rejects loaded curve");
        float beamTop=(float)game.GetType("RoadPartHeights").GetField("MONORAIL_BEAM_TOP_ABOVE_TRACK").GetRawConstantValue();
        Near(beamTop,1.08f,.001f,"Monorail guideway top moved away from the model");
        Near(((Vector3)sampleArgs[4]).y,expected.y-.2f+beamTop,.01f,"Monorail car is not on the guideway top");
        Set(path,"path_type",0);
        Set(manager,"clock",23.0); Call(manager,"DrawLine",lines[0],expected);
        CheckCouplings((Vector3[])Get(lines[0],"positions"),(Vector3[])Get(lines[0],"directions"),20);
        foreach (DictionaryEntry entry in (IDictionary)Get(manager,"visible"))
        {
            var car = ((GameObject)entry.Value).transform;
            var positions = (Vector3[])Get(lines[0],"positions");
            Check(Vector3.Distance(car.position,positions[0])<.001f || Vector3.Distance(car.position,positions[1])<.001f,
                "Rail renderer does not use rigid consist poses");
        }
        ((IDictionary)Get(routeGeometry,"geometry")).Clear();
        Check(!(bool)Call(routeGeometry,"Sample",sampleArgs),"Missing path was connected by a straight line");
        ((IDictionary)Get(routeGeometry,"geometry")).Add(1,g);
        var visibleCars=(IDictionary)Get(manager,"visible");
        GameObject pickedCar=null;
        foreach(DictionaryEntry entry in visibleCars) { pickedCar=(GameObject)entry.Value; break; }
        var bounds=pickedCar.GetComponentInChildren<Renderer>().bounds;
        var ray=new Ray(bounds.center+Vector3.up*100,Vector3.down);
        Physics.SyncTransforms();
        object[] pickArgs={ray,0};
        Check((bool)Call(manager,"TryPick",pickArgs) && (int)pickArgs[1]==123,"Vehicle selection loses line identity when overlay is absent");
        object[] trainArgs={ray,0,0,0f};
        Check((bool)Call(manager,"TryPickTrain",trainArgs) && (int)trainArgs[1]==123 && (int)trainArgs[2]==0,"Vehicle selection loses the train within its line");
        var blocker=GameObject.CreatePrimitive(PrimitiveType.Cube);
        blocker.transform.position=bounds.center+Vector3.up*20; blocker.transform.localScale=Vector3.one*10;
        Physics.SyncTransforms();
        Check(!(bool)Call(manager,"TryPick",pickArgs),"Vehicle behind a building is selected");
        UnityEngine.Object.DestroyImmediate(blocker);
        Call(manager,"HideUnused",true);
        Check(!(bool)Call(manager,"TryPick",pickArgs),"Pooled vehicle is selectable");
        // パス管理が無くても、描画する編成が無い更新では道路形状にアクセスしない。
        Set(data,"vehicle_count",0);
        Call(manager,"RebuildLines",sourceList);
        Call(manager,"RefreshGeometry",Vector3.zero);
        Check(((IDictionary)Get(routeGeometry,"geometry")).Count==0,"Unavailable fleet retains geometry");
        Set(data,"vehicle_count",2);
        Call(manager,"RebuildLines",sourceList);
        Check(lines.Count==1,"Restored fleet does not resume");
        sourceList.Clear();
        Call(manager,"RebuildLines",sourceList);
        Call(manager,"RefreshGeometry",Vector3.zero);
        Check(lines.Count==0,"Removed route retained");
        FreightConsists(manager, data, sourceList, journeys, routeGeometry, g, path);
        BusRoadPlacement(manager, data, sourceList, routeGeometry, g);
        UnityEngine.Object.DestroyImmediate(manager.gameObject);UnityEngine.Object.DestroyImmediate(models.gameObject);
    }

    static void TransitHighlightSelection()
    {
        // 無効なGOでAwakeを遅らせ、描画リソースと独立して画面上の選択契約を検査する。
        var root=new GameObject("HighlightPickQA"); root.SetActive(false);
        var overlay=root.AddComponent(game.GetType("TransitOverlayManager"));
        var cameraObject=new GameObject("HighlightCameraQA"); var cam=cameraObject.AddComponent<Camera>();
        cameraObject.transform.position=new Vector3(0,20,-40); cameraObject.transform.LookAt(Vector3.zero);
        var segmentType=overlay.GetType().GetNestedType("Segment",BindingFlags.NonPublic);
        var segments=(IList)Get(overlay,"segments");
        foreach(int id in new[]{123,456})
        {
            var segment=Activator.CreateInstance(segmentType);
            Set(segment,"a",Vector3.left*10); Set(segment,"b",Vector3.right*10); Set(segment,"id",id); segments.Add(segment);
        }
        Vector2 cursor=cam.WorldToScreenPoint(Vector3.zero);
        var picked=(IList)Call(overlay,"Pick",cam,cursor);
        Check(picked.Count==2 && (int)picked[0]==123 && (int)picked[1]==456,"Overlapping routes do not offer both candidates");
        Check(((IList)Call(overlay,"Pick",cam,cursor+Vector2.up*20)).Count==0,"Route selection captures unrelated ground");
        cameraObject.transform.rotation=Quaternion.LookRotation(Vector3.back);
        Check(((IList)Call(overlay,"Pick",cam,cursor)).Count==0,"Route behind camera is selectable");
        segments.Clear();
        cameraObject.transform.LookAt(Vector3.zero);
        Check(((IList)Call(overlay,"Pick",cam,cursor)).Count==0,"Cleared overlay remains selectable");
        UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(cameraObject);
    }

    static void VehicleProps()
    {
        var paths=new List<string>();
        foreach(int type in new[]{10,11,12,13})
            for(int variant=0;variant<(type==10?10:type==11?4:2);variant++)paths.Add($"{type}/{variant}");
        paths.Add("14/0");paths.Add("14/middle");
        foreach(int type in new[]{20,21,22,23})
        {
            paths.Add($"{type}/0");paths.Add($"{type}/1");
            string middle=type==22?"wagon":"middle";
            paths.Add($"{type}/{middle}");paths.Add($"{type}/{middle}_1");
        }
        var ids=new List<int>();
        for(int index=0;index<paths.Count;index++) ids.Add(index<18?350+index:370+index-18);
        paths.Add("20/2");ids.Add(388);paths.Add("20/middle_2");ids.Add(389);
        paths.Add("21/2");ids.Add(390);paths.Add("21/middle_2");ids.Add(391);
        paths.Add("23/2");ids.Add(392);paths.Add("23/middle_2");ids.Add(393);
        paths.Add("22/2");ids.Add(394);paths.Add("22/wagon_2");ids.Add(395);
        for(int index=0;index<paths.Count;index++)
        {
            int id=ids[index];
            var sourceModel=Resources.Load<GameObject>("Models/Vehicles/"+paths[index]);
            var prop=Resources.Load<GameObject>($"Models/Props/{id}");
            Check(sourceModel!=null && prop!=null,"Missing vehicle prop: "+id);
            var original=sourceModel.GetComponentInChildren<MeshFilter>(true).sharedMesh;
            var copy=prop.GetComponentInChildren<MeshFilter>(true).sharedMesh;
            Check(copy.isReadable && original.vertexCount==copy.vertexCount && original.triangles.Length==copy.triangles.Length,"Vehicle prop topology differs: "+id);
            Near(Vector3.Distance(original.bounds.size,copy.bounds.size),0,.002f,"Vehicle prop dimensions differ");
            var model=New("PropRenderModel",id);
            Check(((IList)Get(model,"normal")).Count>0 && ((IList)Get(model,"far")).Count>0,"Vehicle prop has no drawable parts: "+id);
            {
                var closeSource=Resources.Load<GameObject>("Models/Vehicles/"+paths[index]+"_close");
                var closeProp=Resources.Load<GameObject>($"Models/Props/{id}_close");
                Check(closeSource!=null && closeProp!=null,"Missing close vehicle prop: "+id);
                var a=closeSource.GetComponentInChildren<MeshFilter>(true).sharedMesh;
                var b=closeProp.GetComponentInChildren<MeshFilter>(true).sharedMesh;
                Check(a.vertexCount==b.vertexCount && a.triangles.Length==b.triangles.Length,"Close alias topology differs: "+id);
                Near(Vector3.Distance(a.bounds.size,b.bounds.size),0,.002f,"Close alias bounds differ");
            }
            {
                var source=Resources.Load<GameObject>("Models/Vehicles/"+paths[index]+"_far");
                var far=Resources.Load<GameObject>($"Models/Props/{id}_far");
                Check(source!=null && far!=null,"Missing authored vehicle far: "+id);
                var a=source.GetComponentInChildren<MeshFilter>(true).sharedMesh;
                var b=far.GetComponentInChildren<MeshFilter>(true).sharedMesh;
                Check(a.triangles.Length<=126 && a.triangles.Length==b.triangles.Length,"Far alias budget/topology");
                foreach(var item in (IList)Get(model,"far"))
                    Check(AssetDatabase.GetAssetPath((Mesh)Get(item,"mesh")).EndsWith($"/{id}_far.fbx"),"Prop far not selected");
            }
            Call(model,"Dispose");
        }
    }

    // 道路車両は全外観が接写・通常・遠景の3段階を持つ。閾値と参照先、遠景の三角形数、接写が通常と同じ配置寸法で
    // 遠景が通常の高さの範囲に収まることを確かめる。
    static void RoadLods(GameObject car, int type, int variant)
    {
        var lod=car.GetComponent<LODGroup>();
        Check(lod!=null,"Road vehicle LOD missing: "+type+"/"+variant);
        var levels=lod.GetLODs();
        Check(levels.Length==3 && levels[0].screenRelativeTransitionHeight==.25f && levels[1].screenRelativeTransitionHeight==.035f && levels[2].screenRelativeTransitionHeight==0,"Road vehicle LOD thresholds: "+type+"/"+variant);
        string[] suffix={"_close","","_far"};
        for(int i=0;i<3;i++)
            foreach(var renderer in levels[i].renderers)
                Check(AssetDatabase.GetAssetPath(renderer.GetComponent<MeshFilter>().sharedMesh).EndsWith($"/{type}/{variant}{suffix[i]}.fbx"),"Road LOD uses wrong asset: "+type+"/"+variant+suffix[i]);
        foreach(var renderer in levels[2].renderers)
            Check(renderer.GetComponent<MeshFilter>().sharedMesh.triangles.Length/3<=42,"Road far triangle budget: "+type+"/"+variant);
        var normal=levels[1].renderers[0].bounds;
        var close=levels[0].renderers[0].bounds;
        var far=levels[2].renderers[0].bounds;
        Check((normal.min-close.min).magnitude<.09f && (normal.max-close.max).magnitude<.09f,"Road LOD placement bounds differ: "+type+"/"+variant);
        Check(Mathf.Abs(far.min.y-normal.min.y)<.05f && far.max.y<=normal.max.y+.05f,"Road far height range: "+type+"/"+variant);
    }

    static void TransitModels()
    {
        var manager=new GameObject("ModelManagerQA").AddComponent(game.GetType("ModelManager"));
        game.GetType("ModelManager").GetProperty("Instance").SetValue(null,manager);
        var resolver=game.GetType("VehicleModelResolver");
        foreach(int vehicleType in new[]{10,11,12,13,14,20,21,22,23,24})
        {
            var variants=(IList)game.GetType("VehicleModelConfig").GetMethod("GetVariants").Invoke(null,new object[]{vehicleType});
            Check(variants.Count==(vehicleType==10?10:vehicleType==11?4:vehicleType==14||vehicleType==24?1:vehicleType==12||vehicleType==13?2:3),"Configured variant count differs for "+vehicleType);
            foreach(int variant in variants)
            {
                string path=$"Assets/Models/Vehicles/{vehicleType}/{variant}.fbx";
                var prefab=Resources.Load<GameObject>($"Models/Vehicles/{vehicleType}/{variant}");
                Check(prefab!=null,"Missing vehicle wrapper: "+path);
                Check(AssetDatabase.GetAssetPath(prefab.GetComponentInChildren<MeshFilter>(true).sharedMesh)==path,"Wrong FBX reference: "+path);
                if(vehicleType>13)continue;
                var car=(GameObject)resolver.GetMethod("CreateInstance").Invoke(null,new object[]{vehicleType,variant,null});
                car.SetActive(true);
                var renderers=car.GetComponentsInChildren<Renderer>();
                Check(renderers.Length>0,"Road vehicle falls back to an empty model");
                var bounds=renderers[0].bounds;
                foreach(var renderer in renderers)bounds.Encapsulate(renderer.bounds);
                Near(bounds.min.y,0,.02f,"Road vehicle floats");
                Check(bounds.size.z>3 && bounds.size.z<13 && bounds.size.x>1.4f && bounds.size.x<3,"Road vehicle dimensions");
                RoadLods(car,vehicleType,variant);
                UnityEngine.Object.DestroyImmediate(car);
            }
            foreach(int seed in new[]{0,1,-1,int.MinValue})
                Check((int)resolver.GetMethod("PickVariantId").Invoke(null,new object[]{vehicleType,seed})>=0,"Negative seed gives invalid variant");
        }
        foreach(int id in new[]{308,309})
        {
            var model=New("PropRenderModel",id);
            var parts=(IList)Get(model,"far");
            Check(parts.Count>0,"Monorail far missing");
            foreach(var part in parts)
                Check(AssetDatabase.GetAssetPath((Mesh)Get(part,"mesh")).EndsWith($"/{id}_far.fbx"),"Monorail authored far not selected");
            Call(model,"Dispose");
        }
        VehicleProps();
        Check((int)game.GetType("VehicleModelConfig").GetMethod("GetTransitVehicleType").Invoke(null,new object[]{1003})==14,"LRT model mapping missing");
        Check((int)game.GetType("VehicleModelConfig").GetMethod("GetTransitVehicleType").Invoke(null,new object[]{1004})==24,"Monorail model mapping missing");
        // 走行用のモノレール車両24は自由配置の308/309と同じFBXを置く。原本はprops.blendなので、片方だけ更新されていないことを確かめる。
        foreach(var pair in new[]{("0","308"),("middle","309")})
        foreach(string suffix in new[]{"","_close","_far"})
            Check(System.Linq.Enumerable.SequenceEqual(File.ReadAllBytes($"Assets/Models/Vehicles/24/{pair.Item1}{suffix}.fbx"),File.ReadAllBytes($"Assets/Models/Props/{pair.Item2}{suffix}.fbx")),
                "Monorail vehicle differs from its prop original: "+pair.Item1+suffix);
        // 貨物22は機関車(数値名)だけを確かめる。貨車は機関車の外観と独立に選ぶ前提なので middle 系の名前を持たない。
        foreach (int type in new[]{12,14,20,21,22,23,24})
        {
            foreach(int variant in type==14||type==24?new[]{0}:type==12?new[]{0,1}:new[]{0,1,2})
            foreach(bool middle in type==12||type==22?new[]{false}:new[]{false,true})
            {
                string expected = middle ? (variant==0 ? "middle" : "middle_"+variant) : variant.ToString();
                var car=(GameObject)resolver.GetMethod("CreateTransitCar").Invoke(null,new object[]{type,expected,null,variant});
                car.SetActive(true);
                foreach(var filter in car.GetComponentsInChildren<MeshFilter>())
                {
                    string asset=AssetDatabase.GetAssetPath(filter.sharedMesh);
                    Check(asset.EndsWith($"/{type}/{expected}.fbx") || asset.EndsWith($"/{type}/{expected}_close.fbx") || asset.EndsWith($"/{type}/{expected}_far.fbx"), "Variant silently falls back: "+type+"/"+expected);
                }
                if(type==14 || type==20 || type==21 || type==22 || type==23 || type==24)
                {
                    var lod=car.GetComponent<LODGroup>();
                    Check(lod!=null,"Close LOD missing");
                    var levels=lod.GetLODs();
                    Check(levels.Length==3 && levels[0].screenRelativeTransitionHeight==.25f && levels[1].screenRelativeTransitionHeight==.035f && levels[2].screenRelativeTransitionHeight==0,"Vehicle LOD thresholds");
                    foreach(var renderer in levels[0].renderers)
                        Check(AssetDatabase.GetAssetPath(renderer.GetComponent<MeshFilter>().sharedMesh).EndsWith($"/{expected}_close.fbx"),"Close LOD uses wrong asset");
                    foreach(var renderer in levels[1].renderers)
                        Check(AssetDatabase.GetAssetPath(renderer.GetComponent<MeshFilter>().sharedMesh).EndsWith($"/{expected}.fbx"),"Normal LOD uses wrong asset");
                    if((type==20 || type==21) && !middle)
                    {
                        // メトロ・中距離の3外観は前頭部の窓・キャノピーが前を向いて外板の外に出ていることを確かめる。
                        var renderer=levels[0].renderers[0];
                        var mesh=renderer.GetComponent<MeshFilter>().sharedMesh;
                        var vertices=mesh.vertices;var normals=mesh.normals;
                        int forward=0;
                        for(int sub=0;sub<mesh.subMeshCount;sub++)
                        {
                            if(!renderer.sharedMaterials[sub].name.StartsWith("PROP_Glass"))continue;
                            var indices=mesh.GetTriangles(sub);
                            for(int i=0;i<indices.Length;i+=3)
                            {
                                int a=indices[i],b=indices[i+1],c=indices[i+2];
                                if((vertices[a].z+vertices[b].z+vertices[c].z)/3>8f && (normals[a]+normals[b]+normals[c]).z>.9f) forward++;
                            }
                        }
                        Check(forward>0,"Cab glass must face forward: "+type+"/"+expected);
                    }
                    if(type==23 && !middle)
                    {
                        var renderer=levels[0].renderers[0];
                        var mesh=renderer.GetComponent<MeshFilter>().sharedMesh;
                        var points=mesh.vertices;var normals=mesh.normals;
                        // 高速車の運転台窓は鼻先の上面の曲面に沿う。前後か左右のどちらかへ曲がりが残っていることを確かめる。
                        float min=1,max=-1,minX=1,maxX=-1;int count=0;
                        for(int sub=0;sub<mesh.subMeshCount;sub++)
                        {
                            if(!renderer.sharedMaterials[sub].name.StartsWith("PROP_Glass"))continue;
                            foreach(int index in mesh.GetTriangles(sub))
                            {
                                if(points[index].z<0 || normals[index].y<.3f)continue;
                                min=Mathf.Min(min,normals[index].z);max=Mathf.Max(max,normals[index].z);
                                minX=Mathf.Min(minX,normals[index].x);maxX=Mathf.Max(maxX,normals[index].x);count++;
                            }
                        }
                        Check(count>100 && Mathf.Max(max-min,maxX-minX)>.1f,"High speed windshield must retain its curved surface: "+expected);
                    }
                    foreach(var renderer in levels[2].renderers)
                    {
                        var mesh=renderer.GetComponent<MeshFilter>().sharedMesh;
                        Check(AssetDatabase.GetAssetPath(mesh).EndsWith($"/{expected}_far.fbx"),"Wrong far LOD");
                        Check(mesh.triangles.Length/3<=(type==24?64:42),"Rail far triangle budget");
                    }
                    var normal=levels[1].renderers[0].bounds;
                    var close=levels[0].renderers[0].bounds;
                    Check((normal.min-close.min).magnitude<.09f && (normal.max-close.max).magnitude<.09f,"LOD placement bounds differ");
                    resolver.GetMethod("FitTransitLength").Invoke(null,new object[]{car,10f});
                    Near(levels[0].renderers[0].bounds.size.z,10,.06f,"Close nominal length");
                    Near(levels[1].renderers[0].bounds.size.z,10,.06f,"Normal nominal length");
                    Near(levels[2].renderers[0].bounds.size.z,10,.06f,"Far nominal length");
                    car.transform.localScale=Vector3.one;
                }
                foreach(var filter in car.GetComponentsInChildren<MeshFilter>()) Check(filter.sharedMesh.isReadable,"Vehicle FBX is not readable");
                var renderers=car.GetComponentsInChildren<Renderer>();
                Check(renderers.Length>0,"Transit FBX has no renderer");
                var bounds=renderers[0].bounds;
                foreach(var renderer in renderers)bounds.Encapsulate(renderer.bounds);
                Check(bounds.size.z>5 && bounds.size.z<35,"Transit FBX length/axis is not metres: "+bounds.size);
                Check(Mathf.Abs(bounds.center.z)<1,"Transit model pivot is not centred: "+bounds.center);
                if(type==14)
                {
                    Near(bounds.size.z,10,.08f,"LRT nominal length");
                    Near(bounds.size.x,2.54f,.05f,"LRT width");
                    Near(bounds.min.y,0,.001f,"LRT ground");
                    Near(bounds.size.y,middle?5.21f:3.369f,.05f,"LRT roof height");
                }
                accessTimings.Add($"model type={type} variant={variant} middle={middle} bounds={bounds.size} center={bounds.center}");
                UnityEngine.Object.DestroyImmediate(car);
            }
        }
        LiveryColors();
        UnityEngine.Object.DestroyImmediate(manager.gameObject);
    }

    static void MotionContract()
    {
        var stepType=game.GetType("GeneratedModels.TransitMotionStep");
        var journeyType=game.GetType("GeneratedModels.TransitJourney");
        var steps=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(stepType));
        foreach (var speeds in new[]{new[]{0f,10f},new[]{10f,0f}})
        {
            var step=Activator.CreateInstance(stepType);
            Set(step,"path_id",1);Set(step,"length_m",50f);
            Set(step,"start_speed_mps",speeds[0]);Set(step,"end_speed_mps",speeds[1]);
            Set(step,"duration_seconds",10.0);Set(step,"dwell_seconds",speeds[1]==0?30.0:0.0);
            steps.Add(step);
        }
        var journeys=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(journeyType));
        var journey=Activator.CreateInstance(journeyType);Set(journey,"steps",steps);journeys.Add(journey);
        var clock=New("TransitMotionClock",journeys);
        Near(Property(clock,"Duration"),50,0.0001f,"Stop duration counted twice");
        foreach(var pair in new[]{new[]{5.0,12.5},new[]{10.0,50.0},new[]{15.0,87.5},new[]{20.0,100.0},new[]{49.0,100.0},new[]{55.0,12.5}})
        {
            object[] args={pair[0],null,null};Call(clock,"AtTime",args);
            Near(Convert.ToSingle(args[1]),(float)pair[1],.001f,"Shared acceleration/time position");
        }
        var control=game.GetType("RoadMotionControl");
        float early=(float)control.GetMethod("StopLineGap").Invoke(null,new object[]{100f,20f,12f});
        Near(early,99.75f,0.0001f,"Early car does not stop with its front at the stop line");
        float late=(float)control.GetMethod("StopLineGap").Invoke(null,new object[]{100f,5f,12f});
        Check(float.IsPositiveInfinity(late),"Late vehicle waits at the stop line");
        float stopped=(float)control.GetMethod("FollowingSpeed").Invoke(null,new object[]{12f,12f,2f,.1f});
        Near(stopped,0,0.0001f,"Following vehicle overlaps stopped leader");
        var v=New("Vehicle");Call(v,"SetPath",1,Line(Vector3.zero,Vector3.forward*100),true,50f);
        Set(v,"speed",0f);Call(v,"Advance",1f);Near((float)Get(v,"s"),50,.001f,"Stopped car creeps forward");
    }

    static void Junction()
    {
        var master=new GameObject("JunctionMaster").AddComponent(game.GetType("MasterDataStore"));
        game.GetType("MasterDataStore").GetField("<Instance>k__BackingField",Flags).SetValue(null,master);
        // side_width 未設定=区画線なしのバリアントにして、車線別の進入制限を掛けずに交通量の重みだけで分岐させる。
        ((IDictionary)Get(master,"pathMasterById"))[0]=New("PathMasterItem");
        var paths=new GameObject("JunctionPaths").AddComponent(game.GetType("PathManager"));
        var manager=new GameObject("JunctionVehicles").AddComponent(game.GetType("VehicleManager"));
        Set(manager,"pathManager",paths);
        var roads=(IDictionary)Get(manager,"roads");
        var roadType=manager.GetType().GetNestedType("Road",BindingFlags.NonPublic);
        object inbound=null;
        for(int i=1;i<=4;i++)
        {
            var p=New("GeneratedModels.PathSummary");Set(p,"path_id",i);
            Set(p,"start_node_id",i==1?10:20);Set(p,"end_node_id",i==1?20:20+i);
            Set(p,"travel_direction",i==4?2:1);
            Set(p,"status",(int)game.GetType("Constants").GetField("STATUS_ACTIVE").GetRawConstantValue());
            Set(p,"path_type",(int)game.GetType("Constants").GetField("PATH_STREET").GetRawConstantValue());
            Set(p,"path_traffic",new Dictionary<string,float>{{"10",i==4?100000:100}});
            Call(paths,"SavePathToCache",p);
            var road=Activator.CreateInstance(roadType);Set(road,"data",p);
            var line=i==1?Line(new Vector3(-50,0,0),new Vector3(-5,0,0)):
                i==2?Line(new Vector3(5,0,0),new Vector3(50,0,0)):
                i==3?Line(new Vector3(0,0,5),new Vector3(0,0,50)):Line(new Vector3(0,0,-5),new Vector3(0,0,-50));
            Set(road,"line",line);Set(road,"lanes",New("RoadLaneLayout",12f,2f,.5f,0f,2,1));
            roads.Add(i,road);if(i==1)inbound=road;
        }
        int straight=0,turned=0;
        for(int i=0;i<200;i++)
        {
            var v=New("Vehicle");Set(v,"vehicleType",10);Set(v,"speed",12f);
            var line=Get(inbound,"line");Call(v,"SetPath",1,line,true,Property(line,"Length"));Call(manager,"SetLane",v,inbound,false);
            Check((bool)Call(manager,"Transition",v),"T junction transition refused");
            int chosen=(int)Get(v,"pathId");Check(chosen==2 || chosen==3,"Entered forbidden direction");
            if(chosen==2)straight++;else turned++;
            Check(Get(v,"connector")!=null && Property(Get(v,"connector"),"Length")>0,"Intersection curve absent");
        }
        Check(straight>50 && turned>50,"Traffic weighted junction choices biased");
        foreach (int requested in new[] { -1, 3, 4 })
        {
            var driver=New("Vehicle"); Set(driver,"vehicleType",10); Set(driver,"speed",12f); Set(driver,"age",100f);
            var line=Get(inbound,"line"); Call(driver,"SetPath",1,line,true,Property(line,"Length")); Call(manager,"SetLane",driver,inbound,false);
            manager.GetType().GetProperty("DrivenVehicle").SetValue(manager,driver);
            manager.GetType().GetProperty("RequestedDrivePath").SetValue(manager,requested);
            ((IList)Get(manager,"active")).Add(driver);
            ((IDictionary)Get(manager,"exitRanks")).Add(3L,0);
            Check(!(bool)Call(manager,"ReserveArrival",false),"Driven car was scheduled to disappear");
            Check((bool)Call(manager,"Transition",driver),"Driven car could not cross junction");
            Check((int)Get(driver,"pathId")== (requested==3?3:2),"Drive branch ignores choice, straight default or one-way restriction");
            Check((int)manager.GetType().GetProperty("RequestedDrivePath").GetValue(manager)==-1,"Choice leaked to next junction");
            Call(manager,"StopDrive");
            Check(((IList)Get(manager,"active")).Count==0,"Driven car remains after exit");
            ((IDictionary)Get(manager,"exitRanks")).Clear();
        }
        var watched=New("Vehicle"); Set(watched,"vehicleType",10); Set(watched,"speed",12f); Set(watched,"age",100f);
        var watchedLine=Get(inbound,"line"); Call(watched,"SetPath",1,watchedLine,true,Property(watchedLine,"Length")*.5f); Call(manager,"SetLane",watched,inbound,false);
        ((IList)Get(manager,"active")).Add(watched);
        ((IDictionary)Get(manager,"exitRanks")).Add(3L,0);
        Check((bool)Call(manager,"Watch",watched),"Running car cannot be watched");
        Check((bool)Call(manager,"ReserveArrival",false) && (bool)Get(watched,"arrivalPending"),"Watched car is exempted from NPC arrivals");
        Call(manager,"StopDrive");
        Check(((IList)Get(manager,"active")).Contains(watched) && manager.GetType().GetProperty("WatchedVehicle").GetValue(manager)==null,"Watched car does not keep driving as NPC after exit");
        Check((bool)Call(manager,"Watch",watched),"Running car cannot be watched again");
        Call(manager,"Despawn",((IList)Get(manager,"active")).IndexOf(watched));
        Check(manager.GetType().GetProperty("WatchedVehicle").GetValue(manager)==null,"Despawned car stays watched");
        ((IDictionary)Get(manager,"exitRanks")).Clear();
        UnityEngine.Object.DestroyImmediate(manager.gameObject);UnityEngine.Object.DestroyImmediate(paths.gameObject);UnityEngine.Object.DestroyImmediate(master.gameObject);
    }

    static void Congestion()
    {
        var master=new GameObject("CongestionMaster").AddComponent(game.GetType("MasterDataStore"));
        game.GetType("MasterDataStore").GetField("<Instance>k__BackingField",Flags).SetValue(null,master);
        ((IDictionary)Get(master,"pathMasterById"))[0]=New("PathMasterItem");
        var paths=new GameObject("CongestionPaths").AddComponent(game.GetType("PathManager"));
        var manager=new GameObject("CongestionVehicles").AddComponent(game.GetType("VehicleManager"));
        Set(manager,"pathManager",paths);
        var roads=(IDictionary)Get(manager,"roads");var active=(IList)Get(manager,"active");
        var roadType=manager.GetType().GetNestedType("Road",BindingFlags.NonPublic);
        var constants=game.GetType("Constants");
        int street=(int)constants.GetField("PATH_STREET").GetRawConstantValue(),motorway=(int)constants.GetField("PATH_MOTORWAY").GetRawConstantValue();
        // 1→交差点20→2・3 の丁字路、分割しただけのつなぎ目41で結ぶ 4→5、先の無い高速道路 6・7。
        var specs=new (int id,int from,int to,int type,Vector3 a,Vector3 b)[]{
            (1,10,20,street,new Vector3(-200,0,0),new Vector3(-5,0,0)),
            (2,20,21,street,new Vector3(5,0,0),new Vector3(200,0,0)),
            (3,20,22,street,new Vector3(0,0,5),new Vector3(0,0,200)),
            (4,40,41,street,new Vector3(-100,0,50),new Vector3(0,0,50)),
            (5,41,42,street,new Vector3(0,0,50),new Vector3(100,0,50)),
            (6,60,61,motorway,new Vector3(-100,0,-80),new Vector3(0,0,-80)),
            (7,70,71,motorway,new Vector3(-100,0,-120),new Vector3(0,0,-120))};
        foreach(var spec in specs)
        {
            var p=New("GeneratedModels.PathSummary");Set(p,"path_id",spec.id);Set(p,"start_node_id",spec.from);Set(p,"end_node_id",spec.to);
            Set(p,"travel_direction",1);Set(p,"status",(int)constants.GetField("STATUS_ACTIVE").GetRawConstantValue());
            Set(p,"path_type",spec.type);Set(p,"path_traffic",new Dictionary<string,float>{{"10",100}});
            Call(paths,"SavePathToCache",p);
            var road=Activator.CreateInstance(roadType);Set(road,"data",p);Set(road,"line",Line(spec.a,spec.b));
            Set(road,"lanes",New("RoadLaneLayout",12f,2f,.5f,0f,2,1));roads.Add(spec.id,road);
        }
        Func<int,float> length=path=>Property(Get(roads[path],"line"),"Length");
        // 道路1は自由走行16.25秒に対して混雑込み65秒(利用率100%の4倍)。
        var timing=New("RoadMotionData");Set(timing,"path_id",1);Set(timing,"length_m",length(1));
        Set(timing,"free_seconds",length(1)/12f);Set(timing,"total_seconds",65f);
        ((IDictionary)Get(manager,"motion"))[1]=timing;
        var bodies=new List<GameObject>();
        Func<int,float,bool,object> car=(path,s,withBody)=>
        {
            var road=roads[path];
            var v=New("Vehicle");Set(v,"vehicleType",10);Set(v,"speed",12f);Set(v,"age",100f);
            if(withBody){var body=new GameObject("CongestionCar"+path);bodies.Add(body);Set(v,"go",body);}
            Call(v,"SetPath",path,Get(road,"line"),true,s);Call(manager,"SetLane",v,road,false);Call(manager,"SetMotion",v,0f);
            active.Add(v);return v;
        };

        var leader=car(1,length(1),false);
        Check((bool)Get(leader,"holdAtEnd"),"Junction ahead is not a stop line");
        Check((bool)Call(manager,"Transition",leader),"Leader could not enter the junction");
        var follower=car(1,length(1)-10,false);
        Call(manager,"BuildFollowingGaps");
        Near((float)((IDictionary)Get(manager,"gaps"))[follower],5.5f,.01f,"Car behind does not follow the car inside the junction");
        active.Clear();

        var joined=car(4,length(4),false);
        Check(!(bool)Get(joined,"holdAtEnd"),"Split point is treated as a stop line");
        Set(joined,"motionRemaining",5f);
        Check((bool)Call(manager,"Transition",joined),"Car could not cross the split point");
        Near((float)Get(joined,"motionRemaining"),5f+(length(5)+Property(Get(joined,"connector"),"Length"))/12f,.001f,"Unspent delay is not carried past the split point");
        active.Clear();

        var queued=car(1,0f,true);var stuck=car(6,length(6)-30,true);var driven=car(7,length(7)-30,true);
        manager.GetType().GetProperty("DrivenVehicle").SetValue(manager,driven);
        const int hz=30;
        float departed=-1,waitSpeed=-1,waitRemaining=-1;
        for(int tick=1;tick<=70*hz;tick++)
        {
            Call(manager,"MoveVehicles",1f/hz,Vector3.zero);
            if(departed<0 && (int)Get(queued,"pathId")!=1)departed=(float)tick/hz;
            if(tick==40*hz){waitSpeed=(float)Get(queued,"speed");waitRemaining=Property(queued,"Remaining");Check(active.Contains(queued),"Car waiting before its deadline was removed");}
            if(tick==11*hz)Check(active.Contains(stuck),"Stuck car removed before 10 seconds");
            if(tick==14*hz)Check(!active.Contains(stuck),"Car stuck past its deadline was not removed");
        }
        Near(waitSpeed,0,.001f,"Congested car does not stop before the junction");
        Near(waitRemaining,2.25f,.3f,"Congested car does not wait with its front at the stop line");
        Near(departed,65,3,"Congested car does not leave at the core travel time");
        Check(active.Contains(driven),"Driven car was removed as stuck");
        accessTimings.Add($"congestion departed={departed:F2}s wait={waitRemaining:F2}m");
        Call(manager,"StopDrive");Call(manager,"ClearVehicles");
        foreach(var body in bodies)if(body!=null)UnityEngine.Object.DestroyImmediate(body);
        UnityEngine.Object.DestroyImmediate(manager.gameObject);UnityEngine.Object.DestroyImmediate(paths.gameObject);UnityEngine.Object.DestroyImmediate(master.gameObject);
    }

    static void DriveCamera()
    {
        var go=new GameObject("DriveCameraQA"); var camera=go.AddComponent<Camera>();
        var controller=go.AddComponent(game.GetType("CameraController"));
        game.GetType("CameraController").GetProperty("Instance").SetValue(null,controller);
        var drive=new GameObject("DriveModeQA").AddComponent(game.GetType("DriveMode"));
        game.GetType("DriveMode").GetProperty("Instance").SetValue(null,drive);
        Set(drive,"open",true); Set(drive,"riding",true); Set(drive,"cameraRef",camera);
        Set(drive,"position",new Vector3(500,20,800));
        Set(drive,"savedFov",55f); Set(drive,"savedNear",.8f);
        Set(controller,"isMouseDragging",true); Set(controller,"dragVelocity",Vector3.one*100);
        object[] args={null};
        Check((bool)Call(drive,"TryGetLoadCenter",args),"Ride did not override the ground-ray load center");
        Near(Vector3.Distance((Vector3)args[0],new Vector3(500,20,800)),0,.001f,"Load center is not the ridden vehicle");
        // 地形の無い検証シーンでは地表の高さが0になる。地下の位置で、一人称は制限なく追い、三人称は地表から2m未満へ下げない。
        Set(drive,"position",new Vector3(500,-30,800)); Set(drive,"direction",Vector3.forward); Set(drive,"halfLength",2f); Set(drive,"eyeHeight",1.5f);
        var unplaced=camera.transform.position; Call(drive,"ApplyCamera");
        Near(Vector3.Distance(camera.transform.position,unplaced),0,.001f,"First-person camera moved before the leading car was measured");
        Set(drive,"eyeBody",go); Call(drive,"ApplyCamera");
        Near(Vector3.Distance(camera.transform.position,new Vector3(500,-28.5f,803)),0,.001f,"First-person camera is not just ahead of the leading car");
        // 車窓は通行側(既定の左側通行なら左=-X)の車体の側面より少し外に置く。
        Set(drive,"halfWidth",1.2f); Set(drive,"sideView",true); Call(drive,"ApplyCamera");
        Near(Vector3.Distance(camera.transform.position,new Vector3(497.8f,-28.5f,800)),0,.001f,"Side-window camera is not just outside the curb side of the car");
        Set(drive,"sideView",false);
        Set(drive,"thirdPerson",true); Call(drive,"ApplyCamera");
        Check(camera.transform.position.y>=2-.001f,"Third-person camera goes below the ground clearance");
        // 降りるとその場の視点を通常操作へ引き継ぐ。地下を走っていたときは地表まで上げる。
        Set(drive,"thirdPerson",false); Call(drive,"ApplyCamera");
        var view=camera.transform.rotation; var handedOver=camera.transform.position; handedOver.y=0;
        Call(drive,"Close",true);
        Near(Vector3.Distance(camera.transform.position,handedOver),0,.001f,"Drive exit did not hand over the current view above the ground");
        Near(Quaternion.Angle(camera.transform.rotation,view),0,.001f,"Drive exit changed the view direction");
        Check(!camera.orthographic,"Drive exit switched to orthographic projection");
        Near(camera.fieldOfView,55,.001f,"Drive exit lost field of view"); Near(camera.nearClipPlane,.8f,.001f,"Drive exit lost clipping plane");
        Check(!(bool)Get(controller,"isMouseDragging") && ((Vector3)Get(controller,"dragVelocity")).sqrMagnitude==0,"Camera resumes stale dragging after drive");
        Check(!(bool)Call(drive,"TryGetLoadCenter",args),"Drive load center leaked after exit");
        Near(Property(drive,"SpeedMultiplier"),1,.001f,"Fast-forward leaked after exit");
        UnityEngine.Object.DestroyImmediate(drive.gameObject); UnityEngine.Object.DestroyImmediate(go);
    }
    static void RoadsideExit()
    {
        foreach (int hand in new[] { 0, 1 }) foreach (bool forward in new[] { false, true })
        {
            var v = New("Vehicle");
            Set(v, "speed", 12f); Set(v, "roadHalfWidth", 6f);
            Call(v, "SetPath", 1, Line(Vector3.zero, Vector3.forward * 100), forward, 50f);
            Set(v, "laneOffset", hand == 0 ? -2f : 2f);
            Call(v, "BeginRoadsideExit", hand);
            object[] initial = { null, null }; Call(v, "Sample", initial);
            Call(v, "Advance", .25f);
            object[] midway = { null, null }; Call(v, "Sample", midway);
            Near(Vector3.Distance((Vector3)initial[0], (Vector3)midway[0]), 0, .001f, "Car moves while turning in place");
            Call(v, "Advance", .25f);
            object[] turned = { null, null }; Call(v, "Sample", turned);
            Near(Vector3.Angle((Vector3)initial[1], (Vector3)turned[1]), 90, .01f, "Roadside turn is not 90 degrees");
            Call(v, "Advance", 10f);
            object[] finish = { null, null }; Call(v, "Sample", finish);
            Near(Mathf.Abs(((Vector3)finish[0]).x), 9, .001f, "Exit did not clear road and sidewalk");
            Check((bool)v.GetType().GetProperty("ExitComplete").GetValue(v), "Roadside exit never completes");
        }
    }
    static IEnumerable<int> GetIds(object buildings,int count)
    {
        for(int i=0;i<count;i++)yield return 100+i;
    }
    static void AccessFixture(object manager, int subdivisions, List<GameObject> cleanup)
    {
        var master = new GameObject("AccessMaster").AddComponent(game.GetType("MasterDataStore"));cleanup.Add(master.gameObject);
        game.GetType("MasterDataStore").GetField("<Instance>k__BackingField",Flags).SetValue(null,master);
        var item=New("PathMasterItem");Set(item,"frontage_allowed",true);
        ((IDictionary)Get(master,"pathMasterById"))[0]=item;
        ((IDictionary)Get(master,"agentMasterById"))[0]=New("MasterDataStore+AgentMasterItem");   // 建物の登録は間口索引のために建物マスターを引く(未登録だと例外)
        var parcels=new GameObject("AccessParcels").AddComponent(game.GetType("ParcelManager"));cleanup.Add(parcels.gameObject);
        var buildings=new GameObject("AccessBuildings").AddComponent(game.GetType("BuildingManager"));cleanup.Add(buildings.gameObject);
        Set(manager,"parcelManager",parcels);Set(manager,"buildingManager",buildings);
        int active=(int)game.GetType("Constants").GetField("STATUS_ACTIVE").GetRawConstantValue();
        float x=-100+100f/subdivisions;
        var parcel=New("GeneratedModels.ParcelSummary");Set(parcel,"parcel_id",100);Set(parcel,"front_path_id",1);Set(parcel,"status",active);
        Set(parcel,"polygon",new List<object>{new List<object>{x-3,-94f},new List<object>{x+3,-94f},new List<object>{x+3,-80f},new List<object>{x-3,-80f}});
        Call(parcels,"RegisterParcelDataForLines",parcel);
        var building=New("GeneratedModels.BuildingSummary");Set(building,"agent_id",100);Set(building,"parcel_id",100);Set(building,"status",active);Set(building,"is_bd",true);
        Call(buildings,"RegisterBuildingDataToCache",building);
        int parcelCount=subdivisions==1?1000:1;
        for(int i=1;i<parcelCount;i++)
        {
            var extra=New("GeneratedModels.ParcelSummary");Set(extra,"parcel_id",100+i);Set(extra,"front_path_id",1);Set(extra,"status",active);Set(extra,"polygon",Get(parcel,"polygon"));
            Call(parcels,"RegisterParcelDataForLines",extra);
            var extraBuilding=New("GeneratedModels.BuildingSummary");Set(extraBuilding,"agent_id",100+i);Set(extraBuilding,"parcel_id",100+i);Set(extraBuilding,"status",active);Set(extraBuilding,"is_bd",true);
            Call(buildings,"RegisterBuildingDataToCache",extraBuilding);
        }
        double maxSlice=0,totalMs=0;int slices=0;
        Action refresh=()=>
        {
            var work=(IEnumerator)Call(manager,"UpdateAccess",Vector3.zero);
            while(true)
            {
                var clock=System.Diagnostics.Stopwatch.StartNew();bool more=work.MoveNext();clock.Stop();
                maxSlice=Math.Max(maxSlice,clock.Elapsed.TotalMilliseconds);totalMs+=clock.Elapsed.TotalMilliseconds;slices++;
                if(!more)break;
            }
        };
        refresh();
        Check(((IList)((IDictionary)Get(manager,"entrances"))[1]).Count>0,"Building frontage entrance missing");
        int generation=(int)Get(manager,"accessGeneration");refresh();
        Check((int)Get(manager,"accessGeneration")==generation,"Unchanged data rebuilds route table");
        Set(item,"frontage_allowed",false);refresh();
        Check(((IList)((IDictionary)Get(manager,"entrances"))[1]).Count==0,"Bridge has an entrance");
        Set(item,"frontage_allowed",true);refresh();
        foreach(int bid in new List<int>(GetIds(buildings,parcelCount)))
            Set(Call(buildings,"GetBuildingSummary",bid),"status",-1);
        refresh();
        Check(((IList)((IDictionary)Get(manager,"entrances"))[1]).Count==0,"Empty/inactive building retains entrance");
        foreach(int bid in new List<int>(GetIds(buildings,parcelCount)))
            Set(Call(buildings,"GetBuildingSummary",bid),"status",active);
        refresh();
        var road=((IDictionary)Get(manager,"roads"))[1];var data=Get(road,"data");int type=(int)Get(data,"path_type");
        Set(data,"path_type",(int)game.GetType("Constants").GetField("PATH_MOTORWAY").GetRawConstantValue());refresh();
        Check(((IList)((IDictionary)Get(manager,"entrances"))[1]).Count==0,"Motorway has an entrance");
        Set(data,"path_type",type);refresh();
        Check(((IDictionary)Get(manager,"exitRanks")).Count==4*subdivisions,"Reverse routes do not reach entrance");
        accessTimings.Add($"access parcels={parcelCount} roads={4*subdivisions} refreshes=7 slices={slices} totalMs={totalMs:F2} maxSliceMs={maxSlice:F2}");
    }
    static string Network(int subdivisions, float target, int seconds, bool reduce, int hz = 4, bool withAccess = true)
    {
        UnityEngine.Random.InitState(321+subdivisions);
        var pathManager=new GameObject("Paths").AddComponent(game.GetType("PathManager"));
        var manager=new GameObject("Vehicles").AddComponent(game.GetType("VehicleManager"));
        Set(manager,"pathManager",pathManager);Set(manager,"renderRadius",1000f);
        // APIや地形読み込みから独立して、実際のマネージャーへ道路スナップショットを注入する。
        var roads=(IDictionary)Get(manager,"roads");var buckets=(IList)Get(manager,"buckets");
        var lookup=(IDictionary)Get(manager,"bucketByKey");var active=(IList)Get(manager,"active");
        var budget=Get(manager,"population");Call(budget,"SetTarget",target);
        Type roadType=manager.GetType().GetNestedType("Road",BindingFlags.NonPublic);
        Type bucketType=manager.GetType().GetNestedType("Bucket",BindingFlags.NonPublic);
        if(subdivisions>20) withAccess=false;
        int count=4*subdivisions;
        var corners=new[]{new Vector3(-100,0,-100),new Vector3(100,0,-100),new Vector3(100,0,100),new Vector3(-100,0,100)};
        for(int i=0;i<count;i++)
        {
            int side=i/subdivisions,part=i%subdivisions;
            Vector3 a=Vector3.Lerp(corners[side],corners[(side+1)%4],(float)part/subdivisions);
            Vector3 b=Vector3.Lerp(corners[side],corners[(side+1)%4],(float)(part+1)/subdivisions);
            var data=New("GeneratedModels.PathSummary");Set(data,"path_id",i+1);Set(data,"start_node_id",i+1);Set(data,"end_node_id",(i+1)%count+1);
            Set(data,"status",(int)game.GetType("Constants").GetField("STATUS_ACTIVE").GetRawConstantValue());
            Set(data,"path_type",(int)game.GetType("Constants").GetField("PATH_STREET").GetRawConstantValue());
            Set(data,"travel_direction",1);Set(data,"path_traffic",new Dictionary<string,float>{{"10",100}});
            Call(pathManager,"SavePathToCache",data);
            var line=Line(a,b);float length=Vector3.Distance(a,b);
            var road=Activator.CreateInstance(roadType);Set(road,"data",data);Set(road,"line",line);Set(road,"lanes",New("RoadLaneLayout",12f,2f,.5f,0f,2,1));
            Set(road,"halfWidth",6f);Set(road,"insideLength",length);Set(road,"intervals",new List<Vector2>{new Vector2(0,length)});roads.Add(i+1,road);
            var bucket=Activator.CreateInstance(bucketType);Set(bucket,"road",road);Set(bucket,"type",10);Set(bucket,"target",target/count);
            buckets.Add(bucket);lookup.Add(((long)(i+1)<<8)|10L,bucket);
        }
        var fixtureObjects = new List<GameObject>();
        if(withAccess) AccessFixture(manager,subdivisions,fixtureObjects);
        Call(manager,"PrepareSeeds");
        while ((bool)Get(manager,"seeding")) Call(manager,"FillNewCoverage");
        int observed=0,peak=0,transitions=0;double sum=0;
        var roadSamples = new double[count];
        var priorPaths=new Dictionary<object,int>();
        for(int tick=0;tick<seconds*hz;tick++)
        {
            if(reduce && tick==seconds*hz/2)
            {
                Call(budget,"SetTarget",0f);foreach(var b in buckets)Set(b,"target",0f);
            }
            Call(manager,"MoveVehicles",1f/hz,Vector3.zero);
            if ((tick+1) % (hz/4)==0) Call(manager,"RegulateDensity",.25f);
            peak=Math.Max(peak,active.Count);
            foreach(var v in active)
            {
                int path=(int)Get(v,"pathId");if(priorPaths.TryGetValue(v,out int old) && old!=path)transitions++;
                priorPaths[v]=path;
                var go=(GameObject)Get(v,"go");Check(!float.IsNaN(go.transform.position.x),"Non-finite car position");
                bool exiting=(bool)v.GetType().GetProperty("ExitingRoad").GetValue(v);
                bool completed=(bool)v.GetType().GetProperty("ExitComplete").GetValue(v);
                if(exiting && !completed) Near(go.transform.localScale.x,1,.001f,"Car shrinks before leaving road");
                if((bool)Get(v,"fading")) Check((exiting && completed) || (float)Get(v,"stuckSeconds")>=10,"Fade starts on carriageway");
                Check(active.Count<=300,"Global hard cap");
            }
            if(tick>=60*hz)
            {
                observed++;sum+=active.Count;
                foreach (var vehicle in active)
                    if (!(bool)vehicle.GetType().GetProperty("ExitingRoad").GetValue(vehicle))
                        roadSamples[(int)Get(vehicle,"pathId")-1]++;
            }
        }
        double average=sum/observed;
        if(!reduce)
        {
            Near((float)average,target,Mathf.Max(.05f,target*.12f),"Population drift");
            if(seconds>=1000 && target>=1)
                // 道路間の再配置には0.5台または25%の許容幅があり、退出中は走行密度から除く。
                foreach(double samples in roadSamples) Near((float)(samples/observed),target/count,Mathf.Max(.5f,target/count*.25f),"Road density bias");
        }
        else if(withAccess) Check(active.Count==0,"Reachable entrance did not drain population");
        else Check(active.Count==12,"Closed network without buildings lost vehicles");
        if(target>=1)Check(transitions>10,"Cars disappear before crossing nodes");
        Call(manager,"ClearVehicles");Check(active.Count==0,"Reset leaks active cars");
        UnityEngine.Object.DestroyImmediate(manager.gameObject);UnityEngine.Object.DestroyImmediate(pathManager.gameObject);
        foreach(var obj in fixtureObjects) UnityEngine.Object.DestroyImmediate(obj);
        return $"hz={hz} split={subdivisions} target={target} reduce={reduce} average={average:F3} peak={peak} transitions={transitions}";
    }

    static void PedestrianBands()
    {
        var resolve=game.GetType("PedestrianManager").GetMethod("ResolveBand");
        int street=(int)game.GetType("Constants").GetField("PATH_STREET").GetRawConstantValue();
        int walkway=(int)game.GetType("Constants").GetField("PATH_WALKWAY").GetRawConstantValue();
        object[] a={street,211,12f,2.5f,.3f,1f,null,null,null,null};resolve.Invoke(null,a);
        Near((float)a[6],3.8f,.001f,"Sidewalk band inner");Near((float)a[7],5.7f,.001f,"Sidewalk band outer");
        Near((float)a[8],.3f,.001f,"Sidewalk surface height");Check(!(bool)a[9],"Sidewalk band is centred");
        a=new object[]{street,211,6f,0f,.3f,1f,null,null,null,null};resolve.Invoke(null,a);
        Near((float)a[6],2f,.001f,"Shoulder band inner");Near((float)a[7],2.7f,.001f,"Shoulder band outer");Near((float)a[8],.1f,.001f,"Shoulder on road surface");
        a=new object[]{walkway,301,4f,0f,.3f,1f,null,null,null,null};resolve.Invoke(null,a);
        Check((bool)a[9] && (float)a[6]==0,"Walkway band not centred");Near((float)a[7],1.7f,.001f,"Walkway band outer");
        // LRT付き歩行者専用道路303は複線の車体(軌道中心±1.75m+半幅1.27m)を避けた左右の帯。
        a=new object[]{walkway,303,12f,0f,.3f,1f,null,null,null,null};resolve.Invoke(null,a);
        Check(!(bool)a[9],"LRT walkway band spans the tracks");Near((float)a[6],3.32f,.001f,"LRT walkway band inner");
        Near((float)a[7],5.7f,.001f,"LRT walkway band outer");Near((float)a[8],.1f,.001f,"LRT walkway on road surface");
        a=new object[]{street,211,1f,0f,.3f,1f,null,null,null,null};resolve.Invoke(null,a);
        Check((float)a[6]<=(float)a[7],"Narrow road band inverted");
        var p=New("Pedestrian");Call(p,"SetPath",1,Line(Vector3.zero,Vector3.forward*100),true,50f);
        Set(p,"side",1);Set(p,"lateral",4f);Set(p,"surface",.3f);
        object[] sample={null,null};Call(p,"Sample",sample);
        Near(((Vector3)sample[0]).x,4,.001f,"Right side lateral offset");Near(((Vector3)sample[0]).y,.3f,.001f,"Surface height");
        Check(Vector3.Angle((Vector3)sample[1],Vector3.forward)<.01f,"Forward tangent");
        Set(p,"forward",false);Call(p,"Sample",sample);
        Near(((Vector3)sample[0]).x,4,.001f,"Reversal moves pedestrian to the other side");
        Check(Vector3.Angle((Vector3)sample[1],Vector3.back)<.01f,"Backward tangent");
        Set(p,"speed",1f);Near((float)Call(p,"Advance",100f),50,.001f,"Residual time lost at path end");
        Call(p,"BeginEntering",new Vector3(10,0,0));
        Check((bool)p.GetType().GetProperty("Entering").GetValue(p) && (bool)p.GetType().GetProperty("InConnector").GetValue(p),"Entering connector absent");
        Call(p,"Advance",100f);Check((bool)p.GetType().GetProperty("ConnectorComplete").GetValue(p),"Entering never completes");
    }
    static void CheckPedestrianSurface(Mesh mesh)
    {
        var vertices = mesh.vertices;
        var normals = mesh.normals;
        var triangles = mesh.triangles;
        var ids = new int[vertices.Length];
        var positions = new Dictionary<Vector3Int, int>();
        var parents = new List<int>();
        // FBXのUV・法線分割を位置で戻し、材質境界を穴として誤検出しない。
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i] * 100000f;
            var key = new Vector3Int(Mathf.RoundToInt(v.x), Mathf.RoundToInt(v.y), Mathf.RoundToInt(v.z));
            if (!positions.TryGetValue(key, out int id))
            {
                id = parents.Count; positions.Add(key, id); parents.Add(id);
            }
            ids[i] = id;
        }
        int Root(int id) { while (parents[id] != id) id = parents[id]; return id; }
        var edges = new Dictionary<(int, int), (int count, int balance)>();
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            var cross = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            Check(cross.sqrMagnitude > 1e-12f, "Pedestrian has a degenerate triangle");
            foreach (int index in new[] { a, b, c })
                Check(Vector3.Dot(cross.normalized, normals[index]) > .1f, "Pedestrian normal opposes winding");
            for (int e = 0; e < 3; e++)
            {
                int from = ids[triangles[t + e]], to = ids[triangles[t + (e + 1) % 3]];
                var key = (Math.Min(from, to), Math.Max(from, to));
                edges.TryGetValue(key, out var value);
                edges[key] = (value.count + 1, value.balance + (from < to ? 1 : -1));
                parents[Root(from)] = Root(to);
            }
        }
        foreach (var edge in edges.Values)
            Check(edge.count == 2 && edge.balance == 0, "Pedestrian has an open or reversed face");
        var volumes = new Dictionary<int, double>();
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int root = Root(ids[triangles[t]]);
            volumes.TryGetValue(root, out double volume);
            volumes[root] = volume + Vector3.Dot(vertices[triangles[t]],
                Vector3.Cross(vertices[triangles[t + 1]], vertices[triangles[t + 2]])) / 6.0;
        }
        foreach (double volume in volumes.Values)
            Check(volume > 1e-8, "Pedestrian body part faces inward");
    }
    static readonly string[] PedestrianPoseSuffixes={"","_right","_left"};
    static void PedestrianModels()
    {
        var manager=new GameObject("PedestrianModelsQA").AddComponent(game.GetType("ModelManager"));
        game.GetType("ModelManager").GetProperty("Instance").SetValue(null,manager);
        var getModel=game.GetType("ModelManager").GetMethod("GetModel");
        int count=0;
        for(int i=0;i<16;i++)
        {
            if(getModel.Invoke(manager,new object[]{$"Models/Props/pedestrian_{i}",true})==null) break;
            Bounds standing=default,standingHair=default;
            for(int pose=0;pose<PedestrianPoseSuffixes.Length;pose++)
            {
                string name=$"pedestrian_{i}{PedestrianPoseSuffixes[pose]}";
                var prefab=(GameObject)getModel.Invoke(manager,new object[]{$"Models/Props/{name}",true});
                Check(prefab!=null,$"{name} is missing while pedestrian_{i} exists");
                var filters=prefab.GetComponentsInChildren<MeshFilter>(true);Check(filters.Length>0,"Pedestrian FBX has no mesh");
                int triangles=0;Bounds bounds=default,hair=default;bool any=false,anyHair=false;
                Vector3 front=new Vector3(0,0,float.NegativeInfinity);
                foreach(var filter in filters)
                {
                    Check(filter.sharedMesh.isReadable,"Pedestrian FBX is not readable");
                    triangles+=filter.sharedMesh.triangles.Length/3;
                    CheckPedestrianSurface(filter.sharedMesh);
                    var m=prefab.transform.worldToLocalMatrix*filter.transform.localToWorldMatrix;
                    var vertices=filter.sharedMesh.vertices;
                    foreach(var v in vertices)
                    {
                        var point=m.MultiplyPoint3x4(v);
                        if(!any){bounds=new Bounds(point,Vector3.zero);any=true;}else bounds.Encapsulate(point);
                        if(point.y<.06f && point.z>front.z) front=point;
                    }
                    var materials=filter.GetComponent<MeshRenderer>().sharedMaterials;
                    for(int s=0;s<filter.sharedMesh.subMeshCount && s<materials.Length;s++)
                    {
                        if(materials[s]==null || !materials[s].name.Contains("HAIR")) continue;
                        foreach(int index in filter.sharedMesh.GetIndices(s))
                        {
                            var point=m.MultiplyPoint3x4(vertices[index]);
                            if(!anyHair){hair=new Bounds(point,Vector3.zero);anyHair=true;}else hair.Encapsulate(point);
                        }
                    }
                }
                Check(anyHair,$"{name} has no hair part");
                Check(triangles<400,$"{name} exceeds 400 triangles: {triangles}");
                Check(bounds.size.y>1.4f && bounds.size.y<2f,$"{name} height is not human scale: {bounds.size}");
                Check(Mathf.Abs(bounds.min.y)<.05f,$"{name} is not grounded: {bounds.min.y}");
                Check(Mathf.Abs(bounds.center.x)<.3f && Mathf.Abs(bounds.center.z)<.3f,$"{name} is off centre: {bounds.center}");
                if(pose==0){standing=bounds;standingHair=hair;}
                else
                {
                    // 描画は姿勢を差し替えるだけで位置を補正しないので、頭が揃い姿勢と同じ位置に無いと差し替えのたびに跳ねる。
                    Check(Vector3.Distance(hair.center,standingHair.center)<.001f && Vector3.Distance(hair.size,standingHair.size)<.001f,
                        $"{name} head does not align with the standing pose: {hair.center} vs {standingHair.center}");
                    Check(bounds.size.z>standing.size.z+.2f,$"{name} does not open the legs: {bounds.size.z} vs {standing.size.z}");
                    // 前方は+Z、人物の右手側は+X。一番前の靴底が名前どおりの足にあること。
                    Check(pose==1 ? front.x>0 : front.x<0,$"{name} puts the wrong foot forward: {front}");
                }
                accessTimings.Add($"{name} triangles={triangles} size={bounds.size}");
            }
            count++;
        }
        Check(count>=1,"No pedestrian model found");
        UnityEngine.Object.DestroyImmediate(manager.gameObject);
    }
    static void PedestrianPoses()
    {
        var resolve=game.GetType("PedestrianManager").GetMethod("ResolvePose");
        Func<float,float,bool,int> pose=(walked,step,moving)=>(int)resolve.Invoke(null,new object[]{walked,step,moving});
        var sequence=new List<int>();var runs=new List<float>();float runStart=0;
        for(int i=0;i<=260;i++)
        {
            float walked=i*.01f;int current=pose(walked,.65f,true);
            Check(pose(walked,.65f,false)==0,"Standing pedestrian is not drawn in the standing pose");
            if(sequence.Count>0 && sequence[sequence.Count-1]==current) continue;
            if(sequence.Count>0) runs.Add(walked-runStart);
            sequence.Add(current);runStart=walked;
        }
        Check(string.Join(",",sequence)=="1,0,2,0,1,0,2,0,1",$"Gait order is not right-standing-left-standing: {string.Join(",",sequence)}");
        foreach(float run in runs) Near(run,.325f,.011f,"Pose does not change every half step");
        Check(pose(1f,0f,true)==0,"Zero step length must not animate");
    }
    // 1000人を同じ行列計算と描画要求で描き、全員を揃い姿勢にしたとき(姿勢で描画呼び出しが分かれない)との差を測る。
    static string PedestrianDrawTiming()
    {
        var models=new GameObject("PedestrianTimingModels").AddComponent(game.GetType("ModelManager"));
        game.GetType("ModelManager").GetProperty("Instance").SetValue(null,models);
        var manager=new GameObject("PedestrianTiming").AddComponent(game.GetType("PedestrianManager"));
        var active=(IList)Get(manager,"active");
        var line=Line(new Vector3(-500,0,0),new Vector3(500,0,0));
        UnityEngine.Random.InitState(99);
        for(int i=0;i<1000;i++)
        {
            var p=New("Pedestrian");Call(p,"SetPath",i+1,line,i%2==0,UnityEngine.Random.value*1000f);
            Set(p,"variant",i%16);Set(p,"lateral",4f);Set(p,"age",10f);Set(p,"walked",UnityEngine.Random.value*10f);Set(p,"moving",true);
            active.Add(p);
        }
        Func<int,double> measure=frames=>
        {
            for(int i=0;i<5;i++) Call(manager,"LateUpdate");
            var watch=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<frames;i++) Call(manager,"LateUpdate");
            return watch.Elapsed.TotalMilliseconds/frames;
        };
        double walking=measure(200);
        int slots=0;foreach(IList list in (IList)Get(manager,"batches")) if(list.Count>0) slots++;
        foreach(var p in active) Set(p,"moving",false);
        double standing=measure(200);
        Check(((IList)Get(manager,"models")).Count>0,"Timing ran without pedestrian models");
        Call(manager,"OnDestroy");
        UnityEngine.Object.DestroyImmediate(manager.gameObject);UnityEngine.Object.DestroyImmediate(models.gameObject);
        return $"pedestrian draw 1000 people: walking={walking:F3}ms/frame (model x pose groups={slots}) standing-only={standing:F3}ms/frame";
    }
    static void PedestrianAccessFixture(object manager, List<GameObject> cleanup, int frontPath)
    {
        var master=new GameObject("PedestrianMaster").AddComponent(game.GetType("MasterDataStore"));cleanup.Add(master.gameObject);
        game.GetType("MasterDataStore").GetField("<Instance>k__BackingField",Flags).SetValue(null,master);
        var item=New("PathMasterItem");Set(item,"frontage_allowed",true);
        ((IDictionary)Get(master,"pathMasterById"))[0]=item;
        // 建物の登録は間口索引のためにバリアントの建物マスターを引く(未登録だと例外になる)。
        ((IDictionary)Get(master,"agentMasterById"))[0]=New("MasterDataStore+AgentMasterItem");
        var parcels=new GameObject("PedestrianParcels").AddComponent(game.GetType("ParcelManager"));cleanup.Add(parcels.gameObject);
        var buildings=new GameObject("PedestrianBuildings").AddComponent(game.GetType("BuildingManager"));cleanup.Add(buildings.gameObject);
        Set(manager,"parcelManager",parcels);Set(manager,"buildingManager",buildings);
        int active=(int)game.GetType("Constants").GetField("STATUS_ACTIVE").GetRawConstantValue();
        var parcel=New("GeneratedModels.ParcelSummary");Set(parcel,"parcel_id",100);Set(parcel,"front_path_id",frontPath);Set(parcel,"status",active);
        Set(parcel,"polygon",new List<object>{new List<object>{-3f,-94f},new List<object>{3f,-94f},new List<object>{3f,-80f},new List<object>{-3f,-80f}});
        Call(parcels,"RegisterParcelDataForLines",parcel);
        var building=New("GeneratedModels.BuildingSummary");Set(building,"agent_id",100);Set(building,"parcel_id",100);Set(building,"status",active);Set(building,"is_bd",true);
        Call(buildings,"RegisterBuildingDataToCache",building);
        Action refresh=()=>{var work=(IEnumerator)Call(manager,"UpdateAccess",Vector3.zero);while(work.MoveNext()){}};
        refresh();
        var entrances=(IDictionary)Get(manager,"entrances");
        Check(((IList)entrances[frontPath]).Count>0,"Pedestrian frontage entrance missing");
        // 接道する道路は東向き(+X)で、区画はその北側(+Z)=パス幾何の左(-1)にある。
        foreach(var entry in (IList)entrances[frontPath]) Check((int)Get(entry,"side")==-1,"Entrance side is not the parcel side");
        int generation=(int)Get(manager,"accessGeneration");refresh();
        Check((int)Get(manager,"accessGeneration")==generation,"Unchanged data rebuilds pedestrian entrances");
    }
    static string PedestrianNetwork(int subdivisions, float target, int seconds, bool reduce, bool withAccess, int hz=4)
    {
        UnityEngine.Random.InitState(4321+subdivisions);
        var pathManager=new GameObject("PedestrianPaths").AddComponent(game.GetType("PathManager"));
        var manager=new GameObject("Pedestrians").AddComponent(game.GetType("PedestrianManager"));
        Set(manager,"pathManager",pathManager);Set(manager,"renderRadius",1000f);
        var walks=(IDictionary)Get(manager,"walks");var buckets=(IList)Get(manager,"buckets");
        var lookup=(IDictionary)Get(manager,"bucketByPath");var active=(IList)Get(manager,"active");
        var budget=Get(manager,"population");Call(budget,"SetTarget",target);
        Type walkType=manager.GetType().GetNestedType("Walk",BindingFlags.NonPublic);
        Type bucketType=manager.GetType().GetNestedType("Bucket",BindingFlags.NonPublic);
        int count=4*subdivisions;
        var corners=new[]{new Vector3(-100,0,-100),new Vector3(100,0,-100),new Vector3(100,0,100),new Vector3(-100,0,100)};
        for(int i=0;i<count;i++)
        {
            int side=i/subdivisions,part=i%subdivisions;
            Vector3 a=Vector3.Lerp(corners[side],corners[(side+1)%4],(float)part/subdivisions);
            Vector3 b=Vector3.Lerp(corners[side],corners[(side+1)%4],(float)(part+1)/subdivisions);
            var data=New("GeneratedModels.PathSummary");Set(data,"path_id",i+1);Set(data,"start_node_id",i+1);Set(data,"end_node_id",(i+1)%count+1);
            Set(data,"status",(int)game.GetType("Constants").GetField("STATUS_ACTIVE").GetRawConstantValue());
            Set(data,"path_type",(int)game.GetType("Constants").GetField("PATH_STREET").GetRawConstantValue());
            // 一方通行にしても歩行者は両方向に歩く。
            Set(data,"travel_direction",1);Set(data,"path_traffic",new Dictionary<string,float>{{"0",100}});
            Call(pathManager,"SavePathToCache",data);
            var line=Line(a,b);float length=Vector3.Distance(a,b);
            var walk=Activator.CreateInstance(walkType);Set(walk,"data",data);Set(walk,"line",line);
            Set(walk,"halfWidth",6f);Set(walk,"insideLength",length);Set(walk,"intervals",new List<Vector2>{new Vector2(0,length)});
            Set(walk,"inner",3.8f);Set(walk,"outer",5.7f);Set(walk,"surface",.3f);Set(walk,"centered",false);walks.Add(i+1,walk);
            var bucket=Activator.CreateInstance(bucketType);Set(bucket,"walk",walk);Set(bucket,"target",target/count);
            buckets.Add(bucket);lookup.Add(i+1,bucket);
        }
        var fixtureObjects=new List<GameObject>();
        if(withAccess) PedestrianAccessFixture(manager,fixtureObjects,subdivisions/2+1);   // 南辺のうち区画(x=±3)の前を通る分割
        Call(manager,"PrepareSeeds");Call(manager,"FillNewCoverage");
        int observed=0,peak=0,transitions=0,forwardSeen=0,backwardSeen=0;double sum=0;
        var priorPaths=new Dictionary<object,int>();
        for(int tick=0;tick<seconds*hz;tick++)
        {
            if(reduce && tick==seconds*hz/2)
            {
                Call(budget,"SetTarget",0f);foreach(var b in buckets)Set(b,"target",0f);
            }
            Call(manager,"MovePedestrians",1f/hz,Vector3.zero);
            if((tick+1)%(hz/4)==0) Call(manager,"RegulateDensity",.25f);
            peak=Math.Max(peak,active.Count);
            foreach(var p in active)
            {
                int path=(int)Get(p,"pathId");if(priorPaths.TryGetValue(p,out int old) && old!=path)transitions++;
                priorPaths[p]=path;
                if((bool)Get(p,"forward"))forwardSeen++;else backwardSeen++;
                object[] sample={null,null};Call(p,"Sample",sample);var position=(Vector3)sample[0];
                Check(!float.IsNaN(position.x) && !float.IsNaN(position.z),"Non-finite pedestrian position");
                Check(active.Count<=1000,"Global hard cap");
                // 環状路では建物に入って縮小する間だけ立ち止まる。発生直後(age=0)はまだ移動処理を経ていない。
                if((float)Get(p,"age")>0 && !(bool)Get(p,"fading")) Check((bool)Get(p,"moving"),"Walking pedestrian is drawn in the standing pose");
                if((bool)p.GetType().GetProperty("InConnector").GetValue(p)) continue;
                var road=Get(p,"road");float s=(float)Call(road,"Project",position);
                Sample(road,s,true,out var centre,out _);
                float lateral=Vector2.Distance(new Vector2(position.x,position.z),new Vector2(centre.x,centre.z));
                Check(lateral>=3.8f-.02f && lateral<=5.7f+.02f,$"Pedestrian left the sidewalk band: {lateral}");
                Near(position.y,.3f,.001f,"Pedestrian is not on the sidewalk surface");
            }
            if(tick>=60*hz){observed++;sum+=active.Count;}
        }
        double average=sum/observed;
        if(!reduce) Near((float)average,target,Mathf.Max(.05f,target*.12f),"Pedestrian population drift");
        else if(withAccess) Check(active.Count==0,"Reachable entrance did not drain pedestrians");
        else Check(active.Count==12,"Closed network without buildings lost pedestrians");
        if(target>=1){Check(transitions>10,"Pedestrians disappear before crossing nodes");Check(forwardSeen>0 && backwardSeen>0,"One-way road blocks a walking direction");}
        Call(manager,"ClearPedestrians");Check(active.Count==0,"Reset leaks pedestrians");
        UnityEngine.Object.DestroyImmediate(manager.gameObject);UnityEngine.Object.DestroyImmediate(pathManager.gameObject);
        foreach(var obj in fixtureObjects) UnityEngine.Object.DestroyImmediate(obj);
        return $"pedestrians hz={hz} split={subdivisions} target={target} reduce={reduce} access={withAccess} average={average:F3} peak={peak} transitions={transitions}";
    }
}
