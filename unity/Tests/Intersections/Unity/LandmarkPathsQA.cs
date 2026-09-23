using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 実FBXと本編アセンブリで、端部・植栽接地・高架下端・吊り橋の径間を検証する。
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
            foreach(int id in new[]{251,252,254,256}) foreach(float length in new[]{40f,160f,960f}) Bridge(id,length);
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
        foreach(int id in new[]{241,404,414,251,252,254,256})
            foreach(string file in Directory.GetFiles("Assets/Paths/p"+id,"*.fbx"))
            {
                string source=file.Replace('\\','/');
                string relative="Paths/p"+id+"/"+Path.GetFileName(source);
                string destination=root+"/Assets/Models/"+relative;
                // GUIDが違う既存アセットを上書きすると参照が壊れるため、その場合は停止する。
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
    {
        bool track=id==404||id==414,bridge=id>=251&&id<=256;
        var master=Activator.CreateInstance(assembly.GetType("PathMasterItem"));
        Set(master,"width",width); Set(master,"lanes",lanes); Set(master,"follow_terrain",false);
        Set(master,"height_editable",true); Set(master,"single_span",bridge);
        Set(master,"sidewalk_width",track?0f:3f); Set(master,"side_width",.5f);
        Set(master,"wall_width",track?.35f:bridge?.3f:0f); Set(master,"center_width",id==241?3f:id==254||id==256?.8f:0f);
        Set(master,"street_tree_type_allowed",id==241);
        if(id==414) Set(master,"wire_spacing",35f);
        masters[id]=master;
        var path=Activator.CreateInstance(assembly.GetType("GeneratedModels.PathSummary"));
        Set(path,"path_id",id); Set(path,"path_variant",id); Set(path,"path_type",track?4:2);
        Set(path,"follow_terrain",0); Set(path,"curved",false); Set(path,"street_tree_type",trees?1:0);
        var type=assembly.GetType(track?"TrackPath":"RoadPath");
        var setback=Activator.CreateInstance(assembly.GetType("SidewalkSetback"));
        var result=type.GetMethod("Generate").Invoke(Activator.CreateInstance(type),new[]{path,
            new List<Vector3>{new Vector3(0,height,0),new Vector3(0,height+grade*length,length)},setback,setback});
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
        float width=id==251?7:id==252?12:id==254?22:32;
        var go=Generate(id,width,id==251?1:id==252?2:id==254?4:6,12,length,0,false);
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
        float cableEdge=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name=="SuspensionCables")
            .SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint))
            .Where(v=>Mathf.Abs(v.z)<.4f).Max(v=>Mathf.Abs(v.x));
        float widthScale=Mathf.Max(1,width/12);
        Require(Mathf.Abs(cableEdge-(width*.5f+.8f*widthScale+.25f))<.005f,"main cable diameter 0.5m");
        foreach(var tower in towers)
        {
            var points=tower.GetComponentsInChildren<MeshFilter>().SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
            Require(Mathf.Abs(points.Max(v=>v.y)-12.1f-Mathf.Clamp(length*.12f,8,80)*widthScale)<.02f,"tower height follows road width");
            Require(Mathf.Abs(points.Max(v=>v.z)-points.Min(v=>v.z)-2.4f*widthScale)<.02f,"tower thickness follows road width");
        }
        var trusses=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.EndsWith("/truss")).ToArray();
        Require(id>=254?trusses.Length>0:trusses.Length==0,"truss selection "+id+" length="+length);
        var bodies=go.GetComponentsInChildren<MeshFilter>().Where(f=>f.name.EndsWith("/body")).ToArray();
        float bottom=bodies.SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).Min(v=>v.y);
        Require(Mathf.Abs(bottom-(12.1f-(id==251?1.1f:id==252?1.3f:.8f)))<.01f,"deck thickness");
        if(id>=254)
        {
            float trussBottom=trusses.SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).Min(v=>v.y);
            Require(Mathf.Abs(trussBottom-(12.1f-(id==254?3.2f:4.2f)))<.02f,"truss depth");
        }
        UnityEngine.Object.DestroyImmediate(go);
    }
    static void Arcade(int id,float height)
    {
        var go=Generate(id,5,1,height,24,0,false);
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
                var go=Generate(id,5,1,12,24,0,false);
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
