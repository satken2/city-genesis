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

        using (ApiRequest call = ApiClient.Request("POST", "/zones/", jsonData))
        {
            yield return call.Send();
            UnityWebRequest request = call.Web;

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

        using (ApiRequest call = ApiClient.Request("POST", "/paths/batch-zone", jsonData))
        {
            yield return call.Send();
            UnityWebRequest request = call.Web;

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

    /// <summary>
    /// 地区の識別色("#rrggbb")。地区編集モードのUnityが作成前にカーソル脇へ提示した色を
    /// そのまま送り、提示した色と実際に作られる地区の色を一致させる。
    /// null を送るとバックエンドがランダム生成する(NullValueHandling.Ignore でキーごと省かれる)。
    /// </summary>
    public string color;

    public int? allowed_bd_mask;
    public float? limit_volume_ratio;
    public float? limit_height;
    public float? limit_coverage_ratio;
}
