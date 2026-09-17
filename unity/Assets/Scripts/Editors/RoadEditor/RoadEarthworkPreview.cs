using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>造成後の線路標高を取得する。Terrainや確定済みパスは変更しない。</summary>
public sealed class RoadEarthworkPreview : MonoBehaviour
{
    public sealed class Result
    {
        public List<List<List<float>>> path_profiles;
    }
    string desired, sent;
    object payload;
    float changedAt;
    bool busy;
    public List<List<Vector3>> PathProfiles { get; private set; }
    public event System.Action Changed;

    public void Request(object value)
    {
        string key = JsonConvert.SerializeObject(value);
        if (key == desired) return;
        desired = key;
        payload = value;
        changedAt = Time.unscaledTime;
        ClearProfiles();
    }

    public void Clear()
    {
        desired = sent = null;
        payload = null;
        ClearProfiles();
    }

    void Update()
    {
        if (!busy && desired != null && desired != sent && Time.unscaledTime-changedAt >= .25f)
            StartCoroutine(Fetch(desired, payload));
    }

    IEnumerator Fetch(string key, object value)
    {
        busy = true;
        sent = key;
        try
        {
            yield return ApiClient.Post<Result>("/paths/earthwork-preview", value, response =>
            {
                // 操作変更や編集終了の前に出した結果は表示しない。
                if (desired == key && isActiveAndEnabled) ApplyProfiles(response);
            }, (code, message) =>
            {
                if (desired == key) Debug.LogWarning($"造成後の線路標高を取得できませんでした: {code} {message}");
            });
        }
        finally { busy = false; }
    }

    void ApplyProfiles(Result response)
    {
        ClearProfiles();
        if (response.path_profiles != null)
        {
            PathProfiles = new List<List<Vector3>>();
            foreach (var line in response.path_profiles)
            {
                var points = new List<Vector3>();
                foreach (var p in line) points.Add(new Vector3(p[0], p[1], p[2]));
                PathProfiles.Add(points);
            }
        }
        Changed?.Invoke();
    }

    void ClearProfiles()
    {
        PathProfiles = null;
    }
    void OnDisable() { Clear(); }
    void OnDestroy() { Clear(); }
}
