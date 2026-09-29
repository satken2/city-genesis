using UnityEngine;

/// <summary>チュートリアルの位置制約と道路編集の接点。通常時は状態を変更しない。</summary>
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

    /// <summary>次に置く点の高さを案内の定義へ揃え、変わったときだけパネルの標高表示にも伝える。</summary>
    private void SyncTutorialPlacementHeight()
    {
        if (!TutorialRuntime.PathRestricted) return;
        float height = TutorialRuntime.PointHeight(points.Count);
        if (Mathf.Approximately(height, currentHeight)) return;
        currentHeight = height;
        EmitCurrentHeight();
    }
}
