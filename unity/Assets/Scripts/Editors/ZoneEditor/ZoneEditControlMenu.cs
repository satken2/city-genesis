using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地区編集モード中のツール切替(新規作成 / 既存地区の拡張 / 地区の統合)をUnity内で完結させるドライバ。
/// ZoneEditContext と同じGameObjectに常駐し、地区編集がアクティブかをポーリングして自律動作する
/// (Ctrlラジアルメニュー・カーソルHUDの開閉は共通基底 <see cref="EditControlMenuBase"/> が担う)。
/// ONリング=現在ツール。
///
/// カーソルHUDには現在ツールに加えて「操作対象の色」を常時出す。
/// 新規作成ツールならこれから作る地区の色、拡張・統合ツールなら選択中地区の色と名前を出す。
///
/// ツールの真実源は ZoneEditContext.CurrentTool にあり、切替イベントは存在しない
/// (吸着・曲線と同じくUnity側が真実源)。
/// </summary>
public class ZoneEditControlMenu : EditControlMenuBase
{
    private ZoneEditContext ctx;
    private List<RadialMenuItem> items;

    /* ---- アイコンSprite(Resources/UIIcons、未配置ならnull=ラベル頭文字で代替表示) ---- */
    private Sprite iconCreate;
    private Sprite iconExpand;
    private Sprite iconMerge;

    /// <summary>色チップ用の塗りつぶし円Sprite(実行時生成。全インスタンス共有)。</summary>
    private static Sprite colorChipSprite;

    protected override bool IsSessionActive => ctx != null && ctx.IsActive;
    protected override List<RadialMenuItem> MenuItems => items;

    /// <summary>IpcStateCoordinator.ApplyStateEditZone から呼ばれる初期化。</summary>
    public void Initialize(ZoneEditContext context)
    {
        ctx = context;

        iconCreate = Utils.LoadUIIcon("zone-create");
        iconExpand = Utils.LoadUIIcon("zone-expand");
        iconMerge = Utils.LoadUIIcon("zone-merge");

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

    /// <summary>
    /// カーソルHUDの表示内容。「現在ツールのアイコン」＋「操作対象の色チップ」と、
    /// ツール名・対象地区名のテキストを返す。
    ///
    /// 色チップは、作成ツールならこれから作る地区の色(ZoneEditContext.PreviewColor)、
    /// 拡張・統合ツールなら選択中地区の識別色。どの地区に対して操作しているのかを
    /// カーソルから目を離さずに確認できるようにするための表示。
    /// </summary>
    protected override (List<CursorHudIcon> icons, string text) BuildHudContent()
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
}
