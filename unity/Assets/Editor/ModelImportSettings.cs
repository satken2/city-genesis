using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Assets/Models 配下のモデルアセットのインポート設定を強制するエディタ拡張。
///
/// ランタイムのメッシュ生成は、パーツのメッシュ頂点を読み出して配置行列を焼き込む方式
/// (ModularBodyBuilder.BuildWallRun / BuildingMeshUtils.CombineModuleMeshes /
/// Dreamteck SplineMesh)なので、Read/Write Enabled(ModelImporter.isReadable)が
/// 無効なメッシュは頂点を取得できず、エラーログを出して何も描画されない。
/// Unityの既定値は無効なため、FBXを追加するたび手動でONにする運用は必ず漏れる
/// (漏れると「屋根と地面だけ出て壁が消える」のような部分欠けになり、原因が分かりにくい)。
///
/// Read/Write 有効化はCPU側にメッシュのコピーを保持するぶんメモリとビルドサイズが増えるが、
/// Assets/Models のモデルはすべてランタイム加工の対象になりうるモジュラーパーツなので、
/// 個別判断はせずフォルダ単位で一律有効にする。
/// </summary>
public static class ModelImportSettings
{
    internal const string ModelRoot = "Assets/Models";

    /// <summary>Read/Write を強制する対象アセットか(Assets/Models 配下か)を返す。</summary>
    internal static bool IsManagedModel(string assetPath)
    {
        return !string.IsNullOrEmpty(assetPath)
            && assetPath.StartsWith(ModelRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    [MenuItem("Tools/Models/Enable Read-Write (Assets/Models)")]
    public static void EnableReadWriteAll()
    {
        string[] guids = AssetDatabase.FindAssets("t:Model", new[] { ModelRoot });
        var changed = new List<string>();
        int skipped = 0;

        try
        {
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                EditorUtility.DisplayProgressBar("Model Import Settings", path, (float)i / guids.Length);

                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                {
                    skipped++;
                    continue;
                }
                if (importer.isReadable)
                {
                    continue;
                }

                importer.isReadable = true;
                importer.SaveAndReimport();
                changed.Add(path);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();

        foreach (string path in changed)
        {
            Debug.Log($"ModelImportSettings: Read/Write を有効化: {path}");
        }
        Debug.Log($"ModelImportSettings: 対象 {guids.Length}件 → 有効化 {changed.Count} / " +
                  $"すでに有効 {guids.Length - changed.Count - skipped} / 対象外 {skipped}");
    }

    /// <summary>Read/Write が無効なモデルを、再インポートせずに一覧するだけの確認用。</summary>
    [MenuItem("Tools/Models/List Non-Readable Models")]
    public static void ListNonReadable()
    {
        string[] guids = AssetDatabase.FindAssets("t:Model", new[] { ModelRoot });
        int count = 0;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetImporter.GetAtPath(path) is ModelImporter importer && !importer.isReadable)
            {
                Debug.LogWarning($"ModelImportSettings: Read/Write が無効: {path}");
                count++;
            }
        }
        Debug.Log($"ModelImportSettings: Read/Write が無効なモデル {count}件 / 全 {guids.Length}件");
    }
}

/// <summary>
/// Assets/Models 配下のモデルをインポートするたびに Read/Write を有効化するウォッチャー。
/// 新規追加・再インポート・外部ツールからの上書きのいずれでも通るので、
/// 一度設定を戻してしまっても次のインポートで自己修復する。
/// </summary>
internal sealed class ModelReadWriteEnforcer : AssetPostprocessor
{
    private void OnPreprocessModel()
    {
        if (!ModelImportSettings.IsManagedModel(assetPath))
        {
            return;
        }

        var importer = (ModelImporter)assetImporter;
        if (!importer.isReadable)
        {
            importer.isReadable = true;
        }
    }
}
