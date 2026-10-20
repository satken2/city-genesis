using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Compare core and frontend versions before fetching master data or restoring a session.
/// Startup is blocked when the frontend MAJOR is below the core MAJOR. Waiting cannot change versions, so exit without retrying.
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

    /// <summary>Whether the frontend MAJOR is at least the core MAJOR. Do not assume compatibility if either version cannot be read.</summary>
    public static bool FrontendCoversCore(string frontendVersion, string coreVersion)
    {
        return TryParseMajor(frontendVersion, out int frontend) && TryParseMajor(coreVersion, out int core) && frontend >= core;
    }

    private static bool TryParseMajor(string version, out int major)
    {
        return int.TryParse(version?.Split('.')[0], out major);
    }
}
