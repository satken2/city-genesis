using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// マウスカーソルに追従する簡易ツールチップマネージャー。
/// ScreenSpaceOverlay の Canvas 上にテキストを表示する。
/// </summary>
public class TooltipManager : MonoBehaviour
{
    public static TooltipManager Instance { get; private set; }

    private Canvas canvas;
    private RectTransform panel;
    private Text textComponent;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Canvas 作成
        GameObject root = new GameObject("TooltipCanvas");
        root.transform.SetParent(transform, false);
        canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        root.AddComponent<CanvasScaler>();

        // パネル（背景）作成
        GameObject panelGO = new GameObject("TooltipPanel");
        panelGO.transform.SetParent(root.transform, false);
        panel = panelGO.AddComponent<RectTransform>();
        panel.anchorMin = Vector2.zero;
        panel.anchorMax = Vector2.zero;
        panel.pivot = new Vector2(0f, 1f);
        panel.sizeDelta = new Vector2(400f, 60f);

        Image bg = panelGO.AddComponent<Image>();
        bg.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);

        // テキスト作成
        GameObject textGO = new GameObject("TooltipText");
        textGO.transform.SetParent(panel, false);
        textComponent = textGO.AddComponent<Text>();
        textComponent.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        textComponent.fontSize = 20;
        textComponent.color = Color.white;

        RectTransform textRT = textComponent.rectTransform;
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = new Vector2(10f, 5f);
        textRT.offsetMax = new Vector2(-10f, -5f);

        panelGO.SetActive(false);
    }

    /// <summary>
    /// ツールチップを表示する。
    /// </summary>
    public void Show(string message)
    {
        if (textComponent == null || panel == null) return;
        textComponent.text = message;
        panel.gameObject.SetActive(true);
        UpdatePosition();
    }

    /// <summary>
    /// ツールチップを非表示にする。
    /// </summary>
    public void Hide()
    {
        if (panel != null)
            panel.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (panel != null && panel.gameObject.activeSelf)
        {
            UpdatePosition();
        }
    }

    /// <summary>
    /// マウスカーソル位置にパネルを追従させる。
    /// 画面端では反対側に表示してクリップを防ぐ。
    /// </summary>
    private void UpdatePosition()
    {
        Vector2 mousePos = Input.mousePosition;
        float x = mousePos.x + 15f;
        float y = mousePos.y - 15f;

        if (x + panel.sizeDelta.x > Screen.width)
            x = mousePos.x - panel.sizeDelta.x - 15f;
        if (y - panel.sizeDelta.y < 0)
            y = mousePos.y + panel.sizeDelta.y + 15f;

        panel.anchoredPosition = new Vector2(x, y);
    }
}
