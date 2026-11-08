using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Graphics settings panel opened and closed by the button this component is attached to (GraphicQualityButton).
/// Graphics settings stay within Unity, so the panel lives on Unity's canvas instead of a Tauri window.
/// Labels are English because Unity has no localized resources. A toggle or slider applies GraphicsConfig and saves it at once.
/// The panel sits at the center of the screen.
/// </summary>
[RequireComponent(typeof(Button))]
public class GraphicsSettingsPanel : MonoBehaviour
{
    private const float Width = 210f;
    private const float Padding = 8f;
    private const float RowHeight = 24f;
    private const float BoxSize = 16f;
    private const int FontSize = 14;
    private const float ScaleStep = 0.25f; // Slider step of the distance multipliers.
    private const int MinScaleSteps = 1;   // x0.25
    private const int MaxScaleSteps = 8;   // x2

    private static readonly (string label, Func<bool> get, Action<bool> set)[] Toggles =
    {
        ("Sun shadows", () => GraphicsConfig.SunShadows, v => GraphicsConfig.SunShadows = v),
        ("Soft shadows", () => GraphicsConfig.SoftShadows, v => GraphicsConfig.SoftShadows = v),
        ("Tree and prop shadows", () => GraphicsConfig.PropShadows, v => GraphicsConfig.PropShadows = v),
        ("Ambient occlusion", () => GraphicsConfig.AmbientOcclusion, v => { GraphicsConfig.AmbientOcclusion = v; GraphicsConfig.ApplyAmbientOcclusion(); }),
    };

    private static readonly (string label, Func<float> get, Action<float> set, Func<string> format)[] Sliders =
    {
        ("Prop draw distance", () => GraphicsConfig.PropDistanceScale, v => GraphicsConfig.PropDistanceScale = v,
            () => FormattableString.Invariant($"{DrawLodConfig.PropHiddenEnterDistance:0} m")),
        ("Far model distance", () => GraphicsConfig.FarDistanceScale, v => GraphicsConfig.FarDistanceScale = v,
            () => FormattableString.Invariant($"×{GraphicsConfig.FarDistanceScale:0.00}")),
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
    /// Build the panel on the root canvas, anchored to its center so it stays centered when the window is resized.
    /// It goes right after the canvas child holding the button, so it draws over the button row and below later canvas children such as the loading overlay.
    /// The background is a raycast target, so clicks and wheel input on the panel do not reach the world.
    /// </summary>
    private void Build()
    {
        Transform canvas = GetComponentInParent<Canvas>().rootCanvas.transform;
        Transform buttonRow = transform;
        while (buttonRow.parent != canvas) buttonRow = buttonRow.parent;

        panel = NewRect("GraphicsSettingsPanel", canvas);
        panel.SetSiblingIndex(buttonRow.GetSiblingIndex() + 1);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
        panel.anchoredPosition = Vector2.zero;
        panel.sizeDelta = new Vector2(Width, Padding * 2f + RowHeight * (1 + Toggles.Length + Sliders.Length * 2));
        panel.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.1f, 0.85f);

        int row = 0;
        NewText("Title", Row(row++), "Graphics", FontStyle.Bold);
        foreach (var item in Toggles)
            AddToggle(Row(row++), item.label, item.get(), item.set);
        foreach (var item in Sliders)
            AddSlider(Row(row++), Row(row++), item.label, item.get(), item.set, item.format);
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

    /// <summary>A label row showing the current value, and a slider row below it moving the multiplier in ScaleStep steps.</summary>
    private static void AddSlider(RectTransform labelRow, RectTransform sliderRow, string label, float scale, Action<float> set, Func<string> format)
    {
        NewText("Label", labelRow, label, FontStyle.Normal);
        var value = NewText("Value", labelRow, format(), FontStyle.Normal);
        value.alignment = TextAnchor.MiddleRight;

        // A clear image over the whole row lets a click anywhere on it move the handle.
        sliderRow.gameObject.AddComponent<Image>().color = Color.clear;

        var track = NewRect("Track", sliderRow);
        track.anchorMin = new Vector2(0f, 0.5f);
        track.anchorMax = new Vector2(1f, 0.5f);
        track.sizeDelta = new Vector2(0f, 4f);
        var trackImage = track.gameObject.AddComponent<Image>();
        trackImage.color = new Color(1f, 1f, 1f, 0.2f);
        trackImage.raycastTarget = false;

        // The slider drives the handle's anchors over the whole area height; the negative height shrinks it to BoxSize.
        var area = NewRect("HandleArea", sliderRow);
        area.anchorMin = Vector2.zero;
        area.anchorMax = Vector2.one;
        area.offsetMin = new Vector2(BoxSize * 0.5f, 0f);
        area.offsetMax = new Vector2(-BoxSize * 0.5f, 0f);
        var handle = NewRect("Handle", area);
        handle.sizeDelta = new Vector2(BoxSize, BoxSize - RowHeight);
        var handleImage = handle.gameObject.AddComponent<Image>();

        var slider = sliderRow.gameObject.AddComponent<Slider>();
        slider.handleRect = handle;
        slider.targetGraphic = handleImage;
        // Never selected, so arrow keys are not taken as slider input.
        slider.navigation = new Navigation { mode = Navigation.Mode.None };
        slider.wholeNumbers = true;
        slider.minValue = MinScaleSteps;
        slider.maxValue = MaxScaleSteps;
        slider.value = Mathf.Round(scale / ScaleStep);
        slider.onValueChanged.AddListener(steps =>
        {
            set(steps * ScaleStep);
            GraphicsConfig.ApplyDistances();
            GraphicsConfig.Save();
            value.text = format();
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
