using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// マスター取得・セッション復帰より前に、コアの版とフロントの版を照合する。
/// フロントのMAJORがコアのMAJOR未満なら起動できない。待っても版は変わらないので、再試行せず終了する。
/// </summary>
public static class BackendCompatibility
{
    public static bool Compatible { get; private set; }
    public static bool VersionReceived { get; private set; }

    public static IEnumerator Verify()
    {
        if (VersionReceived) yield break;
        bool compatible = false;
        yield return ApiClient.Get<JObject>("/system/version", version => {
            VersionReceived = true;
            string coreVersion = (string)version["core_version"];
            compatible = FrontendCoversCore(FrontendVersion.Version, coreVersion);
            if (!compatible)
            {
                Debug.LogError($"FRONTEND_VERSION_OUTDATED: core {coreVersion} / UI {FrontendVersion.Version}");
                Application.Quit();
            }
        });
        Compatible = compatible;
    }

    /// <summary>フロントのMAJORがコアのMAJOR以上か。どちらかの版を読めないときは互換と推測しない。</summary>
    public static bool FrontendCoversCore(string frontendVersion, string coreVersion)
    {
        return TryParseMajor(frontendVersion, out int frontend) && TryParseMajor(coreVersion, out int core) && frontend >= core;
    }

    private static bool TryParseMajor(string version, out int major)
    {
        return int.TryParse(version?.Split('.')[0], out major);
    }
}
