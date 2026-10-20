using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// Shared API client for consistent HTTP request handling.
/// Route all local API requests through this client (a standard method such as <see cref="Get{T}"/> or <see cref="Request"/>).
/// </summary>
public static class ApiClient
{
    /// <summary>
    /// Base URL of the backend API.
    /// Can be overridden at startup with the --api-base-url command-line argument.
    /// Public so Editor scripts and other external callers can access it.
    /// </summary>
    public static string BASE_URL = "http://127.0.0.1:5000/api/v1";

    // Core connectivity and compatibility checked by Tauri (system:backend_connection, received at Unity startup and on changes), plus Unity's own connectivity check.
    // While either connection is down, retain the display but block input and write requests.
    public static bool UiBackendConnected { get; set; } = true;
    public static bool HealthBackendConnected { get; set; } = true;
    public static bool BackendConnected => UiBackendConnected && HealthBackendConnected;

    /// <summary>
    /// Create a request that does not fit the standard Get/GetBulk/Post/Patch methods, without sending it yet.
    /// path is relative to BASE_URL (including the query string); json, if provided, becomes the request body.
    /// Send and wait with <see cref="ApiRequest.Send"/>, then read <see cref="ApiRequest.Web"/>. Dispose with using.
    /// </summary>
    public static ApiRequest Request(string method, string path, string json = null, int timeoutSeconds = 0)
    {
        return new ApiRequest(method, $"{BASE_URL}{path}", json != null ? Encoding.UTF8.GetBytes(json) : null, "application/json", timeoutSeconds);
    }

    /// <summary>
    /// Create an unsent request with a binary body (such as an image) and contentType. Sending and reading results work as in <see cref="Request"/>.
    /// </summary>
    public static ApiRequest Request(string method, string path, byte[] body, string contentType, int timeoutSeconds = 0)
    {
        return new ApiRequest(method, $"{BASE_URL}{path}", body, contentType, timeoutSeconds);
    }

    /// <summary>
    /// Send a GET request.
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
    /// Accept header for bulk-fetch endpoints.
    /// The backend's bulk bbox endpoints (buildings/paths/parcels/zones/nodes)
    /// use this to return MessagePack binary data (unsupported endpoints still return JSON).
    /// Identify the response format from its Content-Type.
    /// </summary>
    private const string MSGPACK_MEDIA_TYPE = "application/x-msgpack";
    private const string BULK_ACCEPT_HEADER = MSGPACK_MEDIA_TYPE + ", application/json";

    /// <summary>
    /// Send a GET request for bulk fetches with large responses, such as bbox queries.
    /// Differences from Get&lt;T&gt;:
    /// - Request MessagePack via the Accept header and receive binary data from supported endpoints
    ///   (faster than parsing JSON text; decoded by MsgPackDecoder). Unsupported endpoints
    ///   return JSON, so callers do not need to distinguish the formats.
    /// - Deserialize on a background thread through BackgroundWork,
    ///   avoiding main-thread blocking and frame hitches even for responses of several hundred KB.
    /// - Set timeout and Cache-Control: no-cache by default.
    /// - When UNITY_LOG_TIMING is enabled, log network and deserialize timings separately.
    /// All managers must use this shared request entry point for merged-BBOX bulk loading.
    /// </summary>
    /// <param name="path">Path relative to BASE_URL, including the query string.</param>
    /// <param name="onSuccess">Callback receiving the deserialized result on success.</param>
    /// <param name="onError">Callback for network or deserialization failures; errors have already been logged.</param>
    /// <param name="timingLabel">TIMING log label, e.g. "PathManager nodes". Defaults to path.</param>
    /// <param name="timeoutSeconds">Request timeout in seconds.</param>
    /// <param name="isCanceled">When true, abort the request without decoding, callbacks, or logging.
    /// Neither onSuccess nor onError runs on cancellation, so callers must check their own cancellation state after yielding.</param>
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

            // Identify the response format from Content-Type (whether the endpoint supports msgpack).
            string contentType = request.GetResponseHeader("Content-Type") ?? "";
            bool isMsgPack = contentType.Contains(MSGPACK_MEDIA_TYPE);
            byte[] rawData = isMsgPack ? request.downloadHandler.data : null;
            string json = isMsgPack ? null : request.downloadHandler.text;

            int byteLength = isMsgPack ? rawData?.Length ?? 0 : (int)request.downloadedBytes;
            string format = isMsgPack ? "msgpack" : "json";
            UnityLog.If(LocalConstants.UNITY_LOG_TIMING, () => $"[TIMING] ApiClient  network/{label}: {swNet.ElapsedMilliseconds}ms ({byteLength} bytes, {format})");

            var swDeser = System.Diagnostics.Stopwatch.StartNew();
            Task<T> deserializeTask = BackgroundWork.Run(() => isMsgPack
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
                // Utils.WaitTask has already logged the exception details. Include the response prefix for diagnosis.
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
    /// Send a POST request.
    /// </summary>
    public static IEnumerator Post<T>(string path, object payload, Action<T> onSuccess, Action<long, string> onError = null)
    {
        if (!BackendConnected) { onError?.Invoke(503, "BACKEND_DISCONNECTED"); yield break; }
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
    /// Send a PATCH request.
    /// </summary>
    public static IEnumerator Patch(string path, object payload, Action onSuccess, Action<long, string> onError = null)
    {
        if (!BackendConnected) { onError?.Invoke(503, "BACKEND_DISCONNECTED"); yield break; }
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
/// A single local API request, created with <see cref="ApiClient.Request"/>.
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

    /// <summary>Last request sent. Read the result after <see cref="Send"/> completes.</summary>
    public UnityWebRequest Web { get; private set; }

    /// <summary>The request was aborted by Send's isCanceled callback.</summary>
    public bool Aborted { get; private set; }

    internal ApiRequest(string method, string url, byte[] body, string contentType, int timeoutSeconds)
    {
        this.method = method;
        this.url = url;
        this.body = body;
        this.contentType = contentType;
        this.timeoutSeconds = timeoutSeconds;
    }

    /// <summary>Additional headers.</summary>
    public ApiRequest WithHeader(string name, string value)
    {
        (extraHeaders ??= new List<KeyValuePair<string, string>>()).Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    /// <summary>Start sending without waiting. To send requests in parallel, Begin all of them before awaiting each with Send.</summary>
    public ApiRequest Begin()
    {
        // Do not automatically retry writes whose responses were lost. Also block direct Request calls before sending.
        if (!ApiClient.BackendConnected && method != "GET" && method != "HEAD" && method != "OPTIONS")
            throw new InvalidOperationException("BACKEND_DISCONNECTED");
        Web?.Dispose();
        Web = new UnityWebRequest(url, method, new DownloadHandlerBuffer(), body != null ? new UploadHandlerRaw(body) : null);
        if (body != null) Web.SetRequestHeader("Content-Type", contentType);
        if (timeoutSeconds > 0) Web.timeout = timeoutSeconds;
        if (extraHeaders != null)
        {
            foreach (var header in extraHeaders) Web.SetRequestHeader(header.Key, header.Value);
        }
        operation = Web.SendWebRequest();
        return this;
    }

    /// <summary>
    /// Wait for completion, sending first if needed. If isCanceled becomes true, abort and set <see cref="Aborted"/>.
    /// </summary>
    public IEnumerator Send(Func<bool> isCanceled = null)
    {
        if (operation == null) Begin();
        if (isCanceled == null)
        {
            yield return operation;
            yield break;
        }
        // The scheduler treats WaitUntil as a native wait, so it does not consume the shared budget.
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
