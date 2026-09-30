using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// 本編の配置計画と実FBXによる結合メッシュを、独立したUnityで検証する。
public static class RailYardQA
{
    static int checks;
    static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static void Require(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    public static void Run()
    {
        string[] args = Environment.GetCommandLineArgs();
        string root = args[Array.IndexOf(args, "-intersectionSourceRoot") + 1];
        string report = root + "/Temp/IntersectionUnityQA/result.txt";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
                foreach (string dir in new[] { "Temp/bin/Debug", "Library/ScriptAssemblies", "Assets/Plugins" }) {
                    string file = Path.Combine(root, dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            var assembly = Assembly.LoadFrom(root + "/Temp/bin/Debug/Assembly-CSharp.dll");
            Type yard = assembly.GetType("RailYardBuildingMeshBuilder");
            CheckPlan(yard);
            CheckTerminalFallback(yard);
            Type modelType = assembly.GetType("ModelManager");
            var model = new GameObject("Models").AddComponent(modelType);
            modelType.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, model);
            var cache = (IDictionary)modelType.GetField("_modelCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(model);
            foreach (string part in new[] { "yard_track", "yard_wire", "yard_pole", "yard_beam", "yard_stop", "yard_shed", "floor", "fence-w1" })
            {
                string asset = $"Assets/Buildings/a1060/0/{part}.fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
                importer.isReadable = true; importer.SaveAndReimport();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                cache[$"Models/Buildings/b0/0/{part}"] = prefab;
                cache[$"Models/Buildings/a1060/0/{part}"] = prefab;
            }
            var summary = assembly.GetType("GeneratedModels.BuildingSummary");
            var building = Activator.CreateInstance(summary);
            summary.GetField("variant").SetValue(building, 1060);
            summary.GetField("_polygonArrayCache", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(building,
                new[] { new[] { 19f, 80f }, new[] { -19f, 80f }, new[] { -19f, -80f }, new[] { 19f, -80f } });
            object result = yard.GetMethod("Generate").Invoke(Activator.CreateInstance(yard), new[] { building, (object)false });
            var mesh = (Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
            var materials = (Material[])result.GetType().GetProperty("Materials").GetValue(result);
            Require(mesh.vertexCount > 5000, "Yard fell back to a box");
            Require(mesh.vertices.All(v => !float.IsNaN(v.x) && !float.IsInfinity(v.z)), "Non-finite mesh");
            Require(mesh.vertices.Where(v=>Mathf.Abs(v.x)<1.6f && v.z>0f).Max(v=>v.z) <= 80.01f, "Lead extends beyond terminal");
            Require(mesh.vertices.Any(v=>Mathf.Abs(v.x)>18.8f && v.y>2.1f && v.y<2.3f), "Boundary fence missing");
            Require(!mesh.vertices.Any(v=>Mathf.Abs(v.x)<2.9f && v.z>79.7f && v.y>.5f && v.y<2.3f), "Fence blocks terminal opening");
            Require(Covers(mesh, new Vector2(0f, 77f)), "Missing terminal lead");
            CheckMovedMeshes(assembly,yard,summary,building);
            Render(root, mesh, materials);
            CheckMonorail(root, yard, summary, building, cache);
            File.WriteAllText(report, $"PASS rail yard: {checks} checks; {mesh.vertexCount} vertices\n");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { File.WriteAllText(report, "FAIL " + e); Debug.LogException(e); EditorApplication.Exit(1); }
    }

    static void CheckMonorail(string root, Type yard, Type summary, object building, IDictionary cache)
    {
        foreach (string part in new[] {"yard_wire","yard_pole","yard_beam","floor"})
            cache.Remove($"Models/Buildings/b0/0/{part}");
        foreach (string part in new[] {"yard_track","yard_stop","yard_shed","fence-w1"})
        {
            string asset = $"Assets/Buildings/a1064/0/{part}.fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(asset);
            importer.isReadable = true; importer.SaveAndReimport();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            cache[$"Models/Buildings/b0/0/{part}"] = prefab;
            cache[$"Models/Buildings/a1064/0/{part}"] = prefab;
        }
        summary.GetField("variant").SetValue(building,1064);
        object result=yard.GetMethod("Generate").Invoke(Activator.CreateInstance(yard),new[] {building,(object)false});
        var mesh=(Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
        var materials=(Material[])result.GetType().GetProperty("Materials").GetValue(result);
        Require(mesh.vertexCount>5000,"Monorail yard fell back to box");
        Require(mesh.bounds.max.y<6.1f,"Unwanted overhead wire equipment");
        Require(Covers(mesh,new Vector2(0,79)),"Missing monorail lead");
        var polygon=new[] {new Vector2(-80,-19),new Vector2(80,-19),new Vector2(80,19),new Vector2(-80,19)};
        var rows=((IEnumerable)yard.GetMethod("PlanRows",PrivateStatic).Invoke(null,new object[] {polygon,new Vector2(80,0),0f,7})).Cast<object>();
        foreach(var row in rows)
        {
            var type=row.GetType();
            if((int)type.GetField("Side").GetValue(row)==0) continue;
            var p0=new Vector2((float)type.GetField("StraightEnd").GetValue(row),(float)type.GetField("V").GetValue(row));
            var p3=(Vector2)type.GetField("Target").GetValue(row);
            var direction=(Vector2)type.GetField("Direction").GetValue(row);
            var p1=p0+Vector2.right*((p3.x-p0.x)*.4f);
            var p2=p3-direction*4;
            for(int i=48;i<=96;i++)
            {
                float t=i/96f, a=1-t;
                var p=a*a*a*p0+3*a*a*t*p1+3*a*t*t*p2+t*t*t*p3;
                Require(Covers(mesh,new Vector2(-p.y,p.x)),"Gap in monorail merging guideway at "+p);
            }
        }
        var old=GameObject.Find("RailYard"); if(old!=null) UnityEngine.Object.DestroyImmediate(old);
        Render(root,mesh,materials,"monorail-yard");
    }

    static void CheckPlan(Type yard)
    {
        var polygon = new[] { new Vector2(-80,-19),new Vector2(80,-19),new Vector2(80,19),new Vector2(-80,19) };
        foreach (float u in new[] { 20f,60f,79f,80f })
        foreach (float v in new[] { -19f,-17f,-8f,-.5f,0f,.5f,8f,17f,19f })
        {
            var node=new Vector2(u,v);
            var rows=((IEnumerable)yard.GetMethod("PlanRows",PrivateStatic).Invoke(null,new object[] { polygon,node,0f,7 })).Cast<object>().ToArray();
            Require(rows.Length>0,"Terminal movement removed the whole yard: "+node);
            Func<object,string,float> scalar=(row,field)=>(float)row.GetType().GetField(field).GetValue(row);
            Func<object,int> side=row=>(int)row.GetType().GetField("Side").GetValue(row);
            var main=rows.Single(row=>side(row)==0);
            Require(Mathf.Abs(scalar(main,"V")-node.y)<.001f,"Central track misses terminal");
            Require(Mathf.Abs(scalar(main,"StraightEnd")-(node.x-6f))<.001f,"Central track is not straight up to lead");
            if (v==0f) Require(rows.Any(row=>side(row)<0)&&rows.Any(row=>side(row)>0),"Centered terminal lacks two-sided collection");
            foreach (int direction in new[] { -1,1 })
            {
                var group=rows.Where(row=>side(row)==direction).ToArray();
                if (group.Length==0) continue;
                Require(group.Count(row=>(bool)row.GetType().GetField("IsSpine").GetValue(row))==1,"Collector has multiple owners");
                var first=(Vector2)group[0].GetType().GetField("Target").GetValue(group[0]);
                foreach (var row in group)
                {
                    var target=(Vector2)row.GetType().GetField("Target").GetValue(row);
                    var merge=(Vector2)row.GetType().GetField("MergePoint").GetValue(row);
                    var tangent=(Vector2)row.GetType().GetField("Direction").GetValue(row);
                    var delta=target-first;
                    Require(Mathf.Abs(delta.x*tangent.y-delta.y*tangent.x)<.001f,"Merge points are off collector");
                    Require((scalar(row,"V")-node.y)*direction>=(target.y-node.y)*direction
                        && (target.y-node.y)*direction>0 && Mathf.Abs(merge.y-node.y)<.001f,"Collector crosses terminal axis and curves back");
                    Require(merge.x>target.x && target.x>scalar(row,"StraightEnd"),"Collector reverses longitudinal direction");
                }
            }
        }
    }

    // 端子が未ロードのときの初期位置。長方形の敷地は短辺(2本のうち辺番号の若い方)の中点で、バックエンドと同じ規則。
    static void CheckTerminalFallback(Type yard)
    {
        var method=yard.GetMethod("TryGetRectangleShortSideMidpoint",PrivateStatic);
        Func<Vector2[],Vector2?> midpoint=ring=>{
            var args=new object[] { ring,null };
            return (bool)method.Invoke(null,args) ? (Vector2?)args[1] : null;
        };
        Require(midpoint(new[] { new Vector2(19,80),new Vector2(-19,80),new Vector2(-19,-80),new Vector2(19,-80) })==new Vector2(0,80),"Terminal is not on the first short side");
        Require(midpoint(new[] { new Vector2(0,0),new Vector2(60,0),new Vector2(60,40),new Vector2(0,40) })==new Vector2(60,20),"Terminal is not on the short side");
        Require(midpoint(new[] { new Vector2(0,0),new Vector2(30,0),new Vector2(30,30),new Vector2(0,30) })==new Vector2(15,0),"Square does not use side 0");
        Require(midpoint(new[] { new Vector2(0,0),new Vector2(30,0),new Vector2(60,0),new Vector2(60,40),new Vector2(0,40),new Vector2(0,0) })==new Vector2(60,20),"Mid-edge vertex is counted as a corner");
        Require(midpoint(new[] { new Vector2(0,0),new Vector2(60,0),new Vector2(62,40),new Vector2(2,40) })==null,"Parallelogram treated as rectangle");
        Require(midpoint(new[] { new Vector2(0,0),new Vector2(60,0),new Vector2(60,20),new Vector2(30,20),new Vector2(30,40),new Vector2(0,40) })==null,"L-shape treated as rectangle");
    }

    static void CheckMovedMeshes(Assembly assembly,Type yard,Type summary,object building)
    {
        var managerType=assembly.GetType("PathManager");
        var go=new GameObject("TerminalMoveQA");
        var manager=go.AddComponent(managerType);
        var cache=(IDictionary)managerType.GetField("nodeDataCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(manager);
        var nodeType=assembly.GetType("NodeData");
        summary.GetField("depot_node_id").SetValue(building,1);
        foreach (var pos in new[] { new Vector3(.5f,0,80),new Vector3(-.5f,0,79),new Vector3(8,0,75),new Vector3(-17,0,70),new Vector3(0,0,-75) })
        {
            var node=Activator.CreateInstance(nodeType);
            nodeType.GetField("node_id").SetValue(node,1);
            nodeType.GetField("position").SetValue(node,new[] {pos.x,pos.y,pos.z});
            cache[1]=node;
            var result=yard.GetMethod("Generate").Invoke(Activator.CreateInstance(yard),new[] { building,(object)false });
            var mesh=(Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
            Require(mesh.vertexCount>5000,"Moved terminal produced a white box: "+pos);
            float direction=pos.z<0 ? -1f:1f;
            Require(Covers(mesh,new Vector2(pos.x,pos.z-direction*2f)),"Moved terminal has no straight lead: "+pos);
            UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate((Mesh)result.GetType().GetProperty("ColliderMesh").GetValue(result));
        }
        UnityEngine.Object.DestroyImmediate(go);
        summary.GetField("depot_node_id").SetValue(building,0);
    }

    static bool Covers(Mesh mesh, Vector2 p)
    {
        var v = mesh.vertices; var triangles = mesh.triangles;
        Func<Vector2, Vector2, float> cross = (a, b) => a.x*b.y-a.y*b.x;
        for (int i = 0; i < triangles.Length; i += 3) {
            Vector3 va=v[triangles[i]], vb=v[triangles[i+1]], vc=v[triangles[i+2]];
            if (Mathf.Max(va.y, Mathf.Max(vb.y, vc.y)) > .15f) continue;
            var a=new Vector2(va.x,va.z); var b=new Vector2(vb.x,vb.z); var c=new Vector2(vc.x,vc.z);
            if (Mathf.Abs(cross(b-a,c-a)) < .00001f) continue;
            float x=cross(b-a,p-a), y=cross(c-b,p-b), z=cross(a-c,p-c);
            if ((x>=0 && y>=0 && z>=0) || (x<=0 && y<=0 && z<=0)) return true;
        }
        return false;
    }

    static void Render(string root, Mesh mesh, Material[] source, string prefix = "rail-yard")
    {
        var go = new GameObject("RailYard"); go.AddComponent<MeshFilter>().sharedMesh=mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = source.Select(m => new Material(Shader.Find("Standard")) {
            color = m != null ? m.color : Color.gray }).ToArray();
        RenderSettings.ambientLight = new Color(.65f,.65f,.65f);
        var light=new GameObject("Sun").AddComponent<Light>(); light.type=LightType.Directional;
        light.transform.rotation=Quaternion.Euler(50,-30,0); light.intensity=1.3f;
        var camera=new GameObject("Camera").AddComponent<Camera>();
        camera.orthographic=true; camera.orthographicSize=90;
        camera.transform.position=new Vector3(90,145,155); camera.transform.LookAt(Vector3.zero);
        camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.23f,.30f,.25f);
        Save(camera,root+"/Temp/IntersectionUnityQA/"+prefix+"-preview.png");
        camera.orthographicSize=40;
        camera.transform.position=new Vector3(45,80,110); camera.transform.LookAt(new Vector3(0,0,35));
        Save(camera,root+"/Temp/IntersectionUnityQA/"+prefix+"-throat.png");
    }

    static void Save(Camera camera, string path)
    {
        var rt=new RenderTexture(1400,1100,24); camera.targetTexture=rt; camera.Render(); RenderTexture.active=rt;
        var tex=new Texture2D(1400,1100,TextureFormat.RGB24,false); tex.ReadPixels(new Rect(0,0,1400,1100),0,0); tex.Apply();
        File.WriteAllBytes(path,tex.EncodeToPNG()); RenderTexture.active=null; camera.targetTexture=null;
        UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
    }
}
