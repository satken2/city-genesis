using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Windows API を使用して Unity ウィンドウをフォアグラウンドに持ってくるユーティリティ。
///
/// UI プログラム(Tauri)から IPC で world:change_state を受信した際、
/// Unity ウィンドウが背面に回っているとマウスイベントが処理できない。
/// そのため、編集系ステート(STATE_EDIT_*)への遷移時に本ユーティリティを呼び出し、
/// 強制的に前面化することでユーザーが直感的にマップ操作できるようにする。
///
/// Windows の SetForegroundWindow は呼び出し側がフォアグラウンドでないと失敗する場合がある。
/// これを緩和するため、AllowSetForegroundWindow(ASFW_ANY) を事前に呼び出す。
/// </summary>
public static class WindowUtil
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetActiveWindow();

    /// <summary>
    /// すべてのプロセスに対して SetForegroundWindow の呼び出しを許可する定数。
    /// </summary>
    private const uint ASFW_ANY = unchecked((uint)-1);

    /// <summary>
    /// 現在の Unity ウィンドウを OS のフォアグラウンドに持ってくる。
    ///
    /// Windows の SetForegroundWindow 制約を緩和するため、
    /// AllowSetForegroundWindow(ASFW_ANY) を事前に呼び出している。
    /// エディタ環境でもスタンドアロンビルドでも動作する。
    /// </summary>
    public static void BringToForeground()
    {
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
    }
}
