using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Paint transit-vehicle stripes and similar parts with line_color.
/// Resources/Config/transit_vehicle_livery.json selects painted parts by vehicle type -> appearance index -> material name.
/// </summary>
public static class TransitLivery
{
    private const string ResourcePath = "Config/transit_vehicle_livery";
    private static Dictionary<int, Dictionary<int, HashSet<string>>> config;
    private static readonly VehicleRepaint repaint = new();

    /// <summary>Parse #rrggbb as a line color. Unset/invalid values return null, retaining original paint.</summary>
    public static Color32? Parse(string lineColor)
    {
        if (string.IsNullOrEmpty(lineColor) || !ColorUtility.TryParseHtmlString(lineColor, out var color)) return null;
        color.a = 1;
        return color;
    }

    /// <summary>Set of material names to recolor, or null for unsupported types/appearances. Callers must not modify it.</summary>
    public static HashSet<string> GetMaterialNames(int vehicleType, int variant)
    {
        EnsureLoaded();
        return config.TryGetValue(vehicleType, out var variants) && variants.TryGetValue(variant, out var names) ? names : null;
    }

    /// <summary>Record recolorable slots on a newly created body. Call once before replacing appearance materials.</summary>
    public static void Register(GameObject car, int vehicleType, int variant)
    {
        var names = GetMaterialNames(vehicleType, variant);
        repaint.Register(car, names == null ? null : names.Contains);
    }

    /// <summary>Paint a body with its line color, or restore original paint for null.</summary>
    public static void Apply(GameObject car, Color32? color) => repaint.Apply(car, color);

    /// <summary>Restore all original body paint and destroy cloned materials on city switches.</summary>
    public static void Reset() => repaint.Reset();

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
