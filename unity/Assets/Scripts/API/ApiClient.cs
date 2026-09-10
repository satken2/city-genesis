using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// HTTPリクエストを統一的に処理する共通APIクライアント
/// </summary>
public static class ApiClient
{
    /// <summary>
    /// バックエンド API のベース URL。
    /// 起動時にコマンドライン引数 --api-base-url で上書き可能。
    /// Editor スクリプトなど外部からアクセスできるよう public にしている。
    /// </summary>
    public static string BASE_URL = "http://127.0.0.1:5000/api/v1";

    /// <summary>
    /// GETリクエストを送信
    /// </summary>
    public static IEnumerator Get<T>(string path, Action<T> onSuccess, Action<string> onError = null)
    {
        string url = $"{BASE_URL}{path}";
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string json = request.downloadHandler.text;
                    T response = JsonConvert.DeserializeObject<T>(json);
                    onSuccess?.Invoke(response);
                }
                catch (Exception e)
                {
                    string errorMsg = $"JSON解析エラー: {e.Message}";
                    Debug.LogError(errorMsg);
                    onError?.Invoke(errorMsg);
                }
            }
            else
            {
                string errorMsg = $"GETリクエスト失敗: {request.error}";
                Debug.LogError(errorMsg);
                onError?.Invoke(errorMsg);
            }
        }
    }

    /// <summary>
    /// 一括取得系エンドポイントに要求するAcceptヘッダ。
    /// バックエンドのbbox一括取得エンドポイント(buildings/paths/parcels/zones/nodes)は
    /// これを見て MessagePack バイナリで応答する(未対応エンドポイントは従来どおりJSON)。
    /// どちらで返ってきたかはレスポンスの Content-Type で判別する。
    /// </summary>
    private const string MSGPACK_MEDIA_TYPE = "application/x-msgpack";
    private const string BULK_ACCEPT_HEADER = MSGPACK_MEDIA_TYPE + ", application/json";

    /// <summary>
    /// 一括取得(bbox取得など大きなレスポンス)用のGETリクエストを送信する。
    /// Get&lt;T&gt; との違い:
    /// - Accept ヘッダで MessagePack を要求し、対応エンドポイントではバイナリで受信する
    ///   (JSONテキストのパースより高速。デコードは MsgPackDecoder)。未対応エンドポイント
    ///   にはJSONで応答されるため、呼び出し側はどちらか意識しなくてよい。
    /// - デシリアライズを Task.Run でバックグラウンドスレッドに逃がし、
    ///   数百KB級のレスポンスでもメインスレッドをブロックしない(フレームヒッチ防止)
    /// - timeout と Cache-Control: no-cache を標準で設定する
    /// - UNITY_LOG_TIMING 有効時に network / deserialize を分離計測してログ出力する
    /// 各マネージャーのマージBBOX一括ロードは必ずこれを経由すること(通信の集約点)。
    /// </summary>
    /// <param name="path">BASE_URL からの相対パス(クエリ文字列含む)</param>
    /// <param name="onSuccess">デシリアライズ成功時に結果を受け取るコールバック</param>
    /// <param name="onError">通信失敗・デシリアライズ失敗時のコールバック(エラーログは出力済み)</param>
    /// <param name="timingLabel">TIMINGログの識別子(例: "PathManager nodes")。省略時はpathを使う</param>
    /// <param name="timeoutSeconds">リクエストのタイムアウト秒数</param>
    public static IEnumerator GetBulk<T>(string path, Action<T> onSuccess, Action<string> onError = null, string timingLabel = null, int timeoutSeconds = 10) where T : class
    {
        string url = $"{BASE_URL}{path}";
        string label = timingLabel ?? path;
        var swNet = System.Diagnostics.Stopwatch.StartNew();

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = timeoutSeconds;
            request.SetRequestHeader("Cache-Control", "no-cache");
            request.SetRequestHeader("Accept", BULK_ACCEPT_HEADER);

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string errorMsg = $"GETリクエスト失敗 ({label}): {request.error} (HTTP {request.responseCode}, url: {url})";
                Debug.LogError(errorMsg);
                onError?.Invoke(errorMsg);
                yield break;
            }

            // レスポンス形式はContent-Typeで判別する(サーバーがmsgpack対応済みのエンドポイントか)
            string contentType = request.GetResponseHeader("Content-Type") ?? "";
            bool isMsgPack = contentType.Contains(MSGPACK_MEDIA_TYPE);
            byte[] rawData = isMsgPack ? request.downloadHandler.data : null;
            string json = isMsgPack ? null : request.downloadHandler.text;

            int byteLength = request.downloadHandler.data?.Length ?? 0;
            string format = isMsgPack ? "msgpack" : "json";
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] ApiClient  network/{label}: {swNet.ElapsedMilliseconds}ms ({byteLength} bytes, {format})");

            var swDeser = System.Diagnostics.Stopwatch.StartNew();
            Task<T> deserializeTask = Task.Run(() => isMsgPack
                ? MsgPackDecoder.Deserialize<T>(rawData)
                : JsonConvert.DeserializeObject<T>(json));
            yield return Utils.WaitTask(deserializeTask);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] ApiClient  deserialize/{label}: {swDeser.ElapsedMilliseconds}ms ({format})");

            if (deserializeTask.IsFaulted || deserializeTask.Result == null)
            {
                // 例外の詳細は Utils.WaitTask がログ出力済み。切り分け用にレスポンス先頭を添える。
                string snippet = isMsgPack
                    ? $"(msgpack {byteLength} bytes)"
                    : (json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                string errorMsg = $"レスポンス解析エラー ({label}): url: {url}\n{snippet}";
                Debug.LogError(errorMsg);
                onError?.Invoke(errorMsg);
                yield break;
            }

            onSuccess?.Invoke(deserializeTask.Result);
        }
    }

    /// <summary>
    /// POSTリクエストを送信
    /// </summary>
    public static IEnumerator Post<T>(string path, object payload, Action<T> onSuccess, Action<long, string> onError = null)
    {
        string url = $"{BASE_URL}{path}";
        string json = JsonConvert.SerializeObject(payload, Formatting.Indented);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string responseJson = request.downloadHandler.text;
                    T response = JsonConvert.DeserializeObject<T>(responseJson);
                    onSuccess?.Invoke(response);
                }
                catch (Exception e)
                {
                    string errorMsg = $"JSON解析エラー: {e.Message}";
                    Debug.LogError(errorMsg);
                    onError?.Invoke(500, errorMsg);
                }
            }
            else
            {
                string errorMsg = request.downloadHandler.text;
                Debug.LogError($"POSTリクエスト失敗 (HTTP {request.responseCode}): {errorMsg}");
                onError?.Invoke(request.responseCode, errorMsg);
            }
        }
    }

    /// <summary>
    /// PATCHリクエストを送信
    /// </summary>
    public static IEnumerator Patch(string path, object payload, Action onSuccess, Action<long, string> onError = null)
    {
        string url = $"{BASE_URL}{path}";
        string json = JsonConvert.SerializeObject(payload);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        using (UnityWebRequest request = new UnityWebRequest(url, "PATCH"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                onSuccess?.Invoke();
            }
            else
            {
                string errorMsg = request.downloadHandler.text;
                Debug.LogError($"PATCHリクエスト失敗 (HTTP {request.responseCode}): {errorMsg}");
                onError?.Invoke(request.responseCode, errorMsg);
            }
        }
    }

}

