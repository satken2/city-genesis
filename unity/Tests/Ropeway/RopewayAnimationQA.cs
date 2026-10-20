using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class RopewayAnimationQA
{
    static Assembly game;
    static int checks;
    static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Dictionary<int, List<Vector3>> points = new();
    static object Get(object o, string n) => o.GetType().GetField(n, Flags).GetValue(o);
    static void Set(object o, string n, object v) => o.GetType().GetField(n, Flags).SetValue(o, v);
    static object Call(object o, string n, params object[] a) => o.GetType().GetMethod(n, Flags).Invoke(o, a);
    static object Static(string t, string n, params object[] a) => game.GetType(t).GetMethod(n, Flags).Invoke(null, a);
    static float Value(object o, string n) => Convert.ToSingle(o.GetType().GetProperty(n).GetValue(o));
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static void Near(float a, float b, float tolerance, string message) => Check(Mathf.Abs(a-b) < tolerance, $"{message}: {a} / {b}");
    static object Path(int id, int start, int end, bool station, params Vector3[] p)
    {
        var result = Activator.CreateInstance(game.GetType("GeneratedModels.PathSummary"));
        // Use the API types from the main assembly as-is.
        foreach (var pair in new Dictionary<string,int> { {"path_id",id}, {"start_node_id",start}, {"end_node_id",end},
            {"path_type",6}, {"path_variant",station?602:601}, {"status",1} })
            result.GetType().GetField(pair.Key).SetValue(result, pair.Value);
        points[id] = p.ToList(); return result;
    }
    static IList Routes(params object[] paths)
    {
        var array = Array.CreateInstance(game.GetType("GeneratedModels.PathSummary"), paths.Length);
        for (int i=0;i<paths.Length;i++) array.SetValue(paths[i],i);
        return (IList)Static("RopewayNetwork", "Build", array);
    }
    static List<Vector3> Resolve<T>(T path) => points[(int)path.GetType().GetField("path_id").GetValue(path)];
    static object Geometry(object route)
    {
        var type = typeof(Func<,>).MakeGenericType(game.GetType("GeneratedModels.PathSummary"), typeof(List<Vector3>));
        var resolver = Delegate.CreateDelegate(type, typeof(RopewayAnimationQA).GetMethod("Resolve", Flags).MakeGenericMethod(game.GetType("GeneratedModels.PathSummary")));
        return Static("RopewayGeometry", "Build", route, resolver);
    }
    static Vector3 Sample(object geometry, float distance)
    {
        object[] a = {distance,null,null}; Call(geometry,"Sample",a); return (Vector3)a[1];
    }
    static object Straight(float stationLength=12, float rise=0)
    {
        var a = Path(1,1,2,true, Vector3.zero, new Vector3(0,0,stationLength));
        var b = Path(2,3,2,false, new Vector3(0,rise,112), new Vector3(0,0,stationLength));
        var c = Path(3,3,4,true, new Vector3(0,rise,112),new Vector3(0,rise,112+stationLength));
        return Geometry(Routes(a,b,c)[0]);
    }
    static void Topology()
    {
        var a=Path(1,1,2,true); var b=Path(2,3,2,false); var c=Path(3,3,4,true);
        Check(Routes(b).Count==0,"No station");
        Check(Routes(a,b).Count==0,"One station");
        Check(Routes(a,c).Count==0,"Disconnected stations");
        Check(Routes(a,Path(4,2,3,true)).Count==0,"Split station is one station");
        var route=Routes(a,b,c,Path(5,0,1,false),Path(6,4,5,false));
        Check(route.Count==1 && ((IList)Get(route[0],"Links")).Count==3,"Trim stationless tails");
        Check(!(bool)Get(((IList)Get(route[0],"Links"))[1],"Forward"),"Reversed path direction");
        b.GetType().GetField("status").SetValue(b,2);
        Check(Routes(a,b,c).Count==0,"Unfinished segment breaks route");
        b.GetType().GetField("status").SetValue(b,1);
        Check(Routes(a,b,c,Path(7,2,8,false)).Count==0,"Branch excluded");
        Check((bool)Get(Routes(a,b,c,Path(8,4,1,false))[0],"Closed"),"Closed network");
    }
    static void Motion()
    {
        foreach(float size in new[]{12f,2f}) foreach(float rise in new[]{0f,35f})
        {
            var geometry=Straight(size,rise);
            Check(geometry!=null,"Valid geometry");
            var legs=(IList)Get(geometry,"Legs");
            for(int i=0;i<legs.Count;i++)
            {
                var p=(Vector3[])Get(Get(legs[i],"Line"),"points");
                var q=(Vector3[])Get(Get(legs[(i+1)%legs.Count],"Line"),"points");
                Near(Vector3.Distance(p[p.Length-1],q[0]),0,.001f,"Continuous joins and returns");
            }
            var terminals=(IList)Get(geometry,"Terminals");
            Check(terminals.Count==2,"Two bullwheels");
            var center=(Vector3)Get(terminals[1],"Center");
            Near(center.z,Mathf.Min(4,size/2),.001f,"Short station inset");
            if(size<4.2f) Check(center.z-2.1f<0,"Short station return protrudes");
            Near(Sample(geometry,0).x,2.1f,.001f,"Cable offset");
            float length=Value(geometry,"Length");
            for(int cabin=0;cabin<(int)Value(geometry,"CabinCount");cabin++)
            {
                float a=(float)Call(geometry,"Distance",0d,cabin,0d);
                float b=(float)Call(geometry,"Distance",(double)length/4,cabin,0d);
                Near(Vector3.Distance(Sample(geometry,a),Sample(geometry,b)),0,.002f,"Periodic motion");
                float before=(float)Call(geometry,"Distance",length/4d-.001,cabin,0d);
                float after=(float)Call(geometry,"Distance",length/4d+.001,cabin,0d);
                Check(Vector3.Distance(Sample(geometry,before),Sample(geometry,after))<.009,"No teleport at wrap");
            }
        }
        var loop=Geometry(Routes(Path(1,1,2,true,Vector3.zero,new Vector3(0,0,60)),
            Path(2,2,3,false,new Vector3(0,0,60),new Vector3(60,0,60)),
            Path(3,3,4,true,new Vector3(60,0,60),new Vector3(60,0,0)),
            Path(4,4,1,false,new Vector3(60,0,0),Vector3.zero))[0]);
        Check(((IList)Get(loop,"Terminals")).Count==0,"No terminal on closed circuit");
        DrawPositions(loop);
        DrawPositions(Straight());
        for(int i=0;i<100;i++)
        {
            Check((float)Call(loop,"Distance",i*17d,0,0d)<Value(loop,"ForwardLength"),"Forward stays on its loop");
            Check((float)Call(loop,"Distance",i*17d,1,0d)>=Value(loop,"ForwardLength"),"Reverse stays on its loop");
        }
        var mismatch=Routes(Path(1,1,2,true,Vector3.zero,Vector3.forward*12),Path(2,2,3,false,Vector3.forward*15,Vector3.forward*80),Path(3,3,4,true,Vector3.forward*80,Vector3.forward*92));
        Check(Geometry(mismatch[0])==null,"Missing geometry is not bridged");
    }
    static void Visibility()
    {
        var source=new GameObject("Path");
        var lod=source.AddComponent(game.GetType("DrawLodInfo"));
        var data=Path(1,1,2,true);
        var field=lod.GetType().GetField("drawLod");
        foreach(string name in new[]{"Far","Normal","Close"})
        {
            field.SetValue(lod,Enum.Parse(game.GetType("DrawLod"),name));
            Check((bool)Static("RopewayCabinManager","CanPresent",source,data,1)==(name!="Far"),"LOD "+name);
        }
        var presentation=source.AddComponent(game.GetType("PathPresentation"));
        Set(presentation,"pending",true);
        Check(!(bool)Static("RopewayCabinManager","CanPresent",source,data,1),"Pending replacement hidden");
        var body=new GameObject("Models/Paths/p601/body"); body.transform.SetParent(source.transform);
        var renderer=body.AddComponent<MeshRenderer>(); renderer.forceRenderingOff=true;
        Call(presentation,"HidePending",source);
        Call(presentation,"SetRendererForceOff",renderer,false);
        Check(renderer.forceRenderingOff,"Cable restoration respects pending presentation");
        Set(presentation,"parts",Array.CreateInstance(game.GetType("SplinePartLod"),0));
        Call(presentation,"TryCommit");
        Check(!renderer.forceRenderingOff,"Presentation restores requested cable visibility");
        Set(presentation,"pending",false);
        data.GetType().GetField("status").SetValue(data,2);
        Check(!(bool)Static("RopewayCabinManager","CanPresent",source,data,1),"Construction hidden");
        data.GetType().GetField("status").SetValue(data,1);
        source.SetActive(false);
        Check(!(bool)Static("RopewayCabinManager","CanPresent",source,data,1),"Inactive source");
        UnityEngine.Object.DestroyImmediate(source);
    }
    static void DrawPositions(object geometry)
    {
        var root=new GameObject("CabinManagerQA");
        var manager=root.AddComponent(game.GetType("RopewayCabinManager"));
        var state=Activator.CreateInstance(game.GetType("RopewayCabinManager+State"),true);
        Set(state,"Geometry",geometry);
        var matrices=(List<Matrix4x4>)Get(manager,"cabinMatrices");
        foreach(double time in new[]{0d,.5d,86400d,31536000d})
        {
            matrices.Clear(); Set(manager,"clock",time);
            foreach(object leg in (IList)Get(geometry,"Legs")) Call(manager,"AddCabins",state,leg);
            int count=(int)Value(geometry,"CabinCount");
            Check(matrices.Count==count,$"Visible-leg draw count matches full loop at {time}: {matrices.Count}/{count}, closed={geometry.GetType().GetProperty("Closed").GetValue(geometry)}");
            for(int i=0;i<count;i++)
            {
                var p=Sample(geometry,(float)Call(geometry,"Distance",time,i,0d));
                Check(matrices.Any(m=>Vector3.Distance(m.GetColumn(3),p)<.002f),"Analytic draw placement");
            }
        }
        matrices.Clear(); Set(manager,"center",Vector3.one*10000);
        foreach(object leg in (IList)Get(geometry,"Legs")) Call(manager,"AddCabins",state,leg);
        Check(matrices.Count==0,"Offscreen cabins not submitted");
        UnityEngine.Object.DestroyImmediate(root);
    }
    static void Snapshots()
    {
        var root=new GameObject("RopewayStoreQA"); var store=root.AddComponent(game.GetType("RopewayNetworkStore"));
        var data=Get(store,"paths") as IList;
        object Page(int id, int? cursor)
        {
            var page=Activator.CreateInstance(game.GetType("PagedPathListResponse"));
            var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(game.GetType("ConstructionPathSummary")));
            var path=Activator.CreateInstance(game.GetType("ConstructionPathSummary")); Set(path,"path_id",id); list.Add(path);
            Set(page,"paths",list); Set(page,"next_cursor",cursor); return page;
        }
        void Reply(IEnumerator fetch,object page)
        {
            Check(fetch.MoveNext(),"Fetch awaits page");
            var request=fetch.Current;
            var callback=request.GetType().GetFields(Flags).Select(f=>f.GetValue(request)).OfType<Delegate>().First();
            callback.DynamicInvoke(page);
        }
        var first=(IEnumerator)Call(store,"Fetch",Get(store,"generation"));
        Reply(first,Page(1,500)); Check(data.Count==0,"Partial page not published");
        Reply(first,Page(2,null)); Check(!first.MoveNext() && data.Count==2,"Atomic two-page snapshot");
        var failed=(IEnumerator)Call(store,"Fetch",Get(store,"generation"));
        Reply(failed,Page(3,500)); Check(failed.MoveNext(),"Second page request");
        Check(!failed.MoveNext() && data.Count==2,"Failed page retains prior snapshot");
        var stale=(IEnumerator)Call(store,"Fetch",Get(store,"generation"));
        Reply(stale,Page(9,null)); Call(store,"Clear");
        Check(!stale.MoveNext() && data.Count==0,"City reset discards late response");
        UnityEngine.Object.DestroyImmediate(root);
    }
    static GameObject Model(string name, Vector3 position, Quaternion rotation)
    {
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/Paths/"+name+".prefab");
        Check(prefab!=null,"Generated model "+name);
        var go=UnityEngine.Object.Instantiate(prefab,position,rotation);
        foreach(var renderer in go.GetComponentsInChildren<Renderer>())
            renderer.sharedMaterials=renderer.sharedMaterials.Select(m=> {var mat=new Material(Shader.Find("Standard")); mat.color=m.color; return mat;}).ToArray();
        return go;
    }
    static void Preview(string root)
    {
        var geometry=Straight();
        var material=new Material(Shader.Find("Standard")){color=new Color(.25f,.27f,.3f)};
        foreach(object leg in (IList)Get(geometry,"Legs"))
        {
            var p=(Vector3[])Get(Get(leg,"Line"),"points");
            for(int i=1;i<p.Length;i++)
            {
                var cable=GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                cable.transform.position=(p[i-1]+p[i])/2;
                cable.transform.up=(p[i]-p[i-1]).normalized;
                cable.transform.localScale=new Vector3(.08f,Vector3.Distance(p[i],p[i-1])/2,.08f);
                cable.GetComponent<Renderer>().sharedMaterial=material;
            }
        }
        var terminal=((IList)Get(geometry,"Terminals"))[1];
        Vector3 center=(Vector3)Get(terminal,"Center");
        Quaternion rotation=(Quaternion)Get(terminal,"Rotation");
        Model("p602/terminal_rotor",center,rotation); Model("p602/terminal_support",center,rotation);
        Model("p602/station_shell",Vector3.forward*6+Vector3.up*.1f,Quaternion.identity).transform.localScale=new Vector3(1,1,1.5f);
        Model("p602/pillar_cap",Vector3.forward*8,Quaternion.identity);
        foreach(float d in new[]{0f,5f,Value(geometry,"Length")-3.3f})
        {
            object[] a={d,null,null}; Call(geometry,"Sample",a); var t=(Vector3)a[2]; t.y=0;
            var cabin=Model("p601/cabin",(Vector3)a[1],Quaternion.LookRotation(t));
            var bounds=cabin.GetComponentInChildren<MeshFilter>().sharedMesh.bounds;
            Check(bounds.min.y> -3.36f && bounds.max.y<.2f,"Cabin pivot and floor clearance");
        }
        RenderSettings.ambientLight=new Color(.7f,.7f,.7f);
        var light=new GameObject("Sun").AddComponent<Light>(); light.type=LightType.Directional; light.intensity=1.2f; light.transform.rotation=Quaternion.Euler(45,-30,0);
        var camera=new GameObject("Camera").AddComponent<Camera>(); camera.backgroundColor=new Color(.72f,.81f,.87f); camera.clearFlags=CameraClearFlags.SolidColor;
        camera.transform.position=new Vector3(13,7,-14); camera.transform.LookAt(new Vector3(0,-1,5)); camera.fieldOfView=38;
        var target=new RenderTexture(1200,900,24); camera.targetTexture=target; camera.Render(); RenderTexture.active=target;
        var image=new Texture2D(1200,900,TextureFormat.RGB24,false); image.ReadPixels(new Rect(0,0,1200,900),0,0); image.Apply();
        File.WriteAllBytes(System.IO.Path.Combine(root,"terminal.png"),image.EncodeToPNG());
    }
    public static void Run()
    {
        string root=Directory.GetCurrentDirectory();
        try
        {
            var args=Environment.GetCommandLineArgs(); string source=args[Array.IndexOf(args,"-sourceRoot")+1];
            AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{
                foreach(string dir in new[]{"Temp/bin/Debug","Library/ScriptAssemblies","Assets/Plugins"})
                {string file=System.IO.Path.Combine(source,dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);} return null;};
            ModelImportSettings.EnableReadWriteAll(); ModelPrefabGenerator.SyncAll();
            game=Assembly.LoadFrom(System.IO.Path.Combine(source,"Temp/bin/Debug/Assembly-CSharp.dll"));
            Topology(); Motion(); Visibility(); Snapshots(); Preview(root);
            File.WriteAllText(System.IO.Path.Combine(root,"animation-result.txt"),$"PASS ropeway animation: {checks} checks; topology, motion, short stations, closed loops, LOD and actual models");
            EditorApplication.Exit(0);
        }
        catch(Exception e){File.WriteAllText(System.IO.Path.Combine(root,"animation-result.txt"),e.ToString());Debug.LogException(e);EditorApplication.Exit(1);}
    }
}
