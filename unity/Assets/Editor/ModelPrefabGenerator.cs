using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Assets/Models 配下のFBXから、Assets/Resources/Models 配下に同一相対パスのミラープレハブを生成・同期するツール。
///
/// ランタイム(Utils/ModelManager)は "Models/Buildings/a100/0/floor" のような文字列パスで
/// Resources.Load するため、FBXソースと同じ相対パス構造のプレハブが Resources 配下に必要になる。
/// このミラーを手作業で維持すると追加漏れ・リネーム漏れ・シーンからのドラッグ生成による座標ゴミの
/// 焼き付きが起きるため、本ツールで機械的に同期する。
///
/// - プレハブはFBXルートをオーバーライドなしで包む「純粋なラッパー(プレハブバリアント)」として生成する。
/// - コンポーネント追加・マテリアル差し替え等の実質的な手動編集があるプレハブは上書きせずスキップして警告する。
/// - FBXの新規追加・移動時は AssetPostprocessor(ModelPrefabImportWatcher)が自動で対応プレハブを生成する。
/// - 対応FBXを失った孤児プレハブは自動削除しない。Delete Orphans メニューから明示的に削除する。
/// </summary>
public static class ModelPrefabGenerator
{
    private const string SourceRoot = "Assets/Models";
    private const string PrefabRoot = "Assets/Resources/Models";

    /// <summary>
    /// transform・名前系のオーバーライドはシーンからのドラッグ生成で付くゴミとみなし、再生成で除去してよい。
    /// これ以外のオーバーライドを持つプレハブは手動編集とみなして上書きしない。
    /// </summary>
    private static readonly string[] HarmlessPropertyPrefixes =
    {
        "m_Name",
        "m_LocalPosition.",
        "m_LocalRotation.",
        "m_LocalScale.",
        "m_LocalEulerAnglesHint.",
        "m_RootOrder",
    };

    private enum SyncResult
    {
        Created,
        Cleaned,
        UpToDate,
        Skipped,
    }

    [MenuItem("Tools/Model Prefabs/Sync All (FBX → Prefab)")]
    public static void SyncAll()
    {
        List<string> fbxPaths = CollectSourceFbxPaths();
        var counts = new Dictionary<SyncResult, int>();

        try
        {
            for (int i = 0; i < fbxPaths.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Model Prefabs", fbxPaths[i], (float)i / fbxPaths.Count);
                SyncResult result = SyncOne(fbxPaths[i]);
                counts[result] = counts.GetValueOrDefault(result) + 1;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        AssetDatabase.SaveAssets();

        List<string> orphans = CollectOrphanPrefabPaths();
        foreach (string orphan in orphans)
        {
            Debug.LogWarning($"ModelPrefabGenerator: 対応FBXのない孤児プレハブ: {orphan} " +
                             "(Tools/Model Prefabs/Delete Orphans で削除できます)");
        }

        Debug.Log($"ModelPrefabGenerator: 同期完了。FBX {fbxPaths.Count}件 → " +
                  $"新規生成 {counts.GetValueOrDefault(SyncResult.Created)} / " +
                  $"ゴミ掃除 {counts.GetValueOrDefault(SyncResult.Cleaned)} / " +
                  $"変更なし {counts.GetValueOrDefault(SyncResult.UpToDate)} / " +
                  $"スキップ {counts.GetValueOrDefault(SyncResult.Skipped)} / " +
                  $"孤児 {orphans.Count}");
    }

    [MenuItem("Tools/Model Prefabs/Delete Orphans")]
    public static void DeleteOrphans()
    {
        List<string> orphans = CollectOrphanPrefabPaths();
        if (orphans.Count == 0)
        {
            Debug.Log("ModelPrefabGenerator: 孤児プレハブはありません。");
            return;
        }

        string list = string.Join("\n", orphans);
        bool ok = EditorUtility.DisplayDialog(
            "Model Prefabs",
            $"対応FBXのない孤児プレハブ {orphans.Count}件 を削除します。\n\n{list}",
            "削除する", "キャンセル");
        if (!ok) return;

        foreach (string orphan in orphans)
        {
            if (!AssetDatabase.DeleteAsset(orphan))
            {
                Debug.LogError($"ModelPrefabGenerator: 削除に失敗: {orphan}");
            }
        }
        Debug.Log($"ModelPrefabGenerator: 孤児プレハブ {orphans.Count}件 を削除しました。");
    }

    /// <summary>FBX 1件をミラープレハブと同期する。</summary>
    private static SyncResult SyncOne(string fbxPath)
    {
        string prefabPath = ToPrefabPath(fbxPath);
        if (!File.Exists(prefabPath))
        {
            return CreateWrapperPrefab(fbxPath, prefabPath) ? SyncResult.Created : SyncResult.Skipped;
        }

        GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefabRoot == null)
        {
            Debug.LogWarning($"ModelPrefabGenerator: プレハブをロードできないためスキップ: {prefabPath}");
            return SyncResult.Skipped;
        }

        // ラッパー(FBXのバリアント)でないもの、別FBXを指すものは同期対象外として触らない
        GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(prefabRoot);
        if (source == null || AssetDatabase.GetAssetPath(source) != fbxPath)
        {
            Debug.LogWarning($"ModelPrefabGenerator: ソースが対応FBXではないためスキップ: {prefabPath} " +
                             $"(source={(source == null ? "なし" : AssetDatabase.GetAssetPath(source))})");
            return SyncResult.Skipped;
        }

        // GameObject・コンポーネントの追加は実質的な手動編集なので上書きしない
        if (PrefabUtility.GetAddedGameObjects(prefabRoot).Count > 0 ||
            PrefabUtility.GetAddedComponents(prefabRoot).Count > 0)
        {
            Debug.LogWarning($"ModelPrefabGenerator: 手動編集(オブジェクト/コンポーネント追加)があるためスキップ: {prefabPath}");
            return SyncResult.Skipped;
        }

        PropertyModification[] mods =
            PrefabUtility.GetPropertyModifications(prefabRoot) ?? Array.Empty<PropertyModification>();
        if (mods.Any(m => !IsHarmlessModification(m.propertyPath)))
        {
            Debug.LogWarning($"ModelPrefabGenerator: 手動編集(プロパティオーバーライド)があるためスキップ: {prefabPath}");
            return SyncResult.Skipped;
        }

        // 残るオーバーライドはtransform・名前だけ。ソースと一致していれば何もせず、ズレていれば作り直してゴミを除去する
        if (TransformMatchesSource(prefabRoot.transform, source.transform))
        {
            return SyncResult.UpToDate;
        }
        return CreateWrapperPrefab(fbxPath, prefabPath) ? SyncResult.Cleaned : SyncResult.Skipped;
    }

    /// <summary>FBXをオーバーライドなしで包むラッパープレハブを生成する(既存の場合はGUIDを保ったまま上書き)。</summary>
    internal static bool CreateWrapperPrefab(string fbxPath, string prefabPath)
    {
        GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (fbxAsset == null)
        {
            Debug.LogError($"ModelPrefabGenerator: FBXをロードできません: {fbxPath}");
            return false;
        }

        EnsureFolders(Path.GetDirectoryName(prefabPath));

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
        try
        {
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool success);
            if (!success)
            {
                Debug.LogError($"ModelPrefabGenerator: プレハブの保存に失敗: {prefabPath}");
            }
            return success;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    /// <summary>FBXパスから対応するミラープレハブのパスを求める。</summary>
    internal static string ToPrefabPath(string fbxPath)
    {
        string relative = fbxPath.Substring(SourceRoot.Length + 1);
        return $"{PrefabRoot}/{Path.ChangeExtension(relative, ".prefab")}".Replace('\\', '/');
    }

    internal static bool IsSourceFbx(string assetPath)
    {
        return assetPath.StartsWith(SourceRoot + "/", StringComparison.OrdinalIgnoreCase) &&
               assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHarmlessModification(string propertyPath)
    {
        return HarmlessPropertyPrefixes.Any(prefix =>
            propertyPath == prefix.TrimEnd('.') || propertyPath.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool TransformMatchesSource(Transform prefab, Transform source)
    {
        return prefab.localPosition == source.localPosition &&
               prefab.localRotation == source.localRotation &&
               prefab.localScale == source.localScale;
    }

    private static List<string> CollectSourceFbxPaths()
    {
        return Directory.GetFiles(SourceRoot, "*.fbx", SearchOption.AllDirectories)
            .Select(p => p.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>対応FBXが存在しないミラープレハブを列挙する。</summary>
    private static List<string> CollectOrphanPrefabPaths()
    {
        return Directory.GetFiles(PrefabRoot, "*.prefab", SearchOption.AllDirectories)
            .Select(p => p.Replace('\\', '/'))
            .Where(prefabPath =>
            {
                string relative = prefabPath.Substring(PrefabRoot.Length + 1);
                string fbxPath = $"{SourceRoot}/{Path.ChangeExtension(relative, ".fbx")}";
                return !File.Exists(fbxPath);
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>AssetDatabaseにフォルダを認識させるため、CreateFolderで親から順に作成する。</summary>
    private static void EnsureFolders(string folderPath)
    {
        folderPath = folderPath.Replace('\\', '/');
        if (AssetDatabase.IsValidFolder(folderPath)) return;

        string[] parts = folderPath.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }
            current = next;
        }
    }
}

/// <summary>
/// Assets/Models 配下へのFBX追加・移動を検知し、対応するミラープレハブを自動生成するウォッチャー。
/// 生成はインポート処理の途中で行えないため delayCall で1フレーム遅延させる。
/// FBXの削除では対応プレハブを自動削除せず、孤児化した旨だけ警告する。
/// </summary>
internal sealed class ModelPrefabImportWatcher : AssetPostprocessor
{
    private static void OnPostprocessAllAssets(
        string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        List<string> pending = importedAssets.Concat(movedAssets)
            .Where(ModelPrefabGenerator.IsSourceFbx)
            .Where(p => !File.Exists(ModelPrefabGenerator.ToPrefabPath(p)))
            .ToList();
        if (pending.Count > 0)
        {
            EditorApplication.delayCall += () =>
            {
                foreach (string fbxPath in pending)
                {
                    if (ModelPrefabGenerator.CreateWrapperPrefab(fbxPath, ModelPrefabGenerator.ToPrefabPath(fbxPath)))
                    {
                        Debug.Log($"ModelPrefabGenerator: ミラープレハブを自動生成: {ModelPrefabGenerator.ToPrefabPath(fbxPath)}");
                    }
                }
                AssetDatabase.SaveAssets();
            };
        }

        foreach (string fbxPath in deletedAssets.Concat(movedFromAssetPaths).Where(ModelPrefabGenerator.IsSourceFbx))
        {
            string prefabPath = ModelPrefabGenerator.ToPrefabPath(fbxPath);
            if (File.Exists(prefabPath))
            {
                Debug.LogWarning($"ModelPrefabGenerator: FBXの削除/移動により孤児化したプレハブ: {prefabPath} " +
                                 "(Tools/Model Prefabs/Delete Orphans で削除できます)");
            }
        }
    }
}
