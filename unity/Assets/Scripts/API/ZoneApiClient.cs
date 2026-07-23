using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using GeneratedModels;

public class ZoneApiClient : MonoBehaviour
{
    public static ZoneApiClient Instance { get; private set; }

    /// <summary>
    /// バックエンド API のベース URL。
    /// ApiClient.BASE_URL と同一値を使用し、起動時の --api-base-url 引数に対応する。
    /// </summary>
    private static string API_BASE_URL => ApiClient.BASE_URL;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public IEnumerator CreateZone(CreateZoneRequest createRequest, Action<GeneratedModels.ZoneResponse> onSuccess, Action<string, long> onFailure)
    {
        string jsonData = JsonConvert.SerializeObject(createRequest, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        });

        using (UnityWebRequest request = CreatePostRequest($"{API_BASE_URL}/zones/", jsonData))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"ゾーンの作成に失敗: {request.error} (Code: {request.responseCode})");
                onFailure?.Invoke(request.error, request.responseCode);
            }
            else
            {
                string json = request.downloadHandler.text;
                GeneratedModels.ZoneResponse response = JsonConvert.DeserializeObject<GeneratedModels.ZoneResponse>(json);
                onSuccess?.Invoke(response);
            }
        }
    }

    /// <summary>
    /// 指定した道路IDリストを指定した地区に割り当てる。
    /// POST /api/v1/paths/batch-zone を使用する。
    /// </summary>
    public IEnumerator AssignPathsToZone(int[] pathIds, int zoneId, Action onSuccess, Action<string> onFailure)
    {
        var payload = new { path_ids = pathIds, zone_id = zoneId };
        string jsonData = JsonConvert.SerializeObject(payload);
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);

        using (UnityWebRequest request = new UnityWebRequest($"{API_BASE_URL}/paths/batch-zone", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"道路の地区割り当てに失敗: {request.error} (Code: {request.responseCode})");
                onFailure?.Invoke(request.error);
            }
            else
            {
                onSuccess?.Invoke();
            }
        }
    }

    private UnityWebRequest CreatePostRequest(string url, string jsonData)
    {
        UnityWebRequest request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        return request;
    }
}

[Serializable]
public class CreateZoneRequest
{
    /// <summary>
    /// 地区に所属させる道路（パス）のIDリスト。
    /// 新しい仕様では、プレイヤーが直接指定するのは道路のみであり、
    /// 地区のポリゴンは後付けで所属する土地区画の形状から自動計算される。
    /// </summary>
    public int[] path_ids;
    public string name;
    public int? allowed_bd_mask;
    public float? limit_volume_ratio;
    public float? limit_height;
    public float? limit_coverage_ratio;
}
