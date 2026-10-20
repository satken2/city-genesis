using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Loop 3D construction sounds at under-construction buildings/paths without changing economic state.
/// BuildingManager / PathManager register sites when synchronizing data caches,
/// independently of GameObjects so LOD regeneration/unloading has no effect.
/// Receive backend coordinates without FloatingOrigin displacement and add the offset during playback.
/// </summary>
public sealed class ConstructionAmbience : MonoBehaviour
{
    public enum Kind { Building = 0, Path = 1 }

    public const int MaxVoices = 8;
    public const float MinDistance = 15f, MaxDistance = 250f, RefreshSeconds = 0.25f, FadeSeconds = 0.5f;
    public const float Gain = 0.6f; // Base gain applied in addition to Tauri volume because construction sounds otherwise dominate other ambience.

    private sealed class Site
    {
        public Kind kind;
        public Vector3[] points;
        public Vector3 nearest;
        public float distance;
        public bool removed;
    }

    private sealed class Voice
    {
        public AudioSource source;
        public Site site;
        public float gain;
    }

    private static readonly Dictionary<long, Site> sites = new Dictionary<long, Site>();
    private static readonly string[] clipNames = { "construction-building", "construction-path" };
    private readonly AudioClip[] clips = new AudioClip[2];
    private readonly Voice[] voices = new Voice[MaxVoices];
    private readonly List<Site> candidates = new List<Site>();
    private Transform anchor;
    private IpcClient ipc;
    private float volume, nextRefresh;
    private bool running;

    public static int SiteCount => sites.Count;

    /// <summary>
    /// Register a site: one center point for buildings, a centerline polyline for paths.
    /// For existing IDs, replace only points and keep playback running, avoiding gaps/restarts on epoch re-registration.
    /// </summary>
    public static void Register(Kind kind, int id, IList<Vector3> points)
    {
        if (points == null || points.Count == 0) { Unregister(kind, id); return; }
        var copy = new Vector3[points.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = points[i];
        long key = Key(kind, id);
        if (sites.TryGetValue(key, out var site)) site.points = copy;
        else sites[key] = new Site { kind = kind, points = copy };
    }

    public static void Unregister(Kind kind, int id)
    {
        long key = Key(kind, id);
        if (!sites.TryGetValue(key, out var site)) return;
        site.removed = true;
        sites.Remove(key);
    }

    public static void Clear(Kind kind)
    {
        var keys = new List<long>();
        foreach (var pair in sites) if (pair.Value.kind == kind) { pair.Value.removed = true; keys.Add(pair.Key); }
        foreach (long key in keys) sites.Remove(key);
    }

    /// <summary>Nearest polyline point to from, used as the sound position so long roads remain audible at their ends.</summary>
    public static Vector3 NearestPoint(IList<Vector3> points, Vector3 from)
    {
        Vector3 best = points[0];
        float bestSqr = (from - best).sqrMagnitude;
        for (int i = 1; i < points.Count; i++)
        {
            Vector3 a = points[i - 1], b = points[i];
            Vector3 p = PolylineUtils.ClosestPointOnSegment(from, a, b);
            float sqr = (from - p).sqrMagnitude;
            if (sqr < bestSqr) { bestSqr = sqr; best = p; }
        }
        return best;
    }

    private static long Key(Kind kind, int id) => ((long)kind << 32) | (uint)id;

    private void Awake()
    {
        // An otherwise stationary root object's position represents FloatingOrigin's accumulated displacement.
        anchor = new GameObject("Construction ambience anchor").transform;
        for (int i = 0; i < clips.Length; i++)
        {
            clips[i] = Resources.Load<AudioClip>("Audio/Construction/" + clipNames[i]);
            if (clips[i] == null) Debug.LogError("ConstructionAmbience: 音源がありません: " + clipNames[i]);
        }
        for (int i = 0; i < voices.Length; i++)
        {
            // Keep sources as parentless roots. FloatingOrigin shifts every Transform individually, so parenting would apply offsets twice.
            var source = new GameObject("Construction voice " + i).AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = MinDistance;
            source.maxDistance = MaxDistance;
            source.dopplerLevel = 0f; // Rapid camera movement would cause pitch fluctuations if Doppler remained enabled.
            source.volume = 0f;
            voices[i] = new Voice { source = source };
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
        running = true;
        Vector3 offset = anchor.position;
        Vector3 listener = camera.transform.position - offset;
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh(listener);
        }
        float step = Time.unscaledDeltaTime / FadeSeconds;
        for (int i = 0; i < voices.Length; i++)
        {
            var voice = voices[i];
            if (voice.site == null) continue;
            bool wanted = !voice.site.removed && voice.site.distance < MaxDistance;
            voice.gain = Mathf.MoveTowards(voice.gain, wanted ? 1f : 0f, step);
            voice.source.transform.position = voice.site.nearest + offset;
            voice.source.volume = voice.gain * volume * Gain;
            if (!wanted && voice.gain <= 0f) { voice.source.Stop(); voice.site = null; }
        }
    }

    /// <summary>Select audible sites nearest-camera first and assign free sources, retaining assignments for playing sites.</summary>
    private void Refresh(Vector3 listener)
    {
        candidates.Clear();
        foreach (var site in sites.Values)
        {
            site.nearest = NearestPoint(site.points, listener);
            site.distance = Vector3.Distance(site.nearest, listener);
            if (site.distance < MaxDistance) candidates.Add(site);
        }
        candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
        if (candidates.Count > MaxVoices) candidates.RemoveRange(MaxVoices, candidates.Count - MaxVoices);
        for (int i = 0; i < voices.Length; i++)
        {
            var site = voices[i].site;
            if (site != null && !site.removed && !candidates.Contains(site)) site.distance = float.PositiveInfinity;
        }
        foreach (var site in candidates)
        {
            if (HasVoice(site)) continue;
            var voice = FreeVoice();
            if (voice == null) return;
            var clip = clips[(int)site.kind];
            if (clip == null) return;
            voice.site = site;
            voice.gain = 0f;
            voice.source.clip = clip;
            voice.source.transform.position = site.nearest + anchor.position;
            voice.source.volume = 0f;
            voice.source.time = Random.value * clip.length * 0.95f; // Vary playback start positions so nearby sites do not sound synchronized.
            voice.source.Play();
        }
    }

    private bool HasVoice(Site site)
    {
        foreach (var voice in voices) if (voice.site == site) return true;
        return false;
    }

    private Voice FreeVoice()
    {
        foreach (var voice in voices) if (voice.site == null) return voice;
        return null;
    }

    public void SetVolume(float value)
    {
        volume = float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
        foreach (var voice in voices) if (voice.source != null) voice.source.volume = voice.gain * volume * Gain;
    }

    /// <summary>Number of currently playing sources, for verification.</summary>
    public int PlayingCount
    {
        get { int n = 0; foreach (var voice in voices) if (voice.site != null && voice.source.isPlaying) n++; return n; }
    }

    private void StopAudio()
    {
        if (!running) return;
        // Root sources may be destroyed first during shutdown. Release assignments regardless of source lifetime.
        foreach (var voice in voices)
        {
            if (voice == null) continue;
            if (voice.source != null) { voice.source.Stop(); voice.source.volume = 0f; }
            voice.gain = 0f;
            voice.site = null;
        }
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
        foreach (var voice in voices) if (voice?.source != null) Destroy(voice.source.gameObject);
        if (anchor != null) Destroy(anchor.gameObject);
    }
}
