using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// 観察専用の起動(起動引数 --viewer)。作業用の Unity・Tauri とは別に同じコアへつなぎ、都市を見せるだけの画面にする
/// (定点のカメラで録画するときなどに使う)。MMF は名前が固定で読み手を1つしか持てず、2つ目の Unity が開くと
/// 作業側宛てのメッセージを奪ってしまうため、観察側は MMF を開かない。代わりにこのクラスが Tauri の役を務め、
/// コアの状態を見て Tauri と同じイベントでロードの手順を送る。編集の指示は来ないので都市を書き換えず、
/// 視点もコアへ保存しない(CameraController)。
/// </summary>
public class ViewerSession : MonoBehaviour
{
    public static readonly bool Enabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--viewer") >= 0;

    private const float POLL_SECONDS = 2f;

    private ViewerIpcTransport transport;
    private string shownSessionId;
    private int shownEpoch;
    private int loadingId;

    private class SessionResponse
    {
        public string current_save_id;
        public string session_id;
    }

    private class NavigationStatusResponse
    {
        public bool busy;
    }

    private class CityResponse
    {
        public CityProps city;
    }

    private class CityProps
    {
        public int epoch;
    }

    /// <summary>観察用の送受信路を開き、その相手役としてこのコンポーネントを置く。</summary>
    public static IIpcTransport OpenTransport()
    {
        var go = new GameObject("ViewerSession");
        DontDestroyOnLoad(go);
        var session = go.AddComponent<ViewerSession>();
        session.transport = new ViewerIpcTransport();
        return session.transport;
    }

    private IEnumerator Start()
    {
        yield return new WaitUntil(() => IpcClient.Instance != null && IpcClient.Instance.ProtocolCompatible);
        while (true)
        {
            yield return Poll();
            yield return new WaitForSecondsRealtime(POLL_SECONDS);
        }
    }

    /// <summary>
    /// コアの都市(session_id)とエポックが表示中のものと違えば読み直させる。都市が変わったら Tauri の初期ロードと同じ手順、
    /// エポックだけ進んだらエポック実行後と同じ再読込。エポックの実行中は都市が書き換わっている途中なので読まない。
    /// </summary>
    private IEnumerator Poll()
    {
        SessionResponse session = null;
        yield return ApiClient.Get<SessionResponse>("/sessions", response => session = response);
        if (session == null) yield break;
        string sessionId = session.current_save_id != null ? session.session_id : null;
        if (sessionId == null)
        {
            if (shownSessionId != null) transport.PostAction("game:end");
            shownSessionId = null;
            yield break;
        }

        NavigationStatusResponse status = null;
        yield return ApiClient.Get<NavigationStatusResponse>("/navigation/status", response => status = response);
        if (status == null || status.busy) yield break;
        CityResponse city = null;
        yield return ApiClient.Get<CityResponse>("/city/", response => city = response);
        if (city?.city == null) yield break;

        if (sessionId != shownSessionId)
        {
            transport.PostAction("game:loading");
            transport.PostAction("session:reload");
            transport.PostAction("game:start", new Dictionary<string, object> { ["loading_id"] = ++loadingId });
        }
        else if (city.city.epoch != shownEpoch)
        {
            transport.PostAction("session:reload");
        }
        shownSessionId = sessionId;
        shownEpoch = city.city.epoch;
    }
}

/// <summary>
/// 観察専用の送受信路。相手は同じプロセスの ViewerSession で、Unity の送信のうち Tauri の応答が要る2つにだけ
/// Tauri と同じ応答を返し、ほかは受け手がいないので捨てる。開始ボタンが無いので、描画の準備ができたらすぐ表示させる。
/// </summary>
internal sealed class ViewerIpcTransport : IIpcTransport
{
    private readonly ConcurrentQueue<string> received = new ConcurrentQueue<string>();

    public event Action<string> OnMessageReceived;

    public void Send(string json)
    {
        IpcEnvelope envelope = IpcEnvelope.FromJson(json);
        switch (envelope.@event)
        {
            case "system:graphic_ready":
                Post("system:ready", new Dictionary<string, object>
                {
                    ["ipc_contract_version"] = FrontendVersion.IpcContract,
                    ["accepted"] = (bool?)JObject.FromObject(envelope.payload)["core_compatible"] == true,
                });
                break;
            case "game:loaded":
                PostAction("game:reveal", new Dictionary<string, object> { ["loading_id"] = (int)JObject.FromObject(envelope.payload)["loading_id"] });
                break;
        }
    }

    public void PostAction(string action, Dictionary<string, object> parameters = null)
    {
        Post("ui:action", new Dictionary<string, object>
        {
            ["action"] = action,
            ["params"] = parameters ?? new Dictionary<string, object>(),
        });
    }

    private void Post(string eventName, object payload)
    {
        var envelope = new IpcEnvelope { version = "1.0", source = "tauri", destination = "parent", @event = eventName, payload = payload };
        received.Enqueue(envelope.ToJson());
    }

    public void ProcessReceivedMessages()
    {
        while (received.TryDequeue(out string message))
        {
            try
            {
                OnMessageReceived?.Invoke(message);
            }
            catch (Exception e)
            {
                Debug.LogError($"ViewerIpcTransport: メッセージ処理エラー: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        while (received.TryDequeue(out _)) { }
    }
}
