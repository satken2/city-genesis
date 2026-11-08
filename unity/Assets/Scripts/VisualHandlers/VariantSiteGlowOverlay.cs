using GeneratedModels;

/// <summary>
/// While building construction mode has a variant selected, light the sites of buildings of the same variant
/// so the player can see where that building already stands.
/// Every status counts, because a building under construction or vacant still occupies its site as that building.
/// Columns are much taller than the public-facility highlight so sites stand out across the view.
/// </summary>
public class VariantSiteGlowOverlay : SiteGlowOverlay
{
    private const float ColumnHeightMeters = 200f;

    public static void Ensure() => EnsureInstance<VariantSiteGlowOverlay>();

    protected override bool IsEnabled() => BuildingPlanManager.SelectedVariant > 0;

    protected override bool IsTarget(BuildingSummary building) =>
        building.variant == BuildingPlanManager.SelectedVariant;

    protected override float ColumnHeight(float siteDiagonal) => ColumnHeightMeters;
}
