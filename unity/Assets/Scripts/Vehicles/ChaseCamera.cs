using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 対象を後方上空から追うカメラ。市民の追従とドライブの三人称で共有する。
/// ホイールで距離、右ドラッグで向きと俯角を変え、対象の向きの変化には遅れて追従する。地表から2mより下へは下げない。
/// </summary>
public sealed class ChaseCamera
{
    private float distance = 40f, pitch = 35f, yawOffset, yaw;
    private bool yawInitialized;
    private Vector2 lastMouse;

    /// <summary>向きを対象の進行方向へ合わせ直し、右ドラッグで足した向きを捨てる。距離と俯角は次の追従にも引き継ぐ。</summary>
    public void Reset() { yawInitialized = false; yawOffset = 0; }

    /// <summary>カメラを1フレームぶん動かす。dt は向きと位置の追従に使う経過時間。</summary>
    public void Apply(Camera cam, Vector3 focus, Vector3 heading, float dt)
    {
        HandleInput();
        var flat = new Vector3(heading.x, 0, heading.z);
        float targetYaw = flat.sqrMagnitude > .0001f ? Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg : yaw;
        yaw = yawInitialized ? Mathf.LerpAngle(yaw, targetYaw, 1 - Mathf.Exp(-2f * dt)) : targetYaw;
        yawInitialized = true;
        Vector3 target = focus + Vector3.up * 1.5f;
        Vector3 desired = target + Quaternion.Euler(pitch, yaw + yawOffset, 0) * (Vector3.back * distance);
        Vector3 position = Vector3.Lerp(cam.transform.position, desired, 1 - Mathf.Exp(-4f * dt));
        position.y = Mathf.Max(position.y, Utils.GetGroundHeight(position) + 2f);
        cam.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position, Vector3.up));
    }

    private void HandleInput()
    {
        Vector2 mouse = InputUtil.MousePosition;
        bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        if (Application.isFocused && !overUi)
        {
            float scroll = InputUtil.ScrollY;
            if (Mathf.Abs(scroll) > .001f) distance = Mathf.Clamp(distance * Mathf.Pow(.85f, scroll), 8f, 500f);
            if (InputUtil.RightMouseIsPressed && !InputUtil.RightMouseWasPressedThisFrame)
            {
                Vector2 delta = mouse - lastMouse;
                yawOffset += delta.x * .25f;
                pitch = Mathf.Clamp(pitch - delta.y * .2f, 5f, 85f);
            }
        }
        lastMouse = mouse;
    }
}
