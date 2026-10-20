using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// アセットパック(DLC)のバンドルを作るエディタ拡張。
///
/// 原本は Assets/Packs/{パックID}/ に置く(非公開レポジトリ。公開レポジトリでは無視する)。モデルは標準と同じ相対パスの
/// Models/…(FBX かプレハブ)、名前で引く材質は Materials/{名前}.mat に置く。メニューからパックごとに、
/// 論理名(Models/…・Materials/{名前})で引ける AssetBundle と、論理名の一覧 index.txt を
/// Builds/AssetPacks/{パックID}/assets/windows/ へ書き出す。配布物はこの assets/ を
/// <インストール先>/dlc/{パックID}/assets/ に置き、表示名の pack.json を添える(読み込みは AssetPackStore)。
///
/// 建物と車両のモデルの一覧と重みは、原本の model_weights.json を実在のモデルから作り直し(ModelWeights.Rebuild)、
/// バンドルの隣へ書き出す。標準アセットの表(Resources/Config/model_weights.json)も同じ規則でメニューから作り直す。
/// バンドルは本体と同じ Unity の版でビルドする(版を上げたら作り直す)。
/// </summary>
public static class AssetPackBuilder
{
    private const string PacksRoot = "Assets/Packs";
    private const string OutputRoot = "Builds/AssetPacks";
    private const string ResourcesRoot = "Assets/Resources/";

    [MenuItem("Tools/Asset Packs/Build All (Windows)")]
    public static void BuildAll()
    {
        if (!AssetDatabase.IsValidFolder(PacksRoot))
        {
            Debug.LogWarning($"AssetPackBuilder: {PacksRoot} がありません");
            return;
        }
        foreach (string folder in AssetDatabase.GetSubFolders(PacksRoot))
        {
            Build(Path.GetFileName(folder));
        }
    }

    private static void Build(string packId)
    {
        string root = $"{PacksRoot}/{packId}/";
        var assetPaths = new List<string>();
        var names = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:Model t:Prefab t:Material", new[] { root.TrimEnd('/') }))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (assetPaths.Contains(assetPath)) continue;
            string relative = assetPath.Substring(root.Length);
            bool model = relative.StartsWith("Models/") && !assetPath.EndsWith(".mat");
            bool material = relative.StartsWith(AssetPackStore.MATERIAL_ROOT) && assetPath.EndsWith(".mat");
            if (!model && !material) continue;
            string name = material ? AssetPackStore.MATERIAL_ROOT + Path.GetFileNameWithoutExtension(relative)
                : relative.Substring(0, relative.Length - Path.GetExtension(relative).Length);
            if (names.Contains(name))
            {
                Debug.LogError($"AssetPackBuilder: {packId} の {name} が重複しています({assetPath})。ビルドを中止します");
                return;
            }
            assetPaths.Add(assetPath);
            names.Add(name);
        }
        Dictionary<string, ModelWeights.Model[]> weights = RebuildWeights(root + ModelWeights.FILE_NAME, names);

        string output = Path.Combine(OutputRoot, packId, "assets", AssetPackStore.BUNDLE_DIR);
        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild
        {
            assetBundleName = packId,
            assetNames = assetPaths.ToArray(),
            addressableNames = names.ToArray(),
        };
        BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);
        File.WriteAllLines(Path.Combine(output, AssetPackStore.INDEX_FILE), names);
        File.WriteAllText(Path.Combine(output, ModelWeights.FILE_NAME), ModelWeights.Serialize(weights));
        Debug.Log($"AssetPackBuilder: {packId} を {output} へ書き出しました({names.Count} 件)");
    }

    /// <summary>標準アセットの建物・車両のモデルの一覧と重み(Resources/Config/model_weights.json)を、実在のモデルから作り直す。</summary>
    [MenuItem("Tools/Asset Packs/Update Standard Model Weights")]
    public static void UpdateStandardWeights()
    {
        var names = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { ResourcesRoot + "Models/Buildings", ResourcesRoot + "Models/Vehicles" }))
        {
            string relative = AssetDatabase.GUIDToAssetPath(guid).Substring(ResourcesRoot.Length);
            names.Add(relative.Substring(0, relative.Length - Path.GetExtension(relative).Length));
        }
        string path = ResourcesRoot + "Config/" + ModelWeights.FILE_NAME;
        RebuildWeights(path, names);
        AssetDatabase.ImportAsset(path);
        Debug.Log($"AssetPackBuilder: {path} を作り直しました");
    }

    /// <summary>表を実在のモデルから作り直して書き戻す(既存の重みは残る。ModelWeights.Rebuild)。</summary>
    private static Dictionary<string, ModelWeights.Model[]> RebuildWeights(string path, List<string> names)
    {
        var existing = File.Exists(path) ? ModelWeights.Parse(File.ReadAllText(path)) : null;
        var rebuilt = ModelWeights.Rebuild(existing, names);
        File.WriteAllText(path, ModelWeights.Serialize(rebuilt));
        return rebuilt;
    }
}
