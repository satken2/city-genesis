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
    public static List<TPath> Neighbors<TPath>(int node)=>neighbors.Cast<TPath>().ToList();
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
    static GameObject Generate(object path,string lod)
    {
        Call("PathDrawContext","Begin",Lod(lod),0d,1d);
        var root=new GameObject("QA_Line");
        Call("PowerLinePath","GenerateInto",root,path,new List<Vector3>{Vector3.zero,new Vector3(0,0,240)},Lod(lod));
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
            object Field(object value,string name)=>value.GetType().GetField(name).GetValue(value);
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
            File.WriteAllText("result.txt",$"PASS {checks} checks");
            EditorApplication.Exit(0);
        }
        catch(Exception e){File.WriteAllText("result.txt","FAIL\n"+e); Debug.LogException(e);EditorApplication.Exit(1);}
    }
}
