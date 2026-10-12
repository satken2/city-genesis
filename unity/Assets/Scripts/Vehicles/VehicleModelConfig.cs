using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 車種(Constants.VEHICLE_*)ごとの描画モデル・バリアント定義。
/// Resources/Config/vehicle_model_config.yaml から読み込む(任意)。ファイルが無ければ
/// 全車種フォールバック(箱 or 車種単一モデル)で動作する。
/// AgentModelConfig と同じ発想で「型ID → 複数バリアント」を持たせ、同一車種でも
/// 複数の fbx をランダムに描き分けられるよう拡張余地を確保する。
///
/// 例(YAML):
///   vehicles:
///     10:
///       variants: [0, 1, 2]   # Models/Vehicles/10/0, /1, /2 のいずれかを seed で選ぶ
///     11:
///       variants: [0]
/// </summary>
public static class VehicleModelConfig
{
    private const string ResourcePath = "Config/vehicle_model_config";
    private static readonly Dictionary<int, List<int>> variantsByType = new();
    private static bool isLoaded;
    private static Dictionary<int, TransitDefinition> transitTypes;
    private static Dictionary<int, Dictionary<int, VehicleAmbience.TrainSound>> transitSounds;

    private sealed class TransitDefinition
    {
        public int vehicle_type;
        public string wagon;
    }

    /// <summary>編成内の実体モデル。貨車の種類は機関車の外観から独立し、プールも実体モデルごとに共有する。</summary>
    public readonly struct TransitCarModel
    {
        public readonly string Part;
        public readonly int LiveryVariant;
        public readonly bool ReverseBody;
        public TransitCarModel(string part, int liveryVariant, bool reverseBody)
        { Part = part; LiveryVariant = liveryVariant; ReverseBody = reverseBody; }
    }

    private static TransitDefinition GetTransitDefinition(int pathVariant)
    {
        if (transitTypes == null)
        {
            var asset = Resources.Load<TextAsset>("Config/transit_vehicle_models");
            transitTypes = asset != null ? JsonConvert.DeserializeObject<Dictionary<int, TransitDefinition>>(asset.text) : new();
        }
        return transitTypes.TryGetValue(pathVariant, out var definition) ? definition : null;
    }

    public static int GetTransitVehicleType(int pathVariant) => GetTransitDefinition(pathVariant)?.vehicle_type ?? 0;

    /// <summary>
    /// 公共交通の外観(車種と候補番号)ごとの走行音。Resources/Config/transit_vehicle_sounds.json が正で、
    /// 未登録の外観は null(無音)。音は描画専用で、外観を変えても経済には影響しない。
    /// </summary>
    public static VehicleAmbience.TrainSound GetTransitSound(int vehicleType, int variant)
    {
        if (transitSounds == null)
        {
            var asset = Resources.Load<TextAsset>("Config/transit_vehicle_sounds");
            transitSounds = asset != null ? JsonConvert.DeserializeObject<Dictionary<int, Dictionary<int, VehicleAmbience.TrainSound>>>(asset.text) : new();
        }
        return transitSounds.TryGetValue(vehicleType, out var variants) && variants.TryGetValue(variant, out var sound) ? sound : null;
    }

    /// <summary>identity は折り返しても変わらない車両番号。貨物は0が機関車、それ以外が路線種別の貨車。</summary>
    public static TransitCarModel GetTransitCarModel(int pathVariant, int variant, int identity, int cars)
    {
        var definition = GetTransitDefinition(pathVariant);
        if (!string.IsNullOrEmpty(definition?.wagon))
            return identity == 0 ? new TransitCarModel(variant.ToString(), variant, false)
                : new TransitCarModel(definition.wagon, -1, false);
        bool middle = identity > 0 && identity < cars - 1;
        string part = middle ? (variant == 0 ? "middle" : $"middle_{variant}") : variant.ToString();
        return new TransitCarModel(part, variant, identity == cars - 1 && cars > 1 && definition?.vehicle_type != Constants.VEHICLE_BUS);
    }

    /// <summary>指定車種のバリアントID一覧を返す(未定義なら空リスト)。</summary>
    public static IReadOnlyList<int> GetVariants(int vehicleType)
    {
        EnsureLoaded();
        return variantsByType.TryGetValue(vehicleType, out var list) ? list : System.Array.Empty<int>();
    }

    private static void EnsureLoaded()
    {
        if (isLoaded) return;
        isLoaded = true;

        TextAsset asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null)
        {
            // 設定ファイルは任意。無い場合はフォールバック描画で動作する。
            return;
        }

        try
        {
            var parsed = Parse(asset.text);
            foreach (var kvp in parsed)
            {
                variantsByType[kvp.Key] = kvp.Value
                    .Where(v => v >= 0)
                    .Distinct()
                    .OrderBy(v => v)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"VehicleModelConfig: {ResourcePath} の解析に失敗しました ({ex.Message})");
        }
    }

    private static Dictionary<int, List<int>> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Dictionary<int, List<int>>();

        // JSON 形式(先頭が '{')なら Newtonsoft でパース。
        if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            var root = JsonConvert.DeserializeObject<Root>(text);
            var result = new Dictionary<int, List<int>>();
            if (root?.vehicles != null)
            {
                foreach (var kvp in root.vehicles)
                {
                    if (int.TryParse(kvp.Key, out int type) && kvp.Value?.variants != null)
                    {
                        result[type] = kvp.Value.variants;
                    }
                }
            }
            return result;
        }

        return ParseYaml(text);
    }

    // 最小限の YAML パーサ。vehicles: 配下の "  {type}:" と "    variants: [..]" のみ解釈する。
    private static Dictionary<int, List<int>> ParseYaml(string text)
    {
        var result = new Dictionary<int, List<int>>();
        int currentType = int.MinValue;

        foreach (var raw in text.Split('\n'))
        {
            // 数値キーと数値配列だけを扱う設定なので、行末の説明コメントを先に除く。
            var line = raw.Split('#')[0].Replace('\t', ' ').TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("vehicles:", StringComparison.Ordinal)) continue;

            int indent = line.Length - line.TrimStart(' ').Length;

            // "  {type}:" 形式(車種キー)
            if (indent <= 2 && trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                var key = trimmed[..^1].Trim();
                currentType = int.TryParse(key, out int t) ? t : int.MinValue;
                if (currentType != int.MinValue && !result.ContainsKey(currentType))
                {
                    result[currentType] = new List<int>();
                }
                continue;
            }

            if (currentType == int.MinValue) continue;

            // "    variants: [0, 1, 2]" 形式
            if (trimmed.StartsWith("variants:", StringComparison.Ordinal))
            {
                var value = trimmed["variants:".Length..].Trim();
                if (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal))
                {
                    foreach (var part in value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(part.Trim(), out int v)) result[currentType].Add(v);
                    }
                }
                continue;
            }

            // "- n" 形式(variants: のブロック要素)
            if (trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                if (int.TryParse(trimmed[1..].Trim(), out int v)) result[currentType].Add(v);
            }
        }

        return result;
    }

    [Serializable]
    private class Root
    {
        public Dictionary<string, Entry> vehicles;
    }

    [Serializable]
    private class Entry
    {
        public List<int> variants;
    }
}
