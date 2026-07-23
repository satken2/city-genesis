using System;
using Newtonsoft.Json;

/// <summary>
/// IPC通信のエンベロープ構造体。
/// Tauri側の messages.rs Envelope と同じ構造。
/// </summary>
[Serializable]
public class IpcEnvelope
{
    public string version;
    public string request_id;
    public string timestamp;
    public string source;
    public string destination;
    public string @event;
    public object payload;

    private static int _requestIdCounter = 1;

    /// <summary>
    /// Unity（parent）からTauriへ送信するエンベロープを生成する。
    /// </summary>
    public static IpcEnvelope CreateOutgoing(string eventName, object payloadData)
    {
        return new IpcEnvelope
        {
            version = "1.0",
            request_id = $"msg-{_requestIdCounter++}",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            source = "parent",
            destination = "tauri",
            @event = eventName,
            payload = payloadData
        };
    }

    /// <summary>
    /// エンベロープをJSON文字列にシリアライズする。
    /// </summary>
    public string ToJson()
    {
        return JsonConvert.SerializeObject(this, Formatting.None);
    }

    /// <summary>
    /// JSON文字列からエンベロープをデシリアライズする。
    /// </summary>
    public static IpcEnvelope FromJson(string json)
    {
        return JsonConvert.DeserializeObject<IpcEnvelope>(json);
    }
}
