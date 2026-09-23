using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 建物や道路のステータス変化時にビジュアルエフェクト（パーティクル）や
/// 3D効果音を再生するシングルトンマネージャー。AppRootが生成する。
/// 音量は設定画面のmaster×sfx(ambience:volume)を購読し、受信するまでは無音。
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
        // IpcClientはUnity再起動や後起動で差し替わるため、CameraAmbienceと同じく参照を毎フレーム照合する。
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
    /// 建設完了（完成）時のエフェクトを再生します。
    /// </summary>
    public void PlayConstructionComplete(Vector3 position)
    {
        SpawnParticle(constructionCompleteParticlePrefab, position);
        Play3DSound(constructionCompleteSound, position);
    }

    /// <summary>
    /// 解体または倒壊時のエフェクトを再生します。
    /// </summary>
    public void PlayDemolition(Vector3 position)
    {
        SpawnParticle(demolitionParticlePrefab, position);
        Play3DSound(demolitionSound, position);
    }

    /// <summary>プレイヤーによる爆破だけに使う。通常の解体・ロード時の倒壊では発火しない。</summary>
    public void PlayExplosion(Vector3 position, float radius)
    {
        var effect = FireEffectFactory.CreateExplosion(radius, transform);
        effect.transform.position = position;
        StartCoroutine(DestroyAfterRealtime(effect, 3.5f));
        // 俯瞰カメラからも聞こえる範囲を確保し、都市の反対側では無音にする。
        Play3DSound(explosionSound, position, 80f, 1800f);
    }

    public void ClearTransientEffects()
    {
        StopAllCoroutines();
        foreach (Transform child in transform)
        {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }
        activeSounds.Clear();
    }

    private IEnumerator DestroyAfterRealtime(GameObject target, float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        if (target != null) Destroy(target);
        activeSounds.RemoveAll(source => source == null);
    }

    /// <summary>
    /// 指定されたプレハブを生成し、再生完了後に自動破棄します。
    /// </summary>
    private void SpawnParticle(GameObject prefab, Vector3 position)
    {
        if (prefab == null) return;

        // パーティクルが地面に埋まらないよう、少しY軸を浮かせる（例: +0.5m）
        Vector3 spawnPos = position + new Vector3(0f, 0.5f, 0f);
        GameObject particleObj = Instantiate(prefab, spawnPos, Quaternion.identity);
        particleObj.transform.SetParent(transform, true);

        ParticleSystem ps = particleObj.GetComponent<ParticleSystem>();
        if (ps != null)
        {
            // パーティクルシステムの期間に基づいて自動破棄する
            var main = ps.main;
            float duration = main.duration + main.startLifetime.constantMax;
            Destroy(particleObj, duration);
        }
        else
        {
            // ParticleSystemが見つからない場合は一律で5秒後に破棄
            Destroy(particleObj, 5f);
        }
    }

    /// <summary>
    /// 3D空間の指定座標で減衰設定を施した効果音を再生します。
    /// </summary>
    private void Play3DSound(AudioClip clip, Vector3 position, float minDistance = -1f, float maxDistance = -1f)
    {
        if (clip == null || settingsVolume <= 0f) return;

        GameObject audioObj = new GameObject("3D_Sound_Effect");
        audioObj.transform.SetParent(transform, false);
        audioObj.transform.position = position;

        AudioSource source = audioObj.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.clip = clip;
        source.volume = soundVolume * settingsVolume;

        // 3D音響設定
        source.spatialBlend = 1.0f; // 完全な3Dサウンド
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.minDistance = minDistance > 0f ? minDistance : audioMinDistance;
        source.maxDistance = maxDistance > 0f ? maxDistance : audioMaxDistance;
        if (maxDistance > 0f)
        {
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(0.15f, 0.8f), new Keyframe(0.5f, 0.25f), new Keyframe(1f, 0f)));
        }
        source.dopplerLevel = 0f; // カメラの高速移動で音程が揺れないようにする

        source.Play();
        activeSounds.Add(source);

        // クリップの再生終了後にオブジェクトを破棄
        StartCoroutine(DestroyAfterRealtime(audioObj, clip.length + 0.1f));
    }
}
