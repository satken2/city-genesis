using System.Collections;
using UnityEngine;

/// <summary>
/// 建物や道路のステータス変化時にビジュアルエフェクト（パーティクル）や
/// 3D効果音を再生するシングルトンマネージャー。
/// </summary>
public class VisualEffectManager : MonoBehaviour
{
    public static VisualEffectManager Instance { get; private set; }

    [Header("建設完了エフェクト設定")]
    [Tooltip("完成時に再生するパーティクルプレハブ")]
    public GameObject constructionCompleteParticlePrefab;
    [Tooltip("完成時に再生する効果音")]
    public AudioClip constructionCompleteSound;

    [Header("解体・倒壊エフェクト設定")]
    [Tooltip("解体・倒壊時に再生するパーティクルプレハブ")]
    public GameObject demolitionParticlePrefab;
    [Tooltip("解体・倒壊時に再生する効果音")]
    public AudioClip demolitionSound;

    [Header("3Dオーディオ設定")]
    [Range(0f, 1f)] public float soundVolume = 0.8f;
    [Tooltip("3Dサウンドが減衰を開始する距離")]
    public float audioMinDistance = 10f;
    [Tooltip("3Dサウンドが全く聞こえなくなる距離")]
    public float audioMaxDistance = 100f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
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

    /// <summary>
    /// 指定されたプレハブを生成し、再生完了後に自動破棄します。
    /// </summary>
    private void SpawnParticle(GameObject prefab, Vector3 position)
    {
        if (prefab == null) return;

        // パーティクルが地面に埋まらないよう、少しY軸を浮かせる（例: +0.5m）
        Vector3 spawnPos = position + new Vector3(0f, 0.5f, 0f);
        GameObject particleObj = Instantiate(prefab, spawnPos, Quaternion.identity);

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
    private void Play3DSound(AudioClip clip, Vector3 position)
    {
        if (clip == null) return;

        GameObject audioObj = new GameObject("3D_Sound_Effect");
        audioObj.transform.position = position;

        AudioSource source = audioObj.AddComponent<AudioSource>();
        source.clip = clip;
        source.volume = soundVolume;
        
        // 3D音響設定
        source.spatialBlend = 1.0f; // 完全な3Dサウンド
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.minDistance = audioMinDistance;
        source.maxDistance = audioMaxDistance;

        source.Play();

        // クリップの再生終了後にオブジェクトを破棄
        Destroy(audioObj, clip.length + 0.1f);
    }
}
