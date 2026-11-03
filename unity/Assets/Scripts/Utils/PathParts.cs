using System;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// Folders of path parts. A path draws from its appearance folder Models/Paths/p{variant}/{appearance} when the player chose
/// an appearance, otherwise from its variant folder; generators keep the type folder as their fallback.
/// An appearance folder holds only the parts that differ from its variant folder, and ModelManager takes the parts it lacks
/// from the variant folder (StandardName). Drawing methods follow which parts resolve (tower, arcade, truss, ...),
/// so an appearance can change the drawing method as well as the models. Numbers without a folder draw as standard.
/// </summary>
public static class PathParts
{
    public const string ROOT = "Models/Paths/p";

    public static string Folder(PathSummary path) => Folder(path.path_variant, path.appearance);

    public static string Folder(int variant, int appearance) => appearance == Constants.PATH_APPEARANCE_STANDARD
        ? ROOT + variant : $"{ROOT}{variant}/{appearance}";

    /// <summary>
    /// Appearance numbers of the variant, ascending, from the unit's model table (Resources cannot be enumerated at runtime).
    /// The pack's table applies when the pack owns the variant's unit.
    /// </summary>
    public static IReadOnlyList<int> Appearances(int variant) => ModelWeights.Ids(ROOT + variant);

    /// <summary>The same part in the variant folder for a name under an appearance folder; null for any other name.</summary>
    public static string StandardName(string name)
    {
        if (name == null || !name.StartsWith(ROOT, StringComparison.Ordinal)) return null;
        int variantEnd = name.IndexOf('/', ROOT.Length);
        int appearanceEnd = variantEnd < 0 ? -1 : name.IndexOf('/', variantEnd + 1);
        if (appearanceEnd < 0 || !IsNumber(name, ROOT.Length, variantEnd) || !IsNumber(name, variantEnd + 1, appearanceEnd)) return null;
        return name.Substring(0, variantEnd) + name.Substring(appearanceEnd);
    }

    private static bool IsNumber(string text, int from, int to)
    {
        if (to <= from) return false;
        for (int i = from; i < to; i++) if (text[i] < '0' || text[i] > '9') return false;
        return true;
    }
}
