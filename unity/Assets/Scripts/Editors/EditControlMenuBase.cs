using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 編集モード中の「Ctrl長押しラジアルメニュー＋カーソルHUD」ドライバの共通基底。
/// 対象マネージャと同じGameObjectに常駐し、編集がアクティブかを毎フレーム監視して自律的に動作する
/// (編集終了経路が複数あるため、各出口へのフック追加よりポーリングの方が漏れがない)。
///
/// 操作系:
/// - Ctrl押下: カーソル位置からラジアルメニューを展開。マウスで項目をハイライトし、
///   Ctrlを離すとその項目を選択/トグルする(RadialMenuController)。
/// - アクティブ中はカーソル脇に現在状態を常時表示する(CursorStatusHud)。
///
/// 派生クラスが与えるのは「アクティブ判定」「メニュー項目」「HUDの内容」だけで、
/// HUDの登録/解除・メニューの開閉・自分が開いたメニューの後始末はすべてここが持つ。
/// </summary>
public abstract class EditControlMenuBase : MonoBehaviour
{
    private bool hudActive = false;

    /// <summary>自分が開いたメニューだけを閉じる(他ドライバのメニューを巻き込まない)ための所有フラグ。</summary>
    private bool menuOpenedByThis = false;

    /// <summary>HUD providerが毎フレーム返すリスト。GC回避のため使い回す。</summary>
    protected readonly List<CursorHudIcon> hudIcons = new List<CursorHudIcon>();

    /// <summary>編集セッションがアクティブか。対象マネージャが未初期化なら false を返すこと。</summary>
    protected abstract bool IsSessionActive { get; }

    /// <summary>
    /// ラジアルメニューの項目。状態の読み書きはFunc/Action経由なので初期化時に一度構築すればよい。
    /// null(未初期化)の間はメニューを開かない。
    /// </summary>
    protected abstract List<RadialMenuItem> MenuItems { get; }

    /// <summary>カーソルHUDの表示内容。毎フレーム呼ばれるため軽量に保つこと。</summary>
    protected abstract (List<CursorHudIcon> icons, string text) BuildHudContent();

    /// <summary>Ctrlを離して項目を確定した直後のフック(吸着プレビューの再計算など)。</summary>
    protected virtual void OnMenuCommitted() { }

    protected virtual void Update()
    {
        bool active = IsSessionActive;

        // HUDの登録/解除(セッション開始・終了の検知)
        if (active && !hudActive)
        {
            CursorStatusHud.EnsureInstance().SetProvider(BuildHudContent);
            hudActive = true;
        }
        else if (!active && hudActive)
        {
            ReleaseUi();
        }

        if (!active) return;

        bool ctrlHeld = InputUtil.KeyPressed(Key.LeftCtrl) || InputUtil.KeyPressed(Key.RightCtrl);
        bool ctrlPressedNow = InputUtil.KeyWasPressedThisFrame(Key.LeftCtrl) || InputUtil.KeyWasPressedThisFrame(Key.RightCtrl);

        if (ctrlPressedNow && !RadialMenuController.IsOpen)
        {
            // マウスボタンを押している間は開かない(ドラッグ・回転・角度指定などの押下中ジェスチャが宙に浮くため)
            if (!InputUtil.LeftMouseIsPressed && !InputUtil.RightMouseIsPressed && !InputUtil.MiddleMouseIsPressed)
            {
                List<RadialMenuItem> items = MenuItems;
                if (items == null) return;
                RadialMenuController.EnsureInstance().Open(items, InputUtil.MousePosition);
                menuOpenedByThis = true;
            }
        }
        else if (menuOpenedByThis && RadialMenuController.IsOpen && !ctrlHeld)
        {
            RadialMenuController.Instance.CloseAndCommit();
            menuOpenedByThis = false;
            OnMenuCommitted();
        }
    }

    protected virtual void OnDisable()
    {
        ReleaseUi();
    }

    /// <summary>HUDの登録を解除し、自分が開いたメニューを選択なしで閉じる。</summary>
    private void ReleaseUi()
    {
        if (hudActive)
        {
            CursorStatusHud.Instance?.ClearProvider();
            hudActive = false;
        }
        if (menuOpenedByThis)
        {
            RadialMenuController.Instance?.CloseAndCancel();
            menuOpenedByThis = false;
        }
    }
}
