using UnityEngine;

/// <summary>
/// 3D側UI(uGUIのText)で使う多言語対応フォントの供給元。
///
/// 組み込みの `LegacyRuntime.ttf` は日本語グリフを持たないため、地区名・ツール名などを
/// そのまま描くと豆腐になる。OSにインストールされたフォントから多言語対応のものを
/// 優先して動的生成し、どれも無ければ `LegacyRuntime.ttf` にフォールバックする。
///
/// Yu Gothic UI / Meiryo は日本語・英語・数字に加え、キリル文字・アラビア文字・タイ文字・
/// CJK統合漢字など広範囲のグリフを含むWindows標準フォント。Microsoft YaHei UI(簡体字中国語)、
/// Malgun Gothic(韓国語)、Leelawadee UI(タイ語)、Segoe UI / Arial もフォールバック候補に含める。
///
/// フォント生成は重いためサイズ非依存でキャッシュする(uGUIの動的フォントはサイズを跨いで
/// 同一インスタンスを使い回せる)。
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
    /// 多言語対応フォントを返す。fontSize は動的フォント生成時の基準サイズ。
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
