using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 路線車両の帯などを路線カラー(`line_color`)で塗る。
/// 塗る部位は Resources/Config/transit_vehicle_livery.json の「車種 → 外観番号 → 材質名」で決め、
/// その名前の材質を使う部分メッシュを接写・通常・遠景の全LODで塗り替える。
/// 材質はFBXごとに埋め込まれているので、同じ名前の材質を使う乗用車や自由配置の車両には影響しない。
/// SRP Batcher の対象から外さないよう MaterialPropertyBlock は使わず、「元の材質×色」の複製を共有して差し替える。
/// </summary>
public static class TransitLivery
{
    private const string ResourcePath = "Config/transit_vehicle_livery";
    private static Dictionary<int, Dictionary<int, HashSet<string>>> config;
    private static readonly Dictionary<GameObject, RendererSlots[]> slotsByCar = new();
    private static readonly Dictionary<GameObject, Color32?> appliedByCar = new();
    private static readonly Dictionary<(Material, Color32), Material> tinted = new();
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private sealed class RendererSlots
    {
        public Renderer renderer;
        public int[] indices;
        public Material[] originals;
    }

    /// <summary>`#rrggbb` を路線色として読む。未設定・不正な値は null(元の塗装のまま)。</summary>
    public static Color32? Parse(string lineColor)
    {
        if (string.IsNullOrEmpty(lineColor) || !ColorUtility.TryParseHtmlString(lineColor, out var color)) return null;
        color.a = 1;
        return color;
    }

    /// <summary>塗装の対象になる材質名の集合(対象外の車種・外観は null)。呼び出し側で変更しないこと。</summary>
    public static HashSet<string> GetMaterialNames(int vehicleType, int variant)
    {
        EnsureLoaded();
        return config.TryGetValue(vehicleType, out var variants) && variants.TryGetValue(variant, out var names) ? names : null;
    }

    /// <summary>生成直後の車体から塗装の対象スロットを記録する。外観の材質が差し替わる前に1回だけ呼ぶ。</summary>
    public static void Register(GameObject car, int vehicleType, int variant)
    {
        var names = GetMaterialNames(vehicleType, variant);
        var list = new List<RendererSlots>();
        if (names != null)
        {
            foreach (var renderer in car.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                var indices = new List<int>();
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] != null && names.Contains(BaseName(materials[i].name))) indices.Add(i);
                if (indices.Count == 0) continue;
                var index = indices.ToArray();
                list.Add(new RendererSlots { renderer = renderer, indices = index, originals = Array.ConvertAll(index, i => materials[i]) });
            }
        }
        slotsByCar[car] = list.ToArray();
        appliedByCar.Remove(car);
    }

    /// <summary>車体を路線色で塗る(null なら元の塗装へ戻す)。プールで別の路線へ移った車体も、前回と色が違うときだけ塗り直す。</summary>
    public static void Apply(GameObject car, Color32? color)
    {
        if (!slotsByCar.TryGetValue(car, out var slots) || slots.Length == 0) return;
        if (appliedByCar.TryGetValue(car, out var current) && Same(current, color)) return;
        appliedByCar[car] = color;
        foreach (var slot in slots)
        {
            if (slot.renderer == null) continue;
            var materials = slot.renderer.sharedMaterials;
            for (int i = 0; i < slot.indices.Length; i++)
                materials[slot.indices[i]] = color.HasValue ? Tinted(slot.originals[i], color.Value) : slot.originals[i];
            slot.renderer.sharedMaterials = materials;
        }
    }

    /// <summary>都市の切り替えで全車体を元の塗装へ戻し、複製した材質を破棄する。</summary>
    public static void Reset()
    {
        foreach (var car in new List<GameObject>(slotsByCar.Keys))
        {
            if (car == null) { slotsByCar.Remove(car); continue; }
            Apply(car, null);
        }
        appliedByCar.Clear();
        foreach (var material in tinted.Values) if (material != null) UnityEngine.Object.Destroy(material);
        tinted.Clear();
    }

    private static bool Same(Color32? a, Color32? b)
        => a.HasValue == b.HasValue && (!a.HasValue || (a.Value.r == b.Value.r && a.Value.g == b.Value.g && a.Value.b == b.Value.b));

    private static Material Tinted(Material source, Color32 color)
    {
        if (tinted.TryGetValue((source, color), out var material) && material != null) return material;
        material = new Material(source) { name = source.name + "_line" };
        Color value = color;
        if (material.HasProperty(BaseColorId)) material.SetColor(BaseColorId, value);
        if (material.HasProperty(ColorId)) material.SetColor(ColorId, value);
        // 発光する帯は元の発光の明るさを保ったまま色相だけを路線色にする。
        if (material.IsKeywordEnabled("_EMISSION") && material.HasProperty(EmissionColorId))
            material.SetColor(EmissionColorId, value * Mathf.Max(1f, material.GetColor(EmissionColorId).maxColorComponent));
        tinted[(source, color)] = material;
        return material;
    }

    private static string BaseName(string name)
    {
        int suffix = name.IndexOf(" (", StringComparison.Ordinal);
        return suffix >= 0 ? name.Substring(0, suffix) : name;
    }

    private static void EnsureLoaded()
    {
        if (config != null) return;
        config = new();
        var asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null) return;
        try
        {
            var raw = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, List<string>>>>(asset.text);
            foreach (var type in raw)
            {
                if (!int.TryParse(type.Key, out int vehicleType)) continue;
                var variants = new Dictionary<int, HashSet<string>>();
                foreach (var variant in type.Value)
                    if (int.TryParse(variant.Key, out int id) && variant.Value != null) variants[id] = new HashSet<string>(variant.Value);
                config[vehicleType] = variants;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"TransitLivery: {ResourcePath} の解析に失敗しました ({ex.Message})");
        }
    }
}
