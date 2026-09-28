using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

/// <summary>
/// HTTPリクエストを統一的に処理する共通APIクライアント。
/// バックエンドは通信契約ヘッダーと認証トークンの無い要求を拒否する(401)ため、
/// ローカル API への要求はすべてここ(<see cref="Get{T}"/> などの定型か <see cref="Request"/>)を通す。
/// UnityWebRequest を直接作るとヘッダーが付かない(本番の死活監視 AppRoot.BackendHealthPolling なら 401 続きでアプリが終了する)。
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
    public const string TOKEN_HEADER = "X-City-Genesis-Token";

    /// <summary>トークン欠落・不一致の 401 応答に入るコード。エンドポイント自身の 401(Steam チケット無効など)と区別するのに使う。</summary>
    public const string TOKEN_REJECTED_CODE = "API_TOKEN_REQUIRED";

    private const string TOKEN_ENV = "GENESIS_API_TOKEN";
    private const string TOKEN_FILE_NAME = "api_token";
    private const string APP_DATA_DIR_NAME = "UrbanGenesis";

    private static string apiToken;
    private static bool apiTokenResolved;

    #region 認証トークン

    /// <summary>
    /// 通信契約ヘッダーと認証トークンを付け、付けたトークン(見つからなければ null)を返す。
    /// 要求の組み立ては <see cref="ApiRequest"/> が行うので、呼び出し元は <see cref="Request"/> を使う。
    /// </summary>
    internal static string ApplyHeaders(UnityWebRequest request)
    {
        request.SetRequestHeader(CONTRACT_HEADER, FrontendVersion.ApiContract.ToString());
        if (!apiTokenResolved)
        {
            apiToken = ResolveApiToken();
            apiTokenResolved = true;
        }
        if (apiToken != null) request.SetRequestHeader(TOKEN_HEADER, apiToken);
        return apiToken;
    }

    /// <summary>
    /// 401 を受けたときに呼ぶ。トークンを解決し直し、拒否されたものと違う値が得られたら true(作り直して再送してよい)を返す。
    /// バックエンドは起動のたびにトークンを作り直すので、開発時にバックエンドだけ再起動しても追従できる。
    /// 並列の要求が同時に 401 を受けても、先に解決し直した結果を共有してファイルを読み直さない。
    /// </summary>
    internal static bool RefreshApiToken(string rejectedToken)
    {
        if (!apiTokenResolved || string.Equals(apiToken, rejectedToken, StringComparison.Ordinal))
        {
            apiToken = ResolveApiToken();
            apiTokenResolved = true;
        }
        return apiToken != null && !string.Equals(apiToken, rejectedToken, StringComparison.Ordinal);
    }

    /// <summary>応答が認証トークンの拒否(エンドポイント自身の判断ではない 401)かどうか。</summary>
    public static bool IsTokenRejected(long responseCode, string responseBody)
    {
        return responseCode == 401 && responseBody != null && responseBody.Contains(TOKEN_REJECTED_CODE);
    }

    /// <summary>
    /// 環境変数 GENESIS_API_TOKEN → ユーザーフォルダの api_token の順で探す。
    /// 製品では genesis.py が環境変数で子プロセスへ渡す。手動で起動した開発中の Unity は、
    /// バックエンドが起動時に書くファイルから読む(Webページはローカルファイルを読めないので CSRF 対策が成り立つ)。
    /// </summary>
    private static string ResolveApiToken()
    {
        string fromEnv = NormalizeToken(Environment.GetEnvironmentVariable(TOKEN_ENV));
        if (fromEnv != null) return fromEnv;

        string path = Path.Combine(GetAppDataDir(), TOKEN_FILE_NAME);
        try
        {
            return File.Exists(path) ? NormalizeToken(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Debug.LogWarning($"ApiClient: API トークンのファイルを読めません ({path}): {e.Message}");
            return null;
        }
    }

    /// <summary>前後の空白を除く。空や、HTTP ヘッダーに載らない文字を含む値は無いものとして扱う。</summary>
    private static string NormalizeToken(string raw)
    {
        string token = raw?.Trim();
        if (string.IsNullOrEmpty(token)) return null;
        foreach (char c in token)
        {
            if (c < '!' || c > '~')
            {
                Debug.LogWarning("ApiClient: API トークンに使えない文字が含まれているため無視します");
                return null;
            }
        }
        return token;
    }

    /// <summary>コアの consts._get_appdata_dir と同じ規則で決めるユーザーフォルダ。ずれるとトークンファイルを見つけられない。</summary>
    private static string GetAppDataDir()
    {
        string baseDir;
        switch (Application.platform)
        {
            case RuntimePlatform.WindowsPlayer:
            case RuntimePlatform.WindowsEditor:
                baseDir = NonEmptyEnv("LOCALAPPDATA") ?? NonEmptyEnv("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                break;
            case RuntimePlatform.OSXPlayer:
            case RuntimePlatform.OSXEditor:
                baseDir = Path.Combine(HomeDir(), "Library", "Application Support");
                break;
            default:
                baseDir = NonEmptyEnv("XDG_DATA_HOME") ?? Path.Combine(HomeDir(), ".local", "share");
                break;
        }
        return Path.Combine(baseDir, APP_DATA_DIR_NAME);
    }

    private static string NonEmptyEnv(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string HomeDir()
    {
        return NonEmptyEnv("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    #endregion

    /// <summary>
    /// 定型(Get/GetBulk/Post/Patch)に当てはまらない要求を作る(まだ送らない)。
    /// path は BASE_URL からの相対パス(クエリ文字列含む)、json を渡すと本文として送る。
    /// <see cref="ApiRequest.Send"/> で送って待ち、結果は <see cref="ApiRequest.Web"/> から読む。using で破棄すること。
    /// </summary>
    public static ApiRequest Request(string method, string path, string json = null, int timeoutSeconds = 0)
    {
        return new ApiRequest(method, $"{BASE_URL}{path}", json, timeoutSeconds);
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
/// 送るたびに <see cref="ApiClient.ApplyHeaders"/> でヘッダーを付け、401 を受けてトークンを解決し直せたら
/// 1回だけ作り直して再送する(UnityWebRequest は再送できないため、送信のたびに作り直す)。
/// </summary>
public sealed class ApiRequest : IDisposable
{
    private readonly string method;
    private readonly string url;
    private readonly byte[] body;
    private readonly int timeoutSeconds;
    private List<KeyValuePair<string, string>> extraHeaders;
    private UnityWebRequestAsyncOperation operation;
    private string sentToken;
    private bool retried;

    /// <summary>最後に送った要求。<see cref="Send"/> の完了後に結果を読む。</summary>
    public UnityWebRequest Web { get; private set; }

    /// <summary>Send の isCanceled により通信を中断した。</summary>
    public bool Aborted { get; private set; }

    internal ApiRequest(string method, string url, string json, int timeoutSeconds)
    {
        this.method = method;
        this.url = url;
        body = json != null ? Encoding.UTF8.GetBytes(json) : null;
        this.timeoutSeconds = timeoutSeconds;
    }

    /// <summary>再送時にも付け直す追加ヘッダー。</summary>
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
        if (body != null) Web.SetRequestHeader("Content-Type", "application/json");
        if (timeoutSeconds > 0) Web.timeout = timeoutSeconds;
        if (extraHeaders != null)
        {
            foreach (var header in extraHeaders) Web.SetRequestHeader(header.Key, header.Value);
        }
        sentToken = ApiClient.ApplyHeaders(Web);
        operation = Web.SendWebRequest();
        return this;
    }

    /// <summary>
    /// 完了まで待つ(未送信なら送る)。isCanceled が真になったら通信を中断して <see cref="Aborted"/> を立てる。
    /// </summary>
    public IEnumerator Send(Func<bool> isCanceled = null)
    {
        if (operation == null) Begin();
        while (true)
        {
            if (isCanceled == null) yield return operation;
            else
            {
                // WaitUntil はスケジューラ上でネイティブ待機として扱われ、共通予算を消費しない。
                yield return new WaitUntil(() => operation.isDone || isCanceled());
                if (!operation.isDone)
                {
                    Web.Abort();
                    Aborted = true;
                    yield break;
                }
            }
            if (!RetryIfUnauthorized()) yield break;
        }
    }

    /// <summary>
    /// 完了した要求が 401 で、トークンを解決し直せたら作り直して送り直す(1回だけ)。送り直したら true。
    /// コルーチンで待たない呼び出し元(エディタ拡張の Update でのポーリングなど)が完了を検知したときに呼ぶ。
    /// </summary>
    public bool RetryIfUnauthorized()
    {
        if (retried || Web == null || Web.responseCode != 401 || !ApiClient.RefreshApiToken(sentToken)) return false;
        retried = true;
        Begin();
        return true;
    }

    public void Dispose()
    {
        Web?.Dispose();
        Web = null;
        operation = null;
    }
}
