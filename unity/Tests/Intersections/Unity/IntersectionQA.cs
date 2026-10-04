using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
public static class IntersectionQA
{
 static string root = GetSourceRoot();
 static string GetSourceRoot()
 {
  var args=Environment.GetCommandLineArgs();
  int index=Array.IndexOf(args,"-intersectionSourceRoot");
  if(index<0||index+1>=args.Length)throw new InvalidOperationException("Missing -intersectionSourceRoot");
  return args[index+1];
 }
 static Dictionary<int,List<Vector3>> points=new Dictionary<int,List<Vector3>>();
 static Dictionary<string,List<Vector3>> partVertices=new Dictionary<string,List<Vector3>>();
 static Dictionary<int,List<Vector3>> bodyVertices=new Dictionary<int,List<Vector3>>();
 static Dictionary<int,List<Vector3>> bodyNormals=new Dictionary<int,List<Vector3>>();
 static List<Vector3> Samples<T>(T p) {return points[(int)p.GetType().GetField("path_id").GetValue(p)];}
 public static void Run()
 {
  try {
   AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{string name=new AssemblyName(e.Name).Name+".dll"; foreach(string dir in new[]{root+"/Temp/bin/Debug",root+"/Library/ScriptAssemblies",root+"/Assets/Plugins"}){string path=dir+"/"+name;if(File.Exists(path))return Assembly.LoadFrom(path);}return null;};
   var asm=Assembly.LoadFrom(root+"/Temp/bin/Debug/Assembly-CSharp.dll");
   Type pathType=asm.GetType("GeneratedModels.PathSummary");
   var paths=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pathType));
   var masterType=asm.GetType("MasterDataStore");
   var master=new GameObject("Master").AddComponent(masterType);
   masterType.GetField("<Instance>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,master);
   Type itemType=asm.GetType("PathMasterItem");
   var dict=(IDictionary)masterType.GetField("pathMasterById",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(master);
   masterType.GetProperty("IsInitialized").SetValue(master,true);
   var modelType=asm.GetType("ModelManager"); var model=new GameObject("Models").AddComponent(modelType);
   modelType.GetField("<Instance>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,model);
   var cache=(IDictionary)modelType.GetField("_modelCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(model);
   for(int i=0;i<3;i++) {
    var p=Activator.CreateInstance(pathType); pathType.GetField("path_id").SetValue(p,i+1); pathType.GetField("start_node_id").SetValue(p,1); pathType.GetField("end_node_id").SetValue(p,100+i);pathType.GetField("path_variant").SetValue(p,100+i);pathType.GetField("path_type").SetValue(p,1);pathType.GetField("curved").SetValue(p,false);
    float angle=i*120*Mathf.Deg2Rad;var end=new Vector3(Mathf.Cos(angle)*80,i==0?16f:i==1?-8f:0f,Mathf.Sin(angle)*80);points[i+1]=new List<Vector3>{Vector3.zero,end};
    pathType.GetField("start_point").SetValue(p,new List<object>{0f,0f,0f});pathType.GetField("end_point").SetValue(p,new List<object>{end.x,end.y,end.z});paths.Add(p);
    var item=Activator.CreateInstance(itemType);itemType.GetField("width").SetValue(item,i==0?16f:8f);itemType.GetField("sidewalk_width").SetValue(item,i==2?0f:2f);itemType.GetField("wall_width").SetValue(item,0.4f);itemType.GetField("center_width").SetValue(item,0.6f);itemType.GetField("lanes").SetValue(item,2);itemType.GetField("follow_terrain").SetValue(item,false);itemType.GetField("side_width").SetValue(item,0.5f);
    // 接合検査の下り坂（深さ8m）では、側壁を省略するトンネル区間へ入らないクリアランスを使う。
    itemType.GetField("height").SetValue(item,10f);dict.Add(100+i,item);
    foreach(string part in new[]{"body","sidewalk","wall","center","line"}) {var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=part;go.transform.position=Vector3.down*1000; var mesh=UnityEngine.Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);var v=mesh.vertices;for(int j=0;j<v.Length;j++){v[j].y=part=="body"?v[j].y*.1f-.05f:(v[j].y+.5f)*(part=="wall"?2f:part=="sidewalk"?.2f:.1f);}mesh.vertices=v;mesh.RecalculateBounds();go.GetComponent<MeshFilter>().sharedMesh=mesh;var actual=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Paths/p"+(Array.IndexOf(Environment.GetCommandLineArgs(),"-pavedModels")>=0?2:1)+"/"+part+".fbx");cache.Add("Models/Paths/p"+(100+i)+"/"+part,actual!=null?actual:go);}
   }
   var provider=Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(pathType,typeof(List<Vector3>)),typeof(IntersectionQA).GetMethod("Samples",BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(pathType));
   var gen=Activator.CreateInstance(asm.GetType("RoadIntersectionMeshGenerator"));
   var result=gen.GetType().GetMethod("GenerateWithSections").Invoke(gen,new object[]{1,Vector3.zero,paths,0f,provider,null});
   if(result==null)throw new Exception("null node mesh");
   var meshResult=(Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
   var surface=new GameObject("Surface");surface.AddComponent<MeshFilter>().sharedMesh=meshResult;var mat=new Material(Shader.Find("Standard"));mat.color=Color.gray;surface.AddComponent<MeshRenderer>().sharedMaterial=(Material)result.GetType().GetProperty("Material").GetValue(result)??mat;
   var specs=(IList)result.GetType().GetProperty("SidewalkConnectors").GetValue(result);
   var create=asm.GetType("RoadPath").GetMethod("CreateSidewalkConnectorObject");
   for(int i=0;i<specs.Count;i++)create.Invoke(null,new object[]{surface.transform,specs[i],i});
   var cuts=(IDictionary)result.GetType().GetProperty("ConnectionPoints").GetValue(result);
   var road=Activator.CreateInstance(asm.GetType("RoadPath"));var setback=Activator.CreateInstance(asm.GetType("SidewalkSetback"));
   foreach(var p in paths) {int id=(int)pathType.GetField("path_id").GetValue(p); var piece=new List<Vector3>{(Vector3)cuts[id],points[id][1]};var roadResult=road.GetType().GetMethod("Generate").Invoke(road,new object[]{p,piece,setback,setback});if(roadResult==null)throw new Exception("road null");var go=(GameObject)roadResult.GetType().GetProperty("GeneratedObject").GetValue(roadResult);foreach(var comp in go.GetComponentsInChildren<Component>()){if(comp==null||comp.GetType().Name!="SplineMesh")continue;var rebuild=comp.GetType().GetMethod("RebuildImmediate");var pars=rebuild.GetParameters();rebuild.Invoke(comp,pars.Length==0?null:new object[]{true});}foreach(var filter in go.GetComponentsInChildren<MeshFilter>()){string part=filter.gameObject.name.Split('/').Last();if(filter.sharedMesh!=null){if(!partVertices.ContainsKey(part))partVertices[part]=new List<Vector3>();foreach(var v in filter.sharedMesh.vertices)partVertices[part].Add(filter.transform.TransformPoint(v));}}bodyVertices[id]=new List<Vector3>();bodyNormals[id]=new List<Vector3>();foreach(var filter in go.GetComponentsInChildren<MeshFilter>()){if(filter.gameObject.name.EndsWith("/body")&&filter.sharedMesh!=null){foreach(var v in filter.sharedMesh.vertices)bodyVertices[id].Add(filter.transform.TransformPoint(v));foreach(var n in filter.sharedMesh.normals)bodyNormals[id].Add(filter.transform.TransformDirection(n).normalized);}}}
   var cam=new GameObject("Camera").AddComponent<Camera>();cam.transform.position=new Vector3(0,45,-35);cam.transform.LookAt(Vector3.zero);cam.orthographic=true;cam.orthographicSize=23;cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=new Color(.12f,.15f,.18f);
   var light=new GameObject("Light").AddComponent<Light>();light.type=LightType.Directional;light.transform.rotation=Quaternion.Euler(55,-25,0);
   var rt=new RenderTexture(1000,800,24);cam.targetTexture=rt;cam.Render();RenderTexture.active=rt;var tex=new Texture2D(1000,800,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1000,800),0,0);tex.Apply();File.WriteAllBytes(root+"/Temp/IntersectionUnityQA/preview.png",tex.EncodeToPNG());
      float seamError=0f;
   foreach(var p in paths) {
    int id=(int)pathType.GetField("path_id").GetValue(p);Vector3 cut=(Vector3)cuts[id];Vector3 direction=(points[id][1]-cut).normalized;
    Vector3 right=Vector3.Cross(Vector3.up,direction).normalized;Vector3 normal=Vector3.Cross(direction,right).normalized;
    float width=(float)itemType.GetField("width").GetValue(dict[100+id-1]);
    foreach(float sign in new[]{-1f,1f}) {Vector3 expected=cut+right*width*.5f*sign+Vector3.up*.1f;float nearest=float.PositiveInfinity;foreach(var v in meshResult.vertices)nearest=Mathf.Min(nearest,Vector3.Distance(v,expected));seamError=Mathf.Max(seamError,nearest);if(bodyVertices[id].Count==0)throw new Exception("missing road body");float bodyNearest=bodyVertices[id].Min(v=>Vector3.Distance(v,expected));seamError=Mathf.Max(seamError,bodyNearest);}
   }
   if(seamError>.001f)throw new Exception("surface seam error="+seamError);
   // 口では交差点の路面の法線が本線のbodyの上面の法線と一致し、継ぎ目で陰影が変わらない。
   float normalSeam=0f;var slabVertices=meshResult.vertices;var slabNormals=meshResult.normals;
   foreach(var p in paths){
    int id=(int)pathType.GetField("path_id").GetValue(p);Vector3 cut=(Vector3)cuts[id];Vector3 direction=(points[id][1]-cut).normalized;Vector3 right=Vector3.Cross(Vector3.up,direction).normalized;
    float width=(float)itemType.GetField("width").GetValue(dict[100+id-1]);
    foreach(float sign in new[]{-.5f,0f,.5f}){Vector3 at=cut+right*width*sign+Vector3.up*.1f;
     int slab=-1;for(int v=0;v<slabVertices.Length;v++)if(slabNormals[v].y>.5f&&(slab<0||Vector3.Distance(slabVertices[v],at)<Vector3.Distance(slabVertices[slab],at)))slab=v;
     int body=-1;for(int v=0;v<bodyVertices[id].Count;v++)if(bodyNormals[id][v].y>.5f&&(body<0||Vector3.Distance(bodyVertices[id][v],at)<Vector3.Distance(bodyVertices[id][body],at)))body=v;
     if(Vector3.Distance(slabVertices[slab],at)>.5f||Vector3.Distance(bodyVertices[id][body],at)>1.5f)continue;
     normalSeam=Mathf.Max(normalSeam,Vector3.Angle(slabNormals[slab],bodyNormals[id][body]));}
   }
   if(normalSeam>1f)throw new Exception("surface normal seam="+normalSeam);
   CheckPartSeams(surface, specs, cuts, paths, pathType, cache);
   CheckSurfaceOrigins(asm,cache);
   CheckFanCaps(specs);
   CheckRailCrossing(asm, pathType, itemType, dict, cache, gen);
   CheckLinearCorner(asm, pathType);
   CheckMixedWidthRoadCrossing(asm, pathType, itemType, dict, cache, gen);
   int motorwayCases = MotorwayMergeQA.Run(asm, dict, cache, root);
   File.WriteAllText(root+"/Temp/IntersectionUnityQA/result.txt","PASS vertices="+meshResult.vertexCount+" parts="+specs.Count+" sloped surface seam="+seamError+" normal seam="+normalSeam+"; 24m/3m road crossing; mixed crossing and 2-to-1 rail endpoints; motorway merges="+motorwayCases);
   EditorApplication.Exit(0);
  } catch(Exception ex){File.WriteAllText(root+"/Temp/IntersectionUnityQA/result.txt",ex.ToString());Debug.LogException(ex);EditorApplication.Exit(1);}
 }

 static void CheckMixedWidthRoadCrossing(Assembly asm, Type pathType, Type itemType, IDictionary masters, IDictionary cache, object generator)
 {
  var paths = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pathType));
  var preview = new GameObject("MixedWidthCrossing");
  var roadType = asm.GetType("RoadPath");
  var constants = asm.GetType("Constants");
  int C(string name) => (int)constants.GetField(name).GetValue(null);
  for (int i = 0; i < 4; i++)
  {
   int id = 800 + i, variant = 800 + i;
   bool wide = i % 2 == 0;
   var end = Quaternion.Euler(0, -90f * i, 0) * Vector3.right * 80f;
   var path = Activator.CreateInstance(pathType);
   void Set(string field, object value) => pathType.GetField(field).SetValue(path, value);
   Set("path_id", id); Set("path_variant", variant); Set("path_type", C("PATH_STREET"));
   Set("start_node_id", 1); Set("end_node_id", id + 100); Set("follow_terrain", C("VALUE_NONE"));
   Set("start_point", new List<object> { 0f, 0f, 0f }); Set("end_point", new List<object> { end.x, end.y, end.z });
   points[id] = new List<Vector3> { Vector3.zero, end };
   paths.Add(path);
   var master = Activator.CreateInstance(itemType);
   itemType.GetField("width").SetValue(master, wide ? 24f : 3f);
   itemType.GetField("lanes").SetValue(master, wide ? 4 : 1);
   itemType.GetField("sidewalk_width").SetValue(master, wide ? 3f : 0f);
   itemType.GetField("follow_terrain").SetValue(master, false);
   masters[variant] = master;
   foreach (string part in new[] { "body", "sidewalk", "line" })
    cache["Models/Paths/p" + variant + "/" + part] = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Paths/p2/" + part + ".fbx");
  }
  var provider = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(pathType, typeof(List<Vector3>)),
   typeof(IntersectionQA).GetMethod("Samples", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(pathType));
  var result = generator.GetType().GetMethod("GenerateWithSections").Invoke(generator, new object[] { 1, Vector3.zero, paths, 0f, provider, null });
  if (result == null) throw new Exception("24m/3m crossing mesh missing");
  var mesh = (Mesh)result.GetType().GetProperty("Mesh").GetValue(result);
  var asphalt = new Material(Shader.Find("Standard")) { color = new Color(.28f, .28f, .28f) };
  var pavement = new Material(Shader.Find("Standard")) { color = new Color(.65f, .65f, .65f) };
  var surface = new GameObject("MixedWidthSurface");
  surface.transform.SetParent(preview.transform, false);
  surface.AddComponent<MeshFilter>().sharedMesh = mesh;
  surface.AddComponent<MeshRenderer>().sharedMaterial = asphalt;
  var specs = (IList)result.GetType().GetProperty("SidewalkConnectors").GetValue(result);
  for (int i = 0; i < specs.Count; i++) roadType.GetMethod("CreateSidewalkConnectorObject").Invoke(null, new object[] { surface.transform, specs[i], i });
  var cuts = (IDictionary)result.GetType().GetProperty("ConnectionPoints").GetValue(result);
  foreach (var path in paths)
  {
   int id = (int)pathType.GetField("path_id").GetValue(path);
   var cut = (Vector3)cuts[id];
   var road = Activator.CreateInstance(roadType);
   var setback = Activator.CreateInstance(asm.GetType("SidewalkSetback"));
   var roadResult = roadType.GetMethod("Generate").Invoke(road, new object[] { path, new List<Vector3> { cut, points[id][1] }, setback, setback });
   if (roadResult == null) throw new Exception("Mixed-width road body missing");
   var body = (GameObject)roadResult.GetType().GetProperty("GeneratedObject").GetValue(roadResult);
   body.transform.SetParent(preview.transform, false);
   float halfWidth = id % 2 == 0 ? 12f : 1.5f;
   var direction = new Vector3(points[id][1].x, 0f, points[id][1].z).normalized;
   var right = Vector3.Cross(Vector3.up, direction);
   var bodyVertices = body.GetComponentsInChildren<MeshFilter>().Where(f => f.name.EndsWith("/body"))
    .SelectMany(f => f.sharedMesh.vertices.Select(v => f.transform.TransformPoint(v))).ToList();
   foreach (float side in new[] { -1f, 1f })
   {
    var expected = cut + right * halfWidth * side + Vector3.up * .1f;
    if (mesh.vertices.Min(v => Vector3.Distance(v, expected)) > .001f) throw new Exception("Mixed-width surface seam missing");
    if (bodyVertices.Count == 0 || bodyVertices.Min(v => Vector3.Distance(v, expected)) > .001f) throw new Exception("Mixed-width road seam mismatch");
   }
  }
  foreach (var renderer in preview.GetComponentsInChildren<MeshRenderer>())
   renderer.sharedMaterials = renderer.sharedMaterials.Select(m => renderer.name.Contains("sidewalk") ? pavement : asphalt).ToArray();
  preview.transform.position = new Vector3(3000f, 0f, 3000f);
  var camera = new GameObject("MixedWidthCamera").AddComponent<Camera>();
  camera.orthographic = true; camera.orthographicSize = 30f;
  camera.transform.position = preview.transform.position + Vector3.up * 100f;
  camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
  camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.32f, .42f, .22f);
  var target = new RenderTexture(1000, 800, 24); camera.targetTexture = target;
  camera.Render(); RenderTexture.active = target;
  var image = new Texture2D(1000, 800, TextureFormat.RGB24, false);
  image.ReadPixels(new Rect(0, 0, 1000, 800), 0, 0); image.Apply();
  File.WriteAllBytes(root + "/Temp/IntersectionUnityQA/mixed-width-crossing.png", image.EncodeToPNG());
  camera.targetTexture = null; RenderTexture.active = null; target.Release();
  UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(target);
  UnityEngine.Object.DestroyImmediate(camera.gameObject); UnityEngine.Object.DestroyImmediate(preview);
  UnityEngine.Object.DestroyImmediate(asphalt); UnityEngine.Object.DestroyImmediate(pavement);
 }

 static void CheckSurfaceOrigins(Assembly asm, IDictionary cache)
 {
  foreach(int variant in new[]{100,101})foreach(string part in new[]{"sidewalk","line"})
  {
   string path="Models/Paths/p"+variant+"/"+part;
   var source=((GameObject)cache[path]).GetComponent<MeshFilter>().sharedMesh;
   var mesh=(Mesh)asm.GetType("RoadPath").GetMethod("GetRoadPartMesh").Invoke(null,new object[]{source,path});
   if(mesh.bounds.min.y>.01f||mesh.bounds.min.y<-.03f)throw new Exception("Part base does not meet road surface: "+path+" minY="+mesh.bounds.min.y);
  }
 }

 static void CheckFanCaps(IList specs)
 {
  int checkedCaps=0;
  foreach(var spec in specs)
  {
   var type=spec.GetType();
   if((string)type.GetField("Part").GetValue(spec)!="sidewalk"||(int)type.GetField("EndPrefabVariant").GetValue(spec)>=0)continue;
   var line=(List<Vector3>)type.GetProperty("Points").GetValue(spec);
   var widths=(List<float>)type.GetProperty("Widths").GetValue(spec);
   var directions=(List<Vector2>)type.GetField("Directions").GetValue(spec);
   if(directions==null||line.Count<8||widths.Any(w=>w<=0))throw new Exception("Pointed sidewalk cap");
   Vector2? pivot=null;
   for(int i=0;i<line.Count;i++)
   {
    var center=new Vector2(line[i].x,line[i].z)-new Vector2(directions[i].y,-directions[i].x)*widths[i]*.5f;
    if(pivot.HasValue&&Vector2.Distance(pivot.Value,center)>.001f)throw new Exception("Fan is not centered at road outer edge");
    pivot=center;
   }
   if(Mathf.Abs(Vector2.Dot(directions[0],directions[directions.Count-1]))>.001f)throw new Exception("Curb does not turn outward by 90 degrees");
   checkedCaps++;
  }
  if(checkedCaps!=2)throw new Exception("Expected both sidewalk fan caps");
 }

 static void CheckLinearCorner(Assembly asm, Type pathType)
 {
  var json=Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert");
  var deserialize=json.GetMethod("DeserializeObject",new[]{typeof(string),typeof(Type)});
  var path=deserialize.Invoke(null,new object[]{"{\"path_id\":50,\"path_type\":1,\"path_variant\":102,\"curved\":false,\"start_point\":[0,0,0],\"end_point\":[50,10,50],\"waypoints\":[{\"position\":[50,5,0]}]}",pathType});
  var generator=Activator.CreateInstance(asm.GetType("RoadPath"));
  var setback=Activator.CreateInstance(asm.GetType("SidewalkSetback"));
  var result=generator.GetType().GetMethod("Generate").Invoke(generator,new object[]{path,new List<Vector3>{Vector3.zero,new Vector3(50,5,0),new Vector3(50,10,50)},setback,setback});
  var go=(GameObject)result.GetType().GetProperty("GeneratedObject").GetValue(result);
  var corner=go.transform.Find("Corner_0");
  if(corner==null||corner.GetComponent<MeshFilter>().sharedMesh==null)throw new Exception("Linear waypoint corner missing");
  if(!corner.GetComponentsInChildren<MeshFilter>().Any(f=>f.name.StartsWith("wall_")))throw new Exception("Corner wall missing without sidewalk");
 }

 static void CheckPartSeams(GameObject surface, IList specs, IDictionary cuts, IList paths, Type pathType, IDictionary cache)
 {
  float error=0;int checkedVertices=0;
  for(int i=0;i<specs.Count;i++)
  {
   var spec=specs[i];var type=spec.GetType();string part=(string)type.GetField("Part").GetValue(spec);
   if(!partVertices.ContainsKey(part))continue;
   var line=(List<Vector3>)type.GetProperty("Points").GetValue(spec);
   var generated=surface.transform.Find(part+"_"+i).GetComponent<MeshFilter>().sharedMesh.vertices;
   int variant=(int)type.GetProperty("PrefabVariant").GetValue(spec);
   var source=((GameObject)cache["Models/Paths/p"+variant+"/"+part]).GetComponent<MeshFilter>().sharedMesh;
   var original=source.vertices;
   foreach(bool first in new[]{true,false})
   {
    Vector3 center=line[first?0:line.Count-1]; bool mouth=false;
    foreach(var path in paths){int id=(int)pathType.GetField("path_id").GetValue(path);Vector3 cut=(Vector3)cuts[id];var direction=points[id][1].normalized;direction.y=0;if(Mathf.Abs(Vector3.Dot(center-cut,direction))<.001f)mouth=true;}
    if(!mouth)continue;
    for(int v=0;v<original.Length;v++)
    {
     if(Mathf.Abs(original[v].z-(first?source.bounds.min.z:source.bounds.max.z))>.0001f)continue;
     var vertex=generated[first?v:generated.Length-original.Length+v];
     checkedVertices++;error=Mathf.Max(error,partVertices[part].Min(p=>Vector3.Distance(p,vertex)));
    }
   }
  }
  Debug.Log("PART SEAMS checked="+checkedVertices+" error="+error);if(checkedVertices==0)throw new Exception("No part seams checked");
  if(error>.002f)throw new Exception("Part seam error="+error);
 }

 static void CheckRailCrossing(Assembly asm, Type pathType, Type itemType, IDictionary masters, IDictionary cache, object generator)
 {
  var paths=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pathType));
  for(int i=0;i<4;i++)
  {
   bool rail=i<2; int id=10+i, variant=200+i;
   Vector3 end=i==0?new Vector3(80,8,0):i==1?new Vector3(-80,-8,0):i==2?new Vector3(0,0,80):new Vector3(0,0,-80);
   var path=Activator.CreateInstance(pathType);
   foreach(var pair in new Dictionary<string,int>{{"path_id",id},{"start_node_id",1},{"end_node_id",100+id},{"path_variant",variant},{"path_type",rail?4:1}})pathType.GetField(pair.Key).SetValue(path,pair.Value);
   pathType.GetField("start_point").SetValue(path,new List<object>{0f,0f,0f});
   pathType.GetField("end_point").SetValue(path,new List<object>{end.x,end.y,end.z});
   paths.Add(path);points[id]=new List<Vector3>{Vector3.zero,end};
   var master=Activator.CreateInstance(itemType);
   itemType.GetField("width").SetValue(master,i==0?8f:6f);
   itemType.GetField("lanes").SetValue(master,i==0?2:1);
   itemType.GetField("follow_terrain").SetValue(master,false);
   masters.Add(variant,master);
   foreach(string part in new[]{"body","track"})
   {
    var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Paths/p"+(rail?4:1)+"/"+part+".fbx");
    if(prefab!=null)cache["Models/Paths/p"+variant+"/"+part]=prefab;
   }
  }
  var provider=Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(pathType,typeof(List<Vector3>)),typeof(IntersectionQA).GetMethod("Samples",BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(pathType));
  var result=generator.GetType().GetMethod("GenerateWithSections").Invoke(generator,new object[]{1,Vector3.zero,paths,0f,provider,null});
  if(result==null)throw new Exception("Mixed crossing mesh missing");
  var rails=(IList)result.GetType().GetProperty("RailConnectors").GetValue(result);
  if(rails==null||rails.Count!=2)throw new Exception("2-to-1 track mapping lost an endpoint");
  var cuts=(IDictionary)result.GetType().GetProperty("ConnectionPoints").GetValue(result);
  var starts=new HashSet<float>();
  foreach(var rail in rails)
  {
   var line=(List<Vector3>)rail.GetType().GetProperty("Points").GetValue(rail);
   starts.Add(Mathf.Round(line[0].z*1000));
   foreach(int index in new[]{0,line.Count-1})
   {
    int id=index==0?10:11;Vector3 cut=(Vector3)cuts[id];
    if(Mathf.Abs(line[index].x-cut.x)>.001f)throw new Exception("Rail mouth moved longitudinally");
   }
   var go=new GameObject("RailQA");
   asm.GetType("TrackPath").GetMethod("CreateRailConnectorObject").Invoke(null,new object[]{go.transform,rail,0});
   if(!go.GetComponentsInChildren<MeshFilter>().Any(f=>f.sharedMesh!=null&&f.sharedMesh.vertexCount>0))throw new Exception("Rail mesh missing");
  }
  if(starts.Count!=2)throw new Exception("Double track was collapsed to its centerline");
 }
}
