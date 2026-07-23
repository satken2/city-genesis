using UnityEngine;
using UnityEngine.InputSystem;

public static class InputUtil
{
    public const float ScrollNormalizationFactor = 1f / 120f;

    public static Vector3 MousePosition =>
        Mouse.current != null ? Mouse.current.position.ReadValue() : Vector3.zero;

    public static float ScrollY =>
        Mouse.current != null ? Mouse.current.scroll.ReadValue().y * ScrollNormalizationFactor : 0f;

    public static bool AnyKeyDown =>
        Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame;

    public static bool KeyPressed(Key key) =>
        Keyboard.current != null && Keyboard.current[key].isPressed;

    public static bool KeyWasPressedThisFrame(Key key) =>
        Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;

    public static bool LeftMouseWasPressedThisFrame =>
        Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;

    public static bool RightMouseWasPressedThisFrame =>
        Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;

    public static bool LeftMouseIsPressed =>
        Mouse.current != null && Mouse.current.leftButton.isPressed;

    public static bool LeftMouseWasReleasedThisFrame =>
        Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;

    public static bool RightMouseIsPressed =>
        Mouse.current != null && Mouse.current.rightButton.isPressed;

    public static bool RightMouseWasReleasedThisFrame =>
        Mouse.current != null && Mouse.current.rightButton.wasReleasedThisFrame;

    public static bool MiddleMouseWasPressedThisFrame =>
        Mouse.current != null && Mouse.current.middleButton.wasPressedThisFrame;

    public static bool MiddleMouseIsPressed =>
        Mouse.current != null && Mouse.current.middleButton.isPressed;

    public static bool MiddleMouseWasReleasedThisFrame =>
        Mouse.current != null && Mouse.current.middleButton.wasReleasedThisFrame;
}
