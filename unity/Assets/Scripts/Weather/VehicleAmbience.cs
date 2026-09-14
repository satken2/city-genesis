using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 走行中の車両(一般車・路線バス・鉄道編成)の位置から走行音を3Dループ再生する。経済状態は変更しない。
/// 発音体は VehicleManager / TransitVehicleManager が毎フレーム <see cref="Report"/> で位置と速度を通知し、
/// 通知が途切れた発音体は自動的に消える。座標は通知時点のワールド座標をそのまま使う(車両GameObjectと同じ)。
/// </summary>
public sealed class VehicleAmbience : MonoBehaviour
{
    /// <summary>Car と Bus は同じ音源(エンジン音)で、通知元が違うだけ。</summary>
    public enum Kind { Car = 0, Bus = 1, Train = 2 }

    public const int CarVoices = 6, TrainVoices = 3;
    public const float CarMinDistance = 5f, CarMaxDistance = 120f, TrainMinDistance = 20f, TrainMaxDistance = 300f;
    public const float RefreshSeconds = 0.25f, FadeSeconds = 0.3f, StaleSeconds = 1f;

    private sealed class Emitter
    {
        public Kind kind;
        public Vector3 position;
        public float speed, reported, distance;
        public bool removed;
    }

    private sealed class Voice
    {
        public AudioSource source;
        public Emitter emitter;
        public float gain;
    }

    private static readonly Dictionary<long, Emitter> emitters = new Dictionary<long, Emitter>();
    private static readonly string[] clipNames = { "vehicle-car", "vehicle-train" };
    private readonly AudioClip[] clips = new AudioClip[2];
    private readonly Voice[] carVoices = new Voice[CarVoices], trainVoices = new Voice[TrainVoices];
    private readonly List<Emitter> candidates = new List<Emitter>();
    private readonly List<long> stale = new List<long>();
    private IpcClient ipc;
    private float volume, nextRefresh;
    private bool running;

    public static int EmitterCount => emitters.Count;

    /// <summary>毎フレーム呼ぶ。未登録なら登録し、登録済みなら位置と速度を更新する。</summary>
    public static void Report(Kind kind, int id, Vector3 position, float speed)
    {
        long key = Key(kind, id);
        if (!emitters.TryGetValue(key, out var emitter)) emitters[key] = emitter = new Emitter { kind = kind };
        emitter.position = position;
        emitter.speed = float.IsNaN(speed) || float.IsInfinity(speed) ? 0f : Mathf.Max(0f, speed);
        emitter.reported = Time.unscaledTime;
    }

    public static void Remove(Kind kind, int id)
    {
        long key = Key(kind, id);
        if (!emitters.TryGetValue(key, out var emitter)) return;
        emitter.removed = true;
        emitters.Remove(key);
    }

    public static void Clear(Kind kind)
    {
        var keys = new List<long>();
        foreach (var pair in emitters) if (pair.Value.kind == kind) { pair.Value.removed = true; keys.Add(pair.Key); }
        foreach (long key in keys) emitters.Remove(key);
    }

    /// <summary>エンジン音のピッチ。停止中はアイドル、20m/s以上で最高回転として扱う。</summary>
    public static float CarPitch(float speed) => 0.9f + 0.5f * Mathf.Clamp01(speed / 20f);

    /// <summary>鉄道の走行音ゲイン。駅停車中は小さく残し、8m/s以上で全開にする。</summary>
    public static float TrainGain(float speed) => 0.35f + 0.65f * Mathf.Clamp01(speed / 8f);

    private static bool IsTrain(Kind kind) => kind == Kind.Train;
    private static long Key(Kind kind, int id) => ((long)kind << 32) | (uint)id;

    private void Awake()
    {
        for (int i = 0; i < clips.Length; i++)
        {
            clips[i] = Resources.Load<AudioClip>("Audio/Vehicles/" + clipNames[i]);
            if (clips[i] == null) Debug.LogError("VehicleAmbience: 音源がありません: " + clipNames[i]);
        }
        for (int i = 0; i < carVoices.Length; i++) carVoices[i] = CreateVoice("Vehicle voice car " + i, CarMinDistance, CarMaxDistance);
        for (int i = 0; i < trainVoices.Length; i++) trainVoices[i] = CreateVoice("Vehicle voice train " + i, TrainMinDistance, TrainMaxDistance);
    }

    private static Voice CreateVoice(string name, float minDistance, float maxDistance)
    {
        // 音源は親を持たないルートに置く。FloatingOriginが全Transformを個別にずらすため、親子にすると二重にずれる。
        var source = new GameObject(name).AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        source.dopplerLevel = 0f; // カメラが高速移動するため、ドップラーを残すと音程が揺れる。
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

    /// <summary>通知が途切れた発音体を外す(範囲外へ出た編成や、Removeを経ずに消えた車両の保険)。</summary>
    private void DropStale()
    {
        stale.Clear();
        foreach (var pair in emitters) if (Time.unscaledTime - pair.Value.reported > StaleSeconds) stale.Add(pair.Key);
        foreach (long key in stale) { emitters[key].removed = true; emitters.Remove(key); }
    }

    /// <summary>可聴範囲内の発音体をカメラに近い順に選び、空いている音源へ割り当てる。再生中の発音体は割り当てを保つ。</summary>
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
        var clip = clips[train ? 1 : 0];
        if (clip == null) return;
        foreach (var emitter in candidates)
        {
            if (HasVoice(voices, emitter)) continue;
            var voice = FreeVoice(voices);
            if (voice == null) return;
            voice.emitter = emitter;
            voice.gain = 0f;
            voice.source.clip = clip;
            voice.source.transform.position = emitter.position;
            voice.source.volume = 0f;
            voice.source.time = Random.value * clip.length * 0.95f; // 近接する車両同士が同期して聞こえないよう開始位置を散らす。
            voice.source.Play();
        }
    }

    /// <summary>毎フレーム、音源を発音体へ追従させ、速度に応じたピッチ・ゲインとフェードを適用する。</summary>
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
            voice.source.volume = voice.gain * volume * (train ? TrainGain(emitter.speed) : 1f);
            if (!wanted && voice.gain <= 0f) { voice.source.Stop(); voice.emitter = null; }
        }
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
        voice.source.volume = voice.gain * volume * (emitter != null && IsTrain(emitter.kind) ? TrainGain(emitter.speed) : 1f);
    }

    /// <summary>検証用。現在鳴っている音源の数。</summary>
    public int PlayingCount
    {
        get
        {
            int n = 0;
            foreach (var voice in carVoices) if (voice.emitter != null && voice.source.isPlaying) n++;
            foreach (var voice in trainVoices) if (voice.emitter != null && voice.source.isPlaying) n++;
            return n;
        }
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
        voice.source.Stop(); voice.source.volume = 0f; voice.gain = 0f; voice.emitter = null;
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
        foreach (var voice in trainVoices) if (voice?.source != null) Destroy(voice.source.gameObject);
    }
}
