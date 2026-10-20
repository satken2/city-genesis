using System.Collections.Generic;
using UnityEngine;

public partial class RoadPlanManager
{
    // Invalidate when points or hover positions change.
    private List<Vector3> cachedRenderPoints;

    /// <summary>
    /// Samples for detailed meshes. Fix anchor indices to retain meshes before/after the edited section during input.
    /// Lightweight previews and validation sample the same section geometry at densities appropriate to their purposes.
    /// </summary>
    private List<Vector3> GetRenderPoints()
    {
        if (cachedRenderPoints != null) return cachedRenderPoints;
        var result = new List<Vector3>();
        for (int i = 0; i < points.Count; i++)
        {
            result.Add(points[i].position + Vector3.up * points[i].embankmentHeight);
            if (!Curved || i == points.Count - 1) continue;
            var span = GetShapeSpan(i, false);
            for (int j = 1; j <= bezierSamplesPerSegment; j++)
                result.Add(span.Sample((float)j / (bezierSamplesPerSegment + 1), surfaceOffset: RoadPartHeights.ROAD_SPLINE_OFFSET));
        }
        if (phase == RoadEditorPhase.Placing && points.Count > 0)
        {
            var hover = GetHoverPoint();
            result.Add(hover.position + Vector3.up * hover.embankmentHeight);
        }
        return cachedRenderPoints = result;
    }

    private PathBuildPoint GetHoverPoint()
    {
        Vector3 position = hoverWorldPosition;
        position.y = Utils.GetGroundHeight(position) + (hoverSnapWasElevated ? hoverSnapRelHeight : currentHeight);
        return new PathBuildPoint(position, PathPointRole.End) { embankmentHeight = currentEmbankmentHeight };
    }

    /// <summary>Builds sections used by rendering/validation, including the provisional endpoint and preceding tangent during placement.</summary>
    private RoadShapeGeometry.Span GetShapeSpan(int index, bool placing)
    {
        var a = points[index];
        bool hover = placing && index == points.Count - 1;
        var b = hover ? GetHoverPoint() : points[index + 1];
        if (Curved && hover)
        {
            Vector3 previous = index > 0 ? points[index - 1].position : a.position;
            if (!a.control_out_manual || !a.control_out.HasValue)
                a.control_out = a.position + (b.position - previous) / 6f;
            b.control_in = b.position - (b.position - a.position) / 6f;
        }
        else if (Curved && placing && index + 1 == points.Count - 1 && !b.control_in_manual)
            b.control_in = b.position - (GetHoverPoint().position - a.position) / 6f;
        return RoadShapeGeometry.CreateSpan(a, b, Curved, EffectiveFollowTerrain(), PathManager.GetPathBaseHeight(PathSubtype));
    }

    /// <summary>
    /// Updates the 3D preview.
    /// Samples each segment as a cubic Bezier to generate subdivided points.
    /// Segment structure: P0[i].control_out → P1[i+1].control_in → P1[i+1].position
    /// Uses linear interpolation without controls.
    /// </summary>
    private void Update3DPreview()
    {
        if (IsGridBuild)
        {
            UpdateGridPreview();
            return;
        }
        UpdateEarthworkPreview();
        // Recalculate warnings from the same geometry when the expected completed profile arrives asynchronously.
        cachedAlignmentViolations = null;
        if (roadRenderer == null) return;

        // After input stops, use post-earthworks elevations for the whole path. Continue lightweight local display while fetching/dragging.
        var profiles = earthworkPreview?.PathProfiles;
        if (phase == RoadEditorPhase.Editing && !isDragging && profiles != null
            && profiles.Count > 0 && profiles.TrueForAll(line => line.Count >= 2))
        {
            RegisterPreviewTracks(profiles[0], profiles);
            roadRenderer.UpdateSections(profiles[0], profiles[0], null, null, PathType,
                PathSubtype, Curved, PlatformSide, LateralOffsets, TravelDirections, HouseEnds, profiles, PassageMode);
            return;
        }
        List<Vector3> renderPoints = GetRenderPoints();
        UpdatePreviewSections(renderPoints);
    }

    private void UpdatePreviewSections(List<Vector3> renderPoints)
    {
        if (renderPoints.Count >= 2)
        {
            int stride = Curved ? bezierSamplesPerSegment + 1 : 1;
            int firstMoving = points.Count - 1;
            int endMoving = points.Count - 1;
            bool placing = phase == RoadEditorPhase.Placing;
            // Adding a provisional endpoint also changes the last anchor's incoming tangent, so update the preceding section band too.
            if (placing && Curved) firstMoving = Mathf.Max(0, points.Count - 2);
            if (isDragging && dragPointIndex >= 0)
            {
                // Anchor movement also affects adjacent anchors' automatic tangents.
                // Control movement affects both adjacent sections, including collinearity adjustment of the opposite handle.
                int reach = Curved && dragTarget == DragTarget.Point ? 2 : 1;
                firstMoving = Mathf.Max(0, dragPointIndex - reach);
                endMoving = Mathf.Min(points.Count - 1, dragPointIndex + reach);
            }
            int firstIndex = firstMoving * stride;
            int endIndex = endMoving * stride;
            List<Vector3> before = firstIndex > 0 ? renderPoints.GetRange(0, firstIndex + 1) : null;
            List<Vector3> after = endIndex < (points.Count - 1) * stride
                ? renderPoints.GetRange(endIndex, (points.Count - 1) * stride - endIndex + 1) : null;
            List<Vector3> moving = placing || isDragging
                ? GetMovingPreviewPoints(renderPoints, firstMoving, endMoving, placing) : null;
            RegisterPreviewTracks(renderPoints, null);
            roadRenderer.UpdateSections(renderPoints, before, moving, after, PathType,
                PathSubtype, Curved, PlatformSide, LateralOffsets, TravelDirections, HouseEnds, passageMode: PassageMode);
        }
        else
        {
            roadRenderer.ResetPreview();
        }
    }

    /// <summary>
    /// Registers finalized geometry as a provisional partner for completed-track drawing (junction S-curves, parallel stitching, buffer stops)
    /// through RoadRenderer.RegisterPreviewTracks. During placement, use points up to the last committed point, excluding the provisional endpoint. Retain registration
    /// while dragging and update on release, limiting rebuilds of existing tracks to operation boundaries. Exclude plans undergoing shape editing.
    /// Preview lines also act as partners to each other, so call before building preview meshes.
    /// </summary>
    private void RegisterPreviewTracks(List<Vector3> renderPoints, List<List<Vector3>> profiles)
    {
        if (isDragging || roadRenderer == null) return;
        List<Vector3> committed = renderPoints;
        if (phase == RoadEditorPhase.Placing)
        {
            int stride = Curved ? bezierSamplesPerSegment + 1 : 1;
            int end = (points.Count - 1) * stride;
            committed = points.Count >= 2 && end < renderPoints.Count ? renderPoints.GetRange(0, end + 1) : null;
            profiles = null;
        }
        roadRenderer.RegisterPreviewTracks(committed, PathType, PathSubtype, Curved, PlatformSide, LateralOffsets,
            TravelDirections, HouseEnds, PassageMode, pathId, profiles);
    }

    /// <summary>
    /// Give bands terrain-following, embankment, and elevated heights too, without changing saved geometry.
    /// Curves and provisional endpoint tangents use the same sampler as alignment evaluation.
    /// </summary>
    private List<Vector3> GetMovingPreviewPoints(List<Vector3> renderPoints, int first, int end, bool placing)
    {
        return SamplePreviewSpans(renderPoints, first, end, placing, true);
    }

    /// <summary>Shares provisional endpoint tangents between drawing and alignment evaluation; excludes render-only lift from evaluation.</summary>
    private List<Vector3> SamplePreviewSpans(List<Vector3> renderPoints, int first, int end, bool placing, bool surfaceOffset, Vector3 origin = default, List<bool> followSamples = null)
    {
        var result = new List<Vector3>();
        for (int index = first; index < end + (placing ? 1 : 0); index++)
        {
            var span = GetShapeSpan(index, placing);
            int steps = span.DenseSteps;
            // Interpolate relative coordinates to avoid large-world rounding being amplified into curvature errors.
            for (int i = 0; i < steps; i++)
            {
                result.Add(span.Sample((float)i / steps, origin, surfaceOffset ? RoadPartHeights.ROAD_SPLINE_OFFSET : 0f));
                followSamples?.Add(span.follows);
            }
        }
        int stride = Curved ? bezierSamplesPerSegment + 1 : 1;
        result.Add((placing ? renderPoints[renderPoints.Count - 1] : renderPoints[end * stride]) - origin);
        if (followSamples != null) followSamples.Add(followSamples.Count > 0 && followSamples[followSamples.Count - 1]);
        return result;
    }
}
