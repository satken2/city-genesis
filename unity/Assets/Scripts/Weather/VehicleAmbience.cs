using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Loop 3D running sounds at moving cars, buses, and trains without changing economic state.
/// VehicleManager / TransitVehicleManager report emitter positions and speeds through <see cref="Report"/> each frame;
/// emitters disappear automatically when reports stop. Use reported world coordinates directly, matching vehicle GameObjects.
/// </summary>
public sealed class VehicleAmbience : MonoBehaviour
{
    /// <summary>Car and Bus share the same engine-sound source; only reporters differ.</summary>
    public enum Kind { Car = 0, Bus = 1, Train = 2 }

    /// <summary>Train sound layers, named by clips in Resources/Audio/Vehicles. Empty layers remain silent.</summary>
    public sealed class TrainSound
    {
        public string wheel; // Wheel sounds, also using different-tempo {wheel}-slow / {wheel}-fast clips.
        public string motor;
    }

    public const int CarVoices = 6, TrainVoices = 3;
    public const float CarMinDistance = 5f, CarMaxDistance = 120f, TrainMinDistance = 10f, TrainMaxDistance = 400f;
    public const float RefreshSeconds = 0.25f, FadeSeconds = 0.3f, StaleSeconds = 1f;
    private const string CarClip = "vehicle-car";

    private sealed class Emitter
    {
        public Kind kind;
        public TrainSound sound;
        public Vector3 position;
        public float speed, reported, distance;
        public bool removed;
    }

    private sealed class Voice
    {
        public AudioSource source;
        public AudioSource motor, slow, fast;
        public float tempo;
        public Emitter emitter;
        public float gain;
    }

    private static readonly Dictionary<(Kind, long), Emitter> emitters = new Dictionary<(Kind, long), Emitter>();
    private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
    private readonly Voice[] carVoices = new Voice[CarVoices], trainVoices = new Voice[TrainVoices];
    private readonly List<Emitter> candidates = new List<Emitter>();
    private readonly List<(Kind, long)> stale = new List<(Kind, long)>();
    private IpcClient ipc;
    private float volume, nextRefresh;
    private bool running;

    public static int EmitterCount => emitters.Count;

    /// <summary>
    /// Call every frame to register new emitters or update existing positions/speeds. Only Train uses sound.
    /// If a sound set changes, reregister as a different emitter and crossfade playback.
    /// </summary>
    public static void Report(Kind kind, long id, Vector3 position, float speed, TrainSound sound = null)
    {
        var key = (kind, id);
        if (!emitters.TryGetValue(key, out var emitter) || emitter.sound != sound)
        {
            if (emitter != null) emitter.removed = true;
            emitters[key] = emitter = new Emitter { kind = kind, sound = sound };
        }
        emitter.position = position;
        emitter.speed = float.IsNaN(speed) || float.IsInfinity(speed) ? 0f : Mathf.Max(0f, speed);
        emitter.reported = Time.unscaledTime;
    }

    public static void Remove(Kind kind, long id)
    {
        var key = (kind, id);
        if (!emitters.TryGetValue(key, out var emitter)) return;
        emitter.removed = true;
        emitters.Remove(key);
    }

    public static void Clear(Kind kind)
    {
        var keys = new List<(Kind, long)>();
        foreach (var pair in emitters) if (pair.Value.kind == kind) { pair.Value.removed = true; keys.Add(pair.Key); }
        foreach (var key in keys) emitters.Remove(key);
    }

    /// <summary>Engine pitch: idle while stopped, maximum RPM at 20m/s or above.</summary>
    public static float CarPitch(float speed) => 0.9f + 0.5f * Mathf.Clamp01(speed / 20f);

    /// <summary>Silence wheel running sounds while stopped; retain motor idle in a separate layer.</summary>
    public static float TrainGain(float speed) => 0.75f * Mathf.Clamp01(speed / 8f);
    public static float TrainMotorGain(float speed) => Mathf.Lerp(0.18f, 0.55f, Mathf.Clamp01(speed / 20f));
    // Blend running recordings with different tempos at constant pitch. Only motors also change pitch through AudioSource.pitch.
    public static float TrainTempo(float speed) => Mathf.Lerp(0.65f, 1.5f, Mathf.Clamp01(speed / 30f));
    public static float TrainMotorPitch(float speed) => Mathf.Lerp(0.8f, 2f, Mathf.Clamp01(speed / 30f));

    private static bool IsTrain(Kind kind) => kind == Kind.Train;

    private void Awake()
    {
        for (int i = 0; i < carVoices.Length; i++) carVoices[i] = CreateVoice("Vehicle voice car " + i, false);
        for (int i = 0; i < trainVoices.Length; i++)
        {
            trainVoices[i] = CreateVoice("Vehicle voice train " + i, true);
            trainVoices[i].slow = CreateVoice("Vehicle rolling slow " + i, true).source;
            trainVoices[i].fast = CreateVoice("Vehicle rolling fast " + i, true).source;
            trainVoices[i].motor = CreateVoice("Vehicle motor train " + i, true).source;
        }
    }

    private static Voice CreateVoice(string name, bool train)
    {
        float minDistance = train ? TrainMinDistance : CarMinDistance;
        float maxDistance = train ? TrainMaxDistance : CarMaxDistance;
        // Keep sources as parentless roots. FloatingOrigin shifts every Transform individually, so parenting would apply offsets twice.
        var source = new GameObject(name).AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Custom;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        // Normalize distance with maximum range at 1. Retain near-field impact while leaving only faint low-frequency sound far away.
        float near = minDistance / maxDistance;
        // Treat trains as large emitters audible at distance, controlling volume range independently of high-frequency loss.
        var volumeCurve = train ? new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(near, 1f),
            new Keyframe(30f / maxDistance, 0.8f), new Keyframe(50f / maxDistance, 0.65f),
            new Keyframe(100f / maxDistance, 0.4f), new Keyframe(200f / maxDistance, 0.18f),
            new Keyframe(300f / maxDistance, 0.07f), new Keyframe(1f, 0f)) : new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(near, 1f),
            new Keyframe(near * 3f, 0.35f), new Keyframe(near * 5f, 0.2f),
            new Keyframe(near * 10f, 0.08f), new Keyframe(near * 20f, 0.02f), new Keyframe(1f, 0f));
        source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, volumeCurve);
        var lowPass = source.gameObject.AddComponent<AudioLowPassFilter>();
        lowPass.lowpassResonanceQ = 1f;
        // Frequency is in Hz. Let Unity's distance curves follow both source and listener movement.
        var cutoffCurve = new AnimationCurve(
            new Keyframe(0f, 22000f), new Keyframe(near, 22000f),
            new Keyframe(near * 3f, 14000f), new Keyframe(near * 5f, 10000f),
            new Keyframe(near * 10f, 6000f), new Keyframe(near * 20f, 3500f), new Keyframe(1f, 2500f));
        if (train) cutoffCurve.AddKey(new Keyframe(300f / maxDistance, 2500f));
        lowPass.customCutoffCurve = cutoffCurve;
        source.dopplerLevel = 0f; // Rapid camera movement would cause pitch fluctuations if Doppler remained enabled.
        source.volume = 0f;
        return new Voice { source = source };
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
        Vector3 listener = camera.transform.position;
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            DropStale();
            Refresh(carVoices, listener, false, CarMaxDistance);
            Refresh(trainVoices, listener, true, TrainMaxDistance);
        }
        float step = Time.unscaledDeltaTime / FadeSeconds;
        Drive(carVoices, listener, step, CarMaxDistance);
        Drive(trainVoices, listener, step, TrainMaxDistance);
    }

    /// <summary>Remove emitters whose reports stopped, covering trains leaving range and vehicles disappearing without Remove.</summary>
    private void DropStale()
    {
        stale.Clear();
        foreach (var pair in emitters) if (Time.unscaledTime - pair.Value.reported > StaleSeconds) stale.Add(pair.Key);
        foreach (var key in stale) { emitters[key].removed = true; emitters.Remove(key); }
    }

    /// <summary>Select audible emitters nearest-camera first and assign free sources, retaining assignments for playing emitters.</summary>
    private void Refresh(Voice[] voices, Vector3 listener, bool train, float maxDistance)
    {
        candidates.Clear();
        foreach (var emitter in emitters.Values)
        {
            if (IsTrain(emitter.kind) != train) continue;
            emitter.distance = Vector3.Distance(emitter.position, listener);
            if (emitter.distance < maxDistance) candidates.Add(emitter);
        }
        candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
        if (candidates.Count > voices.Length) candidates.RemoveRange(voices.Length, candidates.Count - voices.Length);
        foreach (var voice in voices)
        {
            var emitter = voice.emitter;
            if (emitter != null && !emitter.removed && !candidates.Contains(emitter)) emitter.distance = float.PositiveInfinity;
        }
        foreach (var emitter in candidates)
        {
            if (HasVoice(voices, emitter)) continue;
            var voice = FreeVoice(voices);
            if (voice == null) return;
            voice.emitter = emitter;
            voice.gain = 0f;
            voice.tempo = TrainTempo(emitter.speed);
            // Vary start positions to avoid synchronized nearby vehicles. Different-tempo wheel layers start at the same phase for blending.
            float phase = Random.value * 0.95f;
            if (!train) { StartLayer(voice.source, Clip(CarClip), emitter.position, CarPitch(emitter.speed), phase); continue; }
            string wheel = emitter.sound?.wheel;
            bool hasWheel = !string.IsNullOrEmpty(wheel);
            StartLayer(voice.source, hasWheel ? Clip(wheel) : null, emitter.position, 1f, phase);
            StartLayer(voice.slow, hasWheel ? Clip(wheel + "-slow") : null, emitter.position, 1f, phase);
            StartLayer(voice.fast, hasWheel ? Clip(wheel + "-fast") : null, emitter.position, 1f, phase);
            StartLayer(voice.motor, Clip(emitter.sound?.motor), emitter.position, TrainMotorPitch(emitter.speed), Random.value * 0.95f);
        }
    }

    /// <summary>Load a named clip. Empty names represent silent layers.</summary>
    private AudioClip Clip(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!clips.TryGetValue(name, out var clip))
        {
            clip = Resources.Load<AudioClip>("Audio/Vehicles/" + name);
            if (clip == null) Debug.LogError("VehicleAmbience: 音源がありません: " + name);
            clips[name] = clip;
        }
        return clip;
    }

    /// <summary>Each frame, follow emitters with sources and apply speed-dependent pitch, gain, and fades.</summary>
    private void Drive(Voice[] voices, Vector3 listener, float step, float maxDistance)
    {
        foreach (var voice in voices)
        {
            var emitter = voice.emitter;
            if (emitter == null) continue;
            bool wanted = !emitter.removed && emitter.distance < maxDistance;
            voice.gain = Mathf.MoveTowards(voice.gain, wanted ? 1f : 0f, step);
            voice.source.transform.position = emitter.position;
            bool train = IsTrain(emitter.kind);
            voice.source.pitch = train ? 1f : CarPitch(emitter.speed);
            voice.tempo = Mathf.MoveTowards(voice.tempo, TrainTempo(emitter.speed), Time.unscaledDeltaTime * 0.8f);
            if (voice.slow != null) voice.slow.transform.position = emitter.position;
            if (voice.fast != null) voice.fast.transform.position = emitter.position;
            if (voice.motor != null)
            {
                voice.motor.transform.position = emitter.position;
                voice.motor.pitch = Mathf.MoveTowards(voice.motor.pitch, TrainMotorPitch(emitter.speed), Time.unscaledDeltaTime * 0.8f);
            }
            Apply(voice);
            if (!wanted && voice.gain <= 0f) Stop(voice);
        }
    }

    /// <summary>phase is the starting position as a fraction of clip length.</summary>
    private static void StartLayer(AudioSource layer, AudioClip clip, Vector3 position, float pitch, float phase)
    {
        if (layer == null || clip == null) return;
        layer.clip = clip;
        layer.transform.position = position;
        layer.pitch = pitch;
        layer.volume = 0f;
        layer.time = phase * clip.length;
        layer.Play();
    }

    private static bool HasVoice(Voice[] voices, Emitter emitter)
    {
        foreach (var voice in voices) if (voice.emitter == emitter) return true;
        return false;
    }

    private static Voice FreeVoice(Voice[] voices)
    {
        foreach (var voice in voices) if (voice.emitter == null) return voice;
        return null;
    }

    public void SetVolume(float value)
    {
        volume = float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
        foreach (var voice in carVoices) Apply(voice);
        foreach (var voice in trainVoices) Apply(voice);
    }

    private void Apply(Voice voice)
    {
        if (voice?.source == null) return;
        var emitter = voice.emitter;
        float gain = voice.gain * volume;
        if (emitter != null && IsTrain(emitter.kind))
        {
            float slowWeight = Mathf.Clamp01((1f - voice.tempo) / 0.35f);
            float fastWeight = Mathf.Clamp01((voice.tempo - 1f) / 0.5f);
            float rollingGain = gain * TrainGain(emitter.speed);
            voice.source.volume = rollingGain * (1f - slowWeight - fastWeight);
            voice.slow.volume = rollingGain * slowWeight;
            voice.fast.volume = rollingGain * fastWeight;
        }
        else voice.source.volume = gain;
        if (voice.motor != null)
            voice.motor.volume = emitter == null ? 0f : gain * TrainMotorGain(emitter.speed);
    }

    /// <summary>Number of currently playing sources, for verification.</summary>
    public int PlayingCount
    {
        get
        {
            int n = 0;
            foreach (var voice in carVoices) if (IsPlaying(voice)) n++;
            foreach (var voice in trainVoices) if (IsPlaying(voice)) n++;
            return n;
        }
    }

    private static bool IsPlaying(Voice voice)
    {
        if (voice.emitter == null) return false;
        foreach (var layer in new[] { voice.source, voice.slow, voice.fast, voice.motor })
            if (layer != null && layer.isPlaying) return true;
        return false;
    }

    private void StopAudio()
    {
        if (!running) return;
        foreach (var voice in carVoices) Stop(voice);
        foreach (var voice in trainVoices) Stop(voice);
        running = false;
    }

    private static void Stop(Voice voice)
    {
        // Main sources and additional layers may be destroyed in different orders during shutdown; check each one's lifetime.
        if (voice == null) return;
        if (voice.source != null) { voice.source.Stop(); voice.source.volume = 0f; }
        voice.gain = 0f;
        voice.emitter = null;
        if (voice.motor != null) { voice.motor.Stop(); voice.motor.volume = 0f; }
        if (voice.slow != null) { voice.slow.Stop(); voice.slow.volume = 0f; }
        if (voice.fast != null) { voice.fast.Stop(); voice.fast.volume = 0f; }
    }

    private void OnDisable()
    {
        StopAudio();
        if (ipc != null) ipc.OnAmbienceVolume -= SetVolume;
        ipc = null;
    }

    private void OnDestroy()
    {
        foreach (var voice in carVoices) if (voice?.source != null) Destroy(voice.source.gameObject);
        foreach (var voice in trainVoices)
        {
            if (voice?.source != null) Destroy(voice.source.gameObject);
            if (voice?.motor != null) Destroy(voice.motor.gameObject);
            if (voice?.slow != null) Destroy(voice.slow.gameObject);
            if (voice?.fast != null) Destroy(voice.fast.gameObject);
        }
    }
}
