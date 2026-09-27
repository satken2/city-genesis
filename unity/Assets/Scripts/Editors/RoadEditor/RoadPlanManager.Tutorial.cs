/// <summary>チュートリアルの位置制約と道路編集の接点。通常時は状態を変更しない。</summary>
public partial class RoadPlanManager
{
    public void ApplyTutorialPathSettings()
    {
        if (!TutorialRuntime.PathRestricted || phase == RoadEditorPhase.Inactive) return;
        Curved = false;
        currentHeight = 0;
        currentEmbankmentHeight = 0;
        PathFollowTerrainOverride = Constants.VALUE_NONE;
        InvalidateShapeCache();
        EmitShapeUpdate();
    }
}
