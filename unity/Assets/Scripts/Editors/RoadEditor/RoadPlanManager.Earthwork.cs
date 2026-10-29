using System.Collections.Generic;
using UnityEngine;

public partial class RoadPlanManager
{
    RoadEarthworkPreview earthworkPreview;

    void UpdateEarthworkPreview()
    {
        var anchors = new List<Vector3>();
        var embankments = new List<float>();
        foreach (var p in points) { anchors.Add(p.position); embankments.Add(p.embankmentHeight); }
        if (phase == RoadEditorPhase.Placing && anchors.Count > 0)
        {
            var hover = hoverWorldPosition;
            hover.y = PlacementSurfaceY(hover) + (hoverSnapWasElevated ? hoverSnapRelHeight : currentHeight);
            anchors.Add(hover); embankments.Add(currentEmbankmentHeight);
        }
        if (anchors.Count < 2) { earthworkPreview?.Clear(); return; }
        if (earthworkPreview == null)
        {
            earthworkPreview = gameObject.AddComponent<RoadEarthworkPreview>();
            earthworkPreview.Changed += OnEarthworkPreviewChanged;
        }
        var paths = new List<object>();
        float[] offsets = LateralOffsets != null && LateralOffsets.Length > 0 ? LateralOffsets : new float[] { 0 };
        for (int line = 0; line < offsets.Length; line++)
        {
            var shifted = RoadRenderer.OffsetPolylineLateral(anchors, offsets[line]);
            var relative = new List<float[]>();
            for (int i=0;i<anchors.Count;i++)
                relative.Add(new[] {shifted[i].x, anchors[i].y-Utils.GetGroundHeight(anchors[i]), shifted[i].z});
            float[] Control(Vector3? control, int i)
            {
                if (!Curved || !control.HasValue) return null;
                var p = control.Value + shifted[i]-anchors[i];
                float y = control.Value.y-Utils.GetGroundHeight(anchors[i]);
                return new[] {p.x,y,p.z};
            }
            var waypoints = new List<object>();
            for (int i=1;i<anchors.Count-1;i++)
                waypoints.Add(new { position=relative[i], control_in=Control(points[i].control_in,i), control_out=Control(points[i].control_out,i) });
            int last = anchors.Count-1;
            paths.Add(new {
                path_variant=PathSubtype, start_point=relative[0], end_point=relative[last], waypoints,
                start_control=Control(points[0].control_out,0),
                end_control=last<points.Count ? Control(points[last].control_in,last) : null,
                curved=Curved, embankment_heights=embankments, follow_terrain=PathFollowTerrainOverride,
                platform_side=PlatformSide,
                travel_direction=TravelDirections != null && line<TravelDirections.Length ? TravelDirections[line] : Constants.DIR_BOTH
            });
        }
        earthworkPreview.Request(new {paths});
    }

    private void OnEarthworkPreviewChanged()
    {
        Update3DPreview();
        EmitShapeUpdate();
    }
}
