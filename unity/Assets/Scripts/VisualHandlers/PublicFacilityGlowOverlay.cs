using UnityEngine;
using GeneratedModels;

/// <summary>
/// Resident overlay rendering public-facility highlights (VisualFlag.HIGHLIGHT_PUBLIC).
/// Lights the sites of STATUS_ACTIVE buildings with public_facility=true; drawing is shared with <see cref="SiteGlowOverlay"/>.
/// </summary>
public class PublicFacilityGlowOverlay : SiteGlowOverlay
{
    /// <summary>Light-column height range (meters), clamping a height proportional to parcel size.</summary>
    private const float ColumnHeightMin = 10f;
    private const float ColumnHeightMax = 34f;

    public static void Ensure() => EnsureInstance<PublicFacilityGlowOverlay>();

    protected override bool IsEnabled()
    {
        var handler = VisualModeHandler.Instance;
        return handler != null && (handler.GetFlags(InputObjectType.BUILDING) & VisualFlag.HIGHLIGHT_PUBLIC) != 0;
    }

    protected override bool IsTarget(BuildingSummary building) =>
        building.public_facility && building.status == Constants.STATUS_ACTIVE;

    /// <summary>Scale column height with parcel size to avoid overly tall columns on small parcels.</summary>
    protected override float ColumnHeight(float siteDiagonal) =>
        Mathf.Clamp(siteDiagonal * 0.7f, ColumnHeightMin, ColumnHeightMax);
}
