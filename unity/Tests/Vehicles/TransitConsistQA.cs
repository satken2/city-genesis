using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;

public static partial class VehicleQA
{
    static Vector3[] RoadPosePoints()
    {
        var points = new Vector3[301];
        for (int i = 0; i < points.Length; i++)
            points[i] = new Vector3(70*Mathf.Sin(i/70f),i*.025f+.08f*(i%2),70*(1-Mathf.Cos(i/70f)));
        return points;
    }

    static void RoadVehicleGeometry()
    {
        var geometry = New("TransitRouteGeometry",new object[]{null});
        var g = ConsistTrack(geometry,1,RoadPosePoints(),2);
        Set(g,"rail",false); Set(g,"trimmed",Get(g,"full"));
        var polyline = Get(g,"full");
        float[] distances = (float[])Get(polyline,"distances");
        float length = Property(polyline,"Length");
        var handProperty = game.GetType("CityInfoStore").GetProperty("TrafficHand");
        int oldHand = (int)handProperty.GetValue(null);
        try
        {
            foreach (int hand in new[]{0,1}) foreach (bool forward in new[]{false,true})
            {
                handProperty.SetValue(null,hand);
                float sign = hand == 0 ? -1f : 1f;
                var vehicle = New("Vehicle"); Call(vehicle,"SetPath",1,polyline,forward,50f);
                Set(vehicle,"laneOffset",sign*2f); Set(vehicle,"shoulderOffset",sign*5f);
                foreach (float blend in new[]{0f,7.5f,15f})
                {
                    Set(vehicle,"spawnBlendRemaining",blend);
                    for (int vertex = 100; vertex < 140; vertex++)
                    {
                        object[] a = {null,null}, b = {null,null};
                        Set(vehicle,"s",distances[vertex]-.0001f); Call(vehicle,"Sample",a);
                        Set(vehicle,"s",distances[vertex]+.0001f); Call(vehicle,"Sample",b);
                        Near(Vector3.Distance((Vector3)a[0],(Vector3)b[0]),0,.001f,"Road car offset jumps at vertex");
                        Check(Vector3.Angle((Vector3)a[1],(Vector3)b[1])<.04f,"Road car pitch/yaw jumps at vertex");
                    }
                }
                foreach (double logical in new[]{180.0,300.0,900.0}) foreach (float carLength in new[]{10f,12f})
                {
                    var route = ConsistRoute(logical,!forward); Set(route,"VehicleType",12);
                    for (int vertex = 100; vertex < 140; vertex++)
                    {
                        double d = (forward ? distances[vertex]/length : 1-distances[vertex]/length)*logical;
                        object[] a = {route,d-.0001,d,forward,carLength,null,null};
                        object[] b = {route,d+.0001,d,forward,carLength,null,null};
                        Check((bool)Call(geometry,"SampleBus",a) && (bool)Call(geometry,"SampleBus",b),"Loaded bus disappeared");
                        Near(Vector3.Distance((Vector3)a[5],(Vector3)b[5]),0,.002f,"Bus offset jumps at vertex");
                        Check(Vector3.Angle((Vector3)a[6],(Vector3)b[6])<.04f,"Bus pitch/yaw jumps at vertex");
                    }
                    foreach (double d in new[]{0.0,logical})
                    {
                        object[] pose = {route,d,d,forward,carLength,null,null};
                        object[] center = {route,d,d,forward,null,null};
                        Check((bool)Call(geometry,"SampleBus",pose),"Bus at road endpoint disappears");
                        Check((bool)Call(geometry,"Sample",center),"Bus endpoint missing");
                        Near(Vector3.Distance((Vector3)pose[5],(Vector3)center[4]),0,.001f,"Bus stop center moved");
                        Near(((Vector3)pose[6]).magnitude,1,.001f,"Bus endpoint attitude is invalid");
                    }
                }
            }
        }
        finally { handProperty.SetValue(null,oldHand); }
        BusLaneBlendGeometry(geometry,g,polyline,distances);
    }

    static void BusLaneBlendGeometry(object geometry, object g, object polyline, float[] distances)
    {
        var turns = Enum.ToObject(game.GetType("RoadMarkingTurns"),7);
        Set(g,"startTurns",turns); Set(g,"endTurns",turns);
        Set(g,"startIncoming",Vector2.up); Set(g,"endIncoming",Vector2.up);
        var next = ConsistTrack(geometry,2,new[]{Vector3.zero,Vector3.forward*100});
        Set(next,"rail",false); Set(next,"startIncoming",Vector2.down); Set(next,"endIncoming",Vector2.down);
        var handProperty = game.GetType("CityInfoStore").GetProperty("TrafficHand");
        int oldHand = (int)handProperty.GetValue(null);
        try
        {
            foreach (int hand in new[]{0,1}) foreach (bool forward in new[]{false,true})
            {
                handProperty.SetValue(null,hand);
                var lanes = New("RoadLaneLayout",24f,3f,.5f,0f,6,0);
                Set(g,"lanes",lanes); Set(g,"offset",Mathf.Abs((float)Call(lanes,"Center",0,hand)));
                Set(Get(next,"data"),"end_node_id",forward?3:1);
                var route = ConsistRoute(300,!forward); Set(route,"VehicleType",12);
                var run = New("TransitRouteGeometry+Run");
                Set(run,"path",2); Set(run,"start",300.0); Set(run,"end",400.0);
                Set(run,"from",forward?0f:1f); Set(run,"to",forward?1f:0f); ((IList)Get(route,"runs")).Add(run);
                for (int vertex = 1; vertex < distances.Length-1; vertex++)
                {
                    float remaining = forward ? Property(polyline,"Length")-distances[vertex] : distances[vertex];
                    if (remaining < 12 || remaining > 42) continue;
                    double d = (forward ? distances[vertex]/Property(polyline,"Length") : 1-distances[vertex]/Property(polyline,"Length"))*300;
                    object[] a = {route,d-.0001,d,forward,null,null};
                    object[] b = {route,d+.0001,d,forward,null,null};
                    Check((bool)Call(geometry,"Sample",a) && (bool)Call(geometry,"Sample",b),"Bus lane blend missing");
                    Near(Vector3.Distance((Vector3)a[4],(Vector3)b[4]),0,.002f,"Bus lane blend jumps at vertex");
                }
            }
        }
        finally { handProperty.SetValue(null,oldHand); }
    }

    static void BusRoadPlacement(Component manager, object data, IList sourceList, object geometry, object g)
    {
        Call(manager,"HideUnused",true);
        Set(data,"path_variant",1001); Set(data,"vehicle_model",0);
        Set(data,"cars_per_train",1); Set(data,"vehicle_count",1); Set(data,"length_per_vehicle",12f);
        sourceList.Add(data); Call(manager,"RebuildLines",sourceList);
        var route = ((IList)Get(manager,"lines"))[0];
        var points = RoadPosePoints();
        var line = Line(points);
        Set(g,"full",line); Set(g,"trimmed",line); Set(g,"rail",false); Set(g,"offset",2f);
        Set(g,"trimEnd",Property(line,"Length"));
        ((IDictionary)Get(geometry,"geometry"))[1] = g;
        Vector3 previous = default;
        for (int frame = 0; frame < 40; frame++)
        {
            double time = 10+frame*.01;
            Set(manager,"clock",time); ((HashSet<string>)Get(manager,"used")).Clear();
            Call(manager,"DrawLine",route,points[120]); Call(manager,"HideUnused",false);
            var visible = (IDictionary)Get(manager,"visible"); Check(visible.Count==1,"Road bus renderer disappeared");
            Vector3 direction = ((Vector3[])Get(route,"directions"))[0];
            if (frame > 0) Check(Vector3.Angle(previous,direction)<.3f,"Rendered road bus pitch/yaw jumps");
            previous = direction;
            foreach (DictionaryEntry entry in visible)
                Check(Vector3.Angle(((GameObject)entry.Value).transform.forward,direction)<.03f,"Bus renderer ignores sampled attitude");
        }
        Call(manager,"HideUnused",true); sourceList.Clear(); Call(manager,"RebuildLines",sourceList);
    }

    /// <summary>旅客の配置検証と同じ時計・線路のフィクスチャで、貨物の実FBXと編成内の個体を確かめる。</summary>
    static void FreightConsists(Component manager, object data, IList sourceList, IList journeys, object geometry, object g, object path)
    {
        foreach (var journey in journeys)
        {
            bool reverse = (bool)Get(journey,"reversed");
            var step = ((IList)Get(journey,"steps"))[0];
            Set(step,"from_fraction",reverse?.8f:.2f); Set(step,"to_fraction",reverse?.2f:.8f);
            Set(step,"length_m",300f); Set(step,"duration_seconds",30.0);
        }
        Set(g,"full",Line(Vector3.zero,Vector3.forward*500)); Set(g,"trimEnd",500f);
        ((IDictionary)Get(geometry,"geometry"))[1] = g;
        sourceList.Add(data);
        foreach (int pathVariant in new[]{1006,1007,1008})
        foreach (int variant in new[]{0,1,2})
        foreach (int cars in new[]{2,3,4,6})
        {
            string wagon = pathVariant == 1006 ? "wagon" : pathVariant == 1007 ? "wagon_1" : "wagon_2";
            Call(manager,"HideUnused",true);
            Set(data,"path_variant",pathVariant); Set(data,"vehicle_model",variant);
            Set(data,"cars_per_train",cars); Set(data,"vehicle_count",cars); Set(data,"line_color","#e5171f");
            // 鉱石・石油は15mの公称長にも対応し、機関車を含むすべての車体を同じ長さへ合わせる。
            float length = pathVariant == 1006 ? 20f : 15f;
            Set(data,"length_per_vehicle",length);
            Call(manager,"RebuildLines",sourceList);
            var lines = (IList)Get(manager,"lines");
            Check(lines.Count == 1,"Freight line is not drawable");
            var warmer = (IEnumerator)Call(manager,"WarmPool",lines);
            while (warmer.MoveNext()) { }
            var pools = (IDictionary)Get(manager,"pool");
            Check(pools.Contains("22:" + variant) && pools.Contains("22:" + wagon),"Warmup omits locomotive or wagon");
            var atTurnback = new Dictionary<string,(GameObject car,Vector3 position,Quaternion rotation)>();
            foreach (double time in new[]{0.0,15.0,30.0,33.999,34.0,49.0,64.0,67.999,68.0})
            {
                ((HashSet<string>)Get(manager,"used")).Clear();
                Set(manager,"clock",time); Set(manager,"trainCreationsThisFrame",0);
                Call(manager,"DrawLine",lines[0],Vector3.forward*250); Call(manager,"HideUnused",false);
                var visible = (IDictionary)Get(manager,"visible");
                Check(visible.Count == cars,"Freight consist is incomplete");
                int locomotives = 0;
                foreach (DictionaryEntry pair in visible)
                {
                    var car = (GameObject)pair.Value;
                    bool locomotive = car.name == "22:" + variant;
                    if (locomotive) locomotives++;
                    else Check(car.name == "22:" + wagon,"Wagon depends on locomotive appearance");
                    string part = locomotive ? variant.ToString() : wagon;
                    var lods = car.GetComponent<LODGroup>().GetLODs();
                    Check(lods.Length == 3,"Freight LOD missing");
                    for (int level = 0; level < 3; level++)
                    foreach (var renderer in lods[level].renderers)
                    {
                        string suffix = level == 0 ? "_close" : level == 2 ? "_far" : "";
                        Check(AssetDatabase.GetAssetPath(renderer.GetComponent<MeshFilter>().sharedMesh).EndsWith($"/22/{part}{suffix}.fbx"),"Freight uses wrong FBX/LOD");
                        if (!locomotive)
                            foreach (var material in renderer.sharedMaterials)
                                Check(!material.name.EndsWith("_line"),"Wagon is painted with locomotive livery");
                    }
                    Check(Vector3.Dot(car.transform.forward,Vector3.forward) > .99f,"Freight body flips like a passenger rear cab");
                    if (time == 34 || time == 68)
                    {
                        var previous = atTurnback[(string)pair.Key];
                        Check(previous.car == car,"Turnback replaces a physical freight car");
                        Near(Vector3.Distance(previous.position,car.transform.position),0,.02f,"Freight turnback teleports a car");
                        Near(Quaternion.Angle(previous.rotation,car.transform.rotation),0,.02f,"Freight turnback flips a car");
                    }
                    atTurnback[(string)pair.Key] = (car,car.transform.position,car.transform.rotation);
                }
                Check(locomotives == 1,"Freight needs exactly one locomotive");
                CheckCouplings((Vector3[])Get(lines[0],"positions"),(Vector3[])Get(lines[0],"directions"),length);
            }
            // 貨車を含む奇数・偶数編成も、起伏のある曲線で既存の剛体配置を使う。
            var curve = Line(Vector3.zero,new Vector3(100,25,100),new Vector3(0,50,300),Vector3.forward*500);
            Set(g,"full",curve); Set(g,"trimEnd",Property(curve,"Length"));
            Set(manager,"clock",15.0); Call(manager,"DrawLine",lines[0],Vector3.forward*250);
            CheckCouplings((Vector3[])Get(lines[0],"positions"),(Vector3[])Get(lines[0],"directions"),length);
            Set(g,"full",Line(Vector3.zero,Vector3.forward*500)); Set(g,"trimEnd",500f);
            // 同じ路線・編成の再表示では、実体モデル別のプールから同じ車体群を使う。
            var before = new HashSet<GameObject>();
            foreach (DictionaryEntry pair in (IDictionary)Get(manager,"visible")) before.Add((GameObject)pair.Value);
            Call(manager,"HideUnused",true);
            ((HashSet<string>)Get(manager,"used")).Clear();
            Set(manager,"clock",15.0); Call(manager,"DrawLine",lines[0],Vector3.forward*250);
            foreach (DictionaryEntry pair in (IDictionary)Get(manager,"visible")) Check(before.Contains((GameObject)pair.Value),"Freight pool does not reuse cars");
        }
        Call(manager,"HideUnused",true);
        sourceList.Clear(); Call(manager,"RebuildLines",sourceList);
        game.GetType("TransitLivery").GetMethod("Reset").Invoke(null,null);
    }

    static object ConsistRoute(double length, bool reverse = false)
    {
        var route = New("TransitRouteGeometry+Route");
        var run = New("TransitRouteGeometry+Run");
        Set(run,"path",1); Set(run,"start",0.0); Set(run,"end",length);
        Set(run,"from",reverse?1f:0f); Set(run,"to",reverse?0f:1f);
        ((IList)Get(route,"runs")).Add(run);
        return route;
    }

    static object ConsistTrack(object geometry, int id, Vector3[] points, float offset = 0)
    {
        var g = Activator.CreateInstance(geometry.GetType().GetNestedType("Geometry",BindingFlags.NonPublic));
        var path = New("GeneratedModels.PathSummary");
        Set(path,"path_id",id); Set(path,"travel_direction",0);
        Set(path,"start_node_id",id); Set(path,"end_node_id",id+1);
        var polyline = Line(points);
        Set(g,"data",path); Set(g,"full",polyline); Set(g,"rail",true);
        Set(g,"trimStart",0f); Set(g,"trimEnd",Property(polyline,"Length")); Set(g,"offset",offset);
        ((IDictionary)Get(geometry,"geometry"))[id] = g;
        return g;
    }

    static void CheckCouplings(Vector3[] positions, Vector3[] directions, float length)
    {
        for (int i = 0; i < positions.Length; i++)
        {
            Near(directions[i].magnitude,1,.0001f,"Rail body direction is invalid");
            if (i == 0) continue;
            Near(Vector3.Distance(positions[i-1]-directions[i-1]*length*.5f,
                positions[i]+directions[i]*length*.5f),0,.003f,"Rail coupler detached on curve/grade");
        }
    }

    static void TransitConsistGeometry()
    {
        var geometry = New("TransitRouteGeometry",new object[]{null});
        var points = new Vector3[601];
        for (int i = 0; i < points.Length; i++)
        {
            float s = i;
            points[i] = new Vector3(70*Mathf.Sin(s/70),s*.025f+.08f*(i%2),70*(1-Mathf.Cos(s/70)));
        }
        ConsistTrack(geometry,1,points,2);
        foreach (int cars in new[]{1,2,3,6}) foreach (double logicalLength in new[]{180.0,600.0,1800.0})
        {
            var route = ConsistRoute(logicalLength);
            var returning = ConsistRoute(logicalLength,true);
            var p = new Vector3[cars]; var t = new Vector3[cars];
            var backP = new Vector3[cars]; var backT = new Vector3[cars];
            var previousP = new Vector3[cars]; var previousT = new Vector3[cars];
            for (int frame = 0; frame < 300; frame++)
            {
                double d = logicalLength*(.4+frame*.0001);
                Check((bool)Call(geometry,"SampleConsist",route,d,true,20f,p,t),"Loaded curved consist vanished");
                CheckCouplings(p,t,20);
                if (frame > 0) for (int car = 0; car < cars; car++)
                {
                    Check(Vector3.Distance(p[car],previousP[car])<.1f,"Rail body position jumps");
                    Check(Vector3.Angle(t[car],previousT[car])<.2f,"Rail pitch/yaw jumps at polyline vertex");
                }
                Array.Copy(p,previousP,cars); Array.Copy(t,previousT,cars);
            }
            // 単線上の逆走で個体順を逆転すると、停止中の全車体位置・姿勢が一致する。
            var g = ((IDictionary)Get(geometry,"geometry"))[1]; Set(g,"offset",0f);
            Check((bool)Call(geometry,"SampleConsist",route,logicalLength*.5,true,20f,p,t),"Outbound consist missing");
            Check((bool)Call(geometry,"SampleConsist",returning,logicalLength*.5,false,20f,backP,backT),"Return consist missing");
            for (int car = 0; car < cars; car++)
            {
                Near(Vector3.Distance(p[car],backP[cars-1-car]),0,.003f,"Turnback moves physical rail car");
                Check(Vector3.Angle(t[car],-backT[cars-1-car])<.04f,"Turnback changes rail body attitude");
            }
            Set(g,"offset",2f);
        }

        var routeSingle = ConsistRoute(600);
        for (int vertex = 100; vertex < 200; vertex++)
        {
            // オフセット付き点列そのものにも、短い線分の切替による横跳びがない。
            var polyline = Get(((IDictionary)Get(geometry,"geometry"))[1],"full");
            float[] distances = (float[])Get(polyline,"distances");
            double d = distances[vertex]/Property(polyline,"Length")*600;
            object[] a = {routeSingle,d-.0001,d,true,null,null};
            object[] b = {routeSingle,d+.0001,d,true,null,null};
            Check((bool)Call(geometry,"Sample",a) && (bool)Call(geometry,"Sample",b),"Vertex sample missing");
            Near(Vector3.Distance((Vector3)a[4],(Vector3)b[4]),0,.001f,"Track offset jumps at vertex");
        }
        TransitConsistConnector(geometry);
        // 実都市の大きい絶対座標でも、floatの丸めによって編成を非表示にしない。
        Call(geometry,"Clear");
        for (int i = 0; i < points.Length; i++) points[i] += new Vector3(30000,0,-30000);
        ConsistTrack(geometry,1,points);
        var farP = new Vector3[6]; var farT = new Vector3[6];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int frame = 0; frame < 300; frame++)
        {
            Check((bool)Call(geometry,"SampleConsist",routeSingle,240.0+frame*.05,true,20f,farP,farT),"Consist disappears far from origin");
            for (int car = 1; car < farP.Length; car++)
                Near(Vector3.Distance(farP[car-1]-farT[car-1]*10,farP[car]+farT[car]*10),0,.02f,"Distant coupler detached");
        }
        Debug.Log($"Consist QA: 300 six-car samples including reflection/assertions = {timer.Elapsed.TotalMilliseconds:F1} ms");
    }

    static void TransitConsistConnector(object geometry)
    {
        Call(geometry,"Clear");
        var a = ConsistTrack(geometry,1,new[]{Vector3.zero,Vector3.forward*100});
        var b = ConsistTrack(geometry,2,new[]{Vector3.forward*100,new Vector3(100,5,100)});
        Set(a,"trimEnd",80f); Set(b,"trimStart",20f);
        var route = ConsistRoute(50);
        var next = New("TransitRouteGeometry+Run");
        Set(next,"path",2); Set(next,"start",50.0); Set(next,"end",2050.0); Set(next,"from",0f); Set(next,"to",1f);
        ((IList)Get(route,"runs")).Add(next);
        var p = new Vector3[3]; var t = new Vector3[3];
        Check(!(bool)Call(geometry,"SampleConsist",route,50.0,true,20f,p,t),"Missing connector bridged");
        var curve = game.GetType("VehiclePolyline").GetMethod("Connector").Invoke(null,
            new object[]{new Vector3(0,.1f,80),Vector3.forward,new Vector3(20,1.1f,100),Vector3.right,1f/3f});
        var connectors = (IDictionary)Get(geometry,"railConnectors");
        var options = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(curve.GetType()));
        options.Add(curve); connectors[2] = options;
        double connectorEnd = 50+20/Property(Get(b,"full"),"Length")*2000;
        for (double distance = 41; distance < connectorEnd+10; distance += .5)
        {
            Check((bool)Call(geometry,"SampleConsist",route,distance,true,20f,p,t),"Consist missing across connector/unequal distance scales");
            CheckCouplings(p,t,20);
        }
        foreach (double boundary in new[]{40.0,50.0,connectorEnd})
        {
            object[] before = {route,boundary-.00001,boundary,true,null,null};
            object[] after = {route,boundary+.00001,boundary,true,null,null};
            Check((bool)Call(geometry,"Sample",before) && (bool)Call(geometry,"Sample",after),"Connector boundary missing");
            Near(Vector3.Distance((Vector3)before[4],(Vector3)after[4]),0,.002f,"Connector boundary position jumps");
        }
        ((IDictionary)Get(geometry,"geometry")).Remove(2);
        Check(!(bool)Call(geometry,"SampleConsist",route,50.0,true,20f,p,t),"Unloaded path bridged");
    }
}
