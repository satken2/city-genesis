using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;


/// <summary>
/// 土地区画関連のAPI通信を一元管理するクライアントクラス
/// </summary>
public class ParcelApiClient : MonoBehaviour
{
    public static ParcelApiClient Instance { get; private set; }

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

    #region 道路沿い土地区画作成API

    /// <summary>
    /// 道路沿いの土地区画を作成
    /// </summary>
    public IEnumerator CreatePathParcel(int pathId, bool isRightSide, float width, float height, bool dry_run, Action<CreatePathParcelResponse> onSuccess, Action<string> onFailure)
    {
        // リクエスト用JSONを作成
        string jsonData = "{" +
            "\"path_id\": " + pathId + ", " +
            "\"side\": " + (isRightSide ? "true" : "false") + ", " +
            "\"wigth\": " + width + ", " +
            "\"height\": " + height + ", " +
            "\"dry_run\": " + (dry_run ? "true" : "false") +
        "}";
        
        using (UnityWebRequest request = CreatePostRequest($"{API_BASE_URL}/parcels/create_path_parcel", jsonData))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"道路沿い土地区画作成に失敗: {request.error}");
                onFailure?.Invoke(request.error);
                yield break;
            }

            string json = request.downloadHandler.text;
            CreatePathParcelResponse response = JsonConvert.DeserializeObject<CreatePathParcelResponse>(json);

            if (response != null)
            {
                onSuccess?.Invoke(response);
            }
            else
            {
                onFailure?.Invoke("レスポンスのパースに失敗しました");
            }
        }
    }

    #endregion

    #region ヘルパーメソッド

    /// <summary>
    /// POST リクエストを作成
    /// </summary>
    private UnityWebRequest CreatePostRequest(string url, string jsonData)
    {
        UnityWebRequest request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        return request;
    }

    #endregion
}

#region APIリクエスト・レスポンスデータモデル

[Serializable]
public class ParcelPreview
{
    public float area;
    public float[] center_point;
    public float[][] polygon;
}

[Serializable]
public class CreatePathParcelResponse
{
    public List<int> new_parcels;
    public List<ParcelPreview> preview;
}

#endregion 