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
    public const float CarMinDistance = 5f, CarMaxDistance = 120f, TrainMinDistance = 10f, TrainMaxDistance = 400f;
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
        public AudioSource motor, slow, fast;
        public float tempo;
        public Emitter emitter;
        public float gain;
    }

    private static readonly Dictionary<(Kind, long), Emitter> emitters = new Dictionary<(Kind, long), Emitter>();
    private static readonly string[] clipNames = { "vehicle-car", "vehicle-train", "vehicle-train-motor", "vehicle-train-slow", "vehicle-train-fast" };
    private readonly AudioClip[] clips = new AudioClip[5];
    private readonly Voice[] carVoices = new Voice[CarVoices], trainVoices = new Voice[TrainVoices];
    private readonly List<Emitter> candidates = new List<Emitter>();
    private readonly List<(Kind, long)> stale = new List<(Kind, long)>();
    private IpcClient ipc;
    private float volume, nextRefresh;
    private bool running;

    public static int EmitterCount => emitters.Count;

    /// <summary>毎フレーム呼ぶ。未登録なら登録し、登録済みなら位置と速度を更新する。</summary>
    public static void Report(Kind kind, long id, Vector3 position, float speed)
    {
        var key = (kind, id);
        if (!emitters.TryGetValue(key, out var emitter)) emitters[key] = emitter = new Emitter { kind = kind };
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

    /// <summary>エンジン音のピッチ。停止中はアイドル、20m/s以上で最高回転として扱う。</summary>
    public static float CarPitch(float speed) => 0.9f + 0.5f * Mathf.Clamp01(speed / 20f);

    /// <summary>車輪の走行音は停車中に消す。モーターのアイドルは別レイヤーで残す。</summary>
    public static float TrainGain(float speed) => 0.75f * Mathf.Clamp01(speed / 8f);
    public static float TrainMotorGain(float speed) => Mathf.Lerp(0.18f, 0.55f, Mathf.Clamp01(speed / 20f));
    // 走行音は音程を保ったテンポ違いの録音を混ぜる。モーターだけAudioSource.pitchで音程も変える。
    public static float TrainTempo(float speed) => Mathf.Lerp(0.65f, 1.5f, Mathf.Clamp01(speed / 30f));
    public static float TrainMotorPitch(float speed) => Mathf.Lerp(0.8f, 2f, Mathf.Clamp01(speed / 30f));

    private static bool IsTrain(Kind kind) => kind == Kind.Train;

    private void Awake()
    {
        for (int i = 0; i < clips.Length; i++)
        {
            clips[i] = Resources.Load<AudioClip>("Audio/Vehicles/" + clipNames[i]);
            if (clips[i] == null) Debug.LogError("VehicleAmbience: 音源がありません: " + clipNames[i]);
        }
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
        // 音源は親を持たないルートに置く。FloatingOriginが全Transformを個別にずらすため、親子にすると二重にずれる。
        var source = new GameObject(name).AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Custom;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        // 距離軸は最大距離を1とする。近景の迫力を保ち、遠景は低い響きだけが薄く残るようにする。
        float near = minDistance / maxDistance;
        // 鉄道は大きな音源として遠方まで聞かせる。音量の届く距離と、高音が失われる距離は独立させる。
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
        // 周波数はHz。Unity側の距離カーブに任せ、音源の移動とリスナーの移動の両方へ追従させる。
        var cutoffCurve = new AnimationCurve(
            new Keyframe(0f, 22000f), new Keyframe(near, 22000f),
            new Keyframe(near * 3f, 14000f), new Keyframe(near * 5f, 10000f),
            new Keyframe(near * 10f, 6000f), new Keyframe(near * 20f, 3500f), new Keyframe(1f, 2500f));
        if (train) cutoffCurve.AddKey(new Keyframe(300f / maxDistance, 2500f));
        lowPass.customCutoffCurve = cutoffCurve;
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
        foreach (var key in stale) { emitters[key].removed = true; emitters.Remove(key); }
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
            voice.tempo = TrainTempo(emitter.speed);
            voice.source.pitch = train ? 1f : CarPitch(emitter.speed);
            voice.source.time = Random.value * clip.length * 0.95f; // 近接する車両同士が同期して聞こえないよう開始位置を散らす。
            voice.source.Play();
            if (train)
            {
                StartRollingLayer(voice.slow, clips[3], voice.source);
                StartRollingLayer(voice.fast, clips[4], voice.source);
            }
            if (voice.motor != null && clips[2] != null)
            {
                voice.motor.clip = clips[2];
                voice.motor.transform.position = emitter.position;
                voice.motor.volume = 0f;
                voice.motor.pitch = TrainMotorPitch(emitter.speed);
                voice.motor.time = Random.value * clips[2].length * 0.95f;
                voice.motor.Play();
            }
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

    private static void StartRollingLayer(AudioSource layer, AudioClip clip, AudioSource reference)
    {
        if (clip == null) return;
        layer.clip = clip;
        layer.transform.position = reference.transform.position;
        layer.pitch = 1f;
        layer.volume = 0f;
        layer.time = reference.time / reference.clip.length * clip.length;
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
        // 終了時は主音源と追加レイヤーが別々の順序で破棄されるため、それぞれの生存を確認する。
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
