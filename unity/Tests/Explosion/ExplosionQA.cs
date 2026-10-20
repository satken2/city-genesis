using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class ExplosionQA
{
    static ExplosionQA()
    {
        if (SessionState.GetBool("ExplosionQA", false)) EditorApplication.delayCall += Start;
    }
    public static void Run()
    {
        SessionState.SetBool("ExplosionQA", true);
        GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        EditorApplication.EnterPlaymode();
    }
    private static void Start()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.delayCall += Start; return; }
        SessionState.SetBool("ExplosionQA", false);
        new GameObject("Explosion QA").AddComponent<ExplosionQaHost>();
    }
}
