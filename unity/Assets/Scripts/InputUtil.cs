using UnityEngine;
using UnityEngine.InputSystem;

public static class InputUtil
{
    private static bool IsBlocked => GameStateManager.Instance != null && !GameStateManager.Instance.IsWorldVisible;

    public const float ScrollNormalizationFactor = 1f / 120f;

    public static Vector3 MousePosition =>
        Mouse.current != null ? Mouse.current.position.ReadValue() : Vector3.zero;

    public static float ScrollY =>
        !IsBlocked && Mouse.current != null ? Mouse.current.scroll.ReadValue().y * ScrollNormalizationFactor : 0f;

    public static bool AnyKeyDown =>
        !IsBlocked && Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame;

    public static bool KeyPressed(Key key) =>
        !IsBlocked && Keyboard.current != null && Keyboard.current[key].isPressed;

    public static bool KeyWasPressedThisFrame(Key key) =>
        !IsBlocked && Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;

    public static bool LeftMouseWasPressedThisFrame =>
        !IsBlocked && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;

    public static bool RightMouseWasPressedThisFrame =>
        !IsBlocked && Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;

    public static bool LeftMouseIsPressed =>
        !IsBlocked && Mouse.current != null && Mouse.current.leftButton.isPressed;

    public static bool LeftMouseWasReleasedThisFrame =>
        !IsBlocked && Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;

    public static bool RightMouseIsPressed =>
        !IsBlocked && Mouse.current != null && Mouse.current.rightButton.isPressed;

    public static bool RightMouseWasReleasedThisFrame =>
        !IsBlocked && Mouse.current != null && Mouse.current.rightButton.wasReleasedThisFrame;

    public static bool MiddleMouseWasPressedThisFrame =>
        !IsBlocked && Mouse.current != null && Mouse.current.middleButton.wasPressedThisFrame;

    public static bool MiddleMouseIsPressed =>
        !IsBlocked && Mouse.current != null && Mouse.current.middleButton.isPressed;

    public static bool MiddleMouseWasReleasedThisFrame =>
        !IsBlocked && Mouse.current != null && Mouse.current.middleButton.wasReleasedThisFrame;
}
