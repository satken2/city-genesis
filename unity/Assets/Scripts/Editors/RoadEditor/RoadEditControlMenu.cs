using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Driver for settings during path construction (curved/straight, snapping toggles), entirely within Unity.
/// Resides on the RoadPlanManager GameObject and independently polls whether a construction session exists
/// (the shared base <see cref="EditControlMenuBase"/> opens/closes the Ctrl radial menu and cursor HUD).
///
/// - During a session, show current settings (curve/snap icons + elevation/embankment) beside the cursor.
/// - Existing RoadPlanManager controls handle elevation (PageUp/PageDown, 1/2) and embankment (3/4).
///
/// Unity owns snap flags (TerrainInputHandler.Enable*) and curve mode (RoadPlanManager.Curved);
/// no UI panel settings events exist for them.
/// </summary>
public class RoadEditControlMenu : EditControlMenuBase
{
    private RoadPlanManager rpm;
    private List<RadialMenuItem> items;

    /* ---- Icon Sprites (Resources/UIIcons; null when absent, falling back to the first label character) ---- */
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

    /// <summary>
    /// Whether this variant supports snapping to features. Station building paths suppress snapping (RoadPlanManager.SnappingSuppressed),
    /// so gray out menu items and show HUD icons OFF to indicate they are ineffective.
    /// </summary>
    private bool SnapUsable => rpm != null && !rpm.SnappingSuppressed;

    protected override bool IsSessionActive => rpm != null && rpm.IsSessionActive;
    protected override List<RadialMenuItem> MenuItems => items;

    /// <summary>Initialization called from RoadPlanManager.Start.</summary>
    public void Initialize(RoadPlanManager manager)
    {
        rpm = manager;

        iconBezier = Utils.LoadUIIcon("bezier");
        iconLine = Utils.LoadUIIcon("line");
        iconNodeSnap = Utils.LoadUIIcon("node-snap");
        iconRoadSnap = Utils.LoadUIIcon("road-snap");
        iconRightAngle = Utils.LoadUIIcon("90-snap");
        iconGrid = Utils.LoadUIIcon("grid");
        iconParallel = CreateParallelIcon(false);
        iconAlignEnds = CreateParallelIcon(true);
        iconAxisHorizontal = CreateAxisArrowIcon(false);
        iconAxisVertical = CreateAxisArrowIcon(true);

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
                // Grid display and snapping are linked, so GridOverlay visibility is authoritative.
                GetState = () => GridOverlay.Instance != null && GridOverlay.Instance.IsVisible,
                IsEnabled = () => GridOverlay.Instance != null,
                OnToggle = () => GridOverlay.Instance?.ToggleVisibility(),
            },
        };
    }

    /// <summary>Recalculates the hover snap preview because snap flags may have changed.</summary>
    protected override void OnMenuCommitted()
    {
        rpm.RefreshSnapPreview();
    }

    /// <summary>
    /// Cursor HUD content: movement-axis arrows for selected nodes (horizontal=XZ, vertical=height),
    /// curve/straight and 7 snap icons (translucent when OFF), and elevation (H)/embankment (E) text.
    /// </summary>
    protected override (List<CursorHudIcon> icons, string text) BuildHudContent()
    {
        hudIcons.Clear();

        // The movement axis matters only while a node is selected; show it first (nearest the cursor) only then.
        if (rpm.HasSelectedNode)
        {
            hudIcons.Add(new CursorHudIcon(rpm.IsVerticalDragMode ? iconAxisVertical : iconAxisHorizontal, true));
        }

        // Curve/straight (only for curve-enabled variants; always active to indicate the current toggle face).
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

        // ASCII-only text (the built-in font has no Japanese glyphs).
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

    private void OnDestroy()
    {
        if (iconParallel != null) { Destroy(iconParallel.texture); Destroy(iconParallel); }
        if (iconAlignEnds != null) { Destroy(iconAlignEnds.texture); Destroy(iconAlignEnds); }
        if (iconAxisHorizontal != null) { Destroy(iconAxisHorizontal.texture); Destroy(iconAxisHorizontal); }
        if (iconAxisVertical != null) { Destroy(iconAxisVertical.texture); Destroy(iconAxisVertical); }
    }

    /// <summary>
    /// Draws a double-headed arrow indicating the selected node's movement axis (horizontal=XZ, vertical=height).
    /// Draws the vertical arrow by transposing the horizontal coordinates.
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
            // u=along the arrow axis; v=perpendicular. A central 3px shaft with linearly widening arrowheads at both ends.
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

    /// <summary>Draws a small icon of two parallel lines and crossbars aligning their ends.</summary>
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
}
