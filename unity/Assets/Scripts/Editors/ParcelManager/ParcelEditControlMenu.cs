using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 土地区画作成モード中の「作成モード切替」をUnity内で完結させるドライバ。
/// ParcelEditorManager と同じGameObjectに常駐し、作成モードがアクティブかを毎フレーム監視して
/// 自律的に動作する(道路敷設の RoadEditControlMenu・地区編集の ZoneEditControlMenu と同型。
/// 編集終了経路が複数あるためフックよりポーリングの方が漏れがない)。
///
/// 操作系:
/// - Ctrl押下: カーソル位置からラジアルメニューを展開。マウスで項目をハイライトし、
///   Ctrlを離すとその項目を選択(RadialMenuController)。ONリング=現在の作成モード。
/// - 作成モード中はカーソル脇に現在のモードと次の操作の案内を常時表示(CursorStatusHud)。
///
/// 作成モードの真実源は ParcelEditorManager.CurrentMode にあり、ここで切り替えると
/// `edit_parcel:set_mode` でUI(作成パネル)へ通知される(パネルのタイル選択も同じ状態を指す)。
/// 幅・奥行き・角度などの数値パラメータはパネル側が持つ。
/// </summary>
public class ParcelEditControlMenu : MonoBehaviour
{
    private ParcelEditorManager manager;
    private List<RadialMenuItem> items;
    private bool hudActive = false;
    private bool menuOpenedByThis = false;

    /// <summary>モードIDごとのアイコン(Resources/UIIcons、未配置ならnull=ラベル頭文字で代替表示)。</summary>
    private readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();

    /// <summary>HUD providerが毎フレーム返すリスト。GC回避のため使い回す。</summary>
    private readonly List<CursorHudIcon> hudIcons = new List<CursorHudIcon>();

    /// <summary>IpcStateCoordinator.ApplyStateEditParcel から呼ばれる初期化。</summary>
    public void Initialize(ParcelEditorManager editorManager)
    {
        manager = editorManager;

        // 項目リスト。状態の読み書きはすべてFunc/Action経由なので一度だけ構築すればよい。
        items = new List<RadialMenuItem>();
        foreach (var info in ParcelEditorManager.Modes)
        {
            if (info.HiddenFromMenu) continue;

            icons[info.Id] = LoadIcon(info.IconName);

            // ループ変数をクロージャに閉じ込めるためローカルへ退避する
            ParcelEditorManager.ModeInfo mode = info;
            items.Add(new RadialMenuItem
            {
                Id = mode.Id,
                Label = mode.Label,
                GetIcon = () => icons.TryGetValue(mode.Id, out var sprite) ? sprite : null,
                GetState = () => manager != null && manager.CurrentMode == mode.Id,
                OnToggle = () => manager?.SetCreationMode(mode.Id),
            });
        }
    }

    private void Update()
    {
        if (manager == null) return;

        bool active = manager.IsActive;

        // HUDの登録/解除(作成モード開始・終了の検知)
        if (active && !hudActive)
        {
            CursorStatusHud.EnsureInstance().SetProvider(BuildHudContent);
            hudActive = true;
        }
        else if (!active && hudActive)
        {
            CursorStatusHud.Instance?.ClearProvider();
            hudActive = false;
            if (menuOpenedByThis)
            {
                RadialMenuController.Instance?.CloseAndCancel();
                menuOpenedByThis = false;
            }
        }

        if (!active) return;

        bool ctrlHeld = InputUtil.KeyPressed(Key.LeftCtrl) || InputUtil.KeyPressed(Key.RightCtrl);
        bool ctrlPressedNow = InputUtil.KeyWasPressedThisFrame(Key.LeftCtrl) || InputUtil.KeyWasPressedThisFrame(Key.RightCtrl);

        if (ctrlPressedNow && !RadialMenuController.IsOpen)
        {
            // マウスボタンを押している間は開かない(押下中のジェスチャ=角度ドラッグ等が宙に浮くため)
            if (!InputUtil.LeftMouseIsPressed && !InputUtil.RightMouseIsPressed && !InputUtil.MiddleMouseIsPressed)
            {
                RadialMenuController.EnsureInstance().Open(items, InputUtil.MousePosition);
                menuOpenedByThis = true;
            }
        }
        else if (menuOpenedByThis && RadialMenuController.IsOpen && !ctrlHeld)
        {
            RadialMenuController.Instance.CloseAndCommit();
            menuOpenedByThis = false;
        }
    }

    private void OnDisable()
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

    /// <summary>
    /// カーソルHUDの表示内容。現在の作成モードのアイコンと、
    /// 「モード名｜次の操作」のテキストを返す。毎フレーム呼ばれるため軽量に保つこと。
    ///
    /// 選択済みのときは「パネルで確定」を案内する(確定ボタンはTauri側にあり、
    /// 3Dビューだけを見ていると次に何をすればよいか分からないため)。
    /// </summary>
    private (List<CursorHudIcon> icons, string text) BuildHudContent()
    {
        hudIcons.Clear();
        if (manager == null) return (hudIcons, "");

        ParcelEditorManager.ModeInfo info = manager.CurrentModeInfo;
        hudIcons.Add(new CursorHudIcon(
            icons.TryGetValue(info.Id, out var sprite) ? sprite : null, true));

        string action = manager.HasSelection() ? "パネルで確定 (Escで選び直し)" : info.Hint;
        return (hudIcons, $"{info.Label}｜{action}");
    }

    /// <summary>
    /// Resources/UIIcons からアイコンSpriteをロードする。
    /// テクスチャの Sprite Mode が Multiple でインポートされていると単体ロードが null になるため、
    /// その場合はサブスプライトの先頭を採用する(正しい設定は Single)。
    /// </summary>
    private static Sprite LoadIcon(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var sprite = Resources.Load<Sprite>($"UIIcons/{name}");
        if (sprite != null) return sprite;
        var all = Resources.LoadAll<Sprite>($"UIIcons/{name}");
        return all != null && all.Length > 0 ? all[0] : null;
    }
}
