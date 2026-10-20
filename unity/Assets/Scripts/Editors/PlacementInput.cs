using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Shared key/mouse input checks for 3D placement editors (direct building placement and individual prop placement).
/// Called every frame from each manager's Update(); stateful interactions are separated into the classes below.
/// </summary>
public static class PlacementInput
{
    /// <summary>
    /// Conditions that block placement key/mouse input. While unfocused, a radial menu is open,
    /// the camera is in fly mode/dragging, or the pointer is over UI, stop placement rotation and keys
    /// (avoids treating camera middle-button rotation or keys typed into UI as placement input).
    /// </summary>
    public static bool IsBlocked() =>
        !Application.isFocused || RadialMenuController.IsOpen ||
        (CameraController.Instance != null && (CameraController.Instance.IsFlyMode || CameraController.Instance.IsMouseDragging)) ||
        (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject());

    public static bool ShiftHeld => InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
}

/// <summary>
/// Middle-button drag rotation. Converts horizontal mouse movement to angle, quantizes to 1-degree steps,
/// and carries the remainder to the next frame (small movements must not produce fractional angles).
/// The caller immediately applies each frame's delta to the preview and reports the final angle on release.
/// </summary>
public sealed class MiddleDragRotation
{
    public enum Result { None, Started, Rotated, Released }

    private const float DegreesPerPixel = 0.5f; // Drag sensitivity (degrees/px)

    public bool IsDragging { get; private set; }
    private float lastMouseX;

    /// <summary>Called every frame. On Rotated, returns an integer angle delta through deltaDegrees.</summary>
    public Result Update(out int deltaDegrees)
    {
        deltaDegrees = 0;
        if (!IsDragging)
        {
            if (!InputUtil.MiddleMouseWasPressedThisFrame) return Result.None;
            IsDragging = true;
            lastMouseX = InputUtil.MousePosition.x;
            return Result.Started;
        }
        if (InputUtil.MiddleMouseIsPressed)
        {
            int step = (int)((InputUtil.MousePosition.x - lastMouseX) * DegreesPerPixel);
            if (step == 0) return Result.None;
            deltaDegrees = step;
            lastMouseX += step / DegreesPerPixel;
            return Result.Rotated;
        }
        IsDragging = false;
        return Result.Released;
    }

    /// <summary>Aborts dragging when input is blocked, the mode is left, etc. (no release notification).</summary>
    public void Cancel() => IsDragging = false;
}

/// <summary>
/// Key hold repeat (0.35-second initial delay, then every 0.075 seconds). Shared by dimension and height keys.
/// </summary>
public sealed class KeyRepeat
{
    private const float InitialDelay = 0.35f;
    private const float RepeatInterval = 0.075f;

    private readonly Key key;
    private float nextFireTime;

    public KeyRepeat(Key key) => this.key = key;

    public bool Held => InputUtil.KeyPressed(key);

    /// <summary>True on initial press or a held frame that passes the repeat time.</summary>
    public bool Fired()
    {
        if (!InputUtil.KeyPressed(key)) return false;
        bool pressed = InputUtil.KeyWasPressedThisFrame(key);
        if (!pressed && Time.unscaledTime < nextFireTime) return false;
        nextFireTime = Time.unscaledTime + (pressed ? InitialDelay : RepeatInterval);
        return true;
    }
}
