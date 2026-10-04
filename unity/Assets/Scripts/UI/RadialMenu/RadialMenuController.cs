using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// カーソル位置から項目アイコンが円形に「ポワン」と飛び出す汎用ラジアルメニュー。
///
/// 操作モデル(呼び出し側が実装する想定):
///   トリガーキー押下で Open() → マウスを動かして項目をハイライト →
///   トリガーキーを離したら CloseAndCommit() でハイライト中の項目をトグル。
///   中心付近(デッドゾーン)で離した場合や無効項目の場合は何もしない。
///
/// 設計上の注意:
/// - Canvas は TooltipManager と同型のコード生成(ScreenSpaceOverlay)。シーンへの事前配置は不要。
/// - GraphicRaycaster は意図的に付けない。付けると InputHandler の
///   EventSystem.IsPointerOverGameObject 判定に引っかかり、3D入力が誤ってブロックされるため。
///   入力の抑止は IsOpen フラグを見る側(InputHandler / CameraController)のゲートで行う。
/// - 表示中の3D入力・カメラ操作の抑止のため、各入力系は static な IsOpen を参照すること。
/// - アイコンSpriteが未設定(Resources未配置等)でも、ラベルの頭文字を代替表示して動作する。
/// </summary>
public class RadialMenuController : MonoBehaviour
{
    public static RadialMenuController Instance { get; private set; }

    /// <summary>メニュー表示中フラグ。InputHandler / CameraController が入力ゲートに使う</summary>
    public static bool IsOpen { get; private set; }

    /* ---- 見た目・挙動の定数 ---- */
    private const float OPEN_DURATION = 0.15f;   // 展開アニメーション時間(秒)
    private const float RADIUS = 100f;           // 中心から各項目までの距離(px)
    private const float DEAD_ZONE = 28f;         // この距離以内で離すとキャンセル(px)
    private const float ITEM_SIZE = 64f;         // 項目円の直径(px)
    private const float ICON_SIZE = 44f;         // 項目内アイコンサイズ(px)
    private const float HOVER_SCALE = 1.15f;     // ホバー中の項目の拡大率

    private static readonly Color COLOR_BG_OFF = new Color(0.13f, 0.13f, 0.15f, 0.92f);
    private static readonly Color COLOR_BG_HOVER = new Color(0.25f, 0.27f, 0.30f, 0.95f);
    private static readonly Color COLOR_RING_ON = new Color(0.25f, 0.70f, 1.00f, 1.00f);
    private const float DISABLED_ALPHA = 0.3f;

    /* ---- 実行時生成テクスチャのキャッシュ(全インスタンス共有) ---- */
    private static Sprite circleSprite;
    private static Sprite ringSprite;

    private Canvas canvas;
    private RectTransform menuRoot;
    private Text centerLabel;

    /// <summary>展開中の項目のUI要素一式</summary>
    private class ItemView
    {
        public RadialMenuItem item;
        public RectTransform rect;
        public Image background;
        public Image ring;
        public Image icon;
        public Text fallbackText;
        public Vector2 direction;   // 中心からの単位ベクトル
    }

    private readonly List<ItemView> views = new List<ItemView>();
    private Vector2 openCenter;
    private float openTime;
    private int hoveredIndex = -1;

    /// <summary>
    /// シングルトンを取得する。未生成なら自動生成する(シーン事前配置は不要)。
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
        // ツールチップ(100)より手前に出す
        canvas.sortingOrder = 120;
        root.AddComponent<CanvasScaler>();

        GameObject menuGO = new GameObject("RadialMenuRoot");
        menuGO.transform.SetParent(root.transform, false);
        menuRoot = menuGO.AddComponent<RectTransform>();
        menuRoot.anchorMin = Vector2.zero;
        menuRoot.anchorMax = Vector2.zero;
        menuRoot.pivot = new Vector2(0.5f, 0.5f);

        // 中心ラベル(ホバー中項目の名前)。項目円と重ならないよう中心のすぐ下に置く
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
        // ラベルは黒フチで視認性を確保(背景が明るい地形でも読めるように)
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
    /// メニューを開く。screenCenter はスクリーン座標(通常はマウス位置)。
    /// すでに開いている場合は作り直す。
    /// </summary>
    public void Open(IReadOnlyList<RadialMenuItem> items, Vector2 screenCenter)
    {
        ClearViews();
        if (items == null || items.Count == 0) return;

        // メニュー全体が画面外へはみ出さないよう中心をクランプする
        float margin = RADIUS + ITEM_SIZE * 0.5f * HOVER_SCALE;
        openCenter = new Vector2(
            Mathf.Clamp(screenCenter.x, margin, Screen.width - margin),
            Mathf.Clamp(screenCenter.y, margin, Screen.height - margin));

        menuRoot.anchoredPosition = openCenter;
        menuRoot.gameObject.SetActive(true);

        // 項目は真上(90°)始まりの時計回り等間隔
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
    /// メニューを閉じ、ハイライト中の項目のトグルを実行する。
    /// デッドゾーン内・無効項目の場合は何も実行しない。
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

    /// <summary>メニューを閉じる(トグルは実行しない)。</summary>
    public void CloseAndCancel()
    {
        ClearViews();
        if (menuRoot != null) menuRoot.gameObject.SetActive(false);
        IsOpen = false;
    }

    private void Update()
    {
        if (!IsOpen) return;

        // 展開アニメーション: EaseOutBack で少し行き過ぎて戻る「ポワン」を作る
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
                // Sprite未配置でも動作するよう、ラベル頭文字で代替表示する
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
    /// マウス位置からハイライト対象の項目インデックスを更新する。
    /// デッドゾーン内は -1(キャンセル扱い)。角度で最も近いセクターを選ぶ。
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
        // 項目iの角度は 90 - step*i。逆算してもっとも近いiを選ぶ
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

        // ON状態を示すアクセント色リング(円の少し外側)
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
    /// 中央ラベル・代替テキスト用の日本語対応フォントを返す。
    /// 項目ラベルに日本語を使うため、組み込みの LegacyRuntime.ttf
    /// (日本語グリフ非対応)ではなくOSの多言語フォントを優先する(`UiFontProvider`)。
    /// ASCIIラベル(道路編集)でも問題なく描画できるため既存の利用にも影響しない。
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
    /// 円・リングのSpriteを実行時生成する(アセット不要にするため)。
    /// 全インスタンス共有のstaticキャッシュ。
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
                // 1pxのグラデーションで縁をアンチエイリアスする
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
