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

    /// <summary>HUD providerが毎フレーム返すリスト。GC回避のため使い回す。</summary>
    private readonly List<CursorHudIcon> hudIcons = new List<CursorHudIcon>();

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
                GetState = () => TerrainInputHandler.EnableNodeSnapping,
                OnToggle = () => TerrainInputHandler.EnableNodeSnapping = !TerrainInputHandler.EnableNodeSnapping,
            },
            new RadialMenuItem
            {
                Id = "path_snap",
                Label = "Path Snap",
                GetIcon = () => iconRoadSnap,
                GetState = () => TerrainInputHandler.EnablePathSnapping,
                OnToggle = () => TerrainInputHandler.EnablePathSnapping = !TerrainInputHandler.EnablePathSnapping,
            },
            new RadialMenuItem
            {
                Id = "straight_ext",
                Label = "Straight Ext",
                GetIcon = () => iconLine,
                GetState = () => TerrainInputHandler.EnableStraightLineSnapping,
                OnToggle = () => TerrainInputHandler.EnableStraightLineSnapping = !TerrainInputHandler.EnableStraightLineSnapping,
            },
            new RadialMenuItem
            {
                Id = "right_angle",
                Label = "Right Angle",
                GetIcon = () => iconRightAngle,
                GetState = () => TerrainInputHandler.EnableRightAngleSnapping,
                OnToggle = () => TerrainInputHandler.EnableRightAngleSnapping = !TerrainInputHandler.EnableRightAngleSnapping,
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
    /// カーソルHUDの表示内容。曲線/直線+吸着5種のアイコン(OFFは半透明)と、
    /// 標高(H)・盛土(E)のテキストを返す。毎フレーム呼ばれるため軽量に保つこと。
    /// </summary>
    private (List<CursorHudIcon> icons, string text) BuildHudContent()
    {
        hudIcons.Clear();

        // 曲線/直線(曲線許可バリアントのみ表示。常にactive=トグルの現在面を示す)
        if (rpm.IsCurveAllowed())
        {
            hudIcons.Add(new CursorHudIcon(rpm.Curved ? iconBezier : iconLine, true));
        }
        hudIcons.Add(new CursorHudIcon(iconNodeSnap, TerrainInputHandler.EnableNodeSnapping));
        hudIcons.Add(new CursorHudIcon(iconRoadSnap, TerrainInputHandler.EnablePathSnapping));
        hudIcons.Add(new CursorHudIcon(iconLine, TerrainInputHandler.EnableStraightLineSnapping));
        hudIcons.Add(new CursorHudIcon(iconRightAngle, TerrainInputHandler.EnableRightAngleSnapping));
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
        return (hudIcons, text);
    }

    private static string FormatMeters(float value)
    {
        int rounded = Mathf.RoundToInt(value);
        return rounded > 0 ? $"+{rounded}m" : $"{rounded}m";
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
