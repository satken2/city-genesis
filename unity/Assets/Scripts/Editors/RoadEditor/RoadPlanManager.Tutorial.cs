using UnityEngine;

/// <summary>Interface between tutorial position constraints and road editing. Does not change state during normal use.</summary>
public partial class RoadPlanManager
{
    public void ApplyTutorialPathSettings()
    {
        if (!TutorialRuntime.PathRestricted || phase == RoadEditorPhase.Inactive) return;
        Curved = false;
        currentEmbankmentHeight = 0;
        PathFollowTerrainOverride = Constants.VALUE_NONE;
        SyncTutorialPlacementHeight();
        InvalidateShapeCache();
        EmitShapeUpdate();
    }

    /// <summary>Aligns the next point's height with guidance and updates panel elevation only if changed.</summary>
    private void SyncTutorialPlacementHeight()
    {
        if (!TutorialRuntime.PathRestricted) return;
        float height = TutorialRuntime.PointHeight(points.Count);
        if (Mathf.Approximately(height, currentHeight)) return;
        currentHeight = height;
        EmitCurrentHeight();
    }
}
