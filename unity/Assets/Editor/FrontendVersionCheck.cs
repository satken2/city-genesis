using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

/// <summary>Unity単体の開発・ビルドでも、配布設定の同期漏れを検出する。</summary>
[InitializeOnLoad]
public class FrontendVersionCheck : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;
    static FrontendVersionCheck()
    {
        EditorApplication.playModeStateChanged += state => {
            if (state == PlayModeStateChange.ExitingEditMode && !IsCurrent()) {
                UnityEngine.Debug.LogError("Run npm run sync:version in tauri before entering Play mode.");
                EditorApplication.isPlaying = false;
            }
        };
    }
    static bool IsCurrent()
    {
        var path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "../../version.json"));
        var value = JObject.Parse(File.ReadAllText(path));
        return (string)value["frontend_version"] == FrontendVersion.Version
            && (int)value["ipc_contract_version"] == FrontendVersion.IpcContract
            && PlayerSettings.bundleVersion == FrontendVersion.Version;
    }
    public void OnPreprocessBuild(BuildReport report)
    {
        if (!IsCurrent()) throw new BuildFailedException("Run npm run sync:version in tauri.");
    }
}
