using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public static class VehicleQA
{
    static Assembly game;
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
            game=Assembly.LoadFrom(Path.Combine(source,"Temp/bin/Debug/Assembly-CSharp.dll"));
            Geometry(); Budget(); Junction(); RoadsideExit();
            var report = new List<string>();
            foreach(int split in new[]{1,5,20}) report.Add(Network(split,12,1200,false));
            report.Add(Network(5,.2f,1200,false));
            report.Add(Network(5,12,1200,true));
            report.Add(Network(200,12,120,true));
            report.Add(Network(5,12,120,false,60));
            File.WriteAllText(result,"PASS\nGeometry, directions, lane boundaries, fractional budget\n"+string.Join("\n",report));
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
    static void Junction()
    {
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
        UnityEngine.Object.DestroyImmediate(manager.gameObject);UnityEngine.Object.DestroyImmediate(paths.gameObject);
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
    static string Network(int subdivisions, float target, int seconds, bool reduce, int hz = 4)
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
            Set(road,"insideLength",length);Set(road,"intervals",new List<Vector2>{new Vector2(0,length)});roads.Add(i+1,road);
            var bucket=Activator.CreateInstance(bucketType);Set(bucket,"road",road);Set(bucket,"type",10);Set(bucket,"target",target/count);
            buckets.Add(bucket);lookup.Add(((long)(i+1)<<8)|10L,bucket);
        }
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
                if((bool)Get(v,"fading")) Check(exiting && completed,"Fade starts on carriageway");
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
        else Check(active.Count==0,"Zero traffic leaves circulating cars");
        if(target>=1)Check(transitions>10,"Cars disappear before crossing nodes");
        Call(manager,"ClearVehicles");Check(active.Count==0,"Reset leaks active cars");
        UnityEngine.Object.DestroyImmediate(manager.gameObject);UnityEngine.Object.DestroyImmediate(pathManager.gameObject);
        return $"hz={hz} split={subdivisions} target={target} reduce={reduce} average={average:F3} peak={peak} transitions={transitions}";
    }
}
