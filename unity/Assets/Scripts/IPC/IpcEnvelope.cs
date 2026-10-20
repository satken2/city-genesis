using System;
using Newtonsoft.Json;

/// <summary>
/// IPC envelope structure.
/// Matches Envelope in Tauri messages.rs.
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
    /// Creates an envelope sent from Unity (parent) to Tauri.
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
    /// Serializes an envelope to a JSON string.
    /// </summary>
    public string ToJson()
    {
        return JsonConvert.SerializeObject(this, Formatting.None);
    }

    /// <summary>
    /// Deserializes an envelope from a JSON string.
    /// </summary>
    public static IpcEnvelope FromJson(string json)
    {
        return JsonConvert.DeserializeObject<IpcEnvelope>(json);
    }
}
