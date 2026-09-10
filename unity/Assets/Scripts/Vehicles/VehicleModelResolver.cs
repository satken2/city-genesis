using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 車種(+バリアント)から描画用 GameObject を生成する解決層。
/// 論理パス "Models/Vehicles/{type}/{variant}" を <see cref="ModelManager"/> 経由で解決し、
/// 見つからなければ車種単一モデル → 汎用車 → 単純な箱、の順にフォールバックする。
/// これにより「初期=箱 / 将来=車種別 fbx / さらに車種内ランダム複数 fbx」を同一 API で拡張できる。
/// 物理配置は genesis-docs「追加コンテンツとアセットパック」の ModelManager 解決層と整合する。
/// </summary>
public static class VehicleModelResolver
{
    // フォールバック箱の寸法[m]（幅・高さ・長さ）。長さは +Z 方向。
    private static readonly Vector3 BoxSize = new Vector3(1.8f, 1.5f, 4.5f);

    // 車種ごとの箱の色（車種を目視で区別するため。fbx 導入までの暫定）。
    private static readonly Dictionary<int, Color> BoxColors = new()
    {
        { Constants.VEHICLE_STANDARD,   new Color(0.85f, 0.85f, 0.90f) }, // 乗用車: 明るいグレー
        { Constants.VEHICLE_TRUCK,      new Color(0.90f, 0.55f, 0.20f) }, // トラック: オレンジ
        { Constants.VEHICLE_BUS,        new Color(0.20f, 0.55f, 0.90f) }, // バス: 青
        { Constants.VEHICLE_COMMERCIAL, new Color(0.30f, 0.75f, 0.40f) }, // 商用車: 緑
    };

    private static Material sharedBoxMaterial;

    public static float GetVehicleLength(int type)
    {
        var prefab = ResolvePrefab(type,0);
        if(prefab == null)return 10;
        var bounds=ModelBounds(prefab.transform);
        return Mathf.Max(1,Mathf.Max(bounds.size.x,bounds.size.z));
    }

    public static GameObject CreateTransitCar(int type, bool middle, Transform parent, int variantId = 0)
    {
        if (!middle) return CreateInstance(type,variantId,parent);
        string part = variantId == 0 ? "middle" : $"middle_{variantId}";
        var prefab = ModelManager.Instance?.GetModel($"Models/Vehicles/{type}/{part}",suppressWarning:true);
        if (prefab == null) prefab = ModelManager.Instance?.GetModel($"Models/Vehicles/{type}/middle",suppressWarning:true);
        if (prefab == null) return CreateInstance(type,variantId,parent);
        var root = new GameObject("TransitCar"); root.transform.SetParent(parent,false);
        var model = Object.Instantiate(prefab,root.transform);
        model.transform.localPosition = Vector3.zero; model.transform.localRotation = Quaternion.identity;
        model.SetActive(true); NormalizeModel(root.transform,model.transform); root.SetActive(false); return root;
    }

    /// <summary>著作時の配置と長手軸をラッパー内で正規化し、走行側は+Z前方・接地中心原点に統一する。</summary>
    private static void NormalizeModel(Transform root,Transform model)
    {
        Bounds bounds = ModelBounds(root);
        if(bounds.size.x > bounds.size.z)model.localRotation=Quaternion.Euler(0,-90,0)*model.localRotation;
        bounds=ModelBounds(root);
        model.localPosition-=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
    }

    private static Bounds ModelBounds(Transform root)
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

    public static void FitTransitLength(GameObject car,float nominalLength)
    {
        car.transform.localScale=Vector3.one;
        float length=ModelBounds(car.transform).size.z;
        if(length>.01f && nominalLength>0)car.transform.localScale=new Vector3(1,1,nominalLength/length);
    }

    /// <summary>seed で決定的にバリアントIDを選ぶ。設定が無ければ 0。</summary>
    public static int PickVariantId(int vehicleType, int seed)
    {
        var variants = VehicleModelConfig.GetVariants(vehicleType);
        if (variants.Count == 0) return 0;
        int idx = (int)((uint)seed % (uint)variants.Count);
        return variants[idx];
    }

    /// <summary>
    /// 車種+バリアントの描画実体を1つ生成して返す。生成直後は非アクティブ。
    /// 返すのは「接地位置がピボット」のラッパー root で、実際のモデル(fbx/箱)はその子に入る。
    /// これにより VehicleManager は box/fbx を区別せず root の position/rotation を設定できる。
    /// プール管理は呼び出し側(VehicleManager)が行う。
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
            model.SetActive(true); // glTF テンプレートは非アクティブで読まれるため明示的に有効化する。
            NormalizeModel(root.transform,model.transform);
        }
        else
        {
            BuildFallbackBox(vehicleType, root.transform);
        }

        root.SetActive(false);
        return root;
    }

    /// <summary>候補パスを順に ModelManager で解決し、最初に見つかった prefab を返す(無ければ null)。</summary>
    private static GameObject ResolvePrefab(int vehicleType, int variantId)
    {
        if (ModelManager.Instance == null) return null;

        // 解決順: 車種+バリアント → 車種単一 → 汎用車(標準乗用車)
        string[] candidates =
        {
            $"Models/Vehicles/{vehicleType}/{variantId}",
            $"Models/Vehicles/{vehicleType}",
            $"Models/Vehicles/{Constants.VEHICLE_STANDARD}",
        };

        foreach (string path in candidates)
        {
            GameObject prefab = ModelManager.Instance.GetModel(path, suppressWarning: true);
            if (prefab != null) return prefab;
        }
        return null;
    }

    /// <summary>fbx 未導入時のフォールバック。車寸の箱(Cube)を接地させて生成する。長さは +Z 方向。</summary>
    private static GameObject BuildFallbackBox(int vehicleType, Transform parent)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = $"VehicleBox_{vehicleType}";
        box.transform.SetParent(parent, false);
        box.transform.localScale = BoxSize;
        // Cube のピボットは中心のため、接地させるよう高さの半分だけ持ち上げる。
        box.transform.localPosition = new Vector3(0f, BoxSize.y * 0.5f, 0f);

        // 当たり判定は不要のため Collider を除去する。
        Collider col = box.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);

        var renderer = box.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = GetBoxMaterial();
            var block = new MaterialPropertyBlock();
            Color color = BoxColors.TryGetValue(vehicleType, out var c) ? c : Color.white;
            // URP/Built-in 双方で効くよう両方のカラープロパティを設定する。
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }

        return box;
    }

    private static Material GetBoxMaterial()
    {
        if (sharedBoxMaterial != null) return sharedBoxMaterial;

        // URP の Lit を優先し、無ければ Standard にフォールバックする。
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        sharedBoxMaterial = new Material(shader) { name = "VehicleBoxMaterial" };
        return sharedBoxMaterial;
    }
}
