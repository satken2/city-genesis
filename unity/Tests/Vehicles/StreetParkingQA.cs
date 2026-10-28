using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static partial class VehicleQA
{
    static void StreetParking()
    {
        var owner = new GameObject("ParkingModelsQA").AddComponent(game.GetType("ModelManager"));
        game.GetType("ModelManager").GetProperty("Instance").SetValue(null, owner);
        var manager = new GameObject("ParkingQA").AddComponent(game.GetType("ParkedVehicleManager"));
        var layout = game.GetType("ParkedVehicleLayout");
        var catalog = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(game.GetType("ParkedVehicleLayout+Model")));
        var sources = new List<IDisposable>();
        var templates = new List<GameObject>();
        try
        {
            foreach (int type in new[] { 10, 13, 11 })
            {
                var variants = (IEnumerable)game.GetType("VehicleModelResolver").GetMethod("GetVariants").Invoke(null, new object[] { type });
                foreach (int variant in variants)
                {
                    var template = (GameObject)game.GetType("VehicleModelResolver").GetMethod("CreateInstance").Invoke(null, new object[] { type, variant, owner.transform });
                    templates.Add(template);
                    var render = New("PropRenderModel", template);
                    sources.Add((IDisposable)render);
                    var bounds = (Bounds)Get(render, "bounds");
                    Check(((IList)Get(render, "normal")).Count > 0, "Model extraction empty");
                    Check(template.transform.Find("VehicleBox_" + type) == null, "Parking uses fallback instead of existing FBX");
                    Near(bounds.center.x, 0, .01f, "Model not centered in width");
                    Near(bounds.center.z, 0, .01f, "Model not centered in length");
                    Near(bounds.min.y, 0, .01f, "Model not grounded");
                    Check(bounds.size.z > bounds.size.x && bounds.size.x > 1, "Model scale or forward axis invalid");
                    var model = New("ParkedVehicleLayout+Model");
                    Set(model, "type", type); Set(model, "variant", variant); Set(model, "size", bounds.size);
                    catalog.Add(model);
                }
            }
            Check(catalog.Count == 16, "Road parking model catalog must reuse all 16 existing car/van/truck variants");
            var count = layout.GetMethod("Count");
            Check((int)count.Invoke(null, new object[] { 0f, 20, 9 }) == 0, "Zero parking creates cars");
            Check((int)count.Invoke(null, new object[] { 7f, 20, 9 }) == 7, "Integer parking changed count");
            Check((int)count.Invoke(null, new object[] { 900f, 20, 9 }) == 20, "Overflow was not capped");
            int fractionSum = 0;
            var mix = new Dictionary<int, int> { {10,0}, {13,0}, {11,0} };
            for (int id = 0; id < 10000; id++)
            {
                fractionSum += (int)count.Invoke(null, new object[] { .25f, 20, id });
                int type = (int)layout.GetMethod("VehicleType").Invoke(null, new object[] { (uint)id });
                mix[type]++;
            }
            Near(fractionSum / 10000f, .25f, .015f, "Fractional roads accumulate a rounding bias");
            Near(mix[10] / 10000f, .6f, .02f, "Private car mix");
            Near(mix[13] / 10000f, .3f, .02f, "Commercial mix");
            Near(mix[11] / 10000f, .1f, .02f, "Truck mix");

            IList Build(object line, int direction = 0, int hand = 0, float width = 12, int lanes = 2) =>
                (IList)layout.GetMethod("Build").Invoke(null, new object[] { line, 31, width, 2f, 0f, lanes, direction, hand, catalog });
            var flat = Line(Vector3.zero, Vector3.forward * 150);
            var flatSlots = Build(flat);
            Check(flatSlots.Count > 25, "Full road has insufficient parking slots");
            var again = Build(flat);
            for (int i = 0; i < flatSlots.Count; i++)
            {
                Check((int)Get(flatSlots[i], "model") == (int)Get(again[i], "model"), "Model reshuffled");
                Near(Vector3.Distance((Vector3)Get(flatSlots[i], "position"), (Vector3)Get(again[i], "position")), 0, 0, "Position reshuffled");
            }
            foreach (int direction in new[] { 0, 1, 2 }) foreach (int hand in new[] { 0, 1 })
            {
                var slots = Build(flat, direction, hand);
                foreach (var p in slots)
                {
                    int side = (int)Get(p, "side");
                    var position = (Vector3)Get(p, "position");
                    var forward = (Quaternion)Get(p, "rotation") * Vector3.forward;
                    bool expected = direction == 1 || (direction == 0 && side == (hand == 0 ? -1 : 1));
                    Check((forward.z > 0) == expected, "Parking faces against traffic");
                    var size = (Vector3)Get(catalog[(int)Get(p, "model")], "size");
                    Check(Mathf.Abs(position.x) + size.x * .5f <= 4.001f, "Car overlaps sidewalk");
                    Near(4f - Mathf.Abs(position.x) - size.x * .5f, .02f, .001f, "Parking is not at the carriageway edge");
                    Check(position.z - size.z * .5f >= 4.999f && position.z + size.z * .5f <= 145.001f, "Car intrudes into junction setback");
                    Near(position.y, .1f, .001f, "Parking does not use body surface height");
                }
                var single = Build(flat, direction, hand, lanes: 1);
                Check(single.Count > 0, "Single-lane street has no parking");
                int expectedSide = (hand == 0 ? -1 : 1) * (direction == 2 ? -1 : 1);
                foreach (var p in single)
                    Check((int)Get(p, "side") == expectedSide, "Single-lane parking uses the opposite side");
            }
            Check(Build(Line(Vector3.zero, Vector3.forward * 9)).Count == 0, "Short path contains cars");
            Check(Build(flat, width: 5).Count == 0, "Narrow road spills into sidewalk");
            var curve = new Vector3[161];
            for (int i = 0; i < curve.Length; i++)
            {
                float angle = i / 160f * Mathf.PI * .8f;
                curve[i] = new Vector3(45 * Mathf.Cos(angle), i * .025f, 45 * Mathf.Sin(angle));
            }
            var curveLine = Line(curve);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var curveSlots = Build(curveLine);
            timer.Stop(); accessTimings.Add($"Curved road layout: {curveSlots.Count} slots in {timer.Elapsed.TotalMilliseconds:F2} ms");
            Check(curveSlots.Count > 10, "Curve lost all parking");
            for (int i = 0; i < curveSlots.Count; i++)
            {
                var a = curveSlots[i]; var aSize = (Vector3)Get(catalog[(int)Get(a,"model")],"size");
                Check(Mathf.Abs(((Quaternion)Get(a,"rotation") * Vector3.forward).y) > .001f, "Car ignores grade");
                for (int j = i + 1; j < curveSlots.Count; j++)
                {
                    var b = curveSlots[j]; var bSize = (Vector3)Get(catalog[(int)Get(b,"model")],"size");
                    Check(!(bool)layout.GetMethod("Overlaps").Invoke(null,new object[]{a,aSize,b,bSize}), "Cars overlap on inner curve");
                }
            }
            var data = New("GeneratedModels.PathSummary");
            Set(data,"path_type",2); Set(data,"status",2); Set(data,"parked_cars",10f); Set(data,"street_parking_capacity",20f);
            var constants = game.GetType("Constants");
            Set(data,"path_type",constants.GetField("PATH_STREET").GetRawConstantValue());
            Set(data,"status",constants.GetField("STATUS_ACTIVE").GetRawConstantValue());
            Check((bool)Call(manager,"Eligible",data),"Active street is excluded");
            Set(data,"street_parking_capacity",0f);
            Check(!(bool)Call(manager,"Eligible",data),"Zero-capacity road is included");
            Set(data,"street_parking_capacity",20f); Set(data,"parked_cars",0f);
            Check(!(bool)Call(manager,"Eligible",data),"Zero-demand road is included");
            Set(data,"parked_cars",10f); Set(data,"path_type",constants.GetField("PATH_MOTORWAY").GetRawConstantValue());
            Check(!(bool)Call(manager,"Eligible",data),"Motorway is included");
            Check(game.GetType("ConstructionPathSummary").GetField("parked_cars") != null, "Paged/individual path subtype loses parking data");
            StreetParkingRefresh(manager, catalog, sources);
            Call(manager,"Clear");
            Check(((IDictionary)Get(manager,"roads")).Count==0 && ((IList)Get(manager,"batches")).Count==0,"Session clear retains cars");
            RenderParkingPreview(flatSlots, templates);
            accessTimings.Add($"Mix over 10,000 seeds: private={mix[10]}, commercial={mix[13]}, truck={mix[11]}");
        }
        finally
        {
            foreach (var model in sources) model.Dispose();
            UnityEngine.Object.DestroyImmediate(manager.gameObject);
            UnityEngine.Object.DestroyImmediate(owner.gameObject);
        }
    }

    static void StreetParkingRefresh(Component manager, IList catalog, List<IDisposable> sources)
    {
        var master=new GameObject("ParkingMasterQA").AddComponent(game.GetType("MasterDataStore"));
        game.GetType("MasterDataStore").GetProperty("Instance").SetValue(null, master);
        var paths=new GameObject("ParkingPathsQA").AddComponent(game.GetType("PathManager"));
        var pathObject=new GameObject("Path_7301");
        try
        {
            var item=New("PathMasterItem");Set(item,"width",12f);Set(item,"sidewalk_width",(float?)2f);Set(item,"lanes",2);
            ((IDictionary)Get(master,"pathMasterById"))[7301]=item;
            var data=New("ConstructionPathSummary");Set(data,"path_id",7301);Set(data,"path_variant",7301);Set(data,"version",7);
            var constants=game.GetType("Constants");
            Set(data,"path_type",constants.GetField("PATH_STREET").GetRawConstantValue());
            Set(data,"status",constants.GetField("STATUS_ACTIVE").GetRawConstantValue());
            Set(data,"start_point",new List<object>{0f,0f,0f});Set(data,"end_point",new List<object>{0f,0f,150f});
            Set(data,"street_parking_capacity",50f);Set(data,"parked_cars",5f);
            ((IDictionary)Get(paths,"pathDataCache"))[7301]=data;
            ((IDictionary)Get(paths,"pathObjectsCache"))[7301]=pathObject;
            ((IDictionary)Get(paths,"pathIdsByCell"))[Vector2Int.zero]=new HashSet<int>{7301};
            Set(manager,"paths",paths);Set(manager,"modelsReady",true);
            var models=(IList)Get(manager,"models");var renderModels=(IList)Get(manager,"renderModels");
            foreach(var model in catalog)models.Add(model);
            foreach(var model in sources)renderModels.Add(model);
            void Refresh(Vector3 center)
            {
                var work=(IEnumerator)Call(manager,"Refresh",center);
                while(work.MoveNext()){}
            }
            Refresh(Vector3.zero);
            var roads=(IDictionary)Get(manager,"roads");
            Check(roads.Count==1,"Road cache did not receive eligible path");
            var road=roads[7301];var slots=Get(road,"slots");
            Check((int)Get(road,"count")==5 && ((IList)Get(manager,"batches")).Count>0,"Initial parking not drawn");
            var unchangedBatches=Get(manager,"batches");
            Refresh(Vector3.zero);
            Check(ReferenceEquals(unchangedBatches,Get(manager,"batches")),"Unchanged parking rebuilds GPU batches");
            Set(data,"parked_cars",12f);Refresh(Vector3.zero);
            Check((int)Get(roads[7301],"count")==12,"Same-version epoch parking update ignored");
            Check(ReferenceEquals(slots,Get(roads[7301],"slots")),"Count-only change rebuilt placement");
            Set(item,"lanes",1);Refresh(Vector3.zero);
            Check(!ReferenceEquals(slots,Get(roads[7301],"slots")),"Lane count change retained two-sided parking");
            foreach(var p in (IList)Get(roads[7301],"slots"))
                Check((int)Get(p,"side")==-1,"Single-lane refresh retains parking on both sides");
            Set(item,"lanes",2);Refresh(Vector3.zero);
            Set(data,"parked_cars",0f);Refresh(Vector3.zero);
            Check(roads.Count==0 && ((IList)Get(manager,"batches")).Count==0,"Zero parking retains old vehicles");
            Set(data,"parked_cars",8f);Refresh(Vector3.zero);
            Check(roads.Count==1,"Parking failed to reappear");
            ((IDictionary)Get(paths,"pathDataCache")).Clear();Refresh(Vector3.zero);
            Check(roads.Count==0 && ((IList)Get(manager,"batches")).Count==0,"Deleted road retains cars");
            ((IDictionary)Get(paths,"pathDataCache"))[7301]=data;Refresh(Vector3.zero);
            Call(manager,"Clear");
            Check(roads.Count==0 && ((IList)Get(manager,"batches")).Count==0,"Session clear retains populated batches");
            models.Clear();renderModels.Clear();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(pathObject);
            UnityEngine.Object.DestroyImmediate(paths.gameObject);
            UnityEngine.Object.DestroyImmediate(master.gameObject);
        }
    }

    static void RenderParkingPreview(IList slots, List<GameObject> templates)
    {
        var root = new GameObject("ParkingPreview");
        var materials = new List<Material>();
        var standard = Shader.Find("Standard");
        Material Material(Color color) { var m = new Material(standard) { color=color }; materials.Add(m); return m; }
        void Box(string name, Vector3 position, Vector3 size, Color color)
        {
            var box=GameObject.CreatePrimitive(PrimitiveType.Cube);box.name=name;box.transform.SetParent(root.transform);
            box.transform.position=position;box.transform.localScale=size;box.GetComponent<Renderer>().sharedMaterial=Material(color);
        }
        Box("road",new Vector3(0,-.15f,75),new Vector3(12,.5f,150),new Color(.16f,.17f,.18f));
        foreach(float x in new[]{-5f,5f})Box("sidewalk",new Vector3(x,.2f,75),new Vector3(2,.2f,150),new Color(.58f,.58f,.55f));
        for(int z=5;z<150;z+=8)Box("line",new Vector3(0,.105f,z),new Vector3(.12f,.01f,3),Color.white);
        foreach(var p in slots)
        {
            var go=UnityEngine.Object.Instantiate(templates[(int)Get(p,"model")],root.transform);
            go.transform.SetPositionAndRotation((Vector3)Get(p,"position"),(Quaternion)Get(p,"rotation"));go.SetActive(true);
            foreach(var renderer in go.GetComponentsInChildren<Renderer>())
            {
                var sourceMaterials=renderer.sharedMaterials;
                for(int i=0;i<sourceMaterials.Length;i++)
                {
                    var sourceMaterial=sourceMaterials[i];
                    sourceMaterials[i]=Material(sourceMaterial.HasProperty("_BaseColor") ? sourceMaterial.GetColor("_BaseColor") : sourceMaterial.color);
                }
                renderer.sharedMaterials=sourceMaterials;
            }
        }
        var light=new GameObject("Light").AddComponent<Light>();light.transform.SetParent(root.transform);light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(45,-30,0);
        var camera=new GameObject("Camera").AddComponent<Camera>();camera.transform.SetParent(root.transform);
        camera.transform.position=new Vector3(32,45,18);camera.transform.LookAt(new Vector3(0,0,61));camera.orthographic=true;camera.orthographicSize=36;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.16f,.23f,.19f);
        var target=new RenderTexture(1200,900,24);camera.targetTexture=target;camera.Render();
        var prior=RenderTexture.active;RenderTexture.active=target;
        var image=new Texture2D(1200,900,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1200,900),0,0);image.Apply();
        File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(),"parking-preview.png"),image.EncodeToPNG());
        RenderTexture.active=prior;camera.targetTexture=null;
        UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(root);
        foreach(var material in materials)UnityEngine.Object.DestroyImmediate(material);
    }
}
