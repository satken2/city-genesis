using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// ランタイム生成かつ drawInstanced = true の Terrain が法線マップを GPU で生成するための
/// 組み込みシェーダーを Always Included Shaders に保証する。
/// シーンに静的な Terrain が無いため、未登録だとビルドから脱落して地形法線が下向きになる。
/// </summary>
public class TerrainBuildShaders : IPreprocessBuildWithReport
{
    private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";
    private const string NormalmapShaderName = "Hidden/TerrainEngine/GenerateNormalmap";

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        EnsureIncluded();
    }

    public static void EnsureIncluded()
    {
        Shader shader = Shader.Find(NormalmapShaderName);
        if (shader == null)
        {
            throw new BuildFailedException($"地形の法線マップ生成シェーダーが見つかりません: {NormalmapShaderName}");
        }

        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(GraphicsSettingsPath);
        if (assets == null || assets.Length == 0 || assets[0] == null)
        {
            throw new BuildFailedException($"{GraphicsSettingsPath} を読み込めませんでした。");
        }

        using (var serialized = new SerializedObject(assets[0]))
        {
            SerializedProperty list = serialized.FindProperty("m_AlwaysIncludedShaders");
            if (list == null || !list.isArray)
            {
                throw new BuildFailedException("GraphicsSettings に m_AlwaysIncludedShaders が見つかりません。");
            }

            for (int i = 0; i < list.arraySize; i++)
            {
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            }

            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            list.GetArrayElementAtIndex(index).objectReferenceValue = shader;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log($"TerrainBuildShaders: Always Included Shaders に {NormalmapShaderName} を追加しました。");
        }
    }
}
