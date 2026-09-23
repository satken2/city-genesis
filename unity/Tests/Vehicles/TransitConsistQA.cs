using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static partial class VehicleQA
{
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
