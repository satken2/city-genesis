using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Camera following a target from above and behind, shared by citizen following and third-person driving.
/// Wheel adjusts distance; right-drag adjusts bearing/pitch. Follow target-heading changes with lag, staying at least 2m above ground.
/// </summary>
public sealed class ChaseCamera
{
    private float distance = 40f, pitch = 35f, yawOffset, yaw;
    private bool yawInitialized;
    private Vector2 lastMouse;

    /// <summary>Realign with target travel direction, discarding right-drag bearing offsets. Retain distance and pitch for subsequent follows.</summary>
    public void Reset() { yawInitialized = false; yawOffset = 0; }

    /// <summary>Advance the camera one frame. dt is elapsed time for orientation and position following.</summary>
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
