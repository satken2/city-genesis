using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// The city's flag and emblem (PNG images of GET /city/insignia/{flag|emblem}) drawn on building models.
/// Model faces using the SLOT_CITY_FLAG / SLOT_CITY_EMBLEM materials show the image over their UV 0–1 frame:
/// the flag frame is 3:2 (the insignia editor's flag size) with the hoist at u=0 and the top at v=1, the emblem frame is square.
/// An image of another aspect ratio keeps its proportions, set against the hoist and top (flag) or centered (emblem),
/// and alpha clipping cuts out the rest of the frame — and the whole frame while the city has no image.
/// One material per slot is shared by every building, so a new image shows without regenerating buildings.
/// </summary>
public static class CityInsignia
{
    private const string TEMPLATE_MATERIAL = "CityInsignia"; // DepthOffsetLit with alpha clipping. Being an asset keeps the clipping variant in player builds.

    private sealed class Slot
    {
        public readonly string materialName, kind;
        public readonly float aspect;
        public readonly Vector2 anchor;
        public Material material;
        public Texture2D texture;
        public byte[] png;

        public Slot(string materialName, string kind, float aspect, Vector2 anchor)
        {
            this.materialName = materialName;
            this.kind = kind;
            this.aspect = aspect;
            this.anchor = anchor;
        }
    }

    private static readonly Slot[] Slots =
    {
        new Slot("SLOT_CITY_FLAG", "flag", 1.5f, new Vector2(0f, 1f)),
        new Slot("SLOT_CITY_EMBLEM", "emblem", 1f, new Vector2(0.5f, 0.5f)),
    };

    private static int generation;
    private static Texture2D transparent;
    private static bool templateMissingWarned;

    // The template may come from the city's asset pack, and the images belong to the city.
    static CityInsignia() => AssetPackStore.CityChanging += Forget;

    private static void Forget()
    {
        generation++;
        foreach (Slot slot in Slots)
        {
            slot.material = null;
            slot.texture = null;
            slot.png = null;
        }
    }

    /// <summary>Shared material drawing the city's image for a model material named after a slot; null for other materials.</summary>
    public static Material MaterialFor(Material source)
    {
        if (source == null) return null;
        foreach (Slot slot in Slots)
        {
            if (source.name != slot.materialName) continue;
            if (slot.material == null)
            {
                Material template = AssetPackStore.LoadNamedMaterial(TEMPLATE_MATERIAL);
                if (template == null)
                {
                    if (!templateMissingWarned) Debug.LogWarning($"CityInsignia: material {TEMPLATE_MATERIAL} not found");
                    templateMissingWarned = true;
                    return null;
                }
                slot.material = new Material(template) { name = slot.materialName };
                Apply(slot);
            }
            return slot.material;
        }
        return null;
    }

    /// <summary>Refetch both images. Runs with every refetch of city data; an unchanged image is not decoded again.</summary>
    public static IEnumerator Refresh()
    {
        int expected = ++generation;
        foreach (Slot slot in Slots)
        {
            byte[] png;
            using (ApiRequest call = ApiClient.Request("GET", $"/city/insignia/{slot.kind}", timeoutSeconds: 10))
            {
                yield return call.Send();
                if (expected != generation) yield break;
                UnityWebRequest web = call.Web;
                if (web.result == UnityWebRequest.Result.Success) png = web.downloadHandler.data;
                else if (web.responseCode == 404) png = null;
                else
                {
                    Debug.LogWarning($"CityInsignia: failed to fetch {slot.kind}: {web.error}");
                    continue;
                }
            }
            if (SameBytes(png, slot.png)) continue;
            Texture2D previous = slot.texture;
            slot.texture = png != null ? Decode(png, slot.materialName) : null;
            slot.png = png;
            Apply(slot);
            if (previous != null) Object.Destroy(previous);
        }
    }

    private static void Apply(Slot slot)
    {
        if (slot.material == null) return;
        Texture2D texture = slot.texture;
        if (texture == null)
        {
            slot.material.SetTexture("_BaseMap", Transparent);
            slot.material.SetTextureScale("_BaseMap", Vector2.one);
            slot.material.SetTextureOffset("_BaseMap", Vector2.zero);
            return;
        }
        int w = texture.width - 2, h = texture.height - 2;
        float aspect = (float)w / h;
        Vector2 size = aspect >= slot.aspect ? new Vector2(1f, slot.aspect / aspect) : new Vector2(aspect / slot.aspect, 1f);
        Vector2 origin = Vector2.Scale(slot.anchor, Vector2.one - size);
        // The image's pixels sit inside a 1px transparent border: texture coordinate = (1 + (uv - origin) / size * pixels) / (pixels + 2).
        slot.material.SetTexture("_BaseMap", texture);
        slot.material.SetTextureScale("_BaseMap", new Vector2(w / (size.x * (w + 2)), h / (size.y * (h + 2))));
        slot.material.SetTextureOffset("_BaseMap", new Vector2((1f - origin.x / size.x * w) / (w + 2), (1f - origin.y / size.y * h) / (h + 2)));
    }

    /// <summary>Decoded image with a 1px border that clamping turns into the transparent rest of the frame. Null if the PNG is invalid.</summary>
    private static Texture2D Decode(byte[] png, string name)
    {
        var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!image.LoadImage(png))
            {
                Debug.LogWarning($"CityInsignia: invalid image for {name}");
                return null;
            }
            int w = image.width, h = image.height, paddedW = w + 2, paddedH = h + 2;
            Color32[] source = image.GetPixels32();
            var pixels = new Color32[paddedW * paddedH];
            // The border repeats the edge colors with zero alpha so mipmaps fade the edge out without a dark fringe.
            for (int y = 0; y < paddedH; y++)
            {
                int row = Mathf.Clamp(y - 1, 0, h - 1) * w;
                for (int x = 0; x < paddedW; x++)
                {
                    Color32 color = source[row + Mathf.Clamp(x - 1, 0, w - 1)];
                    if (x == 0 || y == 0 || x == paddedW - 1 || y == paddedH - 1) color.a = 0;
                    pixels[y * paddedW + x] = color;
                }
            }
            var texture = new Texture2D(paddedW, paddedH, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }
        finally
        {
            Object.Destroy(image);
        }
    }

    private static Texture2D Transparent
    {
        get
        {
            if (transparent != null) return transparent;
            transparent = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "CityInsigniaNone" };
            transparent.SetPixel(0, 0, Color.clear);
            transparent.Apply(false, true);
            return transparent;
        }
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a == null || b == null) return a == b;
        return a.SequenceEqual(b);
    }
}
