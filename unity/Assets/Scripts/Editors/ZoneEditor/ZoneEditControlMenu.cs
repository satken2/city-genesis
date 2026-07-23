using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 地区編集モード中のツール切替(新規地区の作成 / 既存地区の拡張)をUnity内で完結させるドライバ。
/// ZoneEditContext と同じGameObjectに常駐し、地区編集がアクティブかを毎フレーム監視して自律的に動作する
/// (道路敷設の RoadEditControlMenu と同型。編集終了経路が複数あるためフックよりポーリングが漏れない)。
///
/// 操作系:
/// - Ctrl押下: カーソル位置からラジアルメニューを展開。マウスで項目をハイライトし、
///   Ctrlを離すとその項目を選択(RadialMenuController)。ONリング=現在ツール。
/// - 編集中はカーソル脇に現在ツールを常時表示(CursorStatusHud)。
///
/// ツールの真実源は ZoneEditContext.CurrentTool にあり、切替イベントは存在しない
/// (吸着・曲線と同じくUnity側が真実源)。
/// </summary>
public class ZoneEditControlMenu : MonoBehaviour
{
    private ZoneEditContext ctx;
    private List<RadialMenuItem> items;
    private bool hudActive = false;
    private bool menuOpenedByThis = false;

    /* ---- アイコンSprite(Resources/UIIcons、未配置ならnull=ラベル頭文字で代替表示) ---- */
    private Sprite iconCreate;
    private Sprite iconExpand;

    /// <summary>HUD providerが毎フレーム返すリスト。GC回避のため使い回す。</summary>
    private readonly List<CursorHudIcon> hudIcons = new List<CursorHudIcon>();

    /// <summary>IpcStateCoordinator.ApplyStateEditZone から呼ばれる初期化。</summary>
    public void Initialize(ZoneEditContext context)
    {
        ctx = context;

        iconCreate = LoadIcon("zone-create");
        iconExpand = LoadIcon("zone-expand");

        // 項目リスト。状態の読み書きはすべてFunc/Action経由なので一度だけ構築すればよい。
        items = new List<RadialMenuItem>
        {
            new RadialMenuItem
            {
                Id = "create_zone",
                Label = "新規地区の作成",
                GetIcon = () => iconCreate,
                GetState = () => ctx != null && ctx.CurrentTool == ZoneEditTool.Create,
                OnToggle = () => ctx?.SetTool(ZoneEditTool.Create),
            },
            new RadialMenuItem
            {
                Id = "expand_zone",
                Label = "既存地区の拡張",
                GetIcon = () => iconExpand,
                GetState = () => ctx != null && ctx.CurrentTool == ZoneEditTool.Expand,
                OnToggle = () => ctx?.SetTool(ZoneEditTool.Expand),
            },
        };
    }

    private void Update()
    {
        if (ctx == null) return;

        bool active = ctx.IsActive;

        // HUDの登録/解除(編集モード開始・終了の検知)
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
            // ドラッグ中(地区拡張のShift+ドラッグ等)にメニューを開くと掴みが宙に浮くため、
            // マウスボタンを押している間は開かない
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
    /// カーソルHUDの表示内容。現在ツールのアイコン(常にactive)と、
    /// ツール名+選択中地区のASCIIテキストを返す。毎フレーム呼ばれるため軽量に保つこと。
    /// (組み込みフォントが日本語グリフ非対応のためHUDテキストはASCII)
    /// </summary>
    private (List<CursorHudIcon> icons, string text) BuildHudContent()
    {
        hudIcons.Clear();
        if (ctx == null) return (hudIcons, "");

        bool isCreate = ctx.CurrentTool == ZoneEditTool.Create;
        hudIcons.Add(new CursorHudIcon(isCreate ? iconCreate : iconExpand, true));

        string text;
        if (isCreate)
        {
            text = "Create";
        }
        else
        {
            text = ctx.CurrentZoneId.HasValue ? $"Expand #{ctx.CurrentZoneId.Value}" : "Expand";
        }
        return (hudIcons, text);
    }

    /// <summary>
    /// Resources/UIIcons からアイコンSpriteをロードする。
    /// テクスチャの Sprite Mode が Multiple でインポートされていると単体ロードが null になるため、
    /// その場合はサブスプライトの先頭を採用する(正しい設定は Single)。
    /// </summary>
    private static Sprite LoadIcon(string name)
    {
        var sprite = Resources.Load<Sprite>($"UIIcons/{name}");
        if (sprite != null) return sprite;
        var all = Resources.LoadAll<Sprite>($"UIIcons/{name}");
        return all != null && all.Length > 0 ? all[0] : null;
    }
}
