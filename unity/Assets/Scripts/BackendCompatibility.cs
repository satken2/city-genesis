using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>マスター取得・セッション復帰より前に通信契約を確定する。</summary>
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
            compatible = (int?)version["api_contract_version"] == FrontendVersion.ApiContract;
            if (!compatible) Debug.LogError("API_CONTRACT_MISMATCH");
        });
        Compatible = compatible;
    }
}
