using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Singleton playing visual particle effects and 3D sound effects when building or road statuses change.
/// Created by AppRoot.
/// Subscribe to settings master x sfx volume via ambience:volume; remain silent until received.
/// </summary>
public class VisualEffectManager : MonoBehaviour
{
    public static VisualEffectManager Instance { get; private set; }

    [Header("建設完了エフェクト設定")]
    [Tooltip("完成時に再生するパーティクルプレハブ")]
    public GameObject constructionCompleteParticlePrefab;
    [Tooltip("完成時に再生する効果音。未設定なら Resources/Audio/Effects/construction-complete を探す")]
    public AudioClip constructionCompleteSound;

    [Header("解体・倒壊エフェクト設定")]
    [Tooltip("解体・倒壊時に再生するパーティクルプレハブ")]
    public GameObject demolitionParticlePrefab;
    [Tooltip("解体・倒壊時に再生する効果音。未設定なら Resources/Audio/Effects/demolition を探す")]
    public AudioClip demolitionSound;

    [Header("3Dオーディオ設定")]
    [Tooltip("設定音量(master×sfx)に掛ける基準ゲイン")]
    [Range(0f, 1f)] public float soundVolume = 0.8f;
    [Tooltip("3Dサウンドが減衰を開始する距離")]
    public float audioMinDistance = 10f;
    [Tooltip("3Dサウンドが全く聞こえなくなる距離")]
    public float audioMaxDistance = 100f;

    private IpcClient ipc;
    private float settingsVolume;
    private AudioClip explosionSound;
    private readonly List<AudioSource> activeSounds = new List<AudioSource>();
    // AppRoot also contains children of other components, such as ambience and weather, so clear only children created here.
    private readonly List<GameObject> ownedEffects = new List<GameObject>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        if (constructionCompleteSound == null) constructionCompleteSound = Resources.Load<AudioClip>("Audio/Effects/construction-complete");
        if (demolitionSound == null) demolitionSound = Resources.Load<AudioClip>("Audio/Effects/demolition");
        explosionSound = Resources.Load<AudioClip>("Audio/Effects/building-explosion");
    }

    private void Update()
    {
        // IpcClient may be replaced after Unity restart or late startup, so compare its reference each frame as CameraAmbience does.
        if (ipc == IpcClient.Instance) return;
        if (ipc != null) ipc.OnAmbienceVolume -= SetVolume;
        ipc = IpcClient.Instance;
        if (ipc != null) ipc.OnAmbienceVolume += SetVolume;
    }

    public void SetVolume(float value)
    {
        settingsVolume = float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
        activeSounds.RemoveAll(source => source == null);
        foreach (var source in activeSounds) source.volume = soundVolume * settingsVolume;
    }

    private void OnDestroy()
    {
        if (ipc != null) ipc.OnAmbienceVolume -= SetVolume;
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Play the construction-completion effect.
    /// </summary>
    public void PlayConstructionComplete(Vector3 position)
    {
        SpawnParticle(constructionCompleteParticlePrefab, position);
        Play3DSound(constructionCompleteSound, position);
    }

    /// <summary>
    /// Play the demolition or collapse effect.
    /// </summary>
    public void PlayDemolition(Vector3 position)
    {
        SpawnParticle(demolitionParticlePrefab, position);
        Play3DSound(demolitionSound, position);
    }

    /// <summary>Used only for player-triggered blasting, never ordinary demolition or collapsed objects encountered on load.</summary>
    public void PlayExplosion(Vector3 position, float radius)
    {
        // Parent to this manager; a building child would lose its lingering effect when replaced by rubble or evicted by LOD.
        var effect = FireEffectFactory.CreateExplosion(radius, transform);
        Track(effect);
        effect.transform.position = position;
        StartCoroutine(DestroyAfterRealtime(effect, 3.5f));
        // Keep sound audible from the overview camera but silent on the opposite side of the city.
        Play3DSound(explosionSound, position, 80f, 1800f);
    }

    public void ClearTransientEffects()
    {
        StopAllCoroutines();
        foreach (var effect in ownedEffects)
        {
            if (effect == null) continue;
            effect.SetActive(false);
            Destroy(effect);
        }
        ownedEffects.Clear();
        activeSounds.Clear();
    }

    private void Track(GameObject effect)
    {
        ownedEffects.RemoveAll(owned => owned == null);
        ownedEffects.Add(effect);
    }

    private IEnumerator DestroyAfterRealtime(GameObject target, float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        if (target != null) Destroy(target);
        activeSounds.RemoveAll(source => source == null);
    }

    /// <summary>
    /// Instantiate the specified prefab and destroy it automatically after playback.
    /// </summary>
    private void SpawnParticle(GameObject prefab, Vector3 position)
    {
        if (prefab == null) return;

        // Raise particles slightly along Y, e.g. +0.5m, to keep them out of the ground.
        Vector3 spawnPos = position + new Vector3(0f, 0.5f, 0f);
        GameObject particleObj = Instantiate(prefab, spawnPos, Quaternion.identity);
        particleObj.transform.SetParent(transform, true);
        Track(particleObj);

        ParticleSystem ps = particleObj.GetComponent<ParticleSystem>();
        if (ps != null)
        {
            // Schedule destruction based on the particle system's duration.
            var main = ps.main;
            float duration = main.duration + main.startLifetime.constantMax;
            Destroy(particleObj, duration);
        }
        else
        {
            // If no ParticleSystem exists, destroy after a fixed 5 seconds.
            Destroy(particleObj, 5f);
        }
    }

    /// <summary>
    /// Play a sound effect at the specified 3D position with attenuation settings.
    /// </summary>
    private void Play3DSound(AudioClip clip, Vector3 position, float minDistance = -1f, float maxDistance = -1f)
    {
        if (clip == null || settingsVolume <= 0f) return;

        GameObject audioObj = new GameObject("3D_Sound_Effect");
        audioObj.transform.SetParent(transform, false);
        Track(audioObj);
        audioObj.transform.position = position;

        AudioSource source = audioObj.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.clip = clip;
        source.volume = soundVolume * settingsVolume;

        // 3D audio settings.
        source.spatialBlend = 1.0f; // Fully 3D sound.
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.minDistance = minDistance > 0f ? minDistance : audioMinDistance;
        source.maxDistance = maxDistance > 0f ? maxDistance : audioMaxDistance;
        if (maxDistance > 0f)
        {
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(0.15f, 0.8f), new Keyframe(0.5f, 0.25f), new Keyframe(1f, 0f)));
        }
        source.dopplerLevel = 0f; // Prevent pitch changes from rapid camera movement.

        source.Play();
        activeSounds.Add(source);

        // Destroy the object after the clip finishes.
        StartCoroutine(DestroyAfterRealtime(audioObj, clip.length + 0.1f));
    }
}
