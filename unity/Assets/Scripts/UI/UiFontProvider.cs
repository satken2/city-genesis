using UnityEngine;

/// <summary>
/// Provides multilingual fonts for 3D-side UI (uGUI Text).
///
/// The built-in LegacyRuntime.ttf lacks Japanese glyphs, so district names and tool names
/// would render as missing-glyph boxes. Prefer dynamically creating a multilingual font
/// from installed OS fonts, falling back to LegacyRuntime.ttf if none are available.
///
/// Yu Gothic UI / Meiryo are standard Windows fonts covering Japanese, English, digits, Cyrillic, Arabic, Thai,
/// and CJK unified ideographs. Fallback candidates also include Microsoft YaHei UI (Simplified Chinese),
/// Malgun Gothic (Korean), Leelawadee UI (Thai), and Segoe UI / Arial.
///
/// Font creation is expensive, so cache independently of size; uGUI dynamic fonts can share
/// one instance across sizes.
/// </summary>
public static class UiFontProvider
{
    private static readonly string[] PreferredFonts = new string[]
    {
        "Yu Gothic UI",
        "Meiryo",
        "Microsoft YaHei UI",
        "Malgun Gothic",
        "Leelawadee UI",
        "Segoe UI",
        "Arial",
    };

    private static Font cachedFont;

    /// <summary>
    /// Return a multilingual font. fontSize is the reference size for dynamic font creation.
    /// </summary>
    public static Font GetFont(int fontSize)
    {
        if (cachedFont != null) return cachedFont;

        cachedFont = Font.CreateDynamicFontFromOSFont(PreferredFonts, fontSize);
        if (cachedFont == null)
        {
            cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
        return cachedFont;
    }
}
