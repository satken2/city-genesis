using UnityEngine;
using GeneratedModels;

public partial class RoadPlanManager
{
    /// <summary>
    /// Computes all control points from 3D Catmull-Rom spline tangents.
    ///
    /// Tangent V_i = P_{i+1} - P_{i-1}, tension=1/6.
    /// Endpoints use the vector to the single adjacent anchor.
    /// This produces smooth Bezier curves across X, Y, and Z.
    ///
    /// Do not overwrite XZ of manually adjusted controls (control_*_manual). Rebuilding all controls whenever vertices
    /// move, are inserted, or deleted would erase the user's adjusted curves.
    /// Manually adjusted control XZ does not follow adjacent anchor movement (preserves the intended direction).
    /// Unless control_height_manual, determine height using the same longitudinal grade V_i.y / |V_i.xz| as automatic controls,
    /// multiplied by horizontal distance from the anchor. Fixing handle height would keep node grade unchanged
    /// regardless of node height changes and kink the vertical profile when left/right handle lengths differ.
    /// </summary>
    /// <param name="resetManual">If true, clears all manual flags and recalculates every control automatically.</param>
    private void RecomputeAllControlPoints(bool resetManual = false)
    {
        if (points.Count < 2) return;

        int n = points.Count;

        for (int i = 0; i < n; i++)
        {
            var pt = points[i];
            if (resetManual)
            {
                pt.control_out_manual = false;
                pt.control_in_manual = false;
                pt.control_height_manual = false;
            }

            Vector3 tangent = AutoTangent(i);
            float grade = HorizontalGrade(tangent);

            // control_out: outgoing control from this point toward the next
            if (i < n - 1 && pt.role != PathPointRole.End)
            {
                if (!pt.control_out_manual)
                    pt.control_out = pt.position + tangent * (1f / 6f);
                else if (pt.control_out.HasValue && !pt.control_height_manual)
                    pt.control_out = WithGrade(pt.control_out.Value, pt.position, grade);
            }

            // control_in: incoming control from the previous point to this one
            if (pt.role != PathPointRole.Start)
            {
                if (!pt.control_in_manual)
                    pt.control_in = pt.position - tangent * (1f / 6f);
                else if (pt.control_in.HasValue && !pt.control_height_manual)
                    pt.control_in = WithGrade(pt.control_in.Value, pt.position, -grade);
            }

            points[i] = pt;
        }

        // When a start/end connects to an existing path endpoint, snap its endpoint control
        // to the existing path extension direction to avoid a kink at the connection.
        AlignEndpointControlsToExistingPaths();
    }

    /// <summary>Automatic tangent at anchor i: P_{i+1} - P_{i-1} (one-sided at endpoints). Matches backend Path._auto_fill_controls.</summary>
    private Vector3 AutoTangent(int i)
    {
        int n = points.Count;
        return points[Mathf.Min(i + 1, n - 1)].position - points[Mathf.Max(i - 1, 0)].position;
    }

    /// <summary>Tangent longitudinal grade (height per horizontal 1m). 0 for tangents without a horizontal component.</summary>
    private static float HorizontalGrade(Vector3 tangent)
    {
        float horizontal = Mathf.Sqrt(tangent.x * tangent.x + tangent.z * tangent.z);
        return horizontal > 1e-4f ? tangent.y / horizontal : 0f;
    }

    /// <summary>Preserves control XZ, setting height to anchor height + horizontal distance × grade. Reverse the grade sign for incoming controls.</summary>
    private static Vector3 WithGrade(Vector3 control, Vector3 anchor, float grade)
    {
        control.y = anchor.y + grade * Utils.XZDistance(control, anchor);
        return control;
    }

    /// <summary>
    /// Moves an anchor to newPosition and updates its controls.
    /// Manually adjusted controls translate while preserving their relative XZ position to the anchor;
    /// RecomputeAllControlPoints resets height from the updated longitudinal grade.
    /// Unadjusted controls are recalculated by RecomputeAllControlPoints from the updated vertices.
    /// All drag and elevation change paths (keys/IPC) pass here. Anchors constrained to a track junction plane
    /// take that plane's height regardless of the requested height (ConstrainJunctionPlaneHeights).
    /// </summary>
    private void MoveAnchorPosition(int index, Vector3 newPosition)
    {
        if (index < 0 || index >= points.Count) return;

        var pt = points[index];
        Vector3 oldPosition = pt.position;
        Vector3 deltaXZ = new Vector3(newPosition.x - oldPosition.x, 0f, newPosition.z - oldPosition.z);
        pt.position = newPosition;
        pt.proposalGenerated = false;

        if (pt.control_out_manual && pt.control_out.HasValue) pt.control_out = pt.control_out.Value + deltaXZ;
        if (pt.control_in_manual && pt.control_in.HasValue) pt.control_in = pt.control_in.Value + deltaXZ;
        points[index] = pt;

        // Automatic tangents change at both neighbors. The moved anchor also prioritizes the new layout's grade over the route proposal's fixed grade.
        ReleaseControlHeights(index - 1, index + 1);
        ConstrainJunctionPlaneHeights();
        RecomputeAllControlPoints();
    }

    /// <summary>Releases fixed control heights for anchors first through last (ignores out-of-range indices).</summary>
    private void ReleaseControlHeights(int first, int last)
    {
        for (int i = Mathf.Max(0, first); i <= Mathf.Min(points.Count - 1, last); i++)
        {
            var pt = points[i];
            pt.control_height_manual = false;
            points[i] = pt;
        }
    }

    /// <summary>
    /// Moves control handle XZ. Shared by 3D dragging and the 2D panel.
    /// Only XZ becomes manual; recalculate both heights from the anchor's longitudinal grade
    /// (also releases heights fixed by route proposals, etc.). Handles adjust horizontal curvature;
    /// fixing their heights would prevent grade changes when node heights change.
    /// At track connection endpoints, project onto the main line tangent to adjust length while preserving connection direction.
    /// </summary>
    private void MoveControlXZ(int index, bool controlIn, float x, float z)
    {
        var pt = points[index];
        Vector3 control = new Vector3(x, pt.position.y, z);
        if (IsRailJunctionTarget && (index == 0 || index == points.Count - 1)
            && TryGetRailHandleDirection(pt.position, control - pt.position, out Vector3 direction))
        {
            control = pt.position + direction * Vector3.Dot(control - pt.position, direction);
        }
        if (controlIn)
        {
            pt.control_in = control;
            pt.control_in_manual = true;
        }
        else
        {
            pt.control_out = control;
            pt.control_out_manual = true;
        }
        pt.control_height_manual = false;
        points[index] = pt;
        // Keep the opposite control_in / control_out collinear across the vertex to avoid a road kink.
        EnforceCollinearControls(index, movedControlOut: !controlIn);
        RecomputeAllControlPoints();
    }

    /// <summary>
    /// Keeps the specified point's two Bezier controls (control_in / control_out) collinear (180 degrees).
    /// When one is dragged, reposition the opposite control across the vertex
    /// to prevent a road kink (frontend usability behavior; backend validation is unnecessary).
    /// Preserve the opposite handle's length (distance from anchor), changing only its direction.
    /// Works only for points with both controls (waypoints).
    /// Does nothing at start/end points, which have only one control.
    /// Align only in XZ without changing height (the caller's RecomputeAllControlPoints
    /// recalculates both sides with the same longitudinal grade).
    /// Also mark the following opposite control as manual; leaving it automatic would
    /// recalculate it on the next vertex move, break alignment, and kink the road.
    /// </summary>
    /// <param name="index">Target point index</param>
    /// <param name="movedControlOut">true when control_out moved (control_in follows); false for the reverse.</param>
    private void EnforceCollinearControls(int index, bool movedControlOut)
    {
        if (index < 0 || index >= points.Count) return;
        var pt = points[index];
        if (!pt.control_in.HasValue || !pt.control_out.HasValue) return;

        Vector3 anchor = pt.position;


        if (movedControlOut)
        {
            Vector3 outDir = pt.control_out.Value - anchor;
            outDir.y = 0f;
            if (outDir.sqrMagnitude < 1e-6f) return;
            Vector3 inVec = pt.control_in.Value - anchor;
            inVec.y = 0f;
            float inLen = inVec.magnitude;
            Vector3 newIn = anchor - outDir.normalized * inLen;
            newIn.y = pt.control_in.Value.y;
            pt.control_in = newIn;
            pt.control_in_manual = true;
        }
        else
        {
            Vector3 inDir = pt.control_in.Value - anchor;
            inDir.y = 0f;
            if (inDir.sqrMagnitude < 1e-6f) return;
            Vector3 outVec = pt.control_out.Value - anchor;
            outVec.y = 0f;
            float outLen = outVec.magnitude;
            Vector3 newOut = anchor - inDir.normalized * outLen;
            newOut.y = pt.control_out.Value.y;
            pt.control_out = newOut;
            pt.control_out_manual = true;
        }

        points[index] = pt;
    }

    /// <summary>
    /// Road endpoint extensions align when straight-extension snapping is ON and the angle difference is within tolerance.
    /// Tracks align automatic handles to the analytic main line tangent at the endpoint regardless of this flag.
    /// Track manual handles also align to the tangent, preserving connection direction when anchors move.
    /// Applied by overriding RecomputeAllControlPoints automatic results;
    /// preserve control lengths (distance from anchor) and change only directions.
    /// These controls are also submitted on confirmation, matching geometry saved by the backend.
    /// </summary>
    private void AlignEndpointControlsToExistingPaths()
    {
        if ((!TerrainInputHandler.EnableStraightLineSnapping && !IsRailJunctionTarget) || SnappingSuppressed || points.Count < 2) return;
        AlignEndpointControl(0, false);
        AlignEndpointControl(points.Count - 1, true);
    }

    private void AlignEndpointControl(int index, bool incoming)
    {
        var point = points[index];
        Vector3? control = incoming ? point.control_in : point.control_out;
        bool manual = incoming ? point.control_in_manual : point.control_out_manual;
        if (!control.HasValue || (manual && !IsRailJunctionTarget)) return;
        Vector3 direction = control.Value - point.position;
        direction.y = 0f;
        float length = direction.magnitude;
        if (length <= 1e-4f || !TryGetEndpointAlignmentDirection(point.position, direction, out Vector3 aligned)) return;
        // Horizontal alignment does not alter the longitudinal profile.
        Vector3 position = point.position + aligned * length;
        position.y = control.Value.y;
        if (incoming) point.control_in = position;
        else point.control_out = position;
        points[index] = point;
    }

    /// <summary>
    /// Finds existing path segments ending at world XZ and returns the extension direction nearest
    /// handleDir (automatically calculated endpoint handle direction), normalized in XZ with sign resolved.
    /// Used to snap endpoint controls when a new path connects to an existing endpoint
    /// for a smooth junction.
    ///
    /// - Consider only segments whose endpoint nearly matches pos (ignore paths merely crossing mid-segment).
    /// - Exclude network-incompatible paths and the edited path's own segments (prevents snapping back onto itself).
    /// - Among multiple candidates (e.g. at an intersection), choose the smallest angle to handleDir.
    /// - Return false if the smallest angle exceeds PATH_TANGENT_ALIGN_MAX_ANGLE_DEG
    ///   (the user clearly drew in another direction; alignment would ruin the curve).
    /// - Choose the extension sign with positive dot product against handleDir to avoid unintended reversals.
    /// </summary>
    private bool TryGetExistingPathExtensionDir(Vector3 pos, Vector3 handleDir, out Vector3 extensionDir)
    {
        extensionDir = Vector3.zero;
        if (pathManager == null) return false;

        Vector3 handleFlat = new Vector3(handleDir.x, 0f, handleDir.z);
        if (handleFlat.sqrMagnitude < 1e-8f) return false;
        handleFlat.Normalize();

        var segs = TerrainInputHandler.GetVisiblePathSnapSegments(pathManager.QueryNearbySegments(pos, snapThreshold));
        if (segs == null) return false;

        float bestAngle = float.MaxValue;
        bool found = false;
        foreach (var seg in segs)
        {
            // Ignore network-incompatible paths (other types or infrastructure networks), which are not connection targets.
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
            // Exclude segments of the edited path itself (snapping to itself would reverse direction).
            if (seg.path != null && pathId != 0 && seg.path.path_id == pathId) continue;

            float dA = Vector2.Distance(new Vector2(seg.a.x, seg.a.z), new Vector2(pos.x, pos.z));
            float dB = Vector2.Distance(new Vector2(seg.b.x, seg.b.z), new Vector2(pos.x, pos.z));

            Vector3 near, far;
            float endpointDist;
            if (dA <= dB) { near = seg.a; far = seg.b; endpointDist = dA; }
            else { near = seg.b; far = seg.a; endpointDist = dB; }

            // Consider an extension direction only when pos nearly matches a segment endpoint.
            if (endpointDist >= snapThreshold) continue;

            Vector3 dir = new Vector3(near.x - far.x, 0f, near.z - far.z);
            if (dir.sqrMagnitude < 1e-6f) continue;
            dir.Normalize();

            Vector3 candidate = Vector3.Dot(handleFlat, dir) >= 0f ? dir : -dir;
            float angle = Vector3.Angle(handleFlat, candidate);
            if (angle <= Constants.PATH_TANGENT_ALIGN_MAX_ANGLE_DEG && angle < bestAngle)
            {
                bestAngle = angle;
                extensionDir = candidate;
                found = true;
            }
        }
        return found;
    }

    private bool TryGetEndpointAlignmentDirection(Vector3 position, Vector3 current, out Vector3 aligned)
    {
        return IsRailJunctionTarget
            ? TryGetRailHandleDirection(position, current, out aligned)
            : TryGetExistingPathExtensionDir(position, current, out aligned);
    }
}
