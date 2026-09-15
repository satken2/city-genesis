using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// パス敷設中の設定操作(曲線/直線・吸着トグル)をUnity内で完結させるドライバ。
/// RoadPlanManager と同じGameObjectに常駐し、敷設セッションの有無を毎フレーム監視して
/// 自律的に動作する(フェーズをInactiveへ戻す経路が複数あるため、各出口へのフック追加より
/// ポーリングの方が漏れがない)。
///
/// 操作系:
/// - Ctrl押下: カーソル位置からラジアルメニューを展開。マウスで項目をハイライトし、
///   Ctrlを離すとその項目をトグル(RadialMenuController)。
/// - セッション中はカーソル脇に現在設定(曲線/吸着アイコン+標高/盛土)を常時表示(CursorStatusHud)。
/// - 標高(PageUp/PageDown, 1/2)・盛土(3/4)のキー操作は RoadPlanManager 側の既存実装が担う。
///
/// 吸着フラグ(TerrainInputHandler.Enable*)と曲線モード(RoadPlanManager.Curved)の真実源は
/// Unity側にあり、UIパネルからの設定イベントは存在しない。
/// </summary>
public class RoadEditControlMenu : MonoBehaviour
{
    private RoadPlanManager rpm;
    private List<RadialMenuItem> items;
    private bool hudActive = false;
    private bool menuOpenedByThis = false;

    /* ---- アイコンSprite(Resources/UIIcons、未配置ならnull=ラベル頭文字で代替表示) ---- */
    private Sprite iconBezier;
    private Sprite iconLine;
    private Sprite iconNodeSnap;
    private Sprite iconRoadSnap;
    private Sprite iconRightAngle;
    private Sprite iconGrid;
    private Sprite iconParallel;
    private Sprite iconAlignEnds;
    private Sprite iconAxisHorizontal;
    private Sprite iconAxisVertical;

    /// <summary>HUD providerが毎フレーム返すリスト。GC回避のため使い回す。</summary>
    private readonly List<CursorHudIcon> hudIcons = new List<CursorHudIcon>();

    /// <summary>
    /// 地物への吸着が働くバリアントか。駅舎パス敷設中は吸着を持たない(RoadPlanManager.SnappingSuppressed)ため、
    /// メニュー項目をグレーにし、HUDのアイコンもOFFで描いて「押しても効かない」ことを見せる。
    /// </summary>
    private bool SnapUsable => rpm != null && !rpm.SnappingSuppressed;

    /// <summary>RoadPlanManager.Start から呼ばれる初期化。</summary>
    public void Initialize(RoadPlanManager manager)
    {
        rpm = manager;

        iconBezier = LoadIcon("bezier");
        iconLine = LoadIcon("line");
        iconNodeSnap = LoadIcon("node-snap");
        iconRoadSnap = LoadIcon("road-snap");
        iconRightAngle = LoadIcon("90-snap");
        iconGrid = LoadIcon("grid");
        iconParallel = CreateParallelIcon(false);
        iconAlignEnds = CreateParallelIcon(true);
        iconAxisHorizontal = CreateAxisArrowIcon(false);
        iconAxisVertical = CreateAxisArrowIcon(true);

        // 項目リスト。状態の読み書きはすべてFunc/Action経由なので一度だけ構築すればよい。
        items = new List<RadialMenuItem>
        {
            new RadialMenuItem
            {
                Id = "curve",
                Label = "Curve/Straight",
                GetIcon = () => rpm.Curved ? iconBezier : iconLine,
                GetState = () => rpm.Curved,
                IsEnabled = () => rpm.IsCurveAllowed(),
                OnToggle = () => rpm.SetCurved(!rpm.Curved),
            },
            new RadialMenuItem
            {
                Id = "node_snap",
                Label = "Node Snap",
                GetIcon = () => iconNodeSnap,
                GetState = () => SnapUsable && TerrainInputHandler.EnableNodeSnapping,
                IsEnabled = () => SnapUsable,
                OnToggle = () => TerrainInputHandler.EnableNodeSnapping = !TerrainInputHandler.EnableNodeSnapping,
            },
            new RadialMenuItem
            {
                Id = "path_snap",
                Label = "Path Snap",
                GetIcon = () => iconRoadSnap,
                GetState = () => SnapUsable && TerrainInputHandler.EnablePathSnapping,
                IsEnabled = () => SnapUsable,
                OnToggle = () => TerrainInputHandler.EnablePathSnapping = !TerrainInputHandler.EnablePathSnapping,
            },
            new RadialMenuItem
            {
                Id = "straight_ext",
                Label = "Straight Ext",
                GetIcon = () => iconLine,
                GetState = () => SnapUsable && TerrainInputHandler.EnableStraightLineSnapping,
                IsEnabled = () => SnapUsable,
                OnToggle = () => TerrainInputHandler.EnableStraightLineSnapping = !TerrainInputHandler.EnableStraightLineSnapping,
            },
            new RadialMenuItem
            {
                Id = "right_angle",
                Label = "Right Angle",
                GetIcon = () => iconRightAngle,
                GetState = () => SnapUsable && TerrainInputHandler.EnableRightAngleSnapping,
                IsEnabled = () => SnapUsable,
                OnToggle = () => TerrainInputHandler.EnableRightAngleSnapping = !TerrainInputHandler.EnableRightAngleSnapping,
            },
            new RadialMenuItem
            {
                Id = "parallel",
                Label = "Parallel",
                GetIcon = () => iconParallel,
                GetState = () => rpm.ParallelSnapEnabled,
                IsEnabled = () => SnapUsable && !rpm.Curved,
                OnToggle = () => rpm.SetParallelSnapping(!RoadPlanManager.EnableParallelSnapping),
            },
            new RadialMenuItem
            {
                Id = "align_ends",
                Label = "Align Ends",
                GetIcon = () => iconAlignEnds,
                GetState = () => rpm.EndAlignmentEnabled,
                IsEnabled = () => rpm.ParallelSnapEnabled,
                OnToggle = () => rpm.SetEndAlignment(!RoadPlanManager.EnableEndAlignment),
            },
            new RadialMenuItem
            {
                Id = "grid",
                Label = "Grid",
                GetIcon = () => iconGrid,
                // グリッドは「表示⇔吸着連動」のためGridOverlayの表示状態を正とする
                GetState = () => GridOverlay.Instance != null && GridOverlay.Instance.IsVisible,
                IsEnabled = () => GridOverlay.Instance != null,
                OnToggle = () => GridOverlay.Instance?.ToggleVisibility(),
            },
        };
    }

    private void Update()
    {
        if (rpm == null) return;

        bool active = rpm.IsSessionActive;

        // HUDの登録/解除(セッション開始・終了の検知)
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
            // ドラッグ中(ノード/制御点の移動・カメラ回転)にメニューを開くと掴みが宙に浮くため、
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
            rpm.RefreshSnapPreview();
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
    /// カーソルHUDの表示内容。ノード選択中の移動軸矢印(横=XZ移動、縦=高さ変更)、
    /// 曲線/直線+吸着7種のアイコン(OFFは半透明)と、標高(H)・盛土(E)のテキストを返す。
    /// 毎フレーム呼ばれるため軽量に保つこと。
    /// </summary>
    private (List<CursorHudIcon> icons, string text) BuildHudContent()
    {
        hudIcons.Clear();

        // 移動軸はノード選択中だけ意味を持つので、その間だけ先頭(カーソルに最も近い位置)に出す
        if (rpm.HasSelectedNode)
        {
            hudIcons.Add(new CursorHudIcon(rpm.IsVerticalDragMode ? iconAxisVertical : iconAxisHorizontal, true));
        }

        // 曲線/直線(曲線許可バリアントのみ表示。常にactive=トグルの現在面を示す)
        if (rpm.IsCurveAllowed())
        {
            hudIcons.Add(new CursorHudIcon(rpm.Curved ? iconBezier : iconLine, true));
        }
        hudIcons.Add(new CursorHudIcon(iconNodeSnap, SnapUsable && TerrainInputHandler.EnableNodeSnapping));
        hudIcons.Add(new CursorHudIcon(iconRoadSnap, SnapUsable && TerrainInputHandler.EnablePathSnapping));
        hudIcons.Add(new CursorHudIcon(iconLine, SnapUsable && TerrainInputHandler.EnableStraightLineSnapping));
        hudIcons.Add(new CursorHudIcon(iconRightAngle, SnapUsable && TerrainInputHandler.EnableRightAngleSnapping));
        hudIcons.Add(new CursorHudIcon(iconParallel, rpm.ParallelSnapEnabled));
        hudIcons.Add(new CursorHudIcon(iconAlignEnds, rpm.EndAlignmentEnabled));
        hudIcons.Add(new CursorHudIcon(iconGrid, GridOverlay.Instance != null && GridOverlay.Instance.IsVisible));

        // テキストはASCIIのみ(組み込みフォントが日本語グリフ非対応のため)
        string text = "";
        if (rpm.IsHeightEditable())
        {
            text = $"H {FormatMeters(rpm.DisplayHeight)}";
        }
        float embankment = rpm.DisplayEmbankment;
        if (Mathf.Abs(embankment) > 0.01f)
        {
            text += (text.Length > 0 ? "  " : "") + $"E {FormatMeters(embankment)}";
        }
        // 線形: 最小曲線半径(R。直線は"-")と最大勾配(G)。規格を超えた指標だけに "!" を前置する
        if (rpm.HasAlignmentMetrics)
        {
            float radius = rpm.AlignmentMinRadius;
            string radiusText = float.IsInfinity(radius) || radius >= 1e5f ? "R -" : $"R {Mathf.RoundToInt(radius)}m";
            string radiusMark = rpm.HasCurveViolation ? "!" : "";
            // 境界付近の超過が丸めで上限と同じ表示になっても、比較記号で違反理由を明示する。
            string gradeText = rpm.HasGradeViolation
                ? $"!G {rpm.AlignmentMaxGradePct:0.00}% (>{rpm.AlignmentGradeLimit:0.##}%)"
                : $"G {rpm.AlignmentMaxGradePct:0.00}%";
            text += (text.Length > 0 ? "  " : "") + $"{radiusMark}{radiusText}  {gradeText}";
        }
        return (hudIcons, text);
    }

    private static string FormatMeters(float value)
    {
        int rounded = Mathf.RoundToInt(value);
        return rounded > 0 ? $"+{rounded}m" : $"{rounded}m";
    }

    private void OnDestroy()
    {
        if (iconParallel != null) { Destroy(iconParallel.texture); Destroy(iconParallel); }
        if (iconAlignEnds != null) { Destroy(iconAlignEnds.texture); Destroy(iconAlignEnds); }
        if (iconAxisHorizontal != null) { Destroy(iconAxisHorizontal.texture); Destroy(iconAxisHorizontal); }
        if (iconAxisVertical != null) { Destroy(iconAxisVertical.texture); Destroy(iconAxisVertical); }
    }

    /// <summary>
    /// 選択ノードの移動軸を示す両向き矢印(横=XZ移動、縦=高さ変更)を図形アイコンとして描く。
    /// 縦矢印は横矢印の座標を転置して描く。
    /// </summary>
    private static Sprite CreateAxisArrowIcon(bool vertical)
    {
        const int size = 32;
        const int half = size / 2;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = vertical ? "Axis Vertical Icon" : "Axis Horizontal Icon";
        var pixels = new Color32[size * size];
        for (int v = 0; v < size; v++)
        for (int u = 0; u < size; u++)
        {
            // u=矢印の軸方向、v=軸に直交する方向。軸線は中央の太さ3px、両端は幅が線形に広がる矢じり。
            int dv = Mathf.Abs(v - half);
            int distToEnd = Mathf.Min(u - 3, 28 - u);
            bool shaft = dv <= 1 && u >= 6 && u <= 25;
            bool head = distToEnd >= 0 && distToEnd <= 6 && dv <= distToEnd;
            if (!shaft && !head) continue;
            int x = vertical ? v : u;
            int y = vertical ? u : v;
            pixels[y * size + x] = new Color32(255, 255, 255, 255);
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    /// <summary>並行する2本線と、両端を揃える横棒を小さな図形アイコンとして描く。</summary>
    private static Sprite CreateParallelIcon(bool alignEnds)
    {
        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = alignEnds ? "Align Ends Icon" : "Parallel Icon";
        var pixels = new Color32[size * size];
        for (int y = 4; y < 28; y++)
        for (int x = 4; x < 28; x++)
        {
            bool vertical = (x >= 9 && x <= 11) || (x >= 20 && x <= 22);
            bool bar = alignEnds && ((y >= 4 && y <= 6) || (y >= 25 && y <= 27));
            if (vertical || bar) pixels[y * size + x] = new Color32(255, 255, 255, 255);
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    /// <summary>
    /// Resources/UIIconsからロードする。Multipleで取り込まれた場合は先頭のサブSpriteを使う。
    /// </summary>
    private static Sprite LoadIcon(string name)
    {
        var sprite = Resources.Load<Sprite>($"UIIcons/{name}");
        if (sprite != null) return sprite;
        var all = Resources.LoadAll<Sprite>($"UIIcons/{name}");
        return all != null && all.Length > 0 ? all[0] : null;
    }
}
