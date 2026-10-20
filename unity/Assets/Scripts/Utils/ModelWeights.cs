using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Appearance-model lists and weights for buildings and vehicles. Each unit (building-prefix folder Models/Buildings/{prefix}, or vehicle type Models/Vehicles/{type})
/// stores model indices (numbered folders for buildings, numbered model names for vehicles) and weights. Resources cannot enumerate contents at runtime,
/// so the bundled table also serves as the unit's model inventory.
///
/// Default weights come from bundled tables: Resources/Config/model_weights.json for standard assets, or model_weights.json beside the pack bundle.
/// Player weights edited in Asset Manager are in asset_weights.json under the user directory, grouped by source (standard or pack ID);
/// use player values when present. A unit's source is the mounted pack if it owns the unit, otherwise standard assets.
///
/// Selection follows one rule: accumulate weights in index order and choose the interval containing r (0 ≤ r &lt; total). Buildings derive r from visual_variant,
/// vehicles from seed, giving the same model for the same input. If total weight is 0, use the lowest-index model.
/// </summary>
public static class ModelWeights
{
    public const string STANDARD_RESOURCE = "Config/model_weights"; // Standard asset table (Resources).
    public const string FILE_NAME = "model_weights.json"; // Table filename: beside the bundle for packs, under Resources/Config for standard assets.
    public const string USER_FILE = "asset_weights.json"; // Player-adjusted weights in the user directory.
    public const int NEW_MODEL_WEIGHT = 100; // Weight assigned to newly discovered models when rebuilding the table.
    public const string BUILDING_ROOT = "Models/Buildings/";
    public const string VEHICLE_ROOT = "Models/Vehicles/";

    public readonly struct Model
    {
        public readonly int Id;
        public readonly int Weight;
        public Model(int id, int weight) { Id = id; Weight = weight; }
    }

    private static Dictionary<string, Model[]> standard;
    private static Dictionary<string, Model[]> pack = new Dictionary<string, Model[]>();
    private static Dictionary<string, Dictionary<string, Dictionary<int, int>>> user = new Dictionary<string, Dictionary<string, Dictionary<int, int>>>();
    private static readonly Dictionary<string, Model[]> effective = new Dictionary<string, Model[]>();
    private static readonly Dictionary<string, int[]> ids = new Dictionary<string, int[]>();

    private static Dictionary<string, Model[]> Standard
    {
        get
        {
            if (standard != null) return standard;
            TextAsset asset = Resources.Load<TextAsset>(STANDARD_RESOURCE);
            if (asset == null) Debug.LogWarning($"ModelWeights: Resources/{STANDARD_RESOURCE}.json がありません。標準の建物・車両のモデルを選べません");
            standard = asset != null ? Parse(asset.text) : new Dictionary<string, Model[]>();
            return standard;
        }
    }

    /// <summary>Unit source: mounted pack ID if the pack owns it, otherwise standard.</summary>
    public static string SourceOf(string unit)
        => AssetPackStore.HasUnit(unit) ? AssetPackStore.MountedPackId : Constants.ASSET_PACK_STANDARD;

    /// <summary>Models and default weights in the source's bundled table, in ascending index order.</summary>
    public static Model[] Bundled(string unit)
    {
        var table = AssetPackStore.HasUnit(unit) ? pack : Standard;
        return table.TryGetValue(unit, out var models) ? models : Array.Empty<Model>();
    }

    /// <summary>Unit's model indices in ascending order; empty if no models exist.</summary>
    public static IReadOnlyList<int> Ids(string unit)
    {
        if (ids.TryGetValue(unit, out var cached)) return cached;
        Model[] models = Bundled(unit);
        var result = new int[models.Length];
        for (int i = 0; i < models.Length; i++) result[i] = models[i].Id;
        ids[unit] = result;
        return result;
    }

    /// <summary>Unit's models and active weights: player values when present, otherwise defaults.</summary>
    public static IReadOnlyList<Model> Models(string unit)
    {
        if (effective.TryGetValue(unit, out var cached)) return cached;
        Model[] result = Bundled(unit);
        if (user.TryGetValue(SourceOf(unit), out var units) && units.TryGetValue(unit, out var overrides))
        {
            var merged = new Model[result.Length];
            for (int i = 0; i < result.Length; i++)
                merged[i] = new Model(result[i].Id, overrides.TryGetValue(result[i].Id, out int weight) ? weight : result[i].Weight);
            result = merged;
        }
        effective[unit] = result;
        return result;
    }

    /// <summary>Select from a 0-to-1 value (visual_variant), with r = floor(value x total). -1 if no models exist.</summary>
    public static int PickByFraction(string unit, float fraction)
    {
        IReadOnlyList<Model> models = Models(unit);
        if (models.Count == 0) return -1;
        long total = Total(models);
        if (total <= 0) return models[0].Id;
        long r = Math.Min(Mathf.FloorToInt(Mathf.Clamp01(fraction) * total), total - 1);
        return models[IndexAt(models, r)].Id;
    }

    /// <summary>Select from an integer seed, with r = seed mod total. -1 if no models exist.</summary>
    public static int PickBySeed(string unit, uint seed)
    {
        IReadOnlyList<Model> models = Models(unit);
        if (models.Count == 0) return -1;
        long total = Total(models);
        if (total <= 0) return models[0].Id;
        return models[IndexAt(models, seed % total)].Id;
    }

    /// <summary>Index of the cumulative-weight interval containing r = seed mod total, for filtered candidates. 0 if total is 0.</summary>
    public static int IndexBySeed(IReadOnlyList<int> weights, uint seed)
    {
        long total = 0;
        foreach (int weight in weights) total += Math.Max(0, weight);
        if (total <= 0) return 0;
        long r = seed % total;
        long sum = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            sum += Math.Max(0, weights[i]);
            if (r < sum) return i;
        }
        return weights.Count - 1;
    }

    private static long Total(IReadOnlyList<Model> models)
    {
        long total = 0;
        foreach (Model model in models) total += Math.Max(0, model.Weight);
        return total;
    }

    private static int IndexAt(IReadOnlyList<Model> models, long r)
    {
        long sum = 0;
        for (int i = 0; i < models.Count; i++)
        {
            sum += Math.Max(0, models[i].Weight);
            if (r < sum) return i;
        }
        return models.Count - 1;
    }

    /// <summary>
    /// Temporarily place all unit weight on model id for Asset Manager thumbnail rendering.
    /// Do not change the inventory; parts missing from that folder fall back to lower indices as in rendering. Dispose restores weights.
    /// </summary>
    public static IDisposable Isolate(string unit, int id)
    {
        Model[] models = Bundled(unit);
        var isolated = new Model[models.Length];
        for (int i = 0; i < models.Length; i++) isolated[i] = new Model(models[i].Id, models[i].Id == id ? 1 : 0);
        effective[unit] = isolated;
        return new Isolation(unit);
    }

    private sealed class Isolation : IDisposable
    {
        private readonly string unit;
        public Isolation(string unit) { this.unit = unit; }
        public void Dispose() => effective.Remove(unit);
    }

    /// <summary>Mounted pack's table, the contents of model_weights.json. Pass null when unmounting.</summary>
    public static void SetPack(string json)
    {
        pack = string.IsNullOrEmpty(json) ? new Dictionary<string, Model[]>() : Parse(json);
        effective.Clear();
        ids.Clear();
    }

    /// <summary>Reload asset_weights.json from the user directory each time a city opens. Missing/unreadable files mean no player weights.</summary>
    public static void ReloadUser()
    {
        user = new Dictionary<string, Dictionary<string, Dictionary<int, int>>>();
        effective.Clear();
        string root = UserDataDir.Root;
        if (root == null) return;
        string path = Path.Combine(root, USER_FILE);
        if (!File.Exists(path)) return;
        try
        {
            var parsed = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, Dictionary<string, int>>>>(File.ReadAllText(path));
            foreach (var source in parsed ?? new Dictionary<string, Dictionary<string, Dictionary<string, int>>>())
            {
                var units = new Dictionary<string, Dictionary<int, int>>();
                foreach (var unit in source.Value ?? new Dictionary<string, Dictionary<string, int>>())
                    units[unit.Key] = WeightsById(ToModels(unit.Value));
                user[source.Key] = units;
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
        {
            Debug.LogWarning($"ModelWeights: {path} を読めません。プレイヤーの重みを使わずに描きます: {e.Message}");
        }
    }

    /// <summary>
    /// If a logical name identifies a model, return its unit and index: Models/Buildings/{prefix}/{index}/... or Models/Vehicles/{type}/{index}.
    /// </summary>
    public static bool TryParseModel(string logicalName, out string unit, out int id)
    {
        unit = null;
        id = 0;
        string root = logicalName.StartsWith(BUILDING_ROOT, StringComparison.Ordinal) ? BUILDING_ROOT
            : logicalName.StartsWith(VEHICLE_ROOT, StringComparison.Ordinal) ? VEHICLE_ROOT : null;
        if (root == null) return false;
        int folderEnd = logicalName.IndexOf('/', root.Length);
        if (folderEnd < 0) return false;
        int numberEnd = logicalName.IndexOf('/', folderEnd + 1);
        string number = numberEnd < 0 ? logicalName.Substring(folderEnd + 1) : logicalName.Substring(folderEnd + 1, numberEnd - folderEnd - 1);
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out id)) return false;
        unit = logicalName.Substring(0, folderEnd);
        return true;
    }

    /// <summary>
    /// Rebuild the table from actual model logical names. Preserve existing weights, assign NEW_MODEL_WEIGHT to new models, and remove missing ones.
    /// Used for both standard tables through the Editor menu and pack tables during pack builds.
    /// </summary>
    public static Dictionary<string, Model[]> Rebuild(Dictionary<string, Model[]> existing, IEnumerable<string> logicalNames)
    {
        var found = new SortedDictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (string name in logicalNames)
        {
            if (!TryParseModel(name, out string unit, out int id)) continue;
            if (!found.TryGetValue(unit, out var set)) found[unit] = set = new SortedSet<int>();
            set.Add(id);
        }
        var result = new Dictionary<string, Model[]>();
        foreach (var pair in found)
        {
            var previous = new Dictionary<int, int>();
            if (existing != null && existing.TryGetValue(pair.Key, out var models))
                foreach (Model model in models) previous[model.Id] = model.Weight;
            var rebuilt = new List<Model>();
            foreach (int id in pair.Value) rebuilt.Add(new Model(id, previous.TryGetValue(id, out int weight) ? weight : NEW_MODEL_WEIGHT));
            result[pair.Key] = rebuilt.ToArray();
        }
        return result;
    }

    /// <summary>Read a table shaped as {unit: {index: weight}}.</summary>
    public static Dictionary<string, Model[]> Parse(string json)
    {
        var result = new Dictionary<string, Model[]>();
        var parsed = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, int>>>(json);
        foreach (var unit in parsed ?? new Dictionary<string, Dictionary<string, int>>())
            result[unit.Key] = ToModels(unit.Value);
        return result;
    }

    /// <summary>Write a table with one line per unit, sorting units and indices ascending.</summary>
    public static string Serialize(Dictionary<string, Model[]> table)
    {
        var units = new List<string>(table.Keys);
        units.Sort(StringComparer.Ordinal);
        var text = new StringBuilder("{\n");
        for (int i = 0; i < units.Count; i++)
        {
            var entries = new List<string>();
            foreach (Model model in table[units[i]]) entries.Add($"\"{model.Id}\": {model.Weight}");
            text.Append("  ").Append(JsonConvert.ToString(units[i])).Append(": { ").Append(string.Join(", ", entries)).Append(" }");
            text.Append(i < units.Count - 1 ? ",\n" : "\n");
        }
        return text.Append("}\n").ToString();
    }

    private static Model[] ToModels(Dictionary<string, int> weights)
    {
        var models = new List<Model>();
        foreach (var pair in weights ?? new Dictionary<string, int>())
        {
            if (int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                models.Add(new Model(id, Math.Max(0, pair.Value)));
        }
        models.Sort((a, b) => a.Id.CompareTo(b.Id));
        return models.ToArray();
    }

    private static Dictionary<int, int> WeightsById(Model[] models)
    {
        var result = new Dictionary<int, int>();
        foreach (Model model in models) result[model.Id] = model.Weight;
        return result;
    }
}
