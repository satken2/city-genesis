
using UnityEngine;

public class FloatingOrigin : MonoBehaviour
{
    // 移動判定閾値
    [SerializeField] private float threshold = 5000f;

    private void LateUpdate()
    {
        Vector3 camPos = transform.position;
        camPos.y = 0f; // Yは無視してXZ平面のみ

        // 閾値を超えていない場合は何もしない。
        // FindObjectsByType はシーン規模に比例して重いため、
        // 必ず閾値チェックを先に行い、通常時のオーバーヘッドをゼロにする。
        if (camPos.sqrMagnitude <= threshold * threshold)
        {
            return;
        }

        // オフセット分を全オブジェクトに適用
        Vector3 offset = new Vector3(camPos.x, 0f, camPos.z);
        Transform[] transforms = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude);
        foreach (var t in transforms)
        {
            if (t == this.transform) continue; // カメラ自身は後で
            t.position -= offset;
        }

        // カメラを原点付近へ
        transform.position -= offset;
    }
} 