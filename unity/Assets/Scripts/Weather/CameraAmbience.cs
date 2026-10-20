using UnityEngine;

/// <summary>Loop base, near-field, and rain ambience in Unity without changing economic state.</summary>
public sealed class CameraAmbience : MonoBehaviour
{
    public const float NearHeight = 10f, FarHeight = 100f;
    private readonly AudioSource[] sources = new AudioSource[5];
    private readonly float[] gains = new float[5];
    private IpcClient ipc;
    private float volume;
    private bool running;

    private void Awake()
    {
        string[] names = { "city-base", "drizzle", "rain", "heavy-rain", "city-ambience" };
        for (int i = 0; i < names.Length; i++)
        {
            var holder = new GameObject("Ambience " + names[i]);
            holder.transform.SetParent(transform, false);
            var source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.volume = 0f;
            source.clip = Resources.Load<AudioClip>("Audio/Ambience/" + names[i]);
            if (source.clip == null) Debug.LogError("Ambience: 音源がありません: " + names[i]);
            sources[i] = source;
        }
    }

    private void Update()
    {
        if (ipc != IpcClient.Instance)
        {
            if (ipc != null) ipc.OnAmbienceVolume -= SetVolume;
            ipc = IpcClient.Instance;
            if (ipc != null) ipc.OnAmbienceVolume += SetVolume;
        }
        var camera = Camera.main;
        bool active = camera != null && GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted();
        if (!active) { StopAudio(); return; }
        var position = camera.transform.position;
        float proximity = Proximity(RelativeHeight(position.y, Utils.GetGroundHeight(position)));
        float rain = WeatherController.Instance == null ? 0f : WeatherController.Instance.RainAmount;
        for (int i = 0; i < sources.Length; i++)
        {
            gains[i] = Mathf.MoveTowards(gains[i], TargetGain(i, proximity, rain), Time.unscaledDeltaTime / 1.5f);
            sources[i].volume = gains[i] * volume;
            // Preserve playback positions while muted; weather/altitude changes must not restart loops.
            if (!running && sources[i].clip != null) sources[i].Play();
        }
        running = true;
    }

    public void SetVolume(float value)
    {
        volume = float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
        for (int i = 0; i < sources.Length; i++)
            if (sources[i] != null) sources[i].volume = gains[i] * volume;
    }

    public static float Proximity(float? height)
    {
        if (!height.HasValue || float.IsNaN(height.Value) || float.IsInfinity(height.Value)) return 0f;
        float t = Mathf.InverseLerp(FarHeight, NearHeight, height.Value);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Rainfall is continuous from 0 to 3; blend adjacent rain-audio levels during weather transitions.</summary>
    public static float TargetGain(int channel, float proximity, float rain)
    {
        if (channel == 0) return 0.5f - 0.3f * proximity;
        if (channel == 4) return 0.7f * proximity;
        float share = Mathf.Clamp01(1f - Mathf.Abs(rain - channel));
        return share * (channel == 1 ? 0.4f : channel == 2 ? 0.6f : 0.8f);
    }

    public static float? RelativeHeight(float cameraY, float groundY)
    {
        float height = cameraY - groundY;
        return float.IsNaN(height) || float.IsInfinity(height) ? (float?)null : Mathf.Max(0f, height);
    }

    private void StopAudio()
    {
        if (!running) return;
        for (int i = 0; i < sources.Length; i++) { sources[i].Stop(); sources[i].volume = 0f; gains[i] = 0f; }
        running = false;
    }

    private void OnDisable()
    {
        StopAudio();
        if (ipc != null) ipc.OnAmbienceVolume -= SetVolume;
        ipc = null;
    }

    private void OnDestroy()
    {
        foreach (var source in sources) if (source != null) Destroy(source.gameObject);
    }
}
