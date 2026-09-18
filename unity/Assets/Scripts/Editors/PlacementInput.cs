using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// 3D上の配置エディタ(建物の直接配置・プロップの個別配置)が共有するキー/マウス操作の判定。
/// 各マネージャの Update() から毎フレーム呼ぶ前提で、状態を持つ操作は下のクラスに分けている。
/// </summary>
public static class PlacementInput
{
    /// <summary>
    /// 配置操作のキー/マウス入力を受け付けない状況。ウィンドウ非フォーカス・ラジアルメニュー表示中・
    /// カメラのフライモード/ドラッグ中・UI上にポインタがある間は、配置側の回転やキー操作を止める
    /// (カメラ回転の中ボタンや、UI入力中のキーを配置操作として拾わないため)。
    /// </summary>
    public static bool IsBlocked() =>
        !Application.isFocused || RadialMenuController.IsOpen ||
        (CameraController.Instance != null && (CameraController.Instance.IsFlyMode || CameraController.Instance.IsMouseDragging)) ||
        (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject());

    public static bool ShiftHeld => InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift);
}

/// <summary>
/// 中ボタンドラッグで回転角を変える操作。横方向のマウス移動量を角度へ換算し、1度単位に量子化して
/// 端数は次フレームへ持ち越す(小刻みな移動で角度が小数化しないため)。
/// 呼び出し側はドラッグ中の毎フレームの差分をプレビューへ即時反映し、リリース時に確定角度をUIへ通知する。
/// </summary>
public sealed class MiddleDragRotation
{
    public enum Result { None, Started, Rotated, Released }

    private const float DegreesPerPixel = 0.5f; // ドラッグ感度(度/px)

    public bool IsDragging { get; private set; }
    private float lastMouseX;

    /// <summary>毎フレーム呼ぶ。Rotated のとき deltaDegrees に整数の角度差分を返す。</summary>
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

    /// <summary>入力がブロックされた・モードが外れた等でドラッグを打ち切る(リリース通知は出さない)。</summary>
    public void Cancel() => IsDragging = false;
}

/// <summary>
/// キーの長押しリピート(押下直後は0.35秒待ち、以後0.075秒間隔)。寸法・高さの増減キーで共用する。
/// </summary>
public sealed class KeyRepeat
{
    private const float InitialDelay = 0.35f;
    private const float RepeatInterval = 0.075f;

    private readonly Key key;
    private float nextFireTime;

    public KeyRepeat(Key key) => this.key = key;

    public bool Held => InputUtil.KeyPressed(key);

    /// <summary>押下した瞬間、または長押しでリピート時刻を過ぎたフレームで true。</summary>
    public bool Fired()
    {
        if (!InputUtil.KeyPressed(key)) return false;
        bool pressed = InputUtil.KeyWasPressedThisFrame(key);
        if (!pressed && Time.unscaledTime < nextFireTime) return false;
        nextFireTime = Time.unscaledTime + (pressed ? InitialDelay : RepeatInterval);
        return true;
    }
}
