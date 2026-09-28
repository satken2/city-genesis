using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

/// <summary>
/// Steam 認証チケットを取得し、バックエンド(POST /api/v1/system/verify)へ送信する。
///
/// フロー:
///   1. バックエンドの起動を待つ(GET /sessions が応答するまでリトライ)。
///      既に検証済み(テスト環境等)ならスキップする。
///   2. SteamUser.GetAuthTicketForWebApi(identity) でチケットを要求し、
///      GetTicketForWebApiResponse_t コールバックで受け取る。
///   3. チケットを hex 文字列化してバックエンドへ POST する。
///   4. バックエンドが 401/403 を返した場合はチケット無効(確定失敗)として終了。
///      それ以外の失敗(接続不可・502・ローカル API の認証トークン拒否等)は一時障害として、チケットを
///      取り直した上でバックオフ再送する(チケットは使い捨てのため再利用しない)。
///
/// Steam 不在(SteamService.Initialized == false)の場合は何もしない。
/// その場合バックエンドは未検証のままとなり、既存のエポック制限が適用される。
/// </summary>
public class SteamAuthClient : MonoBehaviour
{
#if !DISABLESTEAMWORKS
    /// <summary>バックエンド起動待ちのポーリング間隔(秒)</summary>
    private const float BackendWaitIntervalSec = 3f;
    /// <summary>バックエンド起動待ちの最大時間(秒)</summary>
    private const float BackendWaitTimeoutSec = 60f;
    /// <summary>チケットコールバック待ちのタイムアウト(秒)</summary>
    private const float TicketCallbackTimeoutSec = 10f;
    /// <summary>再送の最大回数</summary>
    private const int MaxRetries = 10;
    /// <summary>バックオフの初期値と上限(秒)</summary>
    private const float BackoffInitialSec = 5f;
    private const float BackoffMaxSec = 60f;

    private Callback<GetTicketForWebApiResponse_t> _ticketCallback;
    private HAuthTicket _currentTicket = HAuthTicket.Invalid;
    private byte[] _ticketData;
    private bool _ticketReceived;
    private bool _ticketFailed;

    private void OnEnable()
    {
        _ticketCallback = Callback<GetTicketForWebApiResponse_t>.Create(OnTicketResponse);
    }

    private void OnDisable()
    {
        _ticketCallback?.Dispose();
        _ticketCallback = null;
        CancelCurrentTicket();
    }

    private void Start()
    {
        StartCoroutine(VerifyFlow());
    }

    private void OnTicketResponse(GetTicketForWebApiResponse_t response)
    {
        if (response.m_hAuthTicket != _currentTicket) return;

        if (response.m_eResult == EResult.k_EResultOK)
        {
            _ticketData = new byte[response.m_cubTicket];
            Array.Copy(response.m_rgubTicket, _ticketData, response.m_cubTicket);
            _ticketReceived = true;
        }
        else
        {
            Debug.LogWarning($"SteamAuthClient: チケット取得に失敗しました (result={response.m_eResult})");
            _ticketFailed = true;
        }
    }

    private void CancelCurrentTicket()
    {
        if (_currentTicket != HAuthTicket.Invalid)
        {
            try { SteamUser.CancelAuthTicket(_currentTicket); }
            catch (Exception) { /* Steam 終了後などは無視 */ }
            _currentTicket = HAuthTicket.Invalid;
        }
    }

    private IEnumerator VerifyFlow()
    {
        // SteamService の初期化(Awake)完了を1フレーム待つ
        yield return null;

        if (SteamService.Instance == null || !SteamService.Instance.Initialized)
        {
            Debug.Log("SteamAuthClient: Steam が利用できないためチケット検証をスキップします");
            yield break;
        }

        // --- 1. バックエンド起動待ち(既に検証済みならスキップ) ---
        bool backendReady = false;
        bool alreadyVerified = false;
        float waitDeadline = Time.realtimeSinceStartup + BackendWaitTimeoutSec;
        while (Time.realtimeSinceStartup < waitDeadline)
        {
            using (ApiRequest call = ApiClient.Request("GET", "/sessions", timeoutSeconds: 5))
            {
                yield return call.Send();
                UnityWebRequest req = call.Web;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    backendReady = true;
                    try
                    {
                        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(
                            req.downloadHandler.text);
                        if (data != null
                            && data.TryGetValue("verification_status", out var status)
                            && Convert.ToInt32(status) == Constants.GAME_VERIFIED)
                        {
                            alreadyVerified = true;
                        }
                    }
                    catch (Exception) { /* パース失敗時は未検証として進む */ }
                    break;
                }
            }
            yield return new WaitForSeconds(BackendWaitIntervalSec);
        }

        if (!backendReady)
        {
            Debug.LogWarning("SteamAuthClient: バックエンドに接続できないためチケット検証を中止します");
            yield break;
        }
        if (alreadyVerified)
        {
            Debug.Log("SteamAuthClient: 既に検証済みのためチケット送信をスキップします");
            yield break;
        }

        // --- 2〜4. チケット取得 → POST → 結果に応じて再送 ---
        string appId = SteamUtils.GetAppID().m_AppId.ToString();
        float backoff = BackoffInitialSec;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            // チケットは使い捨てのため毎回取り直す
            _ticketReceived = false;
            _ticketFailed = false;
            _ticketData = null;
            _currentTicket = SteamUser.GetAuthTicketForWebApi(Constants.STEAM_TICKET_IDENTITY);

            float ticketDeadline = Time.realtimeSinceStartup + TicketCallbackTimeoutSec;
            while (!_ticketReceived && !_ticketFailed && Time.realtimeSinceStartup < ticketDeadline)
            {
                yield return null;
            }

            if (!_ticketReceived)
            {
                Debug.LogWarning(
                    $"SteamAuthClient: チケット取得が{(_ticketFailed ? "失敗" : "タイムアウト")}しました。再試行します ({attempt + 1}/{MaxRetries})");
                CancelCurrentTicket();
                yield return new WaitForSeconds(backoff);
                backoff = Mathf.Min(backoff * 2f, BackoffMaxSec);
                continue;
            }

            string ticketHex = ToHex(_ticketData);
            var payload = new Dictionary<string, object>
            {
                ["app_id"] = appId,
                ["ticket"] = ticketHex,
                ["identity"] = Constants.STEAM_TICKET_IDENTITY,
            };

            bool requestDone = false;
            bool verified = false;
            bool definitiveFailure = false;

            yield return ApiClient.Post<Dictionary<string, object>>(
                "/system/verify",
                payload,
                onSuccess: _ =>
                {
                    verified = true;
                    requestDone = true;
                },
                onError: (code, error) =>
                {
                    // 401/403 はチケット無効の確定失敗(再送しない)。
                    // それ以外(0=接続不可, 502, 5xx)と、ローカル API の認証トークンの拒否は一時障害として再送する。
                    definitiveFailure = (code == 401 || code == 403) && !ApiClient.IsTokenRejected(code, error);
                    Debug.LogWarning($"SteamAuthClient: 検証リクエスト失敗 (HTTP {code}): {error}");
                    requestDone = true;
                });

            while (!requestDone) yield return null;

            // 検証完了後(成否問わず)はハンドルを解放してよい
            CancelCurrentTicket();

            if (verified)
            {
                Debug.Log("SteamAuthClient: Steam チケット検証が完了しました");
                yield break;
            }
            if (definitiveFailure)
            {
                Debug.LogWarning("SteamAuthClient: チケットが拒否されました。検証を中止します(エポック制限が適用されます)");
                yield break;
            }

            yield return new WaitForSeconds(backoff);
            backoff = Mathf.Min(backoff * 2f, BackoffMaxSec);
        }

        Debug.LogWarning($"SteamAuthClient: {MaxRetries} 回の再試行後も検証できませんでした(エポック制限が適用されます)");
    }

    private static string ToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (byte b in data)
        {
            sb.AppendFormat("{0:x2}", b);
        }
        return sb.ToString();
    }
#endif
}
