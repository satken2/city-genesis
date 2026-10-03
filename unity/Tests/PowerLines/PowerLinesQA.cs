using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class PowerLinesQA
{
    static Assembly runtime;
    static int checks;
    static IList neighbors;
    static readonly Dictionary<int,List<Vector3>> trackPoints = new();
    static IList adjacentTracks;
    static object Field(object value,string name)=>value.GetType().GetField(name).GetValue(value);
    static void Set(object value,string name,object field)=>value.GetType().GetField(name).SetValue(value,field);
    public static List<TPath> Neighbors<TPath>(int node)=>neighbors.Cast<TPath>().Where(p=>(int)Field(p,"start_node_id")==node||(int)Field(p,"end_node_id")==node).ToList();
    public static List<Vector3> TrackPoints<TPath>(TPath path)=>trackPoints[(int)Field(path,"path_id")];
    public static List<TInfo> Adjacent<TPath,TInfo>(TPath path)=>adjacentTracks.Cast<TInfo>().ToList();
    static Type T(string name)=>runtime.GetType(name,true);
    static void Check(bool value,string message) { checks++; if(!value)throw new Exception(message); }
    static object Lod(string name)=>Enum.Parse(T("DrawLod"),name);
    static object Call(string type,string method,params object[] args)=>T(type).GetMethod(method).Invoke(null,args);
    static PowerLinesQA() { if(SessionState.GetBool("PowerQA.Pending",false))EditorApplication.delayCall+=Verify; }
    public static void Run()
    {
        ModelPrefabGenerator.SyncAll();
        AssetDatabase.SaveAssets();
        var args=Environment.GetCommandLineArgs();
        SessionState.SetString("PowerQA.Root",args[Array.IndexOf(args,"-sourceRoot")+1]);
        SessionState.SetBool("PowerQA.Pending",true);
        EditorApplication.EnterPlaymode();
    }
    static object PathData(int variant,int id=1,int start=1,int end=2)
    {
        var p=Activator.CreateInstance(T("GeneratedModels.PathSummary"));
        void Set(string k,object v)=>p.GetType().GetField(k).SetValue(p,v);
        Set("path_id",id); Set("path_variant",variant); Set("path_type",11);
        Set("start_node_id",start); Set("end_node_id",end);
        Set("status",T("Constants").GetField("STATUS_ACTIVE").GetValue(null));
        float height=variant==1101?20:10;
        Set("start_point",new List<object>{0f,height,0f}); Set("end_point",new List<object>{0f,height,240f});
        return p;
    }
    static GameObject Generate(object path,string lod,List<Vector3> points=null)
    {
        Call("PathDrawContext","Begin",Lod(lod),0d,1d);
        var root=new GameObject("QA_Line");
        Call("PowerLinePath","GenerateInto",root,path,points??new List<Vector3>{Vector3.zero,new Vector3(0,0,240)},Lod(lod));
        Call("PathDrawContext","End");
        return root;
    }
    static void Verify()
    {
        if(!EditorApplication.isPlaying){EditorApplication.delayCall+=Verify;return;}
        SessionState.SetBool("PowerQA.Pending",false);
        string root=SessionState.GetString("PowerQA.Root","");
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                foreach(string dir in new[]{"Temp/bin/Debug","Library/ScriptAssemblies","Assets/Plugins"}){
                    string file=Path.Combine(root,dir,new AssemblyName(e.Name).Name+".dll");
                    if(File.Exists(file))return Assembly.LoadFrom(file);
                }
                return null;
            };
            runtime=Assembly.LoadFrom(root+"/Temp/bin/Debug/Assembly-CSharp.dll");
            var manager=new GameObject("Models").AddComponent(T("ModelManager"));
            T("ModelManager").GetField("<Instance>k__BackingField",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,manager);
            var cache=(IDictionary)T("ModelManager").GetField("_modelCache",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(manager);
            foreach(int variant in new[]{1101,1102})
            {
                Mesh normal=null;
                foreach(string suffix in new[]{"","_close"})
                {
                    string key=$"Models/Paths/p{variant}/pillar_cap{suffix}";
                    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/"+key+".fbx");
                    Check(prefab!=null,"FBX imported "+key); cache[key]=prefab;
                    var mesh=prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
                    Check(mesh.isReadable,"Readable "+key);
                    Check(mesh.bounds.max.y>(variant==1101?37:16),"FBX Y-up height");
                    if(normal==null)normal=mesh;
                    else{
                        Check(Vector3.Distance(normal.bounds.min,mesh.bounds.min)<.05f&&Vector3.Distance(normal.bounds.max,mesh.bounds.max)<.05f,"LOD bounds");
                        Check(mesh.vertexCount>normal.vertexCount,"Close detail");
                    }
                }
                int previous=0;
                foreach(string lod in new[]{"Far","Normal","Close"})
                {
                    var line=Generate(PathData(variant),lod);
                    var filters=line.GetComponentsInChildren<MeshFilter>();
                    int count=filters.Sum(f=>f.sharedMesh.vertexCount);
                    Check(count>previous,"Increasing geometry for "+lod); previous=count;
                    Check(filters.All(f=>f.sharedMesh.vertexCount<=60000),"Mesh budget");
                    Check(line.GetComponentsInChildren<MeshRenderer>().All(r=>r.sharedMaterials.All(m=>m!=null)),"Materials assigned at "+lod);
                    var cable=filters.Where(f=>f.name=="PowerConductors").SelectMany(f=>f.sharedMesh.vertices).ToArray();
                    Check(cable.Length>0&&cable.Min(v=>v.y)>6,"No ground strip");
                    Check(cable.Min(v=>v.y)<(variant==1101?20:10),"Sag below attachment height");
                    UnityEngine.Object.DestroyImmediate(line);
                }
            }
            var a=Generate(PathData(1101,10),"Normal");
            var b=Generate(PathData(1101,20),"Close");
            int Towers(GameObject go)=>go.GetComponentsInChildren<Transform>().Count(t=>t.name=="TowerVisual");
            Check(Towers(a)+Towers(b)==4,"Two paths share exactly two endpoint towers");
            Check(Towers(b)==3,"Close owns shared nodes");
            b.SetActive(false); Check(Towers(a)==3,"Unload transfers ownership");
            b.SetActive(true); Check(Towers(a)+Towers(b)==4,"Restore does not duplicate");
            UnityEngine.Object.DestroyImmediate(b); Check(Towers(a)==3,"Destroy transfers ownership");
            UnityEngine.Object.DestroyImmediate(a);
            var main=PathData(1101,10,1,2);
            var branch=PathData(1102,20,1,3);
            branch.GetType().GetField("end_point").SetValue(branch,new List<object>{120f,10f,0f});
            neighbors=new ArrayList {branch,main};
            var provider=T("PowerLinePath").GetField("ConnectedPaths");
            provider.SetValue(null,typeof(PowerLinesQA).GetMethod("Neighbors").MakeGenericMethod(T("GeneratedModels.PathSummary")).CreateDelegate(provider.FieldType));
            var sharedA=Call("PowerLinePath","Endpoint",main,1,Vector3.zero,Vector3.forward);
            var sharedB=Call("PowerLinePath","Endpoint",branch,1,Vector3.zero,Vector3.right);
            Check((int)Field(sharedB,"variant")==1101,"Mixed variants use large support");
            Check(Vector3.Distance((Vector3)Field(sharedA,"position"),(Vector3)Field(sharedB,"position"))<.001f,"Branch shares support position");
            Check(Quaternion.Angle((Quaternion)Field(sharedA,"rotation"),(Quaternion)Field(sharedB,"rotation"))<.001f,"Branch shares support direction");
            provider.SetValue(null,null);
            var p=PathData(1101);
            var plan=(IList)Call("PowerLinePath","Plan",p,new List<Vector3>{Vector3.zero,new Vector3(0,0,240)});
            Check(plan.Count==3,"120m tower spacing");
            var forward=(Quaternion)Call("PowerLinePath","Endpoint",p,0,Vector3.zero,Vector3.forward).GetType().GetField("rotation").GetValue(Call("PowerLinePath","Endpoint",p,0,Vector3.zero,Vector3.forward));
            var reverse=Call("PowerLinePath","Endpoint",p,0,Vector3.zero,Vector3.back);
            Check(Quaternion.Angle(forward,(Quaternion)reverse.GetType().GetField("rotation").GetValue(reverse))<.01f,"Reverse path retains circuit side");
            VerifyRailGantries(cache);
            File.WriteAllText("result.txt",$"PASS {checks} checks");
            EditorApplication.Exit(0);
        }
        catch(Exception e){File.WriteAllText("result.txt","FAIL\n"+e); Debug.LogException(e);EditorApplication.Exit(1);}
    }

    static void VerifyRailGantries(IDictionary modelCache)
    {
        var store = new GameObject("QA_Master").AddComponent(T("MasterDataStore"));
        var masters = (IDictionary)T("MasterDataStore").GetField("pathMasterById",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(store);
        foreach (int variant in new[] {1101,1102,411,421,401,501})
        {
            var master = Activator.CreateInstance(T("PathMasterItem"));
            Set(master,"width",variant==421?10f:5f);
            Set(master,"lanes",variant==421?2:1);
            Set(master,"base_height",variant==1101?20f:variant==1102?10f:0f);
            Set(master,"pillar_spacing",120f);
            Set(master,"height_editable",variant==1101||variant==1102);
            Set(master,"infra_products",variant==401?null:new List<int>{1});
            if(variant==411||variant==421)Set(master,"wire_spacing",35f);
            masters[variant]=master;
        }
        T("MasterDataStore").GetField("<IsInitialized>k__BackingField",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(store,true);
        var provider = T("PowerLinePath").GetField("ConnectedPaths");
        provider.SetValue(null,typeof(PowerLinesQA).GetMethod("Neighbors").MakeGenericMethod(T("GeneratedModels.PathSummary")).CreateDelegate(provider.FieldType));
        var polyline = T("PowerLineRailGantry").GetField("TrackPolyline");
        polyline.SetValue(null,typeof(PowerLinesQA).GetMethod("TrackPoints").MakeGenericMethod(T("GeneratedModels.PathSummary")).CreateDelegate(polyline.FieldType));
        var adjacent = T("TrackAdjacency").GetField("NeighborProvider");
        adjacent.SetValue(null,typeof(PowerLinesQA).GetMethod("Adjacent").MakeGenericMethod(T("GeneratedModels.PathSummary"),T("TrackNeighborInfo")).CreateDelegate(adjacent.FieldType));
        adjacentTracks = new ArrayList();
        var rail = PathData(411,100,1,101);
        Set(rail,"path_type",4); Set(rail,"start_point",new List<object>{0f,0f,0f}); Set(rail,"end_point",new List<object>{0f,0f,100f});
        trackPoints[100]=new List<Vector3>{Vector3.zero,new Vector3(0,0,100)};
        var main = PathData(1101,10,102,1);
        Set(main,"start_point",new List<object>{-80f,20f,-60f}); Set(main,"end_point",new List<object>{0f,0f,0f});
        var branch = PathData(1102,20,103,1);
        Set(branch,"start_point",new List<object>{80f,10f,0f}); Set(branch,"end_point",new List<object>{0f,0f,0f});
        neighbors = new ArrayList {branch,rail,main};
        object Endpoint(object path)=>Call("PowerLinePath","Endpoint",path,1,new Vector3(0,-20,0),Vector3.right);
        var support = Endpoint(main);
        Check((bool)support.GetType().GetProperty("IsRailGantry").GetValue(support),"Electrical rail uses gantry");
        Check(((Vector3)Field(support,"position")).y==0,"Gantry uses rail height, without power base_height subtraction");
        Check(Vector3.Angle((Quaternion)Field(support,"rotation")*Vector3.forward,Vector3.forward)<.01f,"Gantry follows rail instead of incoming power line");
        var branchSupport = Endpoint(branch);
        for(int circuit=0;circuit<3;circuit++)
            Check(Vector3.Distance((Vector3)Call("PowerLinePath","SupportAttachment",support,circuit,0,false),(Vector3)Call("PowerLinePath","SupportAttachment",branchSupport,circuit,0,false))<.001f,"Mixed feeders share gantry attachments");
        int previous=0;
        foreach(string lod in new[]{"Far","Normal","Close"})
        {
            var line=Generate(main,lod,new List<Vector3>{new Vector3(-80,0,-60),Vector3.zero});
            var gantry=line.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="RailPowerGantry").ToArray();
            Check(gantry.Length>0,"Gantry generated at "+lod);
            int count=gantry.Sum(f=>f.sharedMesh.vertexCount);
            Check(count>previous,"Gantry increases detail at "+lod);previous=count;
            Check(gantry.All(f=>f.sharedMesh.vertexCount<=60000),"Gantry mesh budget");
            var vertices=gantry.SelectMany(f=>f.sharedMesh.vertices).ToArray();
            Check(vertices.Where(v=>v.y<7f).All(v=>Mathf.Abs(v.x)>2.5f),"Rail running space stays open");
            var conductors=line.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="PowerConductors").SelectMany(f=>f.sharedMesh.vertices).ToArray();
            Check(conductors.Min(v=>v.y)>9f,"Feeder wires stay above rail catenary");
            for(int circuit=0;circuit<6;circuit++)
            {
                var point=(Vector3)Call("PowerLinePath","SupportAttachment",support,circuit,0,lod=="Far");
                Check(conductors.Any(v=>Vector3.Distance(v,point)<.06f),"Conductor reaches gantry insulator");
            }
            UnityEngine.Object.DestroyImmediate(line);
        }
        var sharedMain=Generate(main,"Normal",new List<Vector3>{new Vector3(-80,0,-60),Vector3.zero});
        var sharedBranch=Generate(branch,"Close",new List<Vector3>{new Vector3(80,0,0),Vector3.zero});
        int Gantries(GameObject go)=>go.GetComponentsInChildren<MeshFilter>().Count(f=>f.name=="RailPowerGantry");
        Check(Gantries(sharedMain)+Gantries(sharedBranch)==1,"Shared rail node has one gantry");
        sharedBranch.SetActive(false);Check(Gantries(sharedMain)==1,"Gantry ownership survives feeder unload");
        UnityEngine.Object.DestroyImmediate(sharedBranch);UnityEngine.Object.DestroyImmediate(sharedMain);
        var neighbor=Activator.CreateInstance(T("TrackNeighborInfo"));
        Set(neighbor,"PathId",200);Set(neighbor,"Width",5f);Set(neighbor,"Points",new List<Vector3>{new Vector3(6,0,-100),new Vector3(6,0,100)});
        adjacentTracks=new ArrayList{neighbor};
        support=Endpoint(main);
        Check((float)Field(support,"railRight")>=9.7f,"Gantry spans parallel single track");
        Set(neighbor,"Points",new List<Vector3>{new Vector3(6,5,-100),new Vector3(6,5,100)});
        Check((float)Field(Endpoint(main),"railRight")<6f,"Other height does not widen gantry");
        adjacentTracks=new ArrayList();
        neighbors=new ArrayList{rail,branch};
        var small=Endpoint(branch);
        Check((int)Field(small,"variant")==1102,"Small feeder uses small gantry");
        Check(Mathf.Abs(((Vector3)Call("PowerLinePath","SupportAttachment",small,2,0,false)).y-9.6f)<.001f,"Small gantry attachment height");
        neighbors=new ArrayList{rail,main,branch};
        Set(rail,"path_variant",421);
        Check((float)Field(Endpoint(main),"railRight")>=6.2f,"Gantry spans bundled double track");
        trackPoints[100]=new List<Vector3>{new Vector3(0,6,0),new Vector3(0,6,100)};
        support=Endpoint(main);
        Check(((Vector3)Field(support,"position")).y==6,"Elevated rail sets gantry deck height");
        var elevated=Generate(main,"Normal",new List<Vector3>{new Vector3(-80,0,-60),new Vector3(0,6,0)});
        var elevatedVertices=elevated.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="RailPowerGantry").SelectMany(f=>f.sharedMesh.vertices).ToArray();
        Check(elevatedVertices.Min(v=>v.y)<.1f,"Elevated gantry posts reach ground");UnityEngine.Object.DestroyImmediate(elevated);
        trackPoints[100]=new List<Vector3>{Vector3.zero,new Vector3(40,0,40)};
        support=Endpoint(main);
        Check(Vector3.Angle((Quaternion)Field(support,"rotation")*Vector3.forward,new Vector3(1,0,1))<.01f,"Oblique rail rotates gantry");
        trackPoints[100]=new List<Vector3>{new Vector3(0,0,100),Vector3.zero};Set(rail,"start_node_id",101);Set(rail,"end_node_id",1);
        Check(Quaternion.Angle((Quaternion)Field(Endpoint(main),"rotation"),Quaternion.identity)<.01f,"Reversed rail retains gantry direction");
        Set(rail,"path_variant",401);
        Check(!(bool)Endpoint(main).GetType().GetProperty("IsRailGantry").GetValue(Endpoint(main)),"Non-electrical rail keeps regular support");
        Set(rail,"path_variant",501);Set(rail,"path_type",5);
        Check((bool)Endpoint(main).GetType().GetProperty("IsRailGantry").GetValue(Endpoint(main)),"Electrical monorail uses gantry");
        Set(rail,"path_variant",421);Set(rail,"path_type",4);Set(rail,"start_node_id",1);Set(rail,"end_node_id",101);
        trackPoints[100]=new List<Vector3>{Vector3.zero,new Vector3(0,0,100)};
        Check(!(bool)Call("PowerLinePath","Endpoint",main,0,Vector3.zero,Vector3.forward).GetType().GetProperty("IsRailGantry").GetValue(Call("PowerLinePath","Endpoint",main,0,Vector3.zero,Vector3.forward)),"Unconnected crossing has no gantry");
        neighbors=new ArrayList{rail,main};
        CaptureGantry(main,rail,modelCache);
        VerifyGantryRebuilds(main,rail);
        provider.SetValue(null,null);polyline.SetValue(null,null);adjacent.SetValue(null,null);
        UnityEngine.Object.DestroyImmediate(store.gameObject);
    }

    static void VerifyGantryRebuilds(object main,object rail)
    {
        var manager=new GameObject("QA_Paths").AddComponent(T("PathManager"));
        object Private(string name)=>T("PathManager").GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(manager);
        object Invoke(string name,params object[] args)=>T("PathManager").GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(manager,args);
        var paths=(IDictionary)Private("pathDataCache");paths[10]=main;paths[100]=rail;
        Invoke("AddPathToNodeIndex",main);Invoke("AddPathToNodeIndex",rail);
        Invoke("RegisterPathCells",main);Invoke("RegisterPathCells",rail);
        var queued=(HashSet<int>)Private("pendingPowerRebuildIds");
        Invoke("EnqueuePowerNeighbors",rail);
        Check(queued.Contains(10),"Rail change schedules feeder rebuild including Far LOD");
        var cells=Invoke("GetPathCoveredCellsCached",rail);
        Invoke("EnqueuePillarNeighborRebuilds",rail,cells);
        Check(queued.Contains(10),"Pillar notification preserves full feeder rebuild");
        queued.Clear();
        var parallel=PathData(411,200,104,105);Set(parallel,"path_type",4);
        Set(parallel,"start_point",new List<object>{6f,0f,-100f});Set(parallel,"end_point",new List<object>{6f,0f,100f});
        paths[200]=parallel;Invoke("AddPathToNodeIndex",parallel);Invoke("RegisterPathCells",parallel);
        Invoke("EnqueuePowerNeighbors",parallel);
        Check(queued.Contains(10),"Parallel rail change schedules gantry feeder rebuild");
        ((IDictionary)Private("pathPointsCache"))[200]=new List<Vector3>{new Vector3(6,0,20),new Vector3(6,0,100)};
        var regular=(IList)T("PathManager").GetMethod("GetAdjacentTrackNeighbors").Invoke(manager,new[]{rail});
        var untrimmed=(IList)Invoke("GetPowerLineTrackNeighbors",rail);
        Check(((List<Vector3>)Field(regular[0],"Points"))[0].z==20,"Track neighbors retain intersection trim");
        Check(((List<Vector3>)Field(untrimmed[0],"Points"))[0].z==-100,"Gantry neighbors include intersection interior");
        var support=Call("PowerLinePath","Endpoint",main,1,Vector3.zero,Vector3.right);
        Check((float)Field(support,"railRight")>=9.69f,$"Gantry spans rails even when cached drawing ends before node (right={Field(support,"railRight")}, origin={Field(support,"position")}, width={Field(untrimmed[0],"Width")})");
        UnityEngine.Object.DestroyImmediate(manager.gameObject);
    }

    static void CaptureGantry(object main,object rail,IDictionary modelCache)
    {
        foreach(string file in Directory.GetFiles("Assets/Models/Paths/p421","*.fbx"))
            modelCache["Models/Paths/p421/"+Path.GetFileNameWithoutExtension(file)]=AssetDatabase.LoadAssetAtPath<GameObject>(file.Replace('\\','/'));
        var line=Generate(main,"Close",new List<Vector3>{new Vector3(-80,0,-60),Vector3.zero});
        Call("PathDrawContext","Begin",Lod("Close"),0d,1d);
        var railResult=T("TrackPath").GetMethod("Generate").Invoke(Activator.CreateInstance(T("TrackPath")),new[]{rail,new List<Vector3>{new Vector3(0,0,-30),new Vector3(0,0,35)},Activator.CreateInstance(T("SidewalkSetback")),Activator.CreateInstance(T("SidewalkSetback"))});
        var railObject=(GameObject)railResult.GetType().GetProperty("GeneratedObject").GetValue(railResult);
        Call("PathDrawContext","End");
        // 独立QAの標準レンダラーでも、本編の材質色で形状を確認できるようにする。
        foreach(var renderer in line.GetComponentsInChildren<Renderer>().Concat(railObject.GetComponentsInChildren<Renderer>()))
            renderer.sharedMaterials=renderer.sharedMaterials.Select(source=>new Material(Shader.Find("Standard")){color=source.HasProperty("_BaseColor")?source.GetColor("_BaseColor"):source.color}).ToArray();
        var ground=GameObject.CreatePrimitive(PrimitiveType.Plane);ground.transform.localScale=new Vector3(30,1,30);ground.transform.position=new Vector3(0,-.15f,0);
        ground.GetComponent<Renderer>().sharedMaterial=new Material(Shader.Find("Standard")){color=new Color(.43f,.48f,.39f)};
        RenderSettings.ambientLight=new Color(.65f,.65f,.65f);
        var light=new GameObject("QA_Light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.3f;light.transform.rotation=Quaternion.Euler(35,-30,0);
        var camera=new GameObject("QA_Camera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.73f,.8f,.86f);
        camera.transform.position=new Vector3(24,17,-30);camera.transform.LookAt(new Vector3(0,7,0));camera.fieldOfView=45;
        var target=new RenderTexture(1200,900,24);camera.targetTexture=target;camera.Render();
        var previous=RenderTexture.active;RenderTexture.active=target;var png=new Texture2D(1200,900,TextureFormat.RGB24,false);png.ReadPixels(new Rect(0,0,1200,900),0,0);png.Apply();
        File.WriteAllBytes("gantry-close.png",png.EncodeToPNG());RenderTexture.active=previous;camera.targetTexture=null;
        UnityEngine.Object.DestroyImmediate(png);UnityEngine.Object.DestroyImmediate(target);
        UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(light.gameObject);UnityEngine.Object.DestroyImmediate(ground);
        UnityEngine.Object.DestroyImmediate(railObject);UnityEngine.Object.DestroyImmediate(line);
    }
}
