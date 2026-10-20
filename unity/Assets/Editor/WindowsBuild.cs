using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

/// <summary>有効なシーンから Windows x64 の配布用プレイヤーを作るバッチビルドの入口。</summary>
public static class WindowsBuild
{
    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        int outputIndex = Array.IndexOf(args, "-genesisBuildOutput");
        if (outputIndex < 0 || outputIndex + 1 >= args.Length)
            throw new BuildFailedException("-genesisBuildOutput に出力フォルダを指定してください。");

        string output = Path.GetFullPath(args[outputIndex + 1]);
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
            throw new BuildFailedException("ビルド対象のシーンがありません。");

        Directory.CreateDirectory(output);
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = Path.Combine(output, "Realcity.exe"),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Windows ビルドに失敗しました: {report.summary.result}");
    }
}
