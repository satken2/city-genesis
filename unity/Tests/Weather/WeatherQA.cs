using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class WeatherQA
{
    static WeatherQA()
    {
        if (SessionState.GetBool("WeatherQA.Pending", false)) EditorApplication.delayCall += Start;
    }
    public static void Run()
    {
        SessionState.SetBool("WeatherQA.Pending", true);
        GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        EditorApplication.EnterPlaymode();
    }
    static void Start()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.delayCall += Start; return; }
        SessionState.SetBool("WeatherQA.Pending", false);
        new GameObject("Weather QA").AddComponent<WeatherQaHost>().Run();
    }
}
