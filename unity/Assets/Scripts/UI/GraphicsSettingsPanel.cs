using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Graphics settings panel opened and closed by the button this component is attached to (GraphicQualityButton).
/// Graphics settings stay within Unity, so the panel lives on Unity's canvas instead of a Tauri window.
/// Labels are English because Unity has no localized resources. A toggle applies GraphicsConfig and saves it at once.
/// The panel hangs from the button's bottom-right corner and extends left, clear of the compass under the button row.
/// </summary>
[RequireComponent(typeof(Button))]
public class GraphicsSettingsPanel : MonoBehaviour
{
    private const float Width = 210f;
    private const float Padding = 8f;
    private const float RowHeight = 24f;
    private const float ButtonGap = 6f;
    private const float BoxSize = 16f;
    private const int FontSize = 14;

    private static readonly (string label, Func<bool> get, Action<bool> set)[] Items =
    {
        ("Sun shadows", () => GraphicsConfig.SunShadows, v => GraphicsConfig.SunShadows = v),
        ("Soft shadows", () => GraphicsConfig.SoftShadows, v => GraphicsConfig.SoftShadows = v),
        ("Tree and prop shadows", () => GraphicsConfig.PropShadows, v => GraphicsConfig.PropShadows = v),
        ("Ambient occlusion", () => GraphicsConfig.AmbientOcclusion, v => { GraphicsConfig.AmbientOcclusion = v; GraphicsConfig.ApplyAmbientOcclusion(); }),
    };

    private RectTransform panel;

    private void Awake()
    {
        GetComponent<Button>().onClick.AddListener(TogglePanel);
    }

    private void TogglePanel()
    {
        if (panel == null) Build();
        else panel.gameObject.SetActive(!panel.gameObject.activeSelf);
        // Leaving a Button selected also treats Space/arrow keys as Submit/Navigation.
        EventSystem.current?.SetSelectedGameObject(null);
    }

    /// <summary>
    /// Build the panel as the last sibling of the button, so it draws over the button row and below later canvas children such as the loading overlay.
    /// The background is a raycast target, so clicks and wheel input on the panel do not reach the world.
    /// </summary>
    private void Build()
    {
        var button = (RectTransform)transform;
        panel = NewRect("GraphicsSettingsPanel", button.parent);
        panel.anchorMin = button.anchorMin;
        panel.anchorMax = button.anchorMax;
        panel.pivot = Vector2.one;
        panel.anchoredPosition = button.anchoredPosition
            + new Vector2(button.sizeDelta.x * (1f - button.pivot.x), -button.sizeDelta.y * button.pivot.y - ButtonGap);
        panel.sizeDelta = new Vector2(Width, Padding * 2f + RowHeight * (Items.Length + 1));
        panel.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f, 0.85f);

        NewText("Title", Row(0), "Graphics", FontStyle.Bold);
        for (int i = 0; i < Items.Length; i++)
            AddToggle(Row(i + 1), Items[i].label, Items[i].get(), Items[i].set);
    }

    private RectTransform Row(int index)
    {
        var row = NewRect("Row", panel);
        row.anchorMin = row.anchorMax = row.pivot = new Vector2(0f, 1f);
        row.anchoredPosition = new Vector2(Padding, -Padding - RowHeight * index);
        row.sizeDelta = new Vector2(Width - Padding * 2f, RowHeight);
        return row;
    }

    private static void AddToggle(RectTransform row, string label, bool isOn, Action<bool> set)
    {
        // A clear image over the whole row lets a click on the label flip the toggle as well.
        row.gameObject.AddComponent<Image>().color = Color.clear;

        var box = NewRect("Box", row);
        box.anchorMin = box.anchorMax = new Vector2(0f, 0.5f);
        box.anchoredPosition = new Vector2(BoxSize * 0.5f, 0f);
        box.sizeDelta = new Vector2(BoxSize, BoxSize);
        var boxImage = box.gameObject.AddComponent<Image>();
        boxImage.color = new Color(1f, 1f, 1f, 0.2f);

        var check = NewRect("Check", box);
        check.anchorMin = Vector2.zero;
        check.anchorMax = Vector2.one;
        check.offsetMin = new Vector2(3f, 3f);
        check.offsetMax = new Vector2(-3f, -3f);
        var checkImage = check.gameObject.AddComponent<Image>();
        checkImage.raycastTarget = false;

        var text = NewText("Label", row, label, FontStyle.Normal);
        text.rectTransform.offsetMin = new Vector2(BoxSize + 8f, 0f);

        var toggle = row.gameObject.AddComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = checkImage;
        toggle.isOn = isOn;
        toggle.onValueChanged.AddListener(value =>
        {
            set(value);
            GraphicsConfig.Save();
            EventSystem.current?.SetSelectedGameObject(null);
        });
    }

    private static Text NewText(string name, RectTransform parent, string content, FontStyle style)
    {
        var rect = NewRect(name, parent);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var text = rect.gameObject.AddComponent<Text>();
        text.font = UiFontProvider.GetFont(FontSize);
        text.fontSize = FontSize;
        text.fontStyle = style;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleLeft;
        text.raycastTarget = false;
        text.text = content;
        return text;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }
}
