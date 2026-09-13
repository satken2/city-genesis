using UnityEngine;

/// <summary>環境音用の地表相対高度を通知する。天候描画の有効・無効には依存しない。</summary>
public sealed class CameraAmbience : MonoBehaviour
{
    private float nextSend;

    private void LateUpdate()
    {
        if (Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + 0.2f;
        var ipc = IpcClient.Instance;
        if (ipc == null) return;
        var camera = Camera.main;
        float? height = null;
        if (camera != null && GameStateManager.Instance != null && GameStateManager.Instance.IsGameStarted())
        {
            var position = camera.transform.position;
            height = RelativeHeight(position.y, Utils.GetGroundHeight(position));
        }
        // 定期通知により、Cockpitの後起動やIPC再接続でも問い合わせなしで復帰できる。
        ipc.EmitAmbienceCamera(height);
    }

    public static float? RelativeHeight(float cameraY, float groundY)
    {
        float height = cameraY - groundY;
        return float.IsNaN(height) || float.IsInfinity(height) ? (float?)null : Mathf.Max(0f, height);
    }

    private void OnDisable() => IpcClient.Instance?.EmitAmbienceCamera(null);
}
