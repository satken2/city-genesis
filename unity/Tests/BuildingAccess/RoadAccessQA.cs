using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 本編DLLと実FBXで、モデル内の間口・決定論性・片側の切断・登録削除・接合面を検査する。
public static class RoadAccessQA
{
    static Assembly asm;
    static string root, output;
    static int checks;
    static IDictionary models, agentMasters, pathMasters;
    static Type T(string name) => asm.GetType(name, true);
    static object New(string name) => Activator.CreateInstance(T(name));
    static object Get(object target, string name) => target.GetType().GetField(name)?.GetValue(target) ?? target.GetType().GetProperty(name).GetValue(target);
    static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);
    static object Call(string type, string method, params object[] args) => T(type).GetMethod(method).Invoke(null, args);
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Manager(string name)
    {
        var instance = new GameObject(name).AddComponent(T(name));
        T(name).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, instance);
        return instance;
    }
    static IDictionary Dictionary(object manager, string name) => (IDictionary)manager.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs(); root = args[Array.IndexOf(args, "-sourceRoot") + 1]; output = root + "/Temp/BuildingAccessQA";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" })
                { string file = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll"); if (File.Exists(file)) return Assembly.LoadFrom(file); }
                return null;
            };
            asm = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            var master = Manager("MasterDataStore"); agentMasters = Dictionary(master, "agentMasterById"); pathMasters = Dictionary(master, "pathMasterById");
            models = Dictionary(Manager("ModelManager"), "_modelCache");
            AssetDatabase.Refresh(); ModelPrefabGenerator.SyncAll();
            foreach (string file in Directory.GetFiles("Assets/Models", "*.fbx", SearchOption.AllDirectories))
            {
                string path = file.Replace('\\', '/'); var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
                models[path.Substring("Assets/".Length).Replace(".fbx", "")] = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            ModelMarkers(); Layout(); IndexAndRoad(); Lifecycle();
            File.WriteAllText(output + "/result.txt", "PASS building road access: " + checks + " checks"); EditorApplication.Exit(0);
        }
        catch (Exception e) { File.WriteAllText(output + "/result.txt", e.ToString()); Debug.LogException(e); EditorApplication.Exit(1); }
    }
    static Vector2[] Rectangle(float w = 80, float h = 65) => new[] { new Vector2(-w/2,0), new Vector2(w/2,0), new Vector2(w/2,h), new Vector2(-w/2,h) };
    static IList Openings(object plan) => (IList)Get(plan, "Openings");
    static void ModelMarkers()
    {
        string expected = null;
        foreach (string suffix in new[] { "", "_close", "_far" })
        {
            var model = (GameObject)models["Models/Buildings/a1081/0/top-prebuilt" + suffix];
            Check(model != null, "FBX missing " + suffix);
            var openings = (IList)Call("BuildingRoadAccess", "ReadModel", model, Quaternion.Euler(0,90,0));
            Check(openings.Count == 2, "FBX must contain entry and exit " + suffix);
            var wrapper=Resources.Load<GameObject>("Models/Buildings/a1081/0/top-prebuilt"+suffix);
            Check(wrapper!=null && ((IList)Call("BuildingRoadAccess","ReadModel",wrapper,Quaternion.Euler(0,90,0))).Count==2,"Resources wrapper must retain markers");
            var signatures = new List<string>();
            foreach (var opening in openings)
            {
                Vector3 a = (Vector3)Get(opening,"A"), b = (Vector3)Get(opening,"B"), outward = (Vector3)Get(opening,"Outward");
                string flow = (string)Get(opening,"Flow");
                Check(Mathf.Abs(Vector3.Distance(a,b) - (flow == "entry" ? 45 : 10)) < .01f, "Marker width " + flow);
                Check(Mathf.Abs(a.y - .085f) < .002f, "Marker height");
                Check(Mathf.Abs(a.z + 30) < .01f && Mathf.Abs(b.z + 30) < .01f, "Marker must face front, actual " + a + " " + b);
                Check(Vector3.Dot(outward, Vector3.back) > .999f, "Marker outward direction");
                signatures.Add(flow + a.ToString("F3") + b.ToString("F3"));
            }
            string signature = string.Join(";",signatures);
            if (expected == null) expected = signature; else Check(expected == signature,"LOD changed markers");
        }
    }
    static void Layout()
    {
        bool split = false, joined = false;
        for (int seed = 0; seed <= 12; seed++)
        {
            float visual = seed / 12f;
            var plan = Call("OpenSpaceLayout","Create",Rectangle(),11,visual);
            var again = Call("OpenSpaceLayout","Create",Rectangle(),11,visual);
            var openings = Openings(plan); var other = Openings(again);
            Check(openings.Count == other.Count && openings.Count > 0,"Deterministic count");
            split |= openings.Count == 2; joined |= openings.Count == 1;
            for (int i=0;i<openings.Count;i++)
            {
                Vector3 a=(Vector3)Get(openings[i],"A"), b=(Vector3)Get(openings[i],"B");
                Check(a==(Vector3)Get(other[i],"A") && b==(Vector3)Get(other[i],"B"),"Deterministic opening");
                Check(Mathf.Abs(a.z)<.001f && Mathf.Abs(b.z)<.001f,"Opening must be on front edge");
                Check(Vector3.Distance(a,b)>9f,"Opening must include rounded returns");
            }
            var rotation=Quaternion.Euler(0,37,0); var translation=new Vector3(150,0,-80);
            var rotated=Rectangle().Select(p=>rotation*new Vector3(p.x,0,p.y)+translation).Select(p=>new Vector2(p.x,p.z)).ToArray();
            var transformed=Openings(Call("OpenSpaceLayout","Create",rotated,11,visual));
            Check(transformed.Count==openings.Count,"Rotating changed opening count");
            for(int i=0;i<openings.Count;i++)
            {
                var point=(Vector3)Get(openings[i],"A"); var actual=(Vector3)Get(transformed[i],"A");
                Check(Vector3.Distance(rotation*point+translation,actual)<.003f,"Rotating changed opening position");
            }
        }
        Check(split&&joined,"Both rotary layouts must be covered");
        var fallback=Call("OpenSpaceLayout","Create",Rectangle(30,12),11,.5f);
        Check(!(bool)Get(fallback,"HasLoop") && Openings(fallback).Count==1,"Drive-by fallback opening");
        Check(Openings(Call("OpenSpaceLayout","Create",Rectangle(2,2),11,.5f)).Count==0,"Tiny site has no opening");
    }
    static object Building(int variant, int id=1)
    {
        var b=New("GeneratedModels.BuildingSummary"); Set(b,"agent_id",id); Set(b,"variant",variant); Set(b,"status",1); Set(b,"floors",1);
        Set(b,"visual_variant2",.5f); Set(b,"center_point",new List<object>{0f,variant==1081?30f:0f});
        Vector2[] poly=variant==1081?new[]{new Vector2(-75,-30),new Vector2(75,-30),new Vector2(75,30),new Vector2(-75,30)}:Rectangle();
        Set(b,"polygon",poly.Select(p=>(object)new List<object>{p.x,p.y}).ToList());
        return b;
    }
    static List<Vector3> RoadPoints(float height=.1f,bool curve=false) => Enumerable.Range(0,41).Select(i=>new Vector3(-100+i*5,height,-8+(curve?2*Mathf.Sin(i/40f*Mathf.PI):0))).ToList();
    static object Resolve(object spline,IList openings)
    {
        var array=Array.CreateInstance(T("BuildingRoadAccessIndex+Opening"),openings.Count);openings.CopyTo(array,0);
        return Call("RoadAccessPlan","Resolve",spline,16f,array);
    }
    static object Spline(GameObject go,List<Vector3> points) => Call("RoadPath","BuildSpline",go,points,true);
    static void IndexAndRoad()
    {
        foreach(int variant in new[]{3002,1081})
        {
            var master=New("MasterDataStore+AgentMasterItem");Set(master,"model_type",variant==3002?11:5);Set(master,"height_per_floor",.3f);Set(master,"draw_floor",true);agentMasters[variant]=master;
        }
        var pathMaster=New("PathMasterItem");Set(pathMaster,"width",16f);Set(pathMaster,"sidewalk_width",3f);Set(pathMaster,"side_width",.5f);Set(pathMaster,"lanes",2);
        foreach(string key in new[]{"street_tree_type_allowed","streetlight_type_allowed","fence_type_allowed"})Set(pathMaster,key,true);
        Set(pathMaster,"tree_spacing",10f);pathMasters[221]=pathMaster;
        foreach(int variant in new[]{3002,1081})
        {
            Call("BuildingRoadAccessIndex","Clear"); var building=Building(variant);Call("BuildingRoadAccessIndex","Register",building);
            var candidates=(IList)Call("BuildingRoadAccessIndex","Query",RoadPoints(),20f);Check(candidates.Count>0,"Index registration "+variant);
            var container=new GameObject("Plan");var spline=Spline(container,RoadPoints());var plan=Resolve(spline,candidates);
            Check(((IList)Get(plan,"Connections")).Count==candidates.Count,"All front openings must meet road "+variant);
            Check(((IList)Get(plan,"Left")).Count>0 && ((IList)Get(plan,"Right")).Count==0,"Only facility side must open");
            var left=(List<Vector2>)Get(plan,"Left");float mid=(left[0].x+left[0].y)*.5f;
            Check((bool)plan.GetType().GetMethod("Blocks").Invoke(plan,new object[]{-1,mid,1.5f}),"Tree clearance");
            Check(!(bool)plan.GetType().GetMethod("Blocks").Invoke(plan,new object[]{1,mid,1.5f}),"Opposite trees stay");
            var elevated=new GameObject("Elevated");var high=Resolve(Spline(elevated,RoadPoints(6)),candidates);Check(((IList)Get(high,"Connections")).Count==0,"No elevated connection");
            var far=new GameObject("Far");var distant=RoadPoints().Select(p=>p+Vector3.back*12).ToList();Check(((IList)Get(Resolve(Spline(far,distant),candidates),"Connections")).Count==0,"No distant connection");
            var curved=new GameObject("Curved");var curve=Resolve(Spline(curved,RoadPoints(.1f,true).Select(p=>p+Vector3.back*2).ToList()),candidates);
            Check(((IList)Get(curve,"Connections")).Count==candidates.Count,"Curve keeps openings");
            var reversed=new GameObject("Reversed");var rev=RoadPoints();rev.Reverse();var reversedPlan=Resolve(Spline(reversed,rev),candidates);
            Check(((IList)Get(reversedPlan,"Right")).Count>0 && ((IList)Get(reversedPlan,"Left")).Count==0,"Direction reversal swaps side");
            var p=New("GeneratedModels.PathSummary");Set(p,"path_id",1);Set(p,"path_type",1);Set(p,"path_variant",221);Set(p,"status",1);Set(p,"curved",true);Set(p,"follow_terrain",-1);
            Set(p,"start_point",new List<object>{-100f,0f,-8f});Set(p,"end_point",new List<object>{100f,0f,-8f});Set(p,"street_tree_type",1);Set(p,"streetlight_type",1);Set(p,"fence_type",1);
            Check(((IList)Get(Call("RoadAccessPlan","Create",p,spline,RoadPoints(),16f),"Connections")).Count==candidates.Count,"Runtime access query");
            var setback=New("SidewalkSetback");var roadResult=T("RoadPath").GetMethod("Generate").Invoke(New("RoadPath"),new[]{p,RoadPoints(),setback,setback});
            var road=(GameObject)Get(roadResult,"GeneratedObject");Check(road.transform.Find("BuildingAccessSurface")!=null,"Connection surface generated "+variant);
            foreach(var mesh in road.GetComponentsInChildren<MeshFilter>())Check(mesh.sharedMesh.vertices.All(v=>float.IsFinite(v.x+v.y+v.z)),"Finite road geometry");
            int oppositeTrees=0, sidewalks=0;
            foreach(var transform in road.GetComponentsInChildren<Transform>())
            {
                if(transform.name.StartsWith("tree(Clone)"))
                {
                    int side=transform.position.z>-8?-1:1;
                    Check(!(bool)plan.GetType().GetMethod("Blocks").Invoke(plan,new object[]{side,transform.position.x+100f,1.5f}),"Actual tree occupies opening");
                    if(side==1)oppositeTrees++;
                }
                var filter=transform.GetComponent<MeshFilter>();
                if(filter==null||!transform.name.EndsWith("/sidewalk"))continue;
                var vertices=filter.sharedMesh.vertices.Select(transform.TransformPoint).ToArray();
                if(vertices.Average(v=>v.z)<-8)continue;
                sidewalks++;
                float lo=vertices.Min(v=>v.x)+100,hi=vertices.Max(v=>v.x)+100;
                Check(!left.Any(r=>lo<r.y-.05f&&hi>r.x+.05f),"Actual sidewalk spans opening");
            }
            Check(oppositeTrees>0&&sidewalks>0,"Actual opposite trees and clipped sidewalks exist");
            var result=T(variant==3002?"OpenSpaceBuildingMeshBuilder":"PreBuiltBuildingMeshBuilder").GetMethod("Generate").Invoke(New(variant==3002?"OpenSpaceBuildingMeshBuilder":"PreBuiltBuildingMeshBuilder"),new[]{building,(object)true});
            var facility=new GameObject("Facility");facility.transform.position=new Vector3(0,0,variant==1081?30:0);facility.AddComponent<MeshFilter>().sharedMesh=(Mesh)Get(result,"Mesh");facility.AddComponent<MeshRenderer>().sharedMaterials=(Material[])Get(result,"Materials");
            Render(new[]{road,facility},output+"/a"+variant+".png",variant==1081?100:62);
            foreach(string lod in new[]{"Normal","Close","Far"})
            {
                T("BuildingMeshUtils").GetProperty("CurrentDrawLod").SetValue(null,Enum.Parse(T("DrawLod"),lod));
                var mouths=(IList)Call("BuildingRoadAccess","Resolve",building);
                Check(mouths.Count==candidates.Count,"LOD changed mouth count");
                for(int i=0;i<mouths.Count;i++)Check((Vector3)Get(mouths[i],"A")== (Vector3)Get(Get(candidates[i],"Local"),"A"),"LOD changed mouth position");
            }
            T("BuildingMeshUtils").GetProperty("CurrentDrawLod").SetValue(null,Enum.Parse(T("DrawLod"),"Normal"));
            Call("BuildingRoadAccessIndex","Register",building);Check(((IList)Call("BuildingRoadAccessIndex","Query",RoadPoints(),20f)).Count==candidates.Count,"Registration does not duplicate");
            Set(building,"status",2);Call("BuildingRoadAccessIndex","Register",building);Check(((IList)Call("BuildingRoadAccessIndex","Query",RoadPoints(),20f)).Count==candidates.Count,"Paused facility keeps access");
            Set(building,"status",4);Call("BuildingRoadAccessIndex","Register",building);Check(((IList)Call("BuildingRoadAccessIndex","Query",RoadPoints(),20f)).Count==0,"Construction removes access");
            Set(building,"status",1);Call("BuildingRoadAccessIndex","Register",building);Call("BuildingRoadAccessIndex","Remove",1);Check(((IList)Call("BuildingRoadAccessIndex","Query",RoadPoints(),20f)).Count==0,"Deletion removes access");
            foreach(var go in new[]{container,elevated,far,curved,reversed,road,facility})UnityEngine.Object.DestroyImmediate(go);
        }
    }
    static void Lifecycle()
    {
        var manager=new GameObject("Paths").AddComponent(T("PathManager"));
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        // Editorの独立検証ではMonoBehaviourのロードライフサイクルを明示的に開始する。
        T("PathManager").GetMethod("Awake",flags).Invoke(manager,null);
        var path=New("GeneratedModels.PathSummary");Set(path,"path_id",7);Set(path,"path_type",1);Set(path,"path_variant",221);Set(path,"status",1);
        Set(path,"start_point",new List<object>{-100f,0f,-8f});Set(path,"end_point",new List<object>{100f,0f,-8f});
        Dictionary(manager,"pathDataCache")[7]=path;
        T("PathManager").GetMethod("RegisterPathCells",flags).Invoke(manager,new[]{path});
        var distant=New("GeneratedModels.PathSummary");Set(distant,"path_id",8);Set(distant,"path_type",1);Set(distant,"path_variant",221);Set(distant,"status",1);
        Set(distant,"start_point",new List<object>{-100f,0f,200f});Set(distant,"end_point",new List<object>{100f,0f,200f});
        Dictionary(manager,"pathDataCache")[8]=distant;
        T("PathManager").GetMethod("RegisterPathCells",flags).Invoke(manager,new[]{distant});
        var normal=Enum.Parse(T("DrawLod"),"Normal");
        object Key()=>T("PathManager").GetMethod("PathRenderKey",flags).Invoke(manager,new[]{path,normal,(object)false});
        var before=Key();Call("BuildingRoadAccessIndex","Register",Building(3002));
        var after=Key();Check(!before.Equals(after),"Unloaded road LOD cache must invalidate on building arrival");
        Check(Dictionary(manager,"pendingBuildingAccessRebuilds").Contains(7),"Existing road must be queued");
        Check(!Dictionary(manager,"pendingBuildingAccessRebuilds").Contains(8),"Distant road in same cell must stay cached");
        Call("BuildingRoadAccessIndex","Remove",1);Check(!after.Equals(Key()),"Removing a building restores road geometry");
        Call("BuildingRoadAccessIndex","Register",Building(1081));Call("BuildingRoadAccessIndex","Clear");
        Check(((IList)Call("BuildingRoadAccessIndex","Query",RoadPoints(),20f)).Count==0,"Session reset clears markers");
        T("PathManager").GetMethod("OnDestroy",flags).Invoke(manager,null);
        UnityEngine.Object.DestroyImmediate(manager.gameObject);
    }
    static void Render(GameObject[] objects,string file,float size)
    {
        foreach(var renderer in objects.SelectMany(o=>o.GetComponentsInChildren<MeshRenderer>()))
            renderer.sharedMaterials=renderer.sharedMaterials.Select(m=>{var mat=new Material(Shader.Find("Standard"));mat.color=m!=null&&m.HasProperty("_Color")?m.color:Color.gray;mat.SetFloat("_Glossiness",.05f);return mat;}).ToArray();
        RenderSettings.ambientLight=new Color(.6f,.6f,.6f);var light=new GameObject("Sun").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(60,-25,0);
        var camera=new GameObject("Camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=size;camera.backgroundColor=new Color(.2f,.24f,.29f);camera.clearFlags=CameraClearFlags.SolidColor;
        camera.transform.position=new Vector3(40,140,-110);camera.transform.LookAt(new Vector3(0,0,20));
        var rt=new RenderTexture(1400,1000,24);camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
        var image=new Texture2D(1400,1000,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1400,1000),0,0);image.Apply();File.WriteAllBytes(file,image.EncodeToPNG());
        RenderTexture.active=null;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(light.gameObject);
    }
}
