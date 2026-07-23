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

    /// <summary>seed で決定的にバリアントIDを選ぶ。設定が無ければ 0。</summary>
    public static int PickVariantId(int vehicleType, int seed)
    {
        var variants = VehicleModelConfig.GetVariants(vehicleType);
        if (variants.Count == 0) return 0;
        int idx = Mathf.Abs(seed) % variants.Count;
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
