using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class ConstructionPathQA
{
    static Assembly assembly;
    static Type builder, pathType;
    static int checks;
    static string output;
    static Camera camera;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Set(object target,string field,object value) => target.GetType().GetField(field).SetValue(target,value);
    static object Field(object target,string field) => target.GetType().GetProperty(field)?.GetValue(target) ?? target.GetType().GetField(field).GetValue(target);
    static void Check(bool condition,string label) { checks++; if(!condition) throw new Exception(label); }
    [InitializeOnLoadMethod] static void Resume()
    {
        EditorApplication.delayCall += () => { if(SessionState.GetBool("ConstructionQA",false) && EditorApplication.isPlaying) { SessionState.SetBool("ConstructionQA",false); Run(); } };
    }
    public static void Run()
    {
        if(!EditorApplication.isPlaying)
        {
            GraphicsSettings.defaultRenderPipeline=AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
            QualitySettings.renderPipeline=GraphicsSettings.defaultRenderPipeline;
            ModelPrefabGenerator.SyncAll(); SessionState.SetBool("ConstructionQA",true); EditorApplication.EnterPlaymode(); return;
        }
        output=Path.GetDirectoryName(Application.dataPath);
        try
        {
            string[] args=Environment.GetCommandLineArgs();string source=args[Array.IndexOf(args,"-sourceRoot")+1];
            AppDomain.CurrentDomain.AssemblyResolve += (o,e) => {
                foreach(string dir in new[]{"/Temp/bin/Debug","/Library/ScriptAssemblies","/Assets/Plugins"}) {
                    string file=source+dir+"/"+new AssemblyName(e.Name).Name+".dll";
                    if(File.Exists(file)) return Assembly.LoadFrom(file);
                } return null;
            };
            assembly=Assembly.LoadFrom(source+"/Temp/bin/Debug/Assembly-CSharp.dll");
            builder=assembly.GetType("ConstructionPathBuilder");pathType=assembly.GetType("ConstructionPathSummary");
            var masterType=assembly.GetType("MasterDataStore");var master=new GameObject("Master").AddComponent(masterType);
            masterType.GetProperty("IsInitialized").SetValue(master,true);
            var table=(IDictionary)masterType.GetField("pathMasterById",Private).GetValue(master);
            foreach(int variant in new[]{221,2,401}) {
                var item=Activator.CreateInstance(assembly.GetType("PathMasterItem"));
                Set(item,"type",variant==401?4:2);Set(item,"width",variant==401?5f:8f);Set(item,"height",5f);
                Set(item,"sidewalk_width",1f);Set(item,"lanes",2);Set(item,"pillar_spacing",25f);Set(item,"height_editable",true);
                Set(item,"model_type",variant==401?"track":"default");table.Add(variant,item);
            }
            new GameObject("Models").AddComponent(assembly.GetType("ModelManager"));
            SetupView();
            foreach(int variant in new[]{2,221,401}) foreach(float progress in new[]{0f,12.5f,25f,57.5f,90f,99f,100f}) Verify(variant,progress);
            VerifyStructuralParts(table);
            VerifyCurve(); VerifyTunnel(); VerifyProgressContract(); VerifySingleSpan(table); VerifyDeckVehicles();
            foreach(var message in ShaderUtil.GetShaderMessages(Shader.Find("Custom/PathDirt")))
                Check(message.severity.ToString()!="Error",message.message);
            File.WriteAllText(output+"/result.txt","PASS: "+checks+" checks; actual FBX, URP dirt, staged piers/decks, ground/rail, curves, tunnel, progress decode/cache key.");
            EditorApplication.Exit(0);
        }
        catch(Exception e) { File.WriteAllText(output+"/result.txt","FAIL "+e);EditorApplication.Exit(1); }
    }
    static object PathData(int variant,float progress,float height)
    {
        object path=Activator.CreateInstance(pathType);
        Set(path,"path_id",1);Set(path,"version",1);Set(path,"path_variant",variant);Set(path,"path_type",variant==401?4:2);
        Set(path,"status",(int)assembly.GetType("Constants").GetField("STATUS_CONSTRUCTING").GetRawConstantValue());
        Set(path,"construction_progress",progress);Set(path,"width",variant==401?5f:8f);
        Set(path,"start_point",new List<object>{-50f,height,0f});Set(path,"end_point",new List<object>{50f,height,0f});
        return path;
    }
    static object Generate(object path,List<Vector3> points,bool tunnel=false)
    {
        var context=assembly.GetType("PathDrawContext");context.GetMethod("Begin").Invoke(null,new object[]{Enum.Parse(assembly.GetType("DrawLod"),"Normal"),0d,1d});
        var args=new object[]{path,points,tunnel,true,null};
        try{return builder.GetMethod("Generate").Invoke(null,args);}finally{context.GetMethod("End").Invoke(null,null);}
    }
    static List<Vector3> Line(float height) => Enumerable.Range(0,101).Select(i=>new Vector3(i-50,height+.1f,0)).ToList();
    static void Verify(int variant,float progress)
    {
        float height=variant==2?0:10;var path=PathData(variant,progress,height);
        object result=Generate(path,Line(height));var root=(GameObject)Field(result,"GeneratedObject");
        Render(output+"/"+variant+"-"+(int)progress+".png");
        File.WriteAllText(output+"/bounds.txt",string.Join("\n",root.GetComponentsInChildren<Renderer>().Select(r=>r.name+" "+r.bounds)));
        var collider=(Mesh)Field(result,"Mesh");Check(collider!=null && collider.vertexCount>0,"Missing click collider");
        var supports=root.GetComponentsInChildren<Transform>(true).Where(t=>t.name=="ConstructionSupport").ToArray();
        int visible=supports.Count(t=>t.gameObject.activeSelf);
        if(variant==2) Check(supports.Length==0,"Ground has piers");
        else {
            Check(supports.Length>=4,"Bridge piers missing");
            if(progress==0)Check(visible==0,"Piers at zero progress");
            if(progress>=25)Check(visible==supports.Length && supports.All(t=>t.localScale.y==1),"Piers incomplete at 25 percent");
            if(progress==12.5f)Check(visible>0 && visible<supports.Length,"Sequential pier growth");
            foreach(var support in supports.Where(t=>t.gameObject.activeSelf))
                foreach(var renderer in support.GetComponentsInChildren<Renderer>())
                    Check(renderer.bounds.min.y>=-.05f,"Pier extends below construction ground: "+renderer.bounds);
        }
        var surfaces=root.GetComponentsInChildren<Transform>().Where(t=>t.name=="CompletedSurface").ToArray();
        if(variant!=2 && progress<=25 || progress==0)Check(surfaces.Length==0,"Surface built too early");
        if(variant==2 && progress>0) {
            Check(surfaces.Length>0,"Missing pavement");
            var bounds=surfaces[0].GetComponentsInChildren<Renderer>().Select(r=>r.bounds).Aggregate((a,b)=>{a.Encapsulate(b);return a;});
            Check(Math.Abs(bounds.max.x-(-50+progress))<.3f,"Pavement frontier "+progress+" actual="+bounds.max.x);
        }
        if(variant==2 && progress==100)Check(root.GetComponentsInChildren<Transform>().All(t=>t.name!="ConstructionDirt"),"Dirt remains on finished pavement");
        if(variant==2 && progress==100)Check(root.GetComponentsInChildren<Transform>().All(t=>!t.name.StartsWith("construction_")),"Vehicles at 100 percent");
        if(variant==221 && progress==57.5f)Check(root.GetComponentsInChildren<Transform>().Any(t=>t.name=="construction_crane"),"Missing erection crane");
        if(variant!=2 && progress==57.5f) {
            var body=root.GetComponentsInChildren<Renderer>().First(r=>(r.name.EndsWith("/body") || r.name.EndsWith("/body_elevated")));
            Check(Math.Abs(body.bounds.max.x)<.3f,"Deck must stop at middle pier at 57.5 percent");
        }
        if(variant!=2) {
            Check(root.GetComponentsInChildren<Transform>().All(t=>t.name!="ConstructionDirt"),"Elevated dirt");
            foreach(var vehicle in root.GetComponentsInChildren<Transform>().Where(t=>t.name.StartsWith("construction_"))) {
                Check(Math.Abs(vehicle.position.z)<.01f,"Vehicle outside path centerline");
                Check(Math.Abs(vehicle.position.y-(progress<25?.1f:10.2f))<.02f,"Vehicle must stand on ground during pier work and on deck during erection");
                foreach(var renderer in vehicle.GetComponentsInChildren<Renderer>())
                    Check(renderer.bounds.min.z>=-(variant==401?2.5f:4f) && renderer.bounds.max.z<=(variant==401?2.5f:4f),"Vehicle outside path width");
            }
            if(progress>=90) {
                var body=root.GetComponentsInChildren<Renderer>().First(r=>(r.name.EndsWith("/body") || r.name.EndsWith("/body_elevated")));
                Check(Math.Abs(body.bounds.max.x-50)<.3f,"Deck incomplete during finishing");
                Check(root.GetComponentsInChildren<Transform>().Any(t=>t.name=="construction_crane"),"Missing finishing crane");
            }
        }
        foreach(var mesh in root.GetComponentsInChildren<MeshFilter>(true).Select(m=>m.sharedMesh).Where(m=>m!=null))
            foreach(var v in mesh.vertices)Check(!float.IsNaN(v.y) && !float.IsInfinity(v.x),"Nonfinite mesh");
        Render(output+"/"+variant+"-"+(int)progress+".png");
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(collider);
    }
    static void VerifyStructuralParts(IDictionary table)
    {

        Set(table[401], "wall_width", .3f);
        Set(table[221], "wall_width", .3f);
        foreach (int variant in new[] { 221, 401 }) foreach (float height in new[] { 0f, 10f })
        {
            var path = PathData(variant, 57.5f, height);
            if (variant == 401) Set(path, "path_type", 4);
            var result = Generate(path, Line(height));
            var root = (GameObject)Field(result, "GeneratedObject");
            var names = root.GetComponentsInChildren<Renderer>().Select(r => r.name).ToArray();
            bool rail = variant == 401;
            Check(names.Any(n => n.EndsWith(rail && height > 0 ? "/body_elevated" : "/body")), "Construction roadbed selection");
            if (rail)
                Check(names.Any(n => n.EndsWith(height > 0 ? "/track_elevated" : "/track")), "Construction track selection");
            Check(names.Count(n => n.EndsWith("/wall")) == (rail && height == 0 ? 0 : 2), "Construction wall sides");
            Check(!names.Any(n => n.EndsWith("/wire") || n.EndsWith("/pole") || n.EndsWith("/beam") || n.EndsWith("/center")), "Construction finishing equipment");
            Render(output + "/structural-" + variant + "-" + height + ".png");
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate((Mesh)Field(result, "Mesh"));
        }
    }

    static void VerifyCurve()
    {
        var path=PathData(2,50,0);Set(path,"curved",true);
        var points=Enumerable.Range(0,101).Select(i=>new Vector3(i-50,.1f,15*Mathf.Sin(i*Mathf.PI/100))).ToList();
        var result=Generate(path,points);var root=(GameObject)Field(result,"GeneratedObject");
        Check(root.GetComponentsInChildren<Renderer>().Any(r=>r.bounds.max.z>12),"Curve lost");Render(output+"/curve.png");
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate((Mesh)Field(result,"Mesh"));
    }
    static void VerifyDeckVehicles()
    {
        foreach(float slope in new[]{0f,.15f,-.15f}) foreach(float length in new[]{6f,100f})
        {
            Vector3[] finishingPositions=null;
            foreach(float progress in new[]{25f,30f,45f,57.5f,90f,99f,100f})
            {
                var points=Enumerable.Range(0,(int)length+1).Select(i=>new Vector3(i-50,10.1f+slope*i,0)).ToList();
                var path=PathData(221,progress,10);
                Set(path,"end_point",new List<object>{length-50,10+slope*length,0f});
                var result=Generate(path,points);var root=(GameObject)Field(result,"GeneratedObject");
                var vehicles=root.transform.Cast<Transform>().Where(t=>t.name.StartsWith("construction_")).ToArray();
                var decks=root.GetComponentsInChildren<Renderer>().Where(r=>r.name.EndsWith("/body")).ToArray();
                if(decks.Length==0)Check(vehicles.Length==0,"Vehicles over an unbuilt deck");
                float deckEnd=decks.Length==0?-50:decks.Max(r=>r.bounds.max.x);
                float previousRear=float.PositiveInfinity;
                foreach(var vehicle in vehicles)
                {
                    Check(Vector3.Dot(vehicle.forward,new Vector3(1,slope,0).normalized)>.9999f,"Vehicle must follow deck grade");
                    Check(Math.Abs(vehicle.position.y-(10.2f+slope*(vehicle.position.x+50)))<.02f,"Vehicle ground origin must meet deck plane");
                    var bounds=vehicle.GetComponentsInChildren<Renderer>().Select(r=>r.bounds).Aggregate((a,b)=>{a.Encapsulate(b);return a;});
                    Check(bounds.min.x>=-50-.02f && bounds.max.x<=deckEnd+.02f,"Vehicle extends beyond built deck");
                    Check(bounds.max.x<previousRear,"Deck vehicles overlap");previousRear=bounds.min.x;
                }
                if(progress==90)finishingPositions=vehicles.Select(v=>v.position).ToArray();
                if(progress>90)Check(finishingPositions.SequenceEqual(vehicles.Select(v=>v.position)),"Finishing vehicles moved after 90 percent");
                if(length==100 && progress==57.5f)Check(vehicles.Length==2,"Long built deck must hold both vehicles");
                if(slope==.15f && length==100 && progress==57.5f)Render(output+"/elevated-ramp.png");
                UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate((Mesh)Field(result,"Mesh"));
            }
        }
    }
    static void VerifyTunnel()
    {
        var path=PathData(2,50,-20);var result=Generate(path,Line(-20),true);var root=(GameObject)Field(result,"GeneratedObject");
        Check(root.GetComponentsInChildren<Renderer>().Length==0,"Buried construction visible");UnityEngine.Object.DestroyImmediate(root);
    }
    static void VerifyProgressContract()
    {
        var json=assembly.GetReferencedAssemblies().First(a=>a.Name=="Newtonsoft.Json");var convert=Assembly.Load(json).GetType("Newtonsoft.Json.JsonConvert");
        var decode=convert.GetMethod("DeserializeObject",new[]{typeof(string),typeof(Type)});
        object response=decode.Invoke(null,new object[]{"{\"paths\":[{\"path_id\":1,\"construction_progress\":35}],\"next_cursor\":5}",assembly.GetType("PagedPathListResponse")});
        var path=((IList)Field(response,"paths"))[0];Check(Math.Abs((float)builder.GetMethod("Progress").Invoke(null,new[]{path})-.35f)<.001f,"Progress decoding");
        var manager=new GameObject("Paths").AddComponent(assembly.GetType("PathManager"));
        var key=manager.GetType().GetMethod("PathRenderKey",Private);
        var lod=Enum.Parse(assembly.GetType("DrawLod"),"Normal");
        string a=(string)key.Invoke(manager,new object[]{path,lod,false});Set(path,"construction_progress",60f);
        string b=(string)key.Invoke(manager,new object[]{path,lod,false});Check(a!=b,"Same-version progress reuses old mesh");
        foreach(float value in new[]{float.NaN,float.PositiveInfinity,-20f}) {Set(path,"construction_progress",value);Check((float)builder.GetMethod("Progress").Invoke(null,new[]{path})==0,"Invalid progress");}
    }
    static void VerifySingleSpan(IDictionary table)
    {
        Set(table[221],"single_span",true);
        var path=PathData(221,57.5f,10);var result=Generate(path,Line(10));var root=(GameObject)Field(result,"GeneratedObject");
        Check(root.GetComponentsInChildren<Transform>().Count(t=>t.name=="CompletedSurface")==2,"Single-span decks must use one object per supported span");
        UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate((Mesh)Field(result,"Mesh"));
        Set(table[221],"single_span",false);
    }
    static void SetupView()
    {
        GraphicsSettings.defaultRenderPipeline=AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        QualitySettings.renderPipeline=GraphicsSettings.defaultRenderPipeline;
        File.WriteAllText(output+"/pipeline.txt",GraphicsSettings.defaultRenderPipeline+" / "+QualitySettings.renderPipeline);

        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.55f,.55f,.55f);
        var sun=new GameObject("Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.3f;sun.transform.rotation=Quaternion.Euler(48,-35,0);
        var ground=GameObject.CreatePrimitive(PrimitiveType.Plane);ground.transform.localScale=new Vector3(16,1,10);
        var material=new Material(Shader.Find("Universal Render Pipeline/Lit"));material.color=new Color(.26f,.32f,.20f);ground.GetComponent<Renderer>().sharedMaterial=material;
        camera=new GameObject("Camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=34;camera.aspect=1.8f;
        camera.transform.position=new Vector3(48,58,-78);camera.transform.LookAt(new Vector3(0,3,0));camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.28f,.34f,.40f);
    }
    static void Render(string file)
    {
        var rt=new RenderTexture(1440,800,24);camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
        var image=new Texture2D(1440,800,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1440,800),0,0);image.Apply();File.WriteAllBytes(file,image.EncodeToPNG());
        RenderTexture.active=null;camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(rt);
    }
}
