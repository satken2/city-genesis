using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// General-purpose radial menu whose item icons pop outward in a circle from the cursor.
///
/// Interaction model, implemented by callers:
///   Press the trigger key to Open(), move the mouse to highlight an item,
///   then release the key to CloseAndCommit() and toggle the highlighted item.
///   Releasing near the center (dead zone) or on a disabled item does nothing.
///
/// Design notes:
/// - Generate a ScreenSpaceOverlay Canvas in code as TooltipManager does; no scene placement is required.
/// - Deliberately omit GraphicRaycaster. Adding one would trip InputHandler's
///   EventSystem.IsPointerOverGameObject check and incorrectly block 3D input.
///   Input suppression belongs to the InputHandler / CameraController gates checking IsOpen.
/// - Input systems must check static IsOpen to suppress 3D input and camera controls while open.
/// - If an icon Sprite is unavailable, such as missing Resources, display the label's first character instead.
/// </summary>
public class RadialMenuController : MonoBehaviour
{
    public static RadialMenuController Instance { get; private set; }

    /// <summary>Whether the menu is open, used as an input gate by InputHandler / CameraController.</summary>
    public static bool IsOpen { get; private set; }

    /* ---- Appearance and behavior constants ---- */
    private const float OPEN_DURATION = 0.15f;   // Expansion animation duration (seconds).
    private const float RADIUS = 100f;           // Distance from the center to each item (px).
    private const float DEAD_ZONE = 28f;         // Releasing within this distance cancels (px).
    private const float ITEM_SIZE = 64f;         // Item-circle diameter (px).
    private const float ICON_SIZE = 44f;         // Icon size within an item (px).
    private const float HOVER_SCALE = 1.15f;     // Hovered-item scale multiplier.

    private static readonly Color COLOR_BG_OFF = new Color(0.13f, 0.13f, 0.15f, 0.92f);
    private static readonly Color COLOR_BG_HOVER = new Color(0.25f, 0.27f, 0.30f, 0.95f);
    private static readonly Color COLOR_RING_ON = new Color(0.25f, 0.70f, 1.00f, 1.00f);
    private const float DISABLED_ALPHA = 0.3f;

    /* ---- Runtime-generated texture cache, shared by all instances ---- */
    private static Sprite circleSprite;
    private static Sprite ringSprite;

    private Canvas canvas;
    private RectTransform menuRoot;
    private Text centerLabel;

    /// <summary>UI elements for one expanded item.</summary>
    private class ItemView
    {
        public RadialMenuItem item;
        public RectTransform rect;
        public Image background;
        public Image ring;
        public Image icon;
        public Text fallbackText;
        public Vector2 direction;   // Unit vector from the center.
    }

    private readonly List<ItemView> views = new List<ItemView>();
    private Vector2 openCenter;
    private float openTime;
    private int hoveredIndex = -1;

    /// <summary>
    /// Get the singleton, creating it if needed; no scene placement is required.
    /// </summary>
    public static RadialMenuController EnsureInstance()
    {
        if (Instance == null)
        {
            GameObject go = new GameObject("RadialMenuController");
            Instance = go.AddComponent<RadialMenuController>();
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

        GameObject root = new GameObject("RadialMenuCanvas");
        root.transform.SetParent(transform, false);
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Display in front of tooltips (100).
        canvas.sortingOrder = 120;
        root.AddComponent<CanvasScaler>();

        GameObject menuGO = new GameObject("RadialMenuRoot");
        menuGO.transform.SetParent(root.transform, false);
        menuRoot = menuGO.AddComponent<RectTransform>();
        menuRoot.anchorMin = Vector2.zero;
        menuRoot.anchorMax = Vector2.zero;
        menuRoot.pivot = new Vector2(0.5f, 0.5f);

        // Center label, showing the hovered item's name; place just below center to avoid overlapping item circles.
        GameObject labelGO = new GameObject("RadialMenuLabel");
        labelGO.transform.SetParent(menuRoot, false);
        centerLabel = labelGO.AddComponent<Text>();
        centerLabel.font = GetUIFont(16);
        centerLabel.fontSize = 16;
        centerLabel.color = Color.white;
        centerLabel.alignment = TextAnchor.MiddleCenter;
        centerLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
        centerLabel.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform labelRT = centerLabel.rectTransform;
        labelRT.sizeDelta = new Vector2(10f, 20f);
        labelRT.anchoredPosition = new Vector2(0f, -26f);
        // Use black outlines so labels remain readable over bright terrain.
        Outline outline = labelGO.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
        outline.effectDistance = new Vector2(1f, -1f);

        menuGO.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
            IsOpen = false;
        }
    }

    /// <summary>
    /// Open the menu at screenCenter in screen coordinates, normally the mouse position.
    /// Rebuild it if already open.
    /// </summary>
    public void Open(IReadOnlyList<RadialMenuItem> items, Vector2 screenCenter)
    {
        ClearViews();
        if (items == null || items.Count == 0) return;

        // Clamp the center to keep the whole menu on screen.
        float margin = RADIUS + ITEM_SIZE * 0.5f * HOVER_SCALE;
        openCenter = new Vector2(
            Mathf.Clamp(screenCenter.x, margin, Screen.width - margin),
            Mathf.Clamp(screenCenter.y, margin, Screen.height - margin));

        menuRoot.anchoredPosition = openCenter;
        menuRoot.gameObject.SetActive(true);

        // Space items evenly clockwise starting directly above (90 degrees).
        float step = 360f / items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            float angleDeg = 90f - step * i;
            Vector2 dir = new Vector2(
                Mathf.Cos(angleDeg * Mathf.Deg2Rad),
                Mathf.Sin(angleDeg * Mathf.Deg2Rad));
            views.Add(CreateItemView(items[i], dir));
        }

        openTime = Time.unscaledTime;
        hoveredIndex = -1;
        centerLabel.text = "";
        IsOpen = true;
    }

    /// <summary>
    /// Close the menu and toggle the highlighted item.
    /// Do nothing within the dead zone or for disabled items.
    /// </summary>
    public void CloseAndCommit()
    {
        if (!IsOpen) return;
        UpdateHoveredIndex();
        if (hoveredIndex >= 0 && hoveredIndex < views.Count)
        {
            RadialMenuItem item = views[hoveredIndex].item;
            if (item.Enabled && item.OnToggle != null)
            {
                item.OnToggle();
            }
        }
        CloseAndCancel();
    }

    /// <summary>Close the menu without toggling anything.</summary>
    public void CloseAndCancel()
    {
        ClearViews();
        if (menuRoot != null) menuRoot.gameObject.SetActive(false);
        IsOpen = false;
    }

    private void Update()
    {
        if (!IsOpen) return;

        // Expansion animation: EaseOutBack produces a pop with slight overshoot and return.
        float t = Mathf.Clamp01((Time.unscaledTime - openTime) / OPEN_DURATION);
        float eased = EaseOutBack(t);

        UpdateHoveredIndex();

        for (int i = 0; i < views.Count; i++)
        {
            ItemView v = views[i];
            bool hovered = (i == hoveredIndex);
            float scale = eased * (hovered ? HOVER_SCALE : 1f);
            v.rect.anchoredPosition = v.direction * (RADIUS * eased);
            v.rect.localScale = new Vector3(scale, scale, 1f);

            bool enabled = v.item.Enabled;
            bool on = v.item.GetState != null && v.item.GetState();
            float alpha = enabled ? 1f : DISABLED_ALPHA;

            Color bg = hovered ? COLOR_BG_HOVER : COLOR_BG_OFF;
            bg.a *= alpha;
            v.background.color = bg;

            v.ring.enabled = on;
            Color ring = COLOR_RING_ON;
            ring.a *= alpha;
            v.ring.color = ring;

            Sprite sprite = v.item.GetIcon != null ? v.item.GetIcon() : null;
            if (sprite != null)
            {
                v.icon.enabled = true;
                v.icon.sprite = sprite;
                v.fallbackText.enabled = false;
                v.icon.color = new Color(1f, 1f, 1f, alpha);
            }
            else
            {
                // Fall back to the label's first character when a Sprite is missing.
                v.icon.enabled = false;
                v.fallbackText.enabled = true;
                v.fallbackText.color = new Color(1f, 1f, 1f, alpha);
            }
        }

        centerLabel.text = (hoveredIndex >= 0 && hoveredIndex < views.Count)
            ? views[hoveredIndex].item.Label
            : "";
    }

    /// <summary>
    /// Update the highlighted item index from mouse position.
    /// Use -1 inside the dead zone to cancel; otherwise select the nearest angular sector.
    /// </summary>
    private void UpdateHoveredIndex()
    {
        if (views.Count == 0)
        {
            hoveredIndex = -1;
            return;
        }
        Vector2 mouse = (Vector2)InputUtil.MousePosition;
        Vector2 delta = mouse - openCenter;
        if (delta.magnitude < DEAD_ZONE)
        {
            hoveredIndex = -1;
            return;
        }
        float mouseAngle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        float step = 360f / views.Count;
        // Item i's angle is 90 - step*i. Invert this to find the nearest i.
        float raw = (90f - mouseAngle) / step;
        int index = Mathf.RoundToInt(raw);
        hoveredIndex = ((index % views.Count) + views.Count) % views.Count;
    }

    private ItemView CreateItemView(RadialMenuItem item, Vector2 direction)
    {
        EnsureSprites();

        GameObject go = new GameObject($"RadialItem_{item.Id}");
        go.transform.SetParent(menuRoot, false);
        RectTransform rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(ITEM_SIZE, ITEM_SIZE);
        rect.anchoredPosition = Vector2.zero;
        rect.localScale = Vector3.zero;

        Image background = go.AddComponent<Image>();
        background.sprite = circleSprite;

        // Accent-colored ring just outside the circle indicates ON state.
        GameObject ringGO = new GameObject("Ring");
        ringGO.transform.SetParent(rect, false);
        Image ring = ringGO.AddComponent<Image>();
        ring.sprite = ringSprite;
        RectTransform ringRT = ring.rectTransform;
        ringRT.anchorMin = Vector2.zero;
        ringRT.anchorMax = Vector2.one;
        ringRT.offsetMin = new Vector2(-4f, -4f);
        ringRT.offsetMax = new Vector2(4f, 4f);

        GameObject iconGO = new GameObject("Icon");
        iconGO.transform.SetParent(rect, false);
        Image icon = iconGO.AddComponent<Image>();
        icon.rectTransform.sizeDelta = new Vector2(ICON_SIZE, ICON_SIZE);

        GameObject fallbackGO = new GameObject("FallbackText");
        fallbackGO.transform.SetParent(rect, false);
        Text fallback = fallbackGO.AddComponent<Text>();
        fallback.font = GetUIFont(18);
        fallback.fontSize = 18;
        fallback.alignment = TextAnchor.MiddleCenter;
        fallback.horizontalOverflow = HorizontalWrapMode.Overflow;
        fallback.text = string.IsNullOrEmpty(item.Label)
            ? "?"
            : item.Label.Substring(0, Mathf.Min(2, item.Label.Length));
        RectTransform fbRT = fallback.rectTransform;
        fbRT.anchorMin = Vector2.zero;
        fbRT.anchorMax = Vector2.one;
        fbRT.offsetMin = Vector2.zero;
        fbRT.offsetMax = Vector2.zero;

        return new ItemView
        {
            item = item,
            rect = rect,
            background = background,
            ring = ring,
            icon = icon,
            fallbackText = fallback,
            direction = direction,
        };
    }

    private void ClearViews()
    {
        foreach (ItemView v in views)
        {
            if (v.rect != null) Destroy(v.rect.gameObject);
        }
        views.Clear();
        hoveredIndex = -1;
    }

    /// <summary>
    /// Return a Japanese-capable font for the center label and fallback text.
    /// Item labels may contain Japanese, so prefer an OS multilingual font through UiFontProvider
    /// over the built-in LegacyRuntime.ttf, which lacks Japanese glyphs.
    /// ASCII labels such as road-editing labels also render correctly, preserving existing uses.
    /// </summary>
    private static Font GetUIFont(int fontSize)
    {
        return UiFontProvider.GetFont(fontSize);
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>
    /// Generate circle/ring Sprites at runtime to avoid requiring assets.
    /// Static cache shared by all instances.
    /// </summary>
    private static void EnsureSprites()
    {
        if (circleSprite != null && ringSprite != null) return;

        const int size = 64;
        circleSprite = CreateCircleSprite(size, filled: true, thickness: 0f);
        ringSprite = CreateCircleSprite(size, filled: false, thickness: 4f);
    }

    private static Sprite CreateCircleSprite(int size, bool filled, float thickness)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        float center = (size - 1) * 0.5f;
        float outerR = size * 0.5f - 1f;
        float innerR = filled ? -1f : outerR - thickness;

        Color32[] pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                // Antialias edges with a 1px gradient.
                float outerA = Mathf.Clamp01(outerR - dist + 0.5f);
                float innerA = filled ? 1f : Mathf.Clamp01(dist - innerR + 0.5f);
                byte a = (byte)(255f * outerA * innerA);
                pixels[y * size + x] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }
}
