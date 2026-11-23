using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// Use the main DLL and actual FBXs in an isolated Unity project to verify placement invariants and rendering.
public static class OpenSpaceQA
{
    static Type layout,builder,polygonUtils;
    static int checks;
    static object Field(object value,string name)=>value.GetType().GetField(name)?.GetValue(value) ?? value.GetType().GetProperty(name).GetValue(value);
    static object Optional(object value,string name)=>value.GetType().GetField(name).GetValue(value); // Public field that may be null
    static bool ContainsPoint(object polygon,object point)=>(bool)polygonUtils.GetMethod("Contains",new[]{typeof(IReadOnlyList<Vector2>),typeof(Vector2),typeof(float)}).Invoke(null,new object[]{polygon,point,layout.GetField("BOUNDARY_TOLERANCE").GetValue(null)}); // Check the site using the same boundary margin as the main assembly
    static void Require(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    [Serializable] public class Ring {public Vector2[] points;}
    [Serializable] public class Surface {public string material;public Vector2[] polygon;public List<Ring> holes=new List<Ring>();public float lift;public string side;public Vector2[] points;public int[] triangles;}
    [Serializable] public class Prop {public string part;public Vector2 position;public Vector2 forward;public float scale;}
    [Serializable] public class Snapshot {public Vector2[] polygon;public List<Surface> surfaces=new List<Surface>();public List<Prop> props=new List<Prop>();}
    // The gallery renders faces using the same triangulation as the main assembly, so export the triangulated vertices and indices for faces with holes as well.
    static Snapshot Export(object plan)
    {
        var s=new Snapshot {polygon=(Vector2[])Field(plan,"Polygon")};
        var triangulate=builder.GetMethod("Triangulate");
        foreach(var item in (IEnumerable)Field(plan,"Surfaces"))
        {
            var surface=new Surface {material=(string)Field(item,"Material"),polygon=(Vector2[])Field(item,"Polygon"),lift=(float)Field(item,"Lift"),side=(string)Optional(item,"Side")};
            if(Optional(item,"Holes") is Vector2[][] holes)foreach(var hole in holes)surface.holes.Add(new Ring {points=hole});
            var args=new object[]{item,null,null};triangulate.Invoke(null,args);
            surface.points=((List<Vector2>)args[1]).ToArray();surface.triangles=((List<int>)args[2]).ToArray();
            s.surfaces.Add(surface);
        }
        foreach(var item in (IEnumerable)Field(plan,"Props"))s.props.Add(new Prop {part=(string)Field(item,"Part"),position=(Vector2)Field(item,"Position"),forward=(Vector2)Field(item,"Forward"),scale=(float)Field(item,"Scale")});
        return s;
    }
    // The park places furniture from the actual part sizes (X width, Z depth about the origin), measured as the main assembly does.
    static Dictionary<string,Vector2> Sizes(Dictionary<string,GameObject> parts)
    {
        var sizes=new Dictionary<string,Vector2>();
        foreach(var pair in parts)
        {
            bool any=false;var bounds=new Bounds();
            foreach(var filter in pair.Value.GetComponentsInChildren<MeshFilter>())
                foreach(var v in filter.sharedMesh.vertices)
                {var w=filter.transform.TransformPoint(v);if(any)bounds.Encapsulate(w);else{bounds=new Bounds(w,Vector3.zero);any=true;}}
            if(any)sizes[pair.Key]=new Vector2(2*Mathf.Max(-bounds.min.x,bounds.max.x),2*Mathf.Max(-bounds.min.z,bounds.max.z));
        }
        return sizes;
    }
    static object Plan(Vector2[] polygon,int model,float seed,Dictionary<string,GameObject> parts,float density=.5f)
        =>layout.GetMethod("Create").Invoke(null,new object[]{polygon,model,seed,density,model==12?Sizes(parts):null});
    static object Build(object plan,Dictionary<string,GameObject> parts,Vector3 center,bool terrain)
        =>builder.GetMethod("Build").Invoke(null,new object[]{plan,parts,center,terrain,null,null,null});
    [InitializeOnLoadMethod] static void Resume()
    {
        EditorApplication.delayCall+=()=>{if(SessionState.GetBool("OpenSpaceQA.Pending",false)&&EditorApplication.isPlaying){SessionState.SetBool("OpenSpaceQA.Pending",false);Run();}};
    }
    public static void Run()
    {
        if(!EditorApplication.isPlaying)
        {
            // Reimport an FBX remapped to the shared sign material if it was imported without that material during the previous verification; otherwise, the missing material remains.
            AssetDatabase.Refresh();
            foreach(string f in Directory.GetFiles("Assets/Models/Buildings","*.fbx",SearchOption.AllDirectories))
            {
                string path=f.Replace('\\','/');
                if(File.ReadAllText(path+".meta").Contains("type: UnityEngine:Material"))AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate);
            }
            ModelPrefabGenerator.SyncAll();SessionState.SetBool("OpenSpaceQA.Pending",true);EditorApplication.EnterPlaymode();return;
        }
        var args=Environment.GetCommandLineArgs();string root=args[Array.IndexOf(args,"-sourceRoot")+1];
        string output=root+"/Temp/OpenSpaceQA";
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,e)=>{
                foreach(string dir in new[]{"Temp/bin/Debug","Library/ScriptAssemblies","Assets/Plugins"})
                {string file=Path.Combine(root,dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            var assembly=Assembly.LoadFrom(root+"/Temp/bin/Debug/Assembly-CSharp.dll");
            layout=assembly.GetType("OpenSpaceLayout");builder=assembly.GetType("OpenSpaceBuildingMeshBuilder");polygonUtils=assembly.GetType("PolygonUtils");
            var cases=new Dictionary<string,Vector2[]> {
                {"rectangle",new[]{new Vector2(-25,-20),new Vector2(25,-20),new Vector2(25,20),new Vector2(-25,20)}},
                {"trapezoid",new[]{new Vector2(-25,-20),new Vector2(25,-20),new Vector2(15,20),new Vector2(-12,20)}},
                {"concave",new[]{new Vector2(-25,-20),new Vector2(25,-20),new Vector2(25,-2),new Vector2(2,-2),new Vector2(2,20),new Vector2(-25,20)}},
                {"triangle",new[]{new Vector2(-25,-20),new Vector2(25,-20),new Vector2(0,20)}},
                {"narrow",new[]{new Vector2(-2,-20),new Vector2(2,-20),new Vector2(2,20),new Vector2(-2,20)}},
                {"tiny",new[]{new Vector2(0,0),new Vector2(1,0),new Vector2(0,1)}},
                {"notch",new[]{new Vector2(-25,-20),new Vector2(25,-20),new Vector2(25,20),new Vector2(2,20),new Vector2(2,-10),new Vector2(-2,-10),new Vector2(-2,20),new Vector2(-25,20)}}};
            foreach(int model in new[]{10,11,12})
            {
                int variant=model==12?3004:model==10?3001:3002;
                var parts=LoadParts(variant);
                foreach(var pair in cases)
                {
                    object plan=Plan(pair.Value,model,.5f,parts);
                    object again=Plan(pair.Value,model,.5f,parts);
                    var snapshot=Export(plan);
                    Require(JsonUtility.ToJson(snapshot)==JsonUtility.ToJson(Export(again)),"Non deterministic "+pair.Key);
                    foreach(var s in snapshot.surfaces)Require((bool)layout.GetMethod("ContainsPolygon").Invoke(null,new object[]{pair.Value,s.polygon}),"Surface outside "+pair.Key);
                    foreach(var prop in snapshot.props)Require(ContainsPoint(pair.Value,prop.position),"Prop outside "+pair.Key);
                    foreach(var bay in (IEnumerable)Field(plan,"ParkingBays"))Require((bool)layout.GetMethod("ContainsPolygon").Invoke(null,new object[]{pair.Value,bay}),"Bay outside");
                    if(pair.Key=="rectangle")
                    {
                        Require(model!=10 || ((ICollection)Field(plan,"ParkingBays")).Count>0,"Missing bays");
                        Require(model!=11 || (bool)Field(plan,"HasLoop"),"Missing rotary loop");
                        Require(model!=11 || new[]{"site_shelter","site_light","site_clock","site_tree"}.All(n=>snapshot.props.Any(x=>x.part==n)),"Missing rotary furniture");
                        Require(model!=11 || snapshot.surfaces.Any(x=>x.side!=null),"Missing raised curb surfaces");
                        Require(snapshot.props.Count>0,"Missing furniture");
                        Require(model!=12 || new[]{"site_paving","site_curb"}.All(n=>snapshot.surfaces.Any(x=>x.material==n)),"Missing park paths");
                        Require(model!=12 || new[]{"site_tree","site_shrub","site_feature","site_play","site_bench","site_light","site_hedge"}.All(n=>snapshot.props.Any(x=>x.part.StartsWith(n))),"Missing park furniture");
                    }
                    var rotated=new Vector2[pair.Value.Length];var q=Quaternion.Euler(0,0,37);
                    for(int i=0;i<rotated.Length;i++){var v=q*(Vector3)pair.Value[i];rotated[i]=new Vector2(v.x+100,v.y-55);}
                    var rplan=Plan(rotated,model,.5f,parts);
                    Require(((ICollection)Field(plan,"ParkingBays")).Count==((ICollection)Field(rplan,"ParkingBays")).Count,"Rotation changes bay count "+pair.Key);
                    // The loop road is created in the local coordinates of the roadside edge, so rotation and translation do not change the number of decorations or whether the loop road is present (allow a difference of 1–2 due to rounding errors near the boundary).
                    Require(model!=11 || (bool)Field(plan,"HasLoop")==(bool)Field(rplan,"HasLoop"),"Rotation changes rotary loop "+pair.Key);
                    Require(model!=11 || Math.Abs(snapshot.props.Count-Export(rplan).props.Count)<=2,"Rotation changes rotary furniture "+pair.Key);
                    // The park is also laid out in the local coordinates of the roadside edge, so rotated sites keep their surfaces and furniture inside and nearly the same counts.
                    if(model==12)
                    {
                        var rsnapshot=Export(rplan);
                        foreach(var s in rsnapshot.surfaces)Require((bool)layout.GetMethod("ContainsPolygon").Invoke(null,new object[]{rotated,s.polygon}),"Rotated surface outside "+pair.Key);
                        foreach(var prop in rsnapshot.props)Require(ContainsPoint(rotated,prop.position),"Rotated prop outside "+pair.Key);
                        Require(Math.Abs(snapshot.props.Count-rsnapshot.props.Count)<=2+snapshot.props.Count*.03f,"Rotation changes park furniture "+pair.Key);
                    }
                    object result=Build(plan,parts,Vector3.zero,false);
                    Mesh mesh=(Mesh)Field(result,"Mesh"),collider=(Mesh)Field(result,"ColliderMesh");
                    Require(mesh.vertexCount>0 && collider.vertexCount>0,"Empty surface/collider");
                    foreach(var v in mesh.vertices)Require(!float.IsNaN(v.x)&&!float.IsInfinity(v.y),"Non finite vertex");
                    // If a part remapped to the shared sign material is missing that material, it will not render in the main assembly either.
                    Require(((Material[])Field(result,"Materials")).All(m=>m!=null),"Missing material in "+variant+" "+pair.Key+": "+string.Join(",",parts.Where(x=>x.Value.GetComponentsInChildren<Renderer>().Any(r=>r.sharedMaterials.Any(m=>m==null))).Select(x=>x.Key)));
                    if(pair.Key=="rectangle"||pair.Key=="trapezoid"||pair.Key=="concave"||(model==11&&(pair.Key=="triangle"||pair.Key=="notch")))Render(mesh,(Material[])Field(result,"Materials"),output+"/"+variant+"-"+pair.Key+".png");
                    // visual_variant2 changes the outer rounding, sidewalk width, and entrances of the station-front roundabout. Verify that it fits within the site with another value, and keep a sample.
                    if(model==11&&pair.Key=="rectangle")
                        foreach(float other in new[]{.12f,.87f})
                        {
                            var altPlan=Plan(pair.Value,model,other,parts);
                            var alt=Export(altPlan);
                            foreach(var s2 in alt.surfaces)Require((bool)layout.GetMethod("ContainsPolygon").Invoke(null,new object[]{pair.Value,s2.polygon}),"Surface outside seed "+other);
                            object altResult=Build(altPlan,parts,Vector3.zero,false);
                            Mesh altMesh=(Mesh)Field(altResult,"Mesh");
                            Render(altMesh,(Material[])Field(altResult,"Materials"),output+"/"+variant+"-rectangle-"+Mathf.RoundToInt(other*100)+".png");
                            UnityEngine.Object.Destroy(altMesh);UnityEngine.Object.Destroy((Mesh)Field(altResult,"ColliderMesh"));
                        }
                    // visual_variant3 is the park's tree density: a denser value places more trees, and every value stays inside the site.
                    if(model==12&&pair.Key=="rectangle")
                    {
                        int Trees(Snapshot s)=>s.props.Count(x=>x.part.StartsWith("site_tree"));
                        var sparse=Export(Plan(pair.Value,model,.5f,parts,.05f));
                        foreach(float density in new[]{.05f,.95f})
                        {
                            var altPlan=Plan(pair.Value,model,.5f,parts,density);
                            var alt=Export(altPlan);
                            foreach(var prop in alt.props)Require(ContainsPoint(pair.Value,prop.position),"Prop outside density "+density);
                            if(density>.5f)Require(Trees(alt)>Trees(snapshot)&&Trees(snapshot)>Trees(sparse),"Tree density does not follow visual_variant3");
                            object altResult=Build(altPlan,parts,Vector3.zero,false);
                            Mesh altMesh=(Mesh)Field(altResult,"Mesh");
                            Render(altMesh,(Material[])Field(altResult,"Materials"),output+"/"+variant+"-rectangle-density"+Mathf.RoundToInt(density*100)+".png");
                            UnityEngine.Object.Destroy(altMesh);UnityEngine.Object.Destroy((Mesh)Field(altResult,"ColliderMesh"));
                        }
                    }
                    if(pair.Key=="rectangle")File.WriteAllText(output+"/a"+variant+".json",JsonUtility.ToJson(snapshot,true));
                    UnityEngine.Object.Destroy(mesh);UnityEngine.Object.Destroy(collider);
                }
            }
            // The sports park (3008) uses the park layout with courts: each court is laid out only where it fits, largest first, and the rest of the site stays a park.
            var sports=LoadParts(3008);
            Vector2[] Box(float w,float h)=>new[]{new Vector2(-w/2,-h/2),new Vector2(w/2,-h/2),new Vector2(w/2,h/2),new Vector2(-w/2,h/2)};
            var courtSurfaces=new[]{"site_diamond","site_track","site_pitch","site_court"};
            var sportsCases=new (string name,Vector2[] polygon,string[] courts)[] {
                ("tennis",Box(60,50),new[]{"site_court"}),
                ("default",Box(150,100),new[]{"site_diamond","site_pitch"}),
                ("large",Box(260,200),courtSurfaces),
                ("trapezoid",new[]{new Vector2(-90,-65),new Vector2(90,-65),new Vector2(60,65),new Vector2(-50,65)},new[]{"site_diamond","site_court"}),
                ("concave",new[]{new Vector2(-100,-85),new Vector2(100,-85),new Vector2(100,-15),new Vector2(10,-15),new Vector2(10,85),new Vector2(-100,85)},new[]{"site_diamond","site_track","site_pitch"}),
                ("narrow",Box(30,200),new string[0])};
            foreach(var item in sportsCases)
            {
                object plan=Plan(item.polygon,12,.5f,sports);
                var snapshot=Export(plan);
                Require(JsonUtility.ToJson(snapshot)==JsonUtility.ToJson(Export(Plan(item.polygon,12,.5f,sports))),"Non deterministic sports "+item.name);
                foreach(var s in snapshot.surfaces)Require((bool)layout.GetMethod("ContainsPolygon").Invoke(null,new object[]{item.polygon,s.polygon}),"Sports surface outside "+item.name);
                foreach(var prop in snapshot.props)Require(ContainsPoint(item.polygon,prop.position),"Sports prop outside "+item.name);
                string[] Courts(Snapshot s)=>courtSurfaces.Where(n=>s.surfaces.Any(x=>x.material==n)).ToArray();
                int Count(string part)=>snapshot.props.Count(x=>x.part==part);
                Require(Courts(snapshot).SequenceEqual(courtSurfaces.Where(n=>item.courts.Contains(n))),"Unexpected courts in "+item.name+": "+string.Join(",",Courts(snapshot)));
                Require(snapshot.surfaces.Any(x=>x.material=="site_paving"),"Sports park without paths "+item.name);
                if(item.courts.Length>0)Require(snapshot.surfaces.Any(x=>x.material=="site_marking") && Count("site_floodlight")>=4,"Court without lines or lights "+item.name);
                Require(!item.courts.Contains("site_diamond") || (Count("site_backstop")==1 && Count("site_fence")>20),"Baseball field furniture "+item.name);
                Require(!item.courts.Contains("site_track") || Count("site_stand")>=1,"Track stands "+item.name);
                Require(!item.courts.Contains("site_pitch") || Count("site_goal")==2,"Soccer goals "+item.name);
                Require(!item.courts.Contains("site_court") || (Count("site_net")==2 && Count("site_fence")>20),"Tennis court furniture "+item.name);
                // Courts are placed in the local coordinates of the roadside edge, so a rotated and moved site gets the same courts.
                var rotated=new Vector2[item.polygon.Length];var q=Quaternion.Euler(0,0,37);
                for(int i=0;i<rotated.Length;i++){var v=q*(Vector3)item.polygon[i];rotated[i]=new Vector2(v.x+100,v.y-55);}
                var rsnapshot=Export(Plan(rotated,12,.5f,sports));
                Require(Courts(rsnapshot).SequenceEqual(Courts(snapshot)),"Rotation changes courts "+item.name);
                foreach(var s in rsnapshot.surfaces)Require((bool)layout.GetMethod("ContainsPolygon").Invoke(null,new object[]{rotated,s.polygon}),"Rotated sports surface outside "+item.name);
                foreach(var prop in rsnapshot.props)Require(ContainsPoint(rotated,prop.position),"Rotated sports prop outside "+item.name);
                object result=Build(plan,sports,Vector3.zero,false);
                Mesh mesh=(Mesh)Field(result,"Mesh"),collider=(Mesh)Field(result,"ColliderMesh");
                Require(mesh.vertexCount>0 && collider.vertexCount>0,"Empty sports surface/collider");
                foreach(var v in mesh.vertices)Require(!float.IsNaN(v.x)&&!float.IsInfinity(v.y),"Non finite sports vertex");
                Require(((Material[])Field(result,"Materials")).All(m=>m!=null),"Missing material in 3008 "+item.name);
                float extent=0;foreach(var v in item.polygon)extent=Mathf.Max(extent,v.magnitude);
                Render(mesh,(Material[])Field(result,"Materials"),output+"/3008-"+item.name+".png",extent*.78f,2);
                if(item.name=="default")
                {
                    File.WriteAllText(output+"/a3008.json",JsonUtility.ToJson(snapshot,true));
                    // Generation time of the sports park against the ordinary park on the same site (plan and mesh separately, after a warm-up run).
                    var park=LoadParts(3004);
                    var timings=new System.Text.StringBuilder("variant,plan_ms,build_ms,surfaces,props\n");
                    foreach(var (variant,set) in new[]{(3004,park),(3008,sports)})
                    {
                        double planMs=0,buildMs=0;object timed=null;
                        for(int run=0;run<4;run++)
                        {
                            var watch=System.Diagnostics.Stopwatch.StartNew();
                            timed=Plan(item.polygon,12,.5f,set);
                            double planned=watch.Elapsed.TotalMilliseconds;
                            object built=Build(timed,set,Vector3.zero,false);
                            if(run>0){planMs+=planned/3;buildMs+=(watch.Elapsed.TotalMilliseconds-planned)/3;}
                            UnityEngine.Object.Destroy((Mesh)Field(built,"Mesh"));UnityEngine.Object.Destroy((Mesh)Field(built,"ColliderMesh"));
                        }
                        timings.Append(variant).Append(',').Append(planMs.ToString("F1")).Append(',').Append(buildMs.ToString("F1")).Append(',')
                            .Append(((ICollection)Field(timed,"Surfaces")).Count).Append(',').Append(((ICollection)Field(timed,"Props")).Count).Append("\n");
                    }
                    File.WriteAllText(output+"/timings.csv",timings.ToString());
                }
                UnityEngine.Object.Destroy(mesh);UnityEngine.Object.Destroy(collider);
            }
            // The paved plaza has only a floor and does not pick up decorations from the existing method for ground surfaces.
            var plaza=LoadParts(3003);Require(plaza.Count==1 && plaza.ContainsKey("floor"),"Plaza has decorations");
            var terrainData=new TerrainData {heightmapResolution=33,size=new Vector3(100,10,100)};
            var heights=new float[33,33];
            for(int y=0;y<33;y++)for(int x=0;x<33;x++)heights[y,x]=.2f+.004f*x+.002f*y;
            terrainData.SetHeights(0,0,heights);
            var terrainObject=Terrain.CreateTerrainGameObject(terrainData);terrainObject.transform.position=new Vector3(-50,0,-50);
            var terrain=terrainObject.GetComponent<Terrain>();terrain.drawHeightmap=false;
            assembly.GetType("Utils").GetMethod("InvalidateHeightCache").Invoke(null,null);
            Vector3 center=new Vector3(0,terrain.SampleHeight(Vector3.zero),0);
            foreach(int model in new[]{10,11,12})
            {
                var terrainParts=LoadParts(model==12?3004:model==10?3001:3002);
                object plan=Plan(cases["rectangle"],model,.5f,terrainParts);
                object result=Build(plan,terrainParts,center,true);
                var mesh=(Mesh)Field(result,"Mesh");Require(mesh.bounds.size.y>.5f,"Terrain height not applied");
                // The rendered surface keeps the same slope as the shared terrain grid and does not sink into the terrain.
                var surface=builder.GetMethod("SurfaceMesh",BindingFlags.NonPublic|BindingFlags.Static);
                var panel=(Mesh)surface.Invoke(null,new object[]{cases["rectangle"],center,true,.025f});
                foreach(var v in panel.vertices)
                {float ground=terrain.SampleHeight(new Vector3(v.x,0,v.z));Require(Mathf.Abs(v.y+center.y-ground-.03f)<.003f,"Surface does not follow terrain");}
                UnityEngine.Object.Destroy(panel);UnityEngine.Object.Destroy(mesh);UnityEngine.Object.Destroy((Mesh)Field(result,"ColliderMesh"));
            }
            // Court surfaces follow the slope like the other plan surfaces. Keep a sample of the sports park on sloped ground.
            {
                object plan=Plan(sportsCases[0].polygon,12,.5f,sports);
                object result=Build(plan,sports,center,true);
                var mesh=(Mesh)Field(result,"Mesh");Require(mesh.vertexCount>0,"Sports park on terrain is empty");
                Render(mesh,(Material[])Field(result,"Materials"),output+"/3008-terrain.png",34,2);
                UnityEngine.Object.Destroy(mesh);UnityEngine.Object.Destroy((Mesh)Field(result,"ColliderMesh"));
            }
            UnityEngine.Object.DestroyImmediate(terrainObject);UnityEngine.Object.DestroyImmediate(terrainData);
            File.WriteAllText(output+"/result.txt","PASS checks="+checks);
            EditorApplication.Exit(0);
        }
        catch(Exception e){File.WriteAllText(output+"/result.txt",e.ToString());Debug.LogException(e);EditorApplication.Exit(1);}
    }
    static Dictionary<string,GameObject> LoadParts(int variant)
    {
        var parts=new Dictionary<string,GameObject>();
        foreach(string f in Directory.GetFiles("Assets/Models/Buildings/a"+variant,"*.fbx",SearchOption.AllDirectories))
        {
            string path=f.Replace('\\','/');var importer=(ModelImporter)AssetImporter.GetAtPath(path);
            if(!importer.isReadable){importer.isReadable=true;importer.SaveAndReimport();}
            parts[Path.GetFileNameWithoutExtension(f)]=AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        // The station-front roundabout tree keeps its budget; park trees are instanced and follow the validator's budget.
        if(variant==3002 && parts.TryGetValue("site_tree",out var tree))
        {
            var treeMesh=tree.GetComponentInChildren<MeshFilter>().sharedMesh;
            Require(treeMesh.triangles.Length/3==892,"Tree triangle budget changed");
            var foliage=Array.Find(tree.GetComponentInChildren<MeshRenderer>().sharedMaterials,m=>m.name=="PROP_LEAF_GREEN");
            Require(foliage!=null && foliage.mainTexture!=null,"Tree foliage texture missing");
        }
        return parts;
    }
    // size is the half height of the view in meters, and scale multiplies the image resolution (large sites need both to show court lines).
    static void Render(Mesh mesh,Material[] materials,string file,float size=34,int scale=1)
    {
        var go=new GameObject("Preview");go.AddComponent<MeshFilter>().sharedMesh=mesh;
        var renderer=go.AddComponent<MeshRenderer>();var mats=new Material[materials.Length];
        for(int i=0;i<mats.Length;i++)
        {
            mats[i]=new Material(Shader.Find("Standard"));mats[i].color=materials[i].HasProperty("_Color")?materials[i].color:Color.gray;
            mats[i].mainTexture=materials[i].mainTexture;
            mats[i].mainTextureScale=materials[i].mainTextureScale;mats[i].mainTextureOffset=materials[i].mainTextureOffset;
            mats[i].SetFloat("_Glossiness",.1f);
        }
        renderer.sharedMaterials=mats;
        var light=new GameObject("Sun").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.1f;light.transform.rotation=Quaternion.Euler(48,-30,0);
        RenderSettings.ambientLight=new Color(.55f,.55f,.55f);
        var camera=new GameObject("Camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=size;camera.farClipPlane=4000;camera.backgroundColor=new Color(.19f,.22f,.26f);camera.clearFlags=CameraClearFlags.SolidColor;
        camera.transform.position=new Vector3(40,65,-65)*(size/34);camera.transform.LookAt(Vector3.zero);
        int width=1000*scale,height=800*scale;
        var rt=new RenderTexture(width,height,24);camera.targetTexture=rt;camera.Render();
        RenderTexture.active=rt;var image=new Texture2D(width,height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();File.WriteAllBytes(file,image.EncodeToPNG());RenderTexture.active=null;
        camera.targetTexture=null;
        UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(light.gameObject);UnityEngine.Object.DestroyImmediate(camera.gameObject);foreach(var mat in mats)UnityEngine.Object.DestroyImmediate(mat);
    }
}
