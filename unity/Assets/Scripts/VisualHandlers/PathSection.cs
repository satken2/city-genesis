using UnityEngine;

/// <summary>
/// Path display-section classification. Buried sections render in every view (terrain hides them except through portals);
/// underground view keeps their materials opaque while ghosting the rest. Generation, updates, and lifetime follow the parent path.
/// </summary>
public sealed class PathSection : MonoBehaviour
{
    public bool IsUnderground;

    /// <summary>Shown only in underground view: depth guides, and Far sections that exist only there. Hidden at once on leaving it, before regeneration.</summary>
    public bool UndergroundViewOnly;
}
