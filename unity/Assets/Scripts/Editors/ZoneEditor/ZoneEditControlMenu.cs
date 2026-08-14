using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 地区編集モード中のツール切替(新規作成 / 既存地区の拡張 / 地区の統合)をUnity内で完結させるドライバ。
/// ZoneEditContext と同じGameObjectに常駐し、地区編集がアクティブかを毎フレーム監視して自律的に動作する
/// (道路敷設の RoadEditControlMenu と同型。編集終了経路が複数あるためフックよりポーリングが漏れない)。
///
/// 操作系:
/// - Ctrl押下: カーソル位置からラジアルメニューを展開。マウスで項目をハイライトし、
///   Ctrlを離すとその項目を選択(RadialMenuController)。ONリング=現在ツール。
/// - 編集中はカーソル脇に現在ツールと「操作対象の色」を常時表示(CursorStatusHud)。
///   新規作成ツールならこれから作る地区の色、拡張・統合ツールなら選択中地区の色と名前を出す。
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
    private Sprite iconMerge;

    /// <summary>色チップ用の塗りつぶし円Sprite(実行時生成。全インスタンス共有)。</summary>
    private static Sprite colorChipSprite;

    /// <summary>HUD providerが毎フレーム返すリスト。GC回避のため使い回す。</summary>
    private readonly List<CursorHudIcon> hudIcons = new List<CursorHudIcon>();

    /// <summary>IpcStateCoordinator.ApplyStateEditZone から呼ばれる初期化。</summary>
    public void Initialize(ZoneEditContext context)
    {
        ctx = context;

        iconCreate = LoadIcon("zone-create");
        iconExpand = LoadIcon("zone-expand");
        iconMerge = LoadIcon("zone-merge");

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
            new RadialMenuItem
            {
                Id = "merge_zone",
                Label = "地区の統合",
                GetIcon = () => iconMerge,
                GetState = () => ctx != null && ctx.CurrentTool == ZoneEditTool.Merge,
                OnToggle = () => ctx?.SetTool(ZoneEditTool.Merge),
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
            // マウスボタンを押している間は開かない(押下中のジェスチャが宙に浮くため)
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
    /// カーソルHUDの表示内容。「現在ツールのアイコン」＋「操作対象の色チップ」と、
    /// ツール名・対象地区名のテキストを返す。毎フレーム呼ばれるため軽量に保つこと。
    ///
    /// 色チップは、作成ツールならこれから作る地区の色(ZoneEditContext.PreviewColor)、
    /// 拡張・統合ツールなら選択中地区の識別色。どの地区に対して操作しているのかを
    /// カーソルから目を離さずに確認できるようにするための表示。
    /// </summary>
    private (List<CursorHudIcon> icons, string text) BuildHudContent()
    {
        hudIcons.Clear();
        if (ctx == null) return (hudIcons, "");

        ZoneEditTool tool = ctx.CurrentTool;
        Sprite toolIcon = tool == ZoneEditTool.Create ? iconCreate
            : tool == ZoneEditTool.Expand ? iconExpand
            : iconMerge;
        hudIcons.Add(new CursorHudIcon(toolIcon, true));

        string text;
        if (tool == ZoneEditTool.Create)
        {
            hudIcons.Add(new CursorHudIcon(EnsureColorChipSprite(), ctx.PreviewColor));
            text = "新規地区";
        }
        else
        {
            hudIcons.Add(new CursorHudIcon(EnsureColorChipSprite(), ctx.GetSelectedZoneColor()));
            string zoneName = ctx.GetSelectedZoneName();
            string label = tool == ZoneEditTool.Expand ? "拡張先" : "統合先";
            text = ctx.SelectedZoneId > 0
                ? $"{label}: {zoneName}"
                : $"{label}: 右クリックで地区を選択";
        }
        return (hudIcons, text);
    }

    /// <summary>
    /// 色チップ用の塗りつぶし円Spriteを実行時生成する(アセット不要にするため)。
    /// アイコン枠に収まる単色の丸で、CursorHudIcon の tint で地区色に塗られる。
    /// </summary>
    private static Sprite EnsureColorChipSprite()
    {
        if (colorChipSprite != null) return colorChipSprite;

        const int size = 32;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float center = (size - 1) * 0.5f;
        float radius = size * 0.5f - 1f;

        Color32[] pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                // 1pxのグラデーションで縁をアンチエイリアスする
                byte a = (byte)(255f * Mathf.Clamp01(radius - dist + 0.5f));
                pixels[y * size + x] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        colorChipSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return colorChipSprite;
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
