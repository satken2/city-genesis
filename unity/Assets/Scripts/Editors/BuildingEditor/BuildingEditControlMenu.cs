using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>建物の配置方法を選ぶCtrlメニュー、寸法ガイド、カーソルHUD。</summary>
public class BuildingEditControlMenu : MonoBehaviour
{
    private BuildingPlanManager manager;
    private bool hudActive, menuOwned;
    private readonly List<RadialMenuItem> items = new List<RadialMenuItem>();
    private readonly List<CursorHudIcon> icons = new List<CursorHudIcon>();
    private readonly Sprite[] modeIcons = new Sprite[3];
    private OverlayMeshRenderer overlay;
    private GUIStyle dimensionStyle;
    private Vector3 widthLabel, depthLabel;
    private bool guidesVisible;

    public void Initialize(BuildingPlanManager value)
    {
        manager = value;
        modeIcons[0] = Utils.LoadUIIcon("node-snap");
        modeIcons[1] = Utils.LoadUIIcon("grid");
        modeIcons[2] = Utils.LoadUIIcon("road-snap");
    }

    private string Label(int i) => manager.PlacementText(new[] { "parcel", "free", "roadside" }[i],
        new[] { "区画に配置", "自由配置", "道路沿い配置" }[i]);

    private void Update()
    {
        bool active = manager != null && manager.PlacementActive;
        if (active && !hudActive)
        {
            CursorStatusHud.EnsureInstance().SetProvider(BuildHud);
            hudActive = true;
        }
        if (!active) { ReleaseUi(); return; }
        bool ctrl = InputUtil.KeyPressed(Key.LeftCtrl) || InputUtil.KeyPressed(Key.RightCtrl);
        bool pressed = InputUtil.KeyWasPressedThisFrame(Key.LeftCtrl) || InputUtil.KeyWasPressedThisFrame(Key.RightCtrl);
        if (menuOwned && (!Application.isFocused || InputUtil.KeyWasPressedThisFrame(Key.Escape)))
        {
            RadialMenuController.Instance?.CloseAndCancel();
            menuOwned = false;
        }
        else if (menuOwned && RadialMenuController.IsOpen && !ctrl)
        {
            RadialMenuController.Instance.CloseAndCommit();
            menuOwned = false;
            manager.RefreshPlacementPreview();
        }
        else if (pressed && Application.isFocused && !RadialMenuController.IsOpen &&
            !(CameraController.Instance?.IsFlyMode ?? false) && !InputUtil.LeftMouseIsPressed &&
            !InputUtil.RightMouseIsPressed && !InputUtil.MiddleMouseIsPressed)
        {
            items.Clear();
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                items.Add(new RadialMenuItem
                {
                    Id = ((BuildingPlacementMode)i).ToString(),
                    Label = Label(i),
                    GetIcon = () => modeIcons[index],
                    GetState = () => manager.PlacementMode == (BuildingPlacementMode)index,
                    IsEnabled = () => index == 0 || manager.DirectAllowed,
                    OnToggle = () => manager.SetPlacementMode((BuildingPlacementMode)index),
                });
            }
            RadialMenuController.EnsureInstance().Open(items, InputUtil.MousePosition);
            menuOwned = true;
        }
    }

    private (List<CursorHudIcon> icons, string text) BuildHud()
    {
        icons.Clear();
        int mode = (int)manager.PlacementMode;
        icons.Add(new CursorHudIcon(modeIcons[mode], true));
        string text = Label(mode);
        if (mode == 0) return (icons, text + "\n" + manager.PlacementText("parcelHint", "区画をクリック · Ctrl 配置方法"));
        text += $" | {manager.PlacementText("frontage", "間口")} {manager.PlacementWidth:0.#}m × " +
            $"{manager.PlacementText("depth", "奥行き")} {manager.PlacementDepth:0.#}m";
        text += "\n" + (manager.PlacementCommitted ? manager.PlacementText("committed", "位置固定 · パネルで確定 · Esc 選び直し") :
            manager.PlacementText("placeHint", "左クリック 位置固定 · Ctrl 配置方法"));
        if (mode == 2) text += "\n" + (manager.HasRoadAnchor ? manager.PlacementText("snapped", "道路に吸着中") :
            manager.PlacementText("searching", "道路を探しています"));
        else if (manager.HasRoadAnchor) text += "\n" + manager.PlacementText("snapped", "道路に吸着中");
        text += "\n" + (manager.SizeEditable ? manager.PlacementText("sizeHint", "1/2 間口 · 3/4 奥行き · Shift 5m") :
            manager.PlacementText("fixed", "寸法固定"));
        if (mode == 1) text += " · " + manager.PlacementText("rotateHint", "中ドラッグ 回転");
        return (icons, text);
    }

    private void LateUpdate()
    {
        guidesVisible = manager != null && manager.PlacementVisible && Camera.main != null;
        if (!guidesVisible)
        {
            if (overlay != null) overlay.gameObject.SetActive(false);
            return;
        }
        // 頂点はワールド座標なので、編集用オブジェクトの子にせず変換を重ねない。
        if (overlay == null) overlay = OverlayMeshRenderer.Create("BuildingPlacementGuide", alwaysOnTop: true);
        overlay.gameObject.SetActive(true);
        overlay.BeginFrame();
        BuildingPlacementGeometry.Axes(manager.PlacementAngle, out var u, out var v);
        Vector3 center = manager.PlacementCenter;
        float w = manager.PlacementWidth * 0.5f, d = manager.PlacementDepth * 0.5f;
        Vector3 a = center - u * w - v * d, b = center + u * w - v * d;
        Vector3 c = center + u * w + v * d, e = center - u * w + v * d;
        DrawLine(a, b, new Color(0.3f, 0.9f, 1f), 2);
        DrawLine(b, c, Color.white, 1);
        DrawLine(c, e, Color.white, 1);
        DrawLine(e, a, Color.white, 1);
        Vector3 widthA = a - v * 2, widthB = b - v * 2;
        Vector3 depthA = b + u * 2, depthB = c + u * 2;
        DrawDimension(widthA, widthB, v, manager.ActiveSizeAxis == 1);
        DrawDimension(depthA, depthB, u, manager.ActiveSizeAxis == 2);
        widthLabel = Ground((widthA + widthB) * 0.5f);
        depthLabel = Ground((depthA + depthB) * 0.5f);
        if (manager.HasRoadAnchor)
        {
            DrawLine(manager.RoadGuideA, manager.RoadGuideB, Color.yellow, 3);
            overlay.AddCircle(Ground(manager.RoadFront), 6, Color.yellow);
        }
        overlay.EndFrame(Camera.main);
    }

    private static Vector3 Ground(Vector3 p) { p.y = Utils.GetGroundHeight(p) + 0.05f; return p; }

    private void DrawLine(Vector3 a, Vector3 b, Color color, float thickness)
    {
        int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / 10f), 1, 100);
        Vector3 previous = Ground(a);
        for (int i = 1; i <= steps; i++)
        {
            Vector3 next = Ground(Vector3.Lerp(a, b, (float)i / steps));
            overlay.AddSolidLine(previous, next, color, thickness);
            previous = next;
        }
    }

    private void DrawDimension(Vector3 a, Vector3 b, Vector3 tick, bool active)
    {
        Color color = active ? Color.yellow : new Color(0.6f, 0.9f, 1f);
        DrawLine(a, b, color, active ? 3 : 1.5f);
        DrawLine(a - tick, a + tick, color, 1.5f);
        DrawLine(b - tick, b + tick, color, 1.5f);
    }

    private void OnGUI()
    {
        if (!guidesVisible || Camera.main == null || RadialMenuController.IsOpen) return;
        if (dimensionStyle == null)
            dimensionStyle = new GUIStyle(GUI.skin.box) { font = UiFontProvider.GetFont(14), fontSize = 14, alignment = TextAnchor.MiddleCenter };
        DrawLabel(widthLabel, $"{manager.PlacementWidth:0.#}m" + (manager.SizeEditable ? " [1/2]" : ""));
        DrawLabel(depthLabel, $"{manager.PlacementDepth:0.#}m" + (manager.SizeEditable ? " [3/4]" : ""));
    }

    private void DrawLabel(Vector3 world, string text)
    {
        Vector3 screen = Camera.main.WorldToScreenPoint(world);
        if (screen.z <= 0) return;
        GUI.Label(new Rect(screen.x - 60, Screen.height - screen.y - 11, 120, 22), text, dimensionStyle);
    }

    private void ReleaseUi()
    {
        if (hudActive) { CursorStatusHud.Instance?.ClearProvider(); hudActive = false; }
        if (menuOwned) { RadialMenuController.Instance?.CloseAndCancel(); menuOwned = false; }
    }

    private void OnDisable() { ReleaseUi(); if (overlay != null) overlay.gameObject.SetActive(false); }
    private void OnDestroy()
    {
        ReleaseUi();
        if (overlay != null) Destroy(overlay.gameObject); // メッシュとマテリアルは OverlayMeshRenderer が解放する
    }
}
