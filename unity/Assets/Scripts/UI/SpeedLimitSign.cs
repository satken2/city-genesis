using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Sign design. Apply language-based substitutions here, separately from road positioning and speed lookup.</summary>
public abstract class SpeedLimitSignStyle
{
    public static readonly SpeedLimitSignStyle Japanese = new JapaneseStyle();
    public abstract Color TextColor { get; }
    public virtual Vector2 TextOffset => Vector2.zero;
    public abstract void Populate(VertexHelper mesh, Rect rect);

    private sealed class JapaneseStyle : SpeedLimitSignStyle
    {
        public override Color TextColor => new Color32(27, 83, 150, 255);
        public override Vector2 TextOffset => new Vector2(0f, 8f);

        public override void Populate(VertexHelper mesh, Rect rect)
        {
            float radius = Mathf.Min(rect.width, rect.height) * .5f;
            AddDisc(mesh, rect.center, radius, new Color32(230, 233, 236, 255));
            AddDisc(mesh, rect.center, radius * .96f, new Color32(205, 36, 48, 255));
            AddDisc(mesh, rect.center, radius * .72f, Color.white);
        }

        private static void AddDisc(VertexHelper mesh, Vector2 center, float radius, Color color)
        {
            const int segments = 64;
            int start = mesh.currentVertCount;
            mesh.AddVert(center, color, Vector2.zero);
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                mesh.AddVert(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, color, Vector2.zero);
            }
            for (int i = 0; i < segments; i++) mesh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % segments);
        }
    }
}

/// <summary>Speed sign using no external images. Numbers use the shared font; clicks pass through.</summary>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class SpeedLimitSign : MaskableGraphic
{
    private SpeedLimitSignStyle style = SpeedLimitSignStyle.Japanese;
    private Text number;

    public static SpeedLimitSign Create(Transform parent)
    {
        var go = new GameObject("SpeedLimitSign", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var sign = go.AddComponent<SpeedLimitSign>();
        sign.raycastTarget = false;
        sign.rectTransform.anchorMin = sign.rectTransform.anchorMax = Vector2.zero;
        sign.rectTransform.pivot = Vector2.one * .5f;
        sign.rectTransform.sizeDelta = Vector2.one * 100f;
        var label = new GameObject("Speed", typeof(RectTransform));
        label.transform.SetParent(go.transform, false);
        sign.number = label.AddComponent<Text>();
        sign.number.font = UiFontProvider.GetFont(54);
        sign.number.fontStyle = FontStyle.Bold;
        sign.number.alignment = TextAnchor.MiddleCenter;
        sign.number.resizeTextForBestFit = true;
        sign.number.resizeTextMinSize = 10;
        sign.number.resizeTextMaxSize = 54;
        sign.number.raycastTarget = false;
        var rect = sign.number.rectTransform;
        rect.anchorMin = Vector2.one * .16f;
        rect.anchorMax = Vector2.one * .84f;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        sign.SetStyle(SpeedLimitSignStyle.Japanese);
        return sign;
    }

    public void SetStyle(SpeedLimitSignStyle value)
    {
        style = value ?? SpeedLimitSignStyle.Japanese;
        if (number != null)
        {
            number.color = style.TextColor;
            number.rectTransform.anchoredPosition = style.TextOffset;
        }
        SetVerticesDirty();
    }

    public void SetSpeed(float speed)
    {
        number.text = speed.ToString("G", CultureInfo.InvariantCulture);
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        style.Populate(mesh, GetPixelAdjustedRect());
    }
}
