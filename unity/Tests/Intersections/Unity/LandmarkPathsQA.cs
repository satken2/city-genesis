using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Use actual FBXs and the main assembly to verify endpoints, planting contact with the ground, lower ends of elevated paths, and suspension-bridge spans.
public static class LandmarkPathsQA
{
    static Assembly assembly;
    static IDictionary masters,models;
    static int checks;
    static void Require(bool ok,string message) { checks++; if(!ok) throw new Exception(message); }
    static void Set(object target,string name,object value) => target.GetType().GetField(name).SetValue(target,value);
    public static void Run()
    {
        var args=Environment.GetCommandLineArgs();
        string root=args[Array.IndexOf(args,"-intersectionSourceRoot")+1];
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=> {
                foreach(string dir in new[]{"Temp/bin/Debug","Library/ScriptAssemblies","Assets/Plugins"})
                {
                    string path=Path.Combine(root,dir,new AssemblyName(e.Name).Name+".dll");
                    if(File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            assembly=Assembly.LoadFrom(root+"/Temp/bin/Debug/Assembly-CSharp.dll");
            masters=Manager("MasterDataStore","pathMasterById"); models=Manager("ModelManager","_modelCache");
            foreach(string file in Directory.GetFiles("Assets/Paths","*.fbx",SearchOption.AllDirectories))
            {
                string asset=file.Replace('\\','/');
                var importer=(ModelImporter)AssetImporter.GetAtPath(asset);
                if(!importer.isReadable) { importer.isReadable=true; importer.SaveAndReimport(); }
                models["Models/Paths/"+asset.Substring("Assets/Paths/".Length).Replace(".fbx","")]=AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            }
            foreach(float grade in new[]{0f,.15f,-.15f}) Median(grade,true);
            Median(0,false);
            foreach(int id in new[]{252,254,256}) foreach(float length in new[]{40f,160f,960f}) Bridge(id,length);
            foreach(int id in new[]{252,256}) BentBridge(id);
            foreach(int id in new[]{252,254,256}) PaintedBridge(id);
            foreach(int id in new[]{404,414}) foreach(float height in new[]{6f,15f}) Arcade(id,height);
            foreach(int id in new[]{404,414}) ArcadeCrossing(id);
            if(args.Contains("-syncLandmarkPrefabs")) ExportPrefabs(root);
            File.WriteAllText(root+"/Temp/IntersectionUnityQA/result.txt","PASS landmark paths: "+checks+" checks");
            EditorApplication.Exit(0);
        }
        catch(Exception e) { File.WriteAllText(root+"/Temp/IntersectionUnityQA/result.txt",e.ToString()); Debug.LogException(e); EditorApplication.Exit(1); }
    }
    static IDictionary Manager(string name,string field)
    {
        Type t=assembly.GetType(name); var obj=new GameObject(name).AddComponent(t);
        t.GetField("<Instance>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,obj);
        return (IDictionary)t.GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);
    }
    static void ExportPrefabs(string root)
    {
        foreach(int id in new[]{241,404,414,252,254,256})
            foreach(string file in Directory.GetFiles("Assets/Paths/p"+id,"*.fbx"))
            {
                string source=file.Replace('\\','/');
                string relative="Paths/p"+id+"/"+Path.GetFileName(source);
                string destination=root+"/Assets/Models/"+relative;
                // Overwriting an existing asset with a different GUID would break references, so stop in that case.
                string guid=AssetDatabase.AssetPathToGUID(source);
                if(File.Exists(destination+".meta")) Require(File.ReadAllText(destination+".meta").Contains("guid: "+guid),"FBX GUID conflict "+relative);
                else File.Copy(source+".meta",destination+".meta");
                string prefab="Assets/Resources/Models/"+Path.ChangeExtension(relative,".prefab");
                Require(ModelPrefabGenerator.CreateWrapperPrefab(source,prefab),"prefab export "+relative);
                string output=root+"/"+prefab;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                if(File.Exists(output))
                {
                    Require(File.ReadAllText(output).Contains("guid: "+guid),"prefab GUID conflict "+output);
                    continue;
                }
                File.Copy(prefab,output); File.Copy(prefab+".meta",output+".meta");
            }
        AssetDatabase.SaveAssets();
    }
    static GameObject Generate(int id,float width,int lanes,float height,float length,float grade,bool trees)
        => Generate(id,width,lanes,new List<Vector3>{new Vector3(0,height,0),new Vector3(0,height+grade*length,length)},trees);
    static GameObject Generate(int id,float width,int lanes,List<Vector3> points,bool trees,string color=null)
    {
        bool track=id==404||id==414,bridge=id>=252&&id<=256;
        var master=Activator.CreateInstance(assembly.GetType("PathMasterItem"));
        Set(master,"width",width); Set(master,"lanes",lanes); Set(master,"follow_terrain",false);
        Set(master,"height_editable",true); Set(master,"single_span",bridge);
        Set(master,"sidewalk_width",track?0f:3f); Set(master,"side_width",.5f);
        Set(master,"wall_width",track?.35f:bridge?.3f:0f); Set(master,"center_width",id==241?3f:id==254||id==256?.8f:0f);
        Set(master,"street_tree_type_allowed",id==241);
        if(id==414) Set(master,"wire_spacing",35f);
        masters[id]=master;
        var path=PathFor(id,points,trees);
        Set(path,"color",color);
        var type=assembly.GetType(track?"TrackPath":"RoadPath");
        var setback=Activator.CreateInstance(assembly.GetType("SidewalkSetback"));
        // PathManager sets the paint color of the path being drawn; spline parts such as the truss read it from the context.
        var paint=assembly.GetType("PathDrawContext").GetProperty("PaintColor");
        paint.SetValue(null,assembly.GetType("RoadPath").GetMethod("PaintColorOf").Invoke(null,new[]{path}));
        object result;
        try { result=type.GetMethod("Generate").Invoke(Activator.CreateInstance(type),new[]{path,points,setback,setback}); }
        finally { paint.SetValue(null,null); }
        var go=(GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
        foreach(var c in go.GetComponentsInChildren<Component>()) if(c!=null && c.GetType().Name=="SplineMesh")
        {
            var method=c.GetType().GetMethod("RebuildImmediate");
            method.Invoke(c,method.GetParameters().Length==0?null:new object[]{true});
        }
        foreach(var f in go.GetComponentsInChildren<MeshFilter>()) if(f.sharedMesh!=null)
        {
            Require(f.sharedMesh.vertexCount<=60000,"vertex budget "+f.name);
            Require(f.sharedMesh.vertices.All(v=>!float.IsNaN(v.x)&&!float.IsNaN(v.y)&&!float.IsNaN(v.z)),"finite "+f.name);
        }
        return go;
    }
    static object PathFor(int id,List<Vector3> points,bool trees)
    {
        bool track=id==404||id==414;
        var path=Activator.CreateInstance(assembly.GetType("GeneratedModels.PathSummary"));
        Set(path,"path_id",id); Set(path,"path_variant",id); Set(path,"path_type",track?4:2);
        Set(path,"follow_terrain",0); Set(path,"curved",false); Set(path,"street_tree_type",trees?1:0);
        // Bridge towers and road markings read the anchors; the flat test ground is at 0, so relative and absolute heights agree.
        List<object> Anchor(Vector3 p) => new List<object>{p.x,p.y,p.z};
        Set(path,"start_point",Anchor(points[0])); Set(path,"end_point",Anchor(points[points.Count-1]));
        Set(path,"waypoints",points.Skip(1).Take(points.Count-2)
            .Select(p=>(object)new Dictionary<string,object>{{"position",Anchor(p)}}).ToList());
        return path;
    }
    static void Median(float grade,bool trees)
    {
        var go=Generate(241,32,6,0,80,grade,trees);
        var caps=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.EndsWith("/center_start")||f.name.EndsWith("/center_end")).ToArray();
        Require(caps.Length==2,"two median caps");
        foreach(var f in caps)
        {
            var vs=f.sharedMesh.vertices.Select(f.transform.TransformPoint).ToArray();
            Require(vs.Min(v=>v.z)>-.05f && vs.Max(v=>v.z)<80.05f,"caps stay inside path");
            bool start=f.name.EndsWith("/center_start");
            float tip=start?vs.Min(v=>v.z):vs.Max(v=>v.z);
            float tipWidth=vs.Where(v=>Mathf.Abs(v.z-tip)<.001f).Max(v=>Mathf.Abs(v.x));
            Require(tipWidth<.2f,"rounded tip "+f.name+" x="+tipWidth+" z="+tip);
        }
        var centerTrees=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.Contains("tree")&&Mathf.Abs(f.transform.position.x)<.1f).ToArray();
        Require(trees?centerTrees.Length>0:centerTrees.Length==0,"median tree selection");
        foreach(var f in centerTrees)
        {
            var foot=f.transform.TransformPoint(new Vector3(0,f.sharedMesh.bounds.min.y,0));
            Require(Mathf.Abs(foot.y-(grade*foot.z+.1f+.2f*Mathf.Sqrt(1+grade*grade)))<.015f,"median tree contact");
        }
        UnityEngine.Object.DestroyImmediate(go);
    }
    static void Bridge(int id,float length)
    {
        float width=id==252?12:id==254?22:32;
        var go=Generate(id,width,id==252?2:id==254?4:6,12,length,0,false);
        if(id>=254 && length<256)
        {
            var center=go.GetComponentsInChildren<MeshFilter>().Single(f=>f.name.EndsWith("/center"));
            var points=center.sharedMesh.vertices.Select(center.transform.TransformPoint).ToArray();
            Require(Mathf.Abs(points.Max(v=>v.x)-points.Min(v=>v.x)-.8f)<.01f,"concrete median width");
            Require(Mathf.Abs(points.Max(v=>v.y)-13f)<.01f,"concrete median height");
        }
        var towers=go.GetComponentsInChildren<Transform>().Where(t=>t.name=="SuspensionTower").ToArray();
        Require(towers.Length==2,"two suspension towers");
        Require(towers.Any(t=>Mathf.Abs(t.position.z-length*.2f)<.05f)&&towers.Any(t=>Mathf.Abs(t.position.z-length*.8f)<.05f),"tower positions");
        Require(go.GetComponentsInChildren<MeshFilter>().Any(f=>f.name=="SuspensionCables"),"main cable/hangers");
        var cables=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="SuspensionCables")
            .SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
        float cableEdge=cables.Where(v=>Mathf.Abs(v.z-length*.5f)<.4f).Max(v=>Mathf.Abs(v.x));
        float widthScale=Mathf.Max(1,width/12),lateral=width*.5f+.8f*widthScale;
        Require(Mathf.Abs(cableEdge-(lateral+.25f))<.005f,"main cable diameter 0.5m");
        // The anchorage spans a quarter of the side span (deck end to tower) and the cable enters its bridge-side top.
        var anchorages=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="SuspensionAnchorage")
            .Select(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint).ToArray()).ToArray();
        Require(anchorages.Length==2,"two anchorages");
        float inset=length*.2f*.25f,entry=12.1f+.4f*inset;
        Require(anchorages.Any(a=>Mathf.Abs(a.Min(v=>v.z))<.01f&&Mathf.Abs(a.Max(v=>v.z)-inset)<.01f)
            &&anchorages.Any(a=>Mathf.Abs(a.Min(v=>v.z)-(length-inset))<.01f&&Mathf.Abs(a.Max(v=>v.z)-length)<.01f),"anchorage length");
        foreach(var a in anchorages)
        {
            Require(Mathf.Abs(a.Min(v=>v.y)+.15f)<.01f,"anchorage reaches ground");
            Require(Mathf.Abs(a.Max(v=>v.y)-entry)<.01f,"anchorage height follows its length");
            Require(a.Max(v=>Mathf.Abs(v.x))<=width*.5f+1.7f*widthScale+.01f&&a.Min(v=>Mathf.Abs(v.x))>width*.5f,"anchorage beside the deck");
        }
        foreach(float z in new[]{inset,length-inset}) foreach(int side in new[]{-1,1})
            Require(cables.Any(v=>(v-new Vector3(side*lateral,entry,z)).magnitude<.3f),"cable enters anchorage");
        foreach(var tower in towers)
        {
            var points=tower.GetComponentsInChildren<MeshFilter>().SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
            Require(Mathf.Abs(points.Max(v=>v.y)-12.1f-Mathf.Max(length*.6f*.08f,3.2f)*widthScale)<.02f,"tower height follows road width");
            Require(Mathf.Abs(points.Max(v=>v.z)-points.Min(v=>v.z)-2.4f*widthScale)<.02f,"tower thickness follows road width");
        }
        // Far LOD keeps the towers with their legs below the deck, and drops the cables and anchorages.
        var far=new GameObject("Far");
        var deck=new List<Vector3>{new Vector3(0,12,0),new Vector3(0,12,length)};
        assembly.GetType("SuspensionBridgeGenerator").GetMethod("GenerateFar").Invoke(null,new object[]{far,deck,PathFor(id,deck,false)});
        Require(far.GetComponentsInChildren<Transform>().Count(t=>t.name=="SuspensionTower")==2,"far towers");
        Require(!far.GetComponentsInChildren<MeshFilter>().Any(f=>f.name=="SuspensionAnchorage"),"far drops anchorages");
        Require(far.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="SuspensionCables")
            .SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).All(v=>v.y<=12.1f+.01f),"far drops cables");
        UnityEngine.Object.DestroyImmediate(far);
        var trusses=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.EndsWith("/truss")).ToArray();
        Require(id>=254?trusses.Length>0:trusses.Length==0,"truss selection "+id+" length="+length);
        var bodies=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.EndsWith("/body")).ToArray();
        float bottom=bodies.SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).Min(v=>v.y);
        Require(Mathf.Abs(bottom-(12.1f-(id==252?1.3f:1.2f)))<.01f,"deck thickness");
        if(id>=254)
        {
            float trussBottom=trusses.SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).Min(v=>v.y);
            Require(Mathf.Abs(trussBottom-(12.1f-13.55f))<.02f,"truss depth");
        }
        UnityEngine.Object.DestroyImmediate(go);
    }
    // The path's color paints the PATH_PAINT parts (towers, cables with the legs below the deck, truss); concrete keeps its own.
    static void PaintedBridge(int id)
    {
        const string color="#C0362C";
        ColorUtility.TryParseHtmlString(color,out Color expected);
        var go=Generate(id,id==252?12:id==254?22:32,id==252?2:id==254?4:6,
            new List<Vector3>{new Vector3(0,12,0),new Vector3(0,12,160)},false,color);
        Material[] Of(Func<string,bool> part)=>go.GetComponentsInChildren<Transform>().Where(t=>part(t.name))
            .SelectMany(t=>t.GetComponents<MeshRenderer>()).SelectMany(r=>r.sharedMaterials).ToArray();
        bool Painted(Material m)=>m.name=="PATH_PAINT"+color && ((Vector4)(m.color-expected)).magnitude<.002f;
        var tower=go.GetComponentsInChildren<Transform>().First(t=>t.name=="SuspensionTower")
            .GetComponentsInChildren<MeshRenderer>().SelectMany(r=>r.sharedMaterials).ToArray();
        Require(tower.Any(Painted)&&tower.Any(m=>m.name.StartsWith("PATH_CONCRETE"))&&!tower.Any(m=>m.name=="PATH_PAINT"),"tower painted, foundation concrete");
        Require(Of(n=>n=="SuspensionCables").All(Painted),"cables painted");
        Require(Of(n=>n=="SuspensionAnchorage").All(m=>!m.name.StartsWith("PATH_PAINT")),"anchorage unpainted");
        var truss=Of(n=>n.EndsWith("/truss"));
        Require(id>=254?truss.Length>0&&truss.All(Painted):truss.Length==0,"truss painted");
        UnityEngine.Object.DestroyImmediate(go);
    }
    // A waypoint is a corner: one shared tower stands on it, square to the bisector, with no copies per segment.
    static void BentBridge(int id)
    {
        float width=id==252?12:32;
        Vector3 start=new Vector3(0,12,0),corner=new Vector3(60,12,300),end=new Vector3(0,12,600);
        var go=Generate(id,width,id==252?2:6,new List<Vector3>{start,corner,end},false);
        var towers=go.GetComponentsInChildren<Transform>().Where(t=>t.name=="SuspensionTower").ToArray();
        Require(towers.Length==3,"two end towers and the corner tower");
        var middle=towers.Where(t=>Vector2.Distance(new Vector2(t.position.x,t.position.z),new Vector2(corner.x,corner.z))<.05f).ToArray();
        Require(middle.Length==1,"corner tower at the waypoint");
        var bisector=((corner-start).normalized+(end-corner).normalized).normalized;
        Require(Vector3.Angle(middle[0].forward,bisector)<.5f,"corner tower faces the bisector");
        Require(go.GetComponentsInChildren<MeshFilter>().Count(f=>f.name=="SuspensionAnchorage")==2,"anchorages only at the ends");
        UnityEngine.Object.DestroyImmediate(go);
    }
    static void Arcade(int id,float height)
    {
        var go=Generate(id,id==414?10:5,id==414?2:1,height,24,0,false);
        var meshes=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="BrickArcade").ToArray();
        Require(meshes.Length>0,"arcade present");
        var vs=meshes.SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
        Require(Mathf.Abs(vs.Min(v=>v.y))<.01f,"arcade reaches ground");
        Require(Mathf.Abs(vs.Max(v=>v.y)-height-.1f)<.01f,"arcade meets deck");
        Require(vs.Any(v=>v.y>height*.6f&&v.y<height*.95f),"arch soffit survives height mapping");
        UnityEngine.Object.DestroyImmediate(go);
    }
    static void ArcadeCrossing(int id)
    {
        var clearance=assembly.GetType("PillarClearance");
        var field=clearance.GetField("NeighborProvider");
        var corridorType=assembly.GetType("PillarClearance+Corridor");
        var corridors=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(corridorType));
        var corridor=Activator.CreateInstance(corridorType);
        Set(corridor,"HalfWidth",1f); Set(corridor,"Height",4f);
        corridors.Add(corridor);
        var parameter=System.Linq.Expressions.Expression.Parameter(assembly.GetType("GeneratedModels.PathSummary"));
        var provider=System.Linq.Expressions.Expression.Lambda(field.FieldType,
            System.Linq.Expressions.Expression.Constant(corridors,corridors.GetType()),parameter).Compile();
        var previous=field.GetValue(null);
        try
        {
            field.SetValue(null,provider);
            int baseline=0;
            foreach(var scenario in new[]{"above","crossing","moved","tunnel","same-level","wide","deleted"})
            {
                float y=scenario=="above"?40:scenario=="tunnel"?-20:scenario=="same-level"?12.1f:.1f;
                float z=scenario=="moved"?100:12;
                Set(corridor,"Points",new List<Vector3>{new Vector3(-20,y,z),new Vector3(20,y,z)});
                Set(corridor,"HalfWidth",scenario=="wide"?6f:1f);
                if(scenario=="deleted") corridors.Clear();
                var go=Generate(id,id==414?10:5,id==414?2:1,12,24,0,false);
                var vertices=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="BrickArcade")
                    .SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
                if(scenario=="above") baseline=vertices.Length;
                if(scenario=="crossing")
                {
                    Require(vertices.Length*3==baseline*2,"one whole crossing arch removed");
                    Require(vertices.All(v=>v.z<=8.01f||v.z>=15.99f),"crossing opening contains no bricks");
                }
                else Require(vertices.Length==(scenario=="wide"?0:baseline),"arcade clearance "+scenario);
                Require(go.GetComponentsInChildren<MeshFilter>().Any(f=>f.name.EndsWith("/body")),"crossing preserves deck");
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
        finally { field.SetValue(null,previous); }
    }
}
