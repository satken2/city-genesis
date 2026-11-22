using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Body colors of privately owned road vehicles. A material whose name starts with <see cref="MaterialPrefix"/> is body paint,
/// and each body takes one color of Resources/Config/vehicle_paint.json by weight, so one appearance runs in many colors.
/// Models without such a material (buses, asset-pack models that do not follow the name) keep their own colors.
/// </summary>
public static class VehiclePaint
{
    public const string MaterialPrefix = "SLOT_CAR_PAINT";
    private const string ResourcePath = "Config/vehicle_paint";
    private static readonly VehicleRepaint repaint = new();
    private static readonly List<Color32> colors = new();
    private static readonly List<int> weights = new();
    private static bool loaded;

    private sealed class Entry
    {
        public string color;
        public int weight;
    }

    /// <summary>Colors a body can take, in the order of the config. Empty if the config is missing.</summary>
    public static IReadOnlyList<Color32> Colors { get { EnsureLoaded(); return colors; } }

    public static bool IsPaint(Material material)
        => material != null && material.name.StartsWith(MaterialPrefix, StringComparison.Ordinal);

    /// <summary>Index into <see cref="Colors"/> chosen by weight from the seed; -1 if there are no colors.</summary>
    public static int PickIndex(uint seed)
    {
        EnsureLoaded();
        return colors.Count == 0 ? -1 : ModelWeights.IndexBySeed(weights, seed);
    }

    /// <summary>Paint a moving body with the color the seed selects. Pooled bodies are repainted on every reuse.</summary>
    public static void Apply(GameObject car, uint seed)
    {
        int index = PickIndex(seed);
        if (index < 0) return;
        if (!repaint.IsRegistered(car)) repaint.Register(car, name => name.StartsWith(MaterialPrefix, StringComparison.Ordinal));
        repaint.Apply(car, colors[index]);
    }

    /// <summary>Forget bodies and destroy cloned materials when the bodies are discarded.</summary>
    public static void Reset() => repaint.Reset();

    private static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        var asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null) return;
        try
        {
            foreach (var entry in JsonConvert.DeserializeObject<List<Entry>>(asset.text))
            {
                if (entry.weight <= 0 || !ColorUtility.TryParseHtmlString(entry.color, out var color)) continue;
                color.a = 1;
                colors.Add(color); weights.Add(entry.weight);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"VehiclePaint: failed to parse {ResourcePath} ({ex.Message})");
        }
    }
}
