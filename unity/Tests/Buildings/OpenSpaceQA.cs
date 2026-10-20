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
    [Serializable] public class Prop {public string part;public Vector2 position;public Vector2 forward;}
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
        foreach(var item in (IEnumerable)Field(plan,"Props"))s.props.Add(new Prop {part=(string)Field(item,"Part"),position=(Vector2)Field(item,"Position"),forward=(Vector2)Field(item,"Forward")});
        return s;
    }
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
                    object plan=layout.GetMethod("Create").Invoke(null,new object[]{pair.Value,model,.5f});
                    object again=layout.GetMethod("Create").Invoke(null,new object[]{pair.Value,model,.5f});
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
                    }
                    var rotated=new Vector2[pair.Value.Length];var q=Quaternion.Euler(0,0,37);
                    for(int i=0;i<rotated.Length;i++){var v=q*(Vector3)pair.Value[i];rotated[i]=new Vector2(v.x+100,v.y-55);}
                    var rplan=layout.GetMethod("Create").Invoke(null,new object[]{rotated,model,.5f});
                    Require(((ICollection)Field(plan,"ParkingBays")).Count==((ICollection)Field(rplan,"ParkingBays")).Count,"Rotation changes bay count "+pair.Key);
                    // The loop road is created in the local coordinates of the roadside edge, so rotation and translation do not change the number of decorations or whether the loop road is present (allow a difference of 1–2 due to rounding errors near the boundary).
                    Require(model!=11 || (bool)Field(plan,"HasLoop")==(bool)Field(rplan,"HasLoop"),"Rotation changes rotary loop "+pair.Key);
                    Require(model!=11 || Math.Abs(snapshot.props.Count-Export(rplan).props.Count)<=2,"Rotation changes rotary furniture "+pair.Key);
                    object result=builder.GetMethod("Build").Invoke(null,new object[]{plan,parts,Vector3.zero,false,null});
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
                            var alt=Export(layout.GetMethod("Create").Invoke(null,new object[]{pair.Value,model,other}));
                            foreach(var s2 in alt.surfaces)Require((bool)layout.GetMethod("ContainsPolygon").Invoke(null,new object[]{pair.Value,s2.polygon}),"Surface outside seed "+other);
                            var altPlan=layout.GetMethod("Create").Invoke(null,new object[]{pair.Value,model,other});
                            object altResult=builder.GetMethod("Build").Invoke(null,new object[]{altPlan,parts,Vector3.zero,false,null});
                            Mesh altMesh=(Mesh)Field(altResult,"Mesh");
                            Render(altMesh,(Material[])Field(altResult,"Materials"),output+"/"+variant+"-rectangle-"+Mathf.RoundToInt(other*100)+".png");
                            UnityEngine.Object.Destroy(altMesh);UnityEngine.Object.Destroy((Mesh)Field(altResult,"ColliderMesh"));
                        }
                    if(pair.Key=="rectangle")File.WriteAllText(output+"/a"+variant+".json",JsonUtility.ToJson(snapshot,true));
                    UnityEngine.Object.Destroy(mesh);UnityEngine.Object.Destroy(collider);
                }
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
                object plan=layout.GetMethod("Create").Invoke(null,new object[]{cases["rectangle"],model,.5f});
                object result=builder.GetMethod("Build").Invoke(null,new object[]{plan,LoadParts(model==12?3004:model==10?3001:3002),center,true,null});
                var mesh=(Mesh)Field(result,"Mesh");Require(mesh.bounds.size.y>.5f,"Terrain height not applied");
                // The rendered surface keeps the same slope as the shared terrain grid and does not sink into the terrain.
                var surface=builder.GetMethod("SurfaceMesh",BindingFlags.NonPublic|BindingFlags.Static);
                var panel=(Mesh)surface.Invoke(null,new object[]{cases["rectangle"],center,true,.025f});
                foreach(var v in panel.vertices)
                {float ground=terrain.SampleHeight(new Vector3(v.x,0,v.z));Require(Mathf.Abs(v.y+center.y-ground-.03f)<.003f,"Surface does not follow terrain");}
                UnityEngine.Object.Destroy(panel);UnityEngine.Object.Destroy(mesh);UnityEngine.Object.Destroy((Mesh)Field(result,"ColliderMesh"));
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
        if(parts.TryGetValue("site_tree",out var tree))
        {
            var treeMesh=tree.GetComponentInChildren<MeshFilter>().sharedMesh;
            Require(treeMesh.triangles.Length/3==144,"Tree triangle budget changed");
            var foliage=Array.Find(tree.GetComponentInChildren<MeshRenderer>().sharedMaterials,m=>m.name=="PROP_LEAF_GREEN");
            Require(foliage!=null && foliage.mainTexture!=null,"Tree foliage texture missing");
        }
        return parts;
    }
    static void Render(Mesh mesh,Material[] materials,string file)
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
        var camera=new GameObject("Camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=34;camera.backgroundColor=new Color(.19f,.22f,.26f);camera.clearFlags=CameraClearFlags.SolidColor;
        camera.transform.position=new Vector3(40,65,-65);camera.transform.LookAt(Vector3.zero);
        var rt=new RenderTexture(1000,800,24);camera.targetTexture=rt;camera.Render();
        RenderTexture.active=rt;var image=new Texture2D(1000,800,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1000,800),0,0);image.Apply();File.WriteAllBytes(file,image.EncodeToPNG());RenderTexture.active=null;
        camera.targetTexture=null;
        UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(light.gameObject);UnityEngine.Object.DestroyImmediate(camera.gameObject);foreach(var mat in mats)UnityEngine.Object.DestroyImmediate(mat);
    }
}
