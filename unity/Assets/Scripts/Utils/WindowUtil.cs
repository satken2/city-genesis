using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Unity ウィンドウの操作(フォアグラウンドへの前面化、隣のディスプレイへの移動)をまとめたユーティリティ。
///
/// UI プログラム(Tauri)から IPC で world:change_state を受信した際、
/// Unity ウィンドウが背面に回っているとマウスイベントが処理できない。
/// そのため、編集系ステート(STATE_EDIT_*)への遷移時に本ユーティリティを呼び出し、
/// 強制的に前面化することでユーザーが直感的にマップ操作できるようにする。
///
/// Windows の SetForegroundWindow は呼び出し側がフォアグラウンドでないと失敗する場合がある。
/// これを緩和するため、AllowSetForegroundWindow(ASFW_ANY) を事前に呼び出す。
/// Web ビルドでは 3D 画面はページの canvas なので、前面化は canvas にフォーカスを移すことに当たる。
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
    /// すべてのプロセスに対して SetForegroundWindow の呼び出しを許可する定数。
    /// </summary>
    private const uint ASFW_ANY = unchecked((uint)-1);
#endif

    /// <summary>
    /// 現在の Unity ウィンドウを OS のフォアグラウンドに持ってくる。
    ///
    /// Windows の SetForegroundWindow 制約を緩和するため、
    /// AllowSetForegroundWindow(ASFW_ANY) を事前に呼び出している。
    /// エディタ環境でもスタンドアロンビルドでも動作する。
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
    /// Unity ウィンドウを隣のディスプレイ(direction が正なら次、それ以外は前)へ移す。
    ///
    /// UI(Tauri)とはディスプレイの列挙順が一致する保証がないため、UI からは方向だけを受け取り、
    /// 隣の決定は Unity 自身の列挙(Screen.GetDisplayLayout)で行う。
    /// 表示先はここでは保存しない。Unity プレイヤーが終了時に自分で記録し(UnitySelectMonitor)、次回起動時に復元する。
    /// エディタではゲームビューがエディタのウィンドウに属するため何もしない。
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

        // 全画面表示ではウィンドウは移動先ディスプレイの左上に吸着するので、位置は原点でよい
        AsyncOperation move = Screen.MoveMainWindowTo(target, Vector2Int.zero);
        move.completed += _ =>
        {
            // 起動時と同じく移動先ディスプレイのネイティブ解像度で描く(解像度の違うディスプレイへ移ったとき、移動前の解像度を残さない)
            if (Screen.fullScreenMode != FullScreenMode.Windowed
                && (Screen.width != target.width || Screen.height != target.height))
            {
                Screen.SetResolution(target.width, target.height, Screen.fullScreenMode);
            }
        };
    }
}
