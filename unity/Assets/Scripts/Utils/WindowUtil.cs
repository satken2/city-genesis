using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Utilities for Unity-window operations: bringing it to the foreground and moving it to an adjacent display.
///
/// When the UI (Tauri) sends world:change_state over IPC,
/// Unity cannot process mouse events if its window is in the background.
/// Call this utility when entering editing states (STATE_EDIT_*)
/// to bring the window forward and allow intuitive map interaction.
///
/// Windows SetForegroundWindow can fail when the calling process is not in the foreground.
/// Call AllowSetForegroundWindow(ASFW_ANY) first to mitigate this restriction.
/// In Web builds, the 3D view is a page canvas, so bringing it forward means focusing that canvas.
/// </summary>
public static class WindowUtil
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void GenesisWeb_FocusCanvas();
#else
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetActiveWindow();

    /// <summary>
    /// Constant allowing all processes to call SetForegroundWindow.
    /// </summary>
    private const uint ASFW_ANY = unchecked((uint)-1);
#endif

    /// <summary>
    /// Bring the current Unity window to the OS foreground.
    ///
    /// Call AllowSetForegroundWindow(ASFW_ANY) first
    /// to ease Windows SetForegroundWindow restrictions.
    /// Works in both the Editor and standalone builds.
    /// </summary>
    public static void BringToForeground()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        GenesisWeb_FocusCanvas();
#else
        AllowSetForegroundWindow(ASFW_ANY);

        IntPtr hWnd = GetActiveWindow();
        if (hWnd == IntPtr.Zero)
        {
            Debug.LogWarning("[WindowUtil] GetActiveWindow が IntPtr.Zero を返しました。前面化をスキップします。");
            return;
        }

        bool result = SetForegroundWindow(hWnd);
        if (!result)
        {
            Debug.LogWarning("[WindowUtil] SetForegroundWindow が失敗しました。ウィンドウが既に前面か、OS制約により拒否された可能性があります。");
        }
#endif
    }

    /// <summary>
    /// Move Unity to an adjacent display: next when direction is positive, otherwise previous.
    ///
    /// Display enumeration order is not guaranteed to match Tauri's, so the UI sends only a direction;
    /// Unity chooses the neighbor using its own Screen.GetDisplayLayout enumeration.
    /// Do not save the destination here. Unity records UnitySelectMonitor on exit and restores it at next launch.
    /// Do nothing in the Editor, where the game view belongs to the Editor window.
    /// </summary>
    public static void MoveToAdjacentDisplay(int direction)
    {
        if (Application.isEditor)
        {
            Debug.Log("[WindowUtil] エディタ実行中のため、ディスプレイの移動をスキップします。");
            return;
        }

        var displays = new List<DisplayInfo>();
        Screen.GetDisplayLayout(displays);
        int count = displays.Count;
        if (count <= 1) return;

        DisplayInfo current = Screen.mainWindowDisplayInfo;
        int index = Math.Max(0, displays.FindIndex(d => d.Equals(current)));
        DisplayInfo target = displays[direction > 0 ? (index + 1) % count : (index + count - 1) % count];

        // Fullscreen windows align to the destination display's top-left corner, so use the origin.
        AsyncOperation move = Screen.MoveMainWindowTo(target, Vector2Int.zero);
        move.completed += _ =>
        {
            // Render at the destination display's native resolution, as on startup, rather than retaining the previous display's resolution.
            if (Screen.fullScreenMode != FullScreenMode.Windowed
                && (Screen.width != target.width || Screen.height != target.height))
            {
                Screen.SetResolution(target.width, target.height, Screen.fullScreenMode);
            }
        };
    }
}
