using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// カーソル追従HUDに表示する1アイコン。
/// active=false のアイコンは半透明表示になる(OFF状態の吸着など)。
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
/// マウスカーソル脇に「現在の編集設定」(アイコン列+短いテキスト)を常時表示する汎用HUD。
/// 道路編集の曲線/吸着状態と高さ表示に使うが、特定モードに依存しない汎用部品。
///
/// 使い方: 編集モード開始時に SetProvider() で表示内容を返す関数を登録し、
/// 終了時に ClearProvider() で解除する。providerは毎フレーム呼ばれるため、
/// 重い処理をせず現在状態を読むだけにすること。
///
/// 設計上の注意:
/// - TooltipManager(一時的なテキスト表示)とは責務が異なるため別コンポーネント。
///   カーソルからのオフセットをツールチップ(+15,-15)とずらして重なりを避ける。
/// - Canvas は TooltipManager と同型のコード生成。GraphicRaycaster は付けない
///   (InputHandler の EventSystem 判定に干渉させないため)。
/// - アイコンImageはプールし、毎フレームは sprite/色 の差し替えのみ行う(GC・Rebuild抑制)。
/// - ラジアルメニュー表示中は同じ情報を大きく表示しているため自動で隠れる。
/// </summary>
public class CursorStatusHud : MonoBehaviour
{
    public static CursorStatusHud Instance { get; private set; }

    /* ---- レイアウト定数 ---- */
    private const float ICON_SIZE = 22f;
    private const float ICON_SPACING = 3f;
    private const float PADDING = 5f;
    private const int MAX_ICONS = 8;
    private const float OFFSET_X = 18f;   // カーソルからのオフセット(ツールチップと重ねない)
    private const float OFFSET_Y = -40f;
    // OFF状態は暗い背景パネルに馴染んで目立たなくなるよう、色を暗いグレーまで落とし
    // アルファも下げる。ONの白色とはっきり区別できる(=OFFがくっきり地味になる)ようにする。
    private static readonly Color ICON_COLOR_ACTIVE = Color.white;
    private static readonly Color ICON_COLOR_INACTIVE = new Color(0.22f, 0.22f, 0.22f, 0.55f);

    private Canvas canvas;
    private RectTransform panel;
    private Image panelBg;
    private readonly Image[] iconPool = new Image[MAX_ICONS];
    private Text textComponent;

    private Func<(List<CursorHudIcon> icons, string text)> provider;

    /// <summary>
    /// シングルトンを取得する。未生成なら自動生成する(シーン事前配置は不要)。
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
        // ツールチップ(100)より奥、通常UIより手前
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

        // アイコンプール(横一列)
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

        // テキスト(アイコン列の下段)
        GameObject textGO = new GameObject("HudText");
        textGO.transform.SetParent(panel, false);
        textComponent = textGO.AddComponent<Text>();
        textComponent.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
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
    /// 表示内容を返すproviderを登録して表示を開始する。毎フレームpollされる。
    /// </summary>
    public void SetProvider(Func<(List<CursorHudIcon> icons, string text)> newProvider)
    {
        provider = newProvider;
    }

    /// <summary>providerを解除して非表示にする。</summary>
    public void ClearProvider()
    {
        provider = null;
        if (panel != null) panel.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (provider == null) return;

        // ラジアルメニュー表示中は同じ情報を大きく表示しているため隠す
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

        // パネルサイズをコンテンツに合わせる
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
    /// マウスカーソル脇にパネルを追従させる。画面端では反対側に表示してクリップを防ぐ。
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
