using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Resolves vehicle type and variant into a rendering GameObject.
/// Resolve logical path "Models/Vehicles/{type}/{variant}" through <see cref="ModelManager"/>;
/// if absent, fall back to a single type model, generic vehicle, then a simple box.
/// ModelWeights provides appearance options and selection using per-type weights.
/// With a pack mounted, perform the entire search inside the pack first, using standard assets only if none is found.
/// </summary>
public static class VehicleModelResolver
{
    // Fallback box dimensions [m]: width, height, length, with length along +Z.
    private static readonly Vector3 BoxSize = new Vector3(1.8f, 1.5f, 4.5f);

    // Box colors by vehicle type for visual distinction before FBX assets are available.
    private static readonly Dictionary<int, Color> BoxColors = new()
    {
        { Constants.VEHICLE_STANDARD,   new Color(0.85f, 0.85f, 0.90f) }, // Passenger car: light gray.
        { Constants.VEHICLE_TRUCK,      new Color(0.90f, 0.55f, 0.20f) }, // Truck: orange.
        { Constants.VEHICLE_BUS,        new Color(0.20f, 0.55f, 0.90f) }, // Bus: blue.
        { Constants.VEHICLE_COMMERCIAL, new Color(0.30f, 0.75f, 0.40f) }, // Commercial vehicle: green.
    };

    private static Material sharedBoxMaterial;

    public static float GetVehicleLength(int type)
    {
        var prefab = ResolvePrefab(type,0);
        if(prefab == null)return 10;
        var bounds=ModelBounds(prefab.transform);
        return Mathf.Max(1,Mathf.Max(bounds.size.x,bounds.size.z));
    }

    /// <summary>
    /// Create one transit vehicle. part is an appearance index, a middle-series part, or a wagon series specified by line type.
    /// Do not recolor wagons whose liveryVariant is negative.
    /// Register stripe slots with <see cref="TransitLivery"/> on creation and apply the line color on every placement.
    /// </summary>
    public static GameObject CreateTransitCar(int type, string part, Transform parent, int liveryVariant = 0)
    {
        var car = CreateTransitModel(type, part, parent, liveryVariant);
        if (liveryVariant >= 0) TransitLivery.Register(car, type, liveryVariant);
        return car;
    }

    private static GameObject CreateTransitModel(int type, string part, Transform parent, int variantId)
    {
        if (int.TryParse(part, out int lead)) return CreateInstance(type,lead,parent);
        bool middle = part.StartsWith("middle", System.StringComparison.Ordinal);
        var prefab = ModelManager.Instance?.GetModel($"Models/Vehicles/{type}/{part}",suppressWarning:true);
        if (prefab == null && middle)
        {
            part = "middle";
            prefab = ModelManager.Instance?.GetModel($"Models/Vehicles/{type}/{part}",suppressWarning:true);
        }
        if (prefab == null && middle) return CreateInstance(type,variantId,parent);
        var root = new GameObject("TransitCar"); root.transform.SetParent(parent,false);
        if (prefab == null)
        {
            // Do not substitute locomotives for missing wagons; show missing models using existing fallback boxes.
            BuildFallbackBox(type, root.transform);
            root.SetActive(false); return root;
        }
        var model = Object.Instantiate(prefab,root.transform);
        model.transform.localPosition = Vector3.zero; model.transform.localRotation = Quaternion.identity;
        model.SetActive(true); NormalizeModel(root.transform,model.transform);
        AddModelLods(root, model, $"Models/Vehicles/{type}/{part}");
        root.SetActive(false); return root;
    }

    /// <summary>Select close, normal, or distant models by projected size, sharing the normal model's placement transform across all tiers.</summary>
    private static void AddModelLods(GameObject root, GameObject normal, string path)
    {
        var closePrefab = ModelManager.Instance?.GetModel(path + "_close", suppressWarning: true);
        var farPrefab = ModelManager.Instance?.GetModel(path + "_far", suppressWarning: true);
        if (closePrefab == null && farPrefab == null) return;
        var levels = new List<LOD>();
        Renderer[] InstantiateLod(GameObject prefab)
        {
            var model = Object.Instantiate(prefab, root.transform);
            model.transform.localPosition = normal.transform.localPosition;
            model.transform.localRotation = normal.transform.localRotation;
            model.transform.localScale = normal.transform.localScale;
            model.SetActive(true);
            return model.GetComponentsInChildren<Renderer>();
        }
        if (closePrefab != null) levels.Add(new LOD(.25f, InstantiateLod(closePrefab)));
        levels.Add(new LOD(farPrefab != null ? .035f : 0f, normal.GetComponentsInChildren<Renderer>()));
        if (farPrefab != null) levels.Add(new LOD(0f, InstantiateLod(farPrefab)));
        var lod = root.AddComponent<LODGroup>();
        lod.SetLODs(levels.ToArray());
        lod.RecalculateBounds();
    }

    /// <summary>
    /// Normalize authored placement and longitudinal axis within the wrapper, giving driving code +Z forward and a ground-contact center origin.
    /// Front/back is not inferred from shape; models must be exported facing +Z, Blender -Y.
    /// </summary>
    private static void NormalizeModel(Transform root,Transform model)
    {
        Bounds bounds = ModelBounds(root);
        if(bounds.size.x > bounds.size.z)model.localRotation=Quaternion.Euler(0,-90,0)*model.localRotation;
        bounds=ModelBounds(root);
        model.localPosition-=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
    }

    /// <summary>Bounds of all meshes in root-local coordinates, including hidden LODs and unaffected by root scale.</summary>
    public static Bounds ModelBounds(Transform root)
    {
        Bounds bounds=default;bool any=false;
        foreach(var filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if(filter.sharedMesh==null)continue;
            var b=filter.sharedMesh.bounds;
            Matrix4x4 matrix=root.worldToLocalMatrix*filter.transform.localToWorldMatrix;
            for(int i=0;i<8;i++)
            {
                Vector3 point=matrix.MultiplyPoint3x4(b.center+Vector3.Scale(b.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1)));
                if(!any){bounds=new Bounds(point,Vector3.zero);any=true;}else bounds.Encapsulate(point);
            }
        }
        return bounds;
    }

    /// <summary>
    /// Scale only longitudinal length to the nominal car length. Bounding-box front-to-back extent represents one car,
    /// so transit models must align body ends, or coupling faces for trains, with the bounding-box ends.
    /// </summary>
    public static void FitTransitLength(GameObject car,float nominalLength)
    {
        car.transform.localScale=Vector3.one;
        float length=ModelBounds(car.transform).size.z;
        if(length>.01f && nominalLength>0)car.transform.localScale=new Vector3(1,1,nominalLength/length);
    }

    /// <summary>Vehicle-type unit: ModelWeights Models/Vehicles/{type}.</summary>
    public static string Unit(int vehicleType) => ModelWeights.VEHICLE_ROOT + vehicleType;

    /// <summary>Appearance indices in ascending order, from the pack table if it owns the type, otherwise the standard table.</summary>
    public static IReadOnlyList<int> GetVariants(int vehicleType) => ModelWeights.Ids(Unit(vehicleType));

    /// <summary>Deterministically select an appearance index from seed and type weights; 0 if no options exist.</summary>
    public static int PickVariantId(int vehicleType, int seed)
        => Mathf.Max(0, ModelWeights.PickBySeed(Unit(vehicleType), unchecked((uint)seed)));

    /// <summary>
    /// Choose a train's appearance. Use the player's line selection if present among the vehicle type's options;
    /// otherwise, for unset or unavailable model indices, use <see cref="PickVariantId"/> for deterministic seed-based selection.
    /// </summary>
    public static int PickTransitVariantId(int vehicleType, int selected, int seed)
    {
        var variants = GetVariants(vehicleType);
        for (int i = 0; i < variants.Count; i++)
            if (variants[i] == selected) return selected;
        return PickVariantId(vehicleType, seed);
    }

    /// <summary>
    /// Create and return one vehicle rendering instance for the type/variant, initially inactive.
    /// The returned root is a wrapper pivoted at ground contact, containing the actual FBX/box as a child.
    /// VehicleManager can therefore set root position/rotation without distinguishing boxes from FBX models.
    /// The caller, VehicleManager, manages pooling.
    /// </summary>
    public static GameObject CreateInstance(int vehicleType, int variantId, Transform parent)
    {
        var root = new GameObject($"Vehicle_{vehicleType}_{variantId}");
        root.transform.SetParent(parent, false);

        GameObject prefab = ResolvePrefab(vehicleType, variantId);
        if (prefab != null)
        {
            GameObject model = Object.Instantiate(prefab, root.transform);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.SetActive(true); // Explicitly enable glTF templates because they load inactive.
            NormalizeModel(root.transform,model.transform);
            // Combine LODs of the same type only if its unsuffixed model actually resolves.
            string path = $"Models/Vehicles/{vehicleType}/{variantId}";
            if (ModelManager.Instance?.GetModel(path, suppressWarning: true) == prefab)
                AddModelLods(root, model, path);
        }
        else
        {
            BuildFallbackBox(vehicleType, root.transform);
        }

        root.SetActive(false);
        return root;
    }

    /// <summary>Resolve candidate paths in order and return the first prefab, or null if none. Search inside the pack first.</summary>
    private static GameObject ResolvePrefab(int vehicleType, int variantId)
    {
        if (ModelManager.Instance == null) return null;

        // Resolution order: type+variant -> single type model -> generic vehicle (standard passenger car).
        string[] candidates =
        {
            $"Models/Vehicles/{vehicleType}/{variantId}",
            $"Models/Vehicles/{vehicleType}",
            $"Models/Vehicles/{Constants.VEHICLE_STANDARD}",
        };

        foreach (bool fromPack in new[] { true, false })
        {
            if (fromPack && AssetPackStore.MountedPackId == null) continue;
            foreach (string path in candidates)
            {
                GameObject prefab = ModelManager.Instance.GetModelFrom(path, fromPack, suppressWarning: true);
                if (prefab != null) return prefab;
            }
        }
        return null;
    }

    /// <summary>Fallback without FBX assets: create a vehicle-sized ground-contact Cube, length along +Z.</summary>
    private static GameObject BuildFallbackBox(int vehicleType, Transform parent)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = $"VehicleBox_{vehicleType}";
        box.transform.SetParent(parent, false);
        box.transform.localScale = BoxSize;
        // Cube pivots are centered, so raise by half the height for ground contact.
        box.transform.localPosition = new Vector3(0f, BoxSize.y * 0.5f, 0f);

        // Remove the Collider because collision detection is unnecessary.
        Collider col = box.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);

        var renderer = box.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = GetBoxMaterial();
            var block = new MaterialPropertyBlock();
            Color color = BoxColors.TryGetValue(vehicleType, out var c) ? c : Color.white;
            // Set both color properties for compatibility with URP and Built-in rendering.
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }

        return box;
    }

    private static Material GetBoxMaterial()
    {
        if (sharedBoxMaterial != null) return sharedBoxMaterial;

        // Prefer URP Lit, falling back to Standard if absent.
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        sharedBoxMaterial = new Material(shader) { name = "VehicleBoxMaterial" };
        return sharedBoxMaterial;
    }
}
