using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// HTTPリクエストを統一的に処理する共通APIクライアント。
/// ローカル API への要求は、通信契約ヘッダーを付けるため、すべてここ(<see cref="Get{T}"/> などの定型か <see cref="Request"/>)を通す。
/// UnityWebRequest を直接作るとヘッダーが付かず、コアとの契約版の不一致を検知できない。
/// </summary>
public static class ApiClient
{
    /// <summary>
    /// バックエンド API のベース URL。
    /// 起動時にコマンドライン引数 --api-base-url で上書き可能。
    /// Editor スクリプトなど外部からアクセスできるよう public にしている。
    /// </summary>
    public static string BASE_URL = "http://127.0.0.1:5000/api/v1";

    public const string CONTRACT_HEADER = "X-City-Genesis-API";

    /// <summary>通信契約ヘッダーを付ける。要求の組み立ては <see cref="ApiRequest"/> が行うので、呼び出し元は <see cref="Request"/> を使う。</summary>
    internal static void ApplyHeaders(UnityWebRequest request)
    {
        request.SetRequestHeader(CONTRACT_HEADER, FrontendVersion.ApiContract.ToString());
    }

    /// <summary>
    /// 定型(Get/GetBulk/Post/Patch)に当てはまらない要求を作る(まだ送らない)。
    /// path は BASE_URL からの相対パス(クエリ文字列含む)、json を渡すと本文として送る。
    /// <see cref="ApiRequest.Send"/> で送って待ち、結果は <see cref="ApiRequest.Web"/> から読む。using で破棄すること。
    /// </summary>
    public static ApiRequest Request(string method, string path, string json = null, int timeoutSeconds = 0)
    {
        return new ApiRequest(method, $"{BASE_URL}{path}", json != null ? Encoding.UTF8.GetBytes(json) : null, "application/json", timeoutSeconds);
    }

    /// <summary>
    /// バイナリの本文(画像など)を contentType で送る要求を作る(まだ送らない)。送り方と結果の読み方は <see cref="Request"/> と同じ。
    /// </summary>
    public static ApiRequest Request(string method, string path, byte[] body, string contentType, int timeoutSeconds = 0)
    {
        return new ApiRequest(method, $"{BASE_URL}{path}", body, contentType, timeoutSeconds);
    }

    /// <summary>
    /// GETリクエストを送信
    /// </summary>
    public static IEnumerator Get<T>(string path, Action<T> onSuccess, Action<string> onError = null)
    {
        using (ApiRequest call = Request("GET", path, timeoutSeconds: 10))
        {
            yield return call.Send();
            UnityWebRequest request = call.Web;

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
    /// <param name="isCanceled">真になったら通信を中断し、デコードもコールバックもしない(ログも出さない)。
    /// 取消し時は onSuccess も onError も呼ばれないので、呼び出し側は yield の後に自分の取消しを確認すること。</param>
    public static IEnumerator GetBulk<T>(string path, Action<T> onSuccess, Action<string> onError = null, string timingLabel = null, int timeoutSeconds = 10, Func<bool> isCanceled = null) where T : class
    {
        string url = $"{BASE_URL}{path}";
        string label = timingLabel ?? path;
        var swNet = System.Diagnostics.Stopwatch.StartNew();

        using (ApiRequest call = Request("GET", path, timeoutSeconds: timeoutSeconds)
            .WithHeader("Cache-Control", "no-cache")
            .WithHeader("Accept", BULK_ACCEPT_HEADER))
        {
            yield return call.Send(isCanceled);
            if (isCanceled != null && isCanceled())
            {
                RenderWorkScheduler.CountEvent("api.bulkCanceled");
                yield break;
            }
            UnityWebRequest request = call.Web;

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

            int byteLength = isMsgPack ? rawData?.Length ?? 0 : (int)request.downloadedBytes;
            string format = isMsgPack ? "msgpack" : "json";
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] ApiClient  network/{label}: {swNet.ElapsedMilliseconds}ms ({byteLength} bytes, {format})");

            var swDeser = System.Diagnostics.Stopwatch.StartNew();
            Task<T> deserializeTask = Task.Run(() => isMsgPack
                ? MsgPackDecoder.Deserialize<T>(rawData)
                : JsonConvert.DeserializeObject<T>(json));
            yield return Utils.WaitTask(deserializeTask);
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] ApiClient  deserialize/{label}: {swDeser.ElapsedMilliseconds}ms ({format})");
            if (isCanceled != null && isCanceled())
            {
                RenderWorkScheduler.CountEvent("api.bulkCanceled");
                yield break;
            }

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
        string json = JsonConvert.SerializeObject(payload, Formatting.Indented);

        using (ApiRequest call = Request("POST", path, json))
        {
            yield return call.Send();
            UnityWebRequest request = call.Web;

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
        string json = JsonConvert.SerializeObject(payload);

        using (ApiRequest call = Request("PATCH", path, json))
        {
            yield return call.Send();
            UnityWebRequest request = call.Web;

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

/// <summary>
/// ローカル API への1件の要求(<see cref="ApiClient.Request"/> で作る)。
/// 送るときに <see cref="ApiClient.ApplyHeaders"/> でヘッダーを付ける。
/// </summary>
public sealed class ApiRequest : IDisposable
{
    private readonly string method;
    private readonly string url;
    private readonly byte[] body;
    private readonly string contentType;
    private readonly int timeoutSeconds;
    private List<KeyValuePair<string, string>> extraHeaders;
    private UnityWebRequestAsyncOperation operation;

    /// <summary>最後に送った要求。<see cref="Send"/> の完了後に結果を読む。</summary>
    public UnityWebRequest Web { get; private set; }

    /// <summary>Send の isCanceled により通信を中断した。</summary>
    public bool Aborted { get; private set; }

    internal ApiRequest(string method, string url, byte[] body, string contentType, int timeoutSeconds)
    {
        this.method = method;
        this.url = url;
        this.body = body;
        this.contentType = contentType;
        this.timeoutSeconds = timeoutSeconds;
    }

    /// <summary>追加ヘッダー。</summary>
    public ApiRequest WithHeader(string name, string value)
    {
        (extraHeaders ??= new List<KeyValuePair<string, string>>()).Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    /// <summary>送信を始めるだけで待たない。複数の要求を並列に送るときは、全部 Begin してから1件ずつ Send で待つ。</summary>
    public ApiRequest Begin()
    {
        Web?.Dispose();
        Web = new UnityWebRequest(url, method, new DownloadHandlerBuffer(), body != null ? new UploadHandlerRaw(body) : null);
        if (body != null) Web.SetRequestHeader("Content-Type", contentType);
        if (timeoutSeconds > 0) Web.timeout = timeoutSeconds;
        if (extraHeaders != null)
        {
            foreach (var header in extraHeaders) Web.SetRequestHeader(header.Key, header.Value);
        }
        ApiClient.ApplyHeaders(Web);
        operation = Web.SendWebRequest();
        return this;
    }

    /// <summary>
    /// 完了まで待つ(未送信なら送る)。isCanceled が真になったら通信を中断して <see cref="Aborted"/> を立てる。
    /// </summary>
    public IEnumerator Send(Func<bool> isCanceled = null)
    {
        if (operation == null) Begin();
        if (isCanceled == null)
        {
            yield return operation;
            yield break;
        }
        // WaitUntil はスケジューラ上でネイティブ待機として扱われ、共通予算を消費しない。
        yield return new WaitUntil(() => operation.isDone || isCanceled());
        if (!operation.isDone)
        {
            Web.Abort();
            Aborted = true;
        }
    }

    public void Dispose()
    {
        Web?.Dispose();
        Web = null;
        operation = null;
    }
}
