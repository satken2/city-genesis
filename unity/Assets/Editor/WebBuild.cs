using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.Rendering;

/// <summary>
/// ブラウザ版のグラフィック(Unity の Web ビルド)を作る。エディタのプラットフォームを Web に切り替えてからメニューの Tools/Build/Web、
/// またはバッチモードの `-buildTarget WebGL -executeMethod WebBuild.Build` で実行する。
/// Web にしか無いモジュール(WebGLInput など)は、エディタのプラットフォームが Web のときしかスクリプトのコンパイルで参照されない。
/// 変えるのは Web 向けのプレイヤー設定だけで、Windows 向けの設定には触れない。
/// 出力は Builds/Web(git 管理外)。ブラウザ版のページ(tauri/web)が読み込むファイルの名前を build.json に書き出す。
/// </summary>
public static class WebBuild
{
    private const string OutputDir = "Builds/Web";

    [MenuItem("Tools/Build/Web")]
    public static void Build()
    {
        // 森の配置と地形追従の移動窓はコンピュートシェーダーを使うので、WebGL2 ではなく WebGPU で描く。
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.WebGPU });
        // 配信するサーバーの設定によらず読めるよう、展開はページの JS が行う。
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
        PlayerSettings.WebGL.decompressionFallback = true;
        PlayerSettings.WebGL.nameFilesAsHashes = false;
        PlayerSettings.WebGL.maximumMemorySize = 4096;

        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = OutputDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        });
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new BuildFailedException($"Web ビルドに失敗しました: {report.summary.result}");
        }
        WriteManifest();
    }

    /// <summary>Build フォルダの中から、Unity の読み込みに要るファイルを種類ごとに探して書き出す。</summary>
    private static void WriteManifest()
    {
        string[] files = Directory.GetFiles(Path.Combine(OutputDir, "Build")).Select(Path.GetFileName).ToArray();
        string Find(string kind) =>
            "Build/" + (files.FirstOrDefault(name => name.Contains(kind)) ?? throw new BuildFailedException($"Build/*{kind}* がありません"));
        var manifest = new JObject
        {
            ["loader"] = Find(".loader.js"),
            ["data"] = Find(".data"),
            ["framework"] = Find(".framework.js"),
            ["code"] = Find(".wasm"),
            ["companyName"] = PlayerSettings.companyName,
            ["productName"] = PlayerSettings.productName,
            ["productVersion"] = PlayerSettings.bundleVersion,
        };
        File.WriteAllText(Path.Combine(OutputDir, "build.json"), manifest.ToString());
        UnityEngine.Debug.Log($"WebBuild: {Path.GetFullPath(OutputDir)} に出力しました");
    }
}
