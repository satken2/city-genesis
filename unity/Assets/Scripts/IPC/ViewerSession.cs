using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Viewer-only launch (--viewer). Connects to the same core separately from the working Unity/Tauri, only displaying the city
/// (e.g. recording with a fixed camera). MMF names are fixed with a single reader; a second Unity would steal messages
/// intended for the working instance, so the viewer never opens MMF. This class substitutes for Tauri,
/// observing core state and sending the same loading events. No edit commands arrive, so it never modifies the city
/// or saves viewpoints to the core (CameraController).
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

    /// <summary>Opens viewer transport and creates this component as its peer.</summary>
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
    /// Reload if core session_id/epoch differs from the display. City changes use Tauri's initial loading sequence;
    /// epoch changes use post-epoch reload. Do not read while an epoch is running, as city data is being modified.
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
/// Viewer-only transport, paired with in-process ViewerSession. Returns Tauri-equivalent replies only to the two Unity messages
/// requiring responses; discards others without listeners. With no start button, show the view as soon as rendering is ready.
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
