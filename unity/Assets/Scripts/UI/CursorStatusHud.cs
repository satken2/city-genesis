using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One icon displayed in the cursor-following HUD.
/// Icons with active=false are translucent, such as disabled snapping options.
/// </summary>
public struct CursorHudIcon
{
    public Sprite sprite;
    public bool active;

    public CursorHudIcon(Sprite sprite, bool active)
    {
        this.sprite = sprite;
        this.active = active;
    }
}

/// <summary>
/// General-purpose HUD showing current editing settings beside the cursor as icons and short text.
/// Used for road curve/snapping states and height, but independent of any specific mode.
///
/// Usage: register a function returning display content with SetProvider() when editing starts,
/// and remove it with ClearProvider() when editing ends. The provider runs every frame,
/// so it must only read current state without expensive processing.
///
/// Design notes:
/// - Separate from TooltipManager because temporary text display has a different responsibility.
///   Use a different cursor offset from tooltips (+15,-15) to avoid overlap.
/// - Generate the Canvas in code as TooltipManager does. Do not add GraphicRaycaster,
///   avoiding interference with InputHandler's EventSystem checks.
/// - Text uses UiFontProvider's multilingual font, including Japanese support.
/// - Pool icon Images and only replace sprites/colors each frame to reduce GC and Rebuild work.
/// - Hide automatically while the radial menu shows the same information at a larger size.
/// </summary>
public class CursorStatusHud : MonoBehaviour
{
    public static CursorStatusHud Instance { get; private set; }

    /* ---- Layout constants ---- */
    private const float ICON_SIZE = 22f;
    private const float ICON_SPACING = 3f;
    private const float PADDING = 5f;
    private const int MAX_ICONS = 9;  // Road editing: 1 movement-axis arrow + 1 curve icon + 7 snapping icons.
    private const float OFFSET_X = 18f;   // Cursor offset, avoiding tooltip overlap.
    private const float OFFSET_Y = -40f;
    // Dim OFF icons to dark gray with lower alpha so they blend into the dark panel
    // and remain clearly distinguishable from white ON icons.
    private static readonly Color ICON_COLOR_ACTIVE = Color.white;
    private static readonly Color ICON_COLOR_INACTIVE = new Color(0.22f, 0.22f, 0.22f, 0.55f);

    private Canvas canvas;
    private RectTransform panel;
    private Image panelBg;
    private readonly Image[] iconPool = new Image[MAX_ICONS];
    private Text textComponent;

    private Func<(List<CursorHudIcon> icons, string text)> provider;

    /// <summary>
    /// Get the singleton, creating it if needed; no scene placement is required.
    /// </summary>
    public static CursorStatusHud EnsureInstance()
    {
        if (Instance == null)
        {
            GameObject go = new GameObject("CursorStatusHud");
            Instance = go.AddComponent<CursorStatusHud>();
            DontDestroyOnLoad(go);
        }
        return Instance;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        GameObject root = new GameObject("CursorStatusHudCanvas");
        root.transform.SetParent(transform, false);
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Behind tooltips (100), in front of normal UI.
        canvas.sortingOrder = 95;
        root.AddComponent<CanvasScaler>();

        GameObject panelGO = new GameObject("HudPanel");
        panelGO.transform.SetParent(root.transform, false);
        panel = panelGO.AddComponent<RectTransform>();
        panel.anchorMin = Vector2.zero;
        panel.anchorMax = Vector2.zero;
        panel.pivot = new Vector2(0f, 1f);

        panelBg = panelGO.AddComponent<Image>();
        panelBg.color = new Color(0.1f, 0.1f, 0.1f, 0.7f);

        // Icon pool, arranged in one row.
        for (int i = 0; i < MAX_ICONS; i++)
        {
            GameObject iconGO = new GameObject($"HudIcon_{i}");
            iconGO.transform.SetParent(panel, false);
            Image img = iconGO.AddComponent<Image>();
            RectTransform rt = img.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(ICON_SIZE, ICON_SIZE);
            rt.anchoredPosition = new Vector2(PADDING + i * (ICON_SIZE + ICON_SPACING), -PADDING);
            img.enabled = false;
            iconPool[i] = img;
        }

        // Text below the icon row.
        GameObject textGO = new GameObject("HudText");
        textGO.transform.SetParent(panel, false);
        textComponent = textGO.AddComponent<Text>();
        // Use a multilingual font for strings such as district names that may contain Japanese.
        textComponent.font = UiFontProvider.GetFont(14);
        textComponent.fontSize = 14;
        textComponent.color = Color.white;
        textComponent.alignment = TextAnchor.UpperLeft;
        textComponent.horizontalOverflow = HorizontalWrapMode.Overflow;
        textComponent.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform textRT = textComponent.rectTransform;
        textRT.anchorMin = new Vector2(0f, 1f);
        textRT.anchorMax = new Vector2(0f, 1f);
        textRT.pivot = new Vector2(0f, 1f);
        textRT.anchoredPosition = new Vector2(PADDING, -(PADDING + ICON_SIZE + 3f));

        panelGO.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Register a display-content provider and show the HUD. Polled every frame.
    /// </summary>
    public void SetProvider(Func<(List<CursorHudIcon> icons, string text)> newProvider)
    {
        provider = newProvider;
    }

    /// <summary>Remove the provider and hide the HUD.</summary>
    public void ClearProvider()
    {
        provider = null;
        if (panel != null) panel.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (provider == null) return;

        // Hide while the radial menu shows the same information at a larger size.
        if (RadialMenuController.IsOpen)
        {
            panel.gameObject.SetActive(false);
            return;
        }

        var (icons, text) = provider();
        int iconCount = icons != null ? Mathf.Min(icons.Count, MAX_ICONS) : 0;
        bool hasText = !string.IsNullOrEmpty(text);

        if (iconCount == 0 && !hasText)
        {
            panel.gameObject.SetActive(false);
            return;
        }
        panel.gameObject.SetActive(true);

        for (int i = 0; i < MAX_ICONS; i++)
        {
            if (i < iconCount && icons[i].sprite != null)
            {
                iconPool[i].enabled = true;
                iconPool[i].sprite = icons[i].sprite;
                iconPool[i].color = icons[i].active ? ICON_COLOR_ACTIVE : ICON_COLOR_INACTIVE;
            }
            else
            {
                iconPool[i].enabled = false;
            }
        }

        textComponent.text = hasText ? text : "";

        // Size the panel to its content.
        float width = PADDING * 2f + Mathf.Max(
            iconCount * (ICON_SIZE + ICON_SPACING) - (iconCount > 0 ? ICON_SPACING : 0f),
            hasText ? textComponent.preferredWidth : 0f);
        float height = PADDING * 2f
            + (iconCount > 0 ? ICON_SIZE : 0f)
            + (hasText ? textComponent.preferredHeight + (iconCount > 0 ? 3f : 0f) : 0f);
        panel.sizeDelta = new Vector2(width, height);

        UpdatePosition();
    }

    /// <summary>
    /// Follow the cursor with the panel, switching sides near screen edges to prevent clipping.
    /// </summary>
    private void UpdatePosition()
    {
        Vector2 mousePos = InputUtil.MousePosition;
        float x = mousePos.x + OFFSET_X;
        float y = mousePos.y + OFFSET_Y;

        if (x + panel.sizeDelta.x > Screen.width)
            x = mousePos.x - panel.sizeDelta.x - OFFSET_X;
        if (y - panel.sizeDelta.y < 0)
            y = mousePos.y + panel.sizeDelta.y - OFFSET_Y;

        panel.anchoredPosition = new Vector2(x, y);
    }
}
