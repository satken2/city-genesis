using GeneratedModels;

/// <summary>
/// While building construction mode has a variant selected, light the sites of buildings of the same variant
/// so the player can see where that building already stands.
/// Every status counts, because a building under construction or vacant still occupies its site as that building.
/// Exclude ubiquitous building types (residential, retail, office, factory, farm) whose dense distribution
/// would flood the view with light columns.
/// Columns are much taller than the public-facility highlight so sites stand out across the view.
/// </summary>
public class VariantSiteGlowOverlay : SiteGlowOverlay
{
    private const float ColumnHeightMeters = 200f;

    /// <summary>Building types excluded from same-variant highlighting because they occur densely across a city.</summary>
    private const int ExcludedBdTypeMask =
        Constants.BUILDING_APARTMENT |
        Constants.BUILDING_HOUSE |
        Constants.BUILDING_RETAIL |
        Constants.BUILDING_OFFICE |
        Constants.BUILDING_FACTORY |
        Constants.BUILDING_FARM;

    public static void Ensure() => EnsureInstance<VariantSiteGlowOverlay>();

    protected override bool IsEnabled()
    {
        int variant = BuildingPlanManager.SelectedVariant;
        if (variant <= 0) return false;

        var store = MasterDataStore.Instance;
        if (store != null
            && store.GetAgentMasterDictionary().TryGetValue(variant, out var master)
            && master?.bd_type is int bdType
            && (bdType & ExcludedBdTypeMask) != 0)
        {
            return false;
        }

        return true;
    }

    protected override bool IsTarget(BuildingSummary building) =>
        building.variant == BuildingPlanManager.SelectedVariant;

    protected override float ColumnHeight(float siteDiagonal) => ColumnHeightMeters;
}
