using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

public static class AgentModelConfig
{
    private const string ResourcePath = "Config/agent_model_config";
    private static readonly Dictionary<int, AgentModelDefinition> agentDefinitions = new();
    // 観光名所つきプレハブフォルダ(landmark-{model_prefix})の一覧。
    // 生成器(generate_agent_model_config.py)が実在フォルダから列挙した名前だけが入る。
    private static readonly HashSet<string> landmarkPrefixes = new(StringComparer.Ordinal);
    private static bool isLoaded;

    public static bool TryGetAgentDefinition(int agentId, out AgentModelDefinition definition)
    {
        EnsureLoaded();
        return agentDefinitions.TryGetValue(agentId, out definition);
    }

    /// <summary>
    /// 観光名所のプレハブフォルダ(例 "landmark-great_tower")が存在するか。
    /// BuildingMeshUtils.DeterminePrefix が名所専用パーツの有無判定に使う。
    /// </summary>
    public static bool HasLandmarkPrefix(string prefix)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(prefix) && landmarkPrefixes.Contains(prefix);
    }

    private static void EnsureLoaded()
    {
        if (isLoaded)
        {
            return;
        }

        isLoaded = true;
        TextAsset asset = Resources.Load<TextAsset>(ResourcePath);
        if (asset == null)
        {
            Debug.LogWarning($"AgentModelConfig: Resources/{ResourcePath}.yaml が見つかりません。カスタムモデルを使用する場合はファイルを追加してください。");
            return;
        }

        try
        {
            var root = ParseAgentModelConfig(asset.text);
            if (root?.landmark_prefixes != null)
            {
                foreach (var prefix in root.landmark_prefixes)
                {
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        landmarkPrefixes.Add(prefix.Trim());
                    }
                }
            }
            if (root?.agents == null)
            {
                return;
            }

            foreach (var kvp in root.agents)
            {
                if (!int.TryParse(kvp.Key, out int agentId))
                {
                    Debug.LogWarning($"AgentModelConfig: agent_id '{kvp.Key}' を数値に変換できませんでした。");
                    continue;
                }

                AgentModelDefinition definition = kvp.Value ?? new AgentModelDefinition();
                definition.NormalizeVariants();
                agentDefinitions[agentId] = definition;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"AgentModelConfig: {ResourcePath}.yaml の解析に失敗しました ({ex.Message})");
        }
    }

    private static AgentModelConfigRoot ParseAgentModelConfig(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return JsonConvert.DeserializeObject<AgentModelConfigRoot>(text);
        }

        return ParseAgentModelConfigYaml(text);
    }

    private static AgentModelConfigRoot ParseAgentModelConfigYaml(string text)
    {
        var root = new AgentModelConfigRoot
        {
            agents = new Dictionary<string, AgentModelDefinition>(),
            landmark_prefixes = new List<string>()
        };

        AgentModelDefinition currentDefinition = null;
        bool readingVariants = false;
        // トップレベルの landmark_prefixes: セクションを読んでいる間 true
        bool readingLandmarkPrefixes = false;

        foreach (var rawLine in text.Split(new[] { '\n' }, StringSplitOptions.None))
        {
            var sanitizedLine = rawLine.Replace('\t', ' ').TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(sanitizedLine))
            {
                continue;
            }

            var trimmedLine = sanitizedLine.Trim();
            if (trimmedLine.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int indent = sanitizedLine.Length - sanitizedLine.TrimStart(' ').Length;

            if (indent == 0)
            {
                // トップレベルキーでセクションを切り替える(agents: / landmark_prefixes:)
                readingLandmarkPrefixes = trimmedLine.StartsWith("landmark_prefixes:", StringComparison.Ordinal);
                currentDefinition = null;
                continue;
            }

            if (readingLandmarkPrefixes)
            {
                if (trimmedLine.StartsWith("-", StringComparison.Ordinal))
                {
                    var prefix = TrimQuotes(trimmedLine[1..].Trim());
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        root.landmark_prefixes.Add(prefix);
                    }
                }
                continue;
            }

            if (indent == 2 && trimmedLine.EndsWith(":", StringComparison.Ordinal))
            {
                var keyCandidate = TrimQuotes(trimmedLine[..^1].Trim());
                currentDefinition = new AgentModelDefinition();
                root.agents[keyCandidate] = currentDefinition;
                readingVariants = false;
                continue;
            }

            if (currentDefinition == null)
            {
                continue;
            }

            if (indent >= 4)
            {
                if (trimmedLine.StartsWith("variants:", StringComparison.Ordinal))
                {
                    var inlineValue = trimmedLine["variants:".Length..].Trim();
                    currentDefinition.variants = new List<int>();

                    if (inlineValue.StartsWith("[", StringComparison.Ordinal) && inlineValue.EndsWith("]", StringComparison.Ordinal))
                    {
                        var listContent = inlineValue[1..^1];
                        foreach (var part in listContent.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (int.TryParse(part.Trim(), out int variant))
                            {
                                currentDefinition.variants.Add(variant);
                            }
                        }

                        readingVariants = false;
                    }
                    else if (string.IsNullOrEmpty(inlineValue))
                    {
                        readingVariants = true;
                    }
                    else
                    {
                        if (int.TryParse(TrimQuotes(inlineValue), out int variant))
                        {
                            currentDefinition.variants.Add(variant);
                        }

                        readingVariants = false;
                    }

                    continue;
                }

                if (readingVariants && trimmedLine.StartsWith("-", StringComparison.Ordinal))
                {
                    var value = trimmedLine[1..].Trim();
                    if (int.TryParse(value, out int variant))
                    {
                        currentDefinition.variants.Add(variant);
                    }

                    continue;
                }

                var colonIndex = trimmedLine.IndexOf(':');
                readingVariants = false;
                if (colonIndex > 0)
                {
                    var property = trimmedLine[..colonIndex].Trim();
                    var value = TrimQuotes(trimmedLine[(colonIndex + 1)..].Trim());

                    if (property.Equals("model", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool modelValue))
                    {
                        currentDefinition.model = modelValue;
                    }
                    else if (property.Equals("prebuilt", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool prebuiltValue))
                    {
                        currentDefinition.prebuilt = prebuiltValue;
                    }
                }
            }
        }

        return root;
    }

    private static string TrimQuotes(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        if ((input.Length >= 2 && input[0] == '"' && input[^1] == '"') ||
            (input.Length >= 2 && input[0] == '\'' && input[^1] == '\''))
        {
            return input.Substring(1, input.Length - 2);
        }

        return input;
    }

    [Serializable]
    private class AgentModelConfigRoot
    {
        public Dictionary<string, AgentModelDefinition> agents;
        public List<string> landmark_prefixes;
    }

    [Serializable]
    public class AgentModelDefinition
    {
        public bool model = true;
        public bool prebuilt = false;
        public List<int> variants;

        internal void NormalizeVariants()
        {
            if (variants == null || variants.Count == 0)
            {
                variants = new List<int>();
                return;
            }

            variants = variants
                .Where(v => v >= 0 && v <= 1000)
                .Distinct()
                .OrderBy(v => v)
                .ToList();
        }
    }
}

