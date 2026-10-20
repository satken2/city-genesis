using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

public partial class RoadPlanManager
{
    // Temporary state for right-angle snap guides
    private bool isRightAngleSnapActive;
    private Vector3 rightAngleSnapOrigin;
    private Vector3 rightAngleSnapTarget;
    private Vector3 rightAngleSnapSnappedPos;

    // Temporary state for straight-extension snap guides
    private bool isStraightLineSnapActive;
    private Vector3 straightLineSnapAnchor;
    private Vector3 straightLineSnapExtension;
    private Vector3 straightLineSnapSnappedPos;

    // Whether the latest ApplySnapping snapped to an elevated node/path/construction endpoint, and its relative height (elevation).
    // Used to inherit height for placed nodes snapped to elevated edges.
    // Reset to false on paths such as grid/straight-extension/right-angle snapping that flatten Y to ground.
    private bool lastSnapWasElevated;
    private float lastSnapRelHeight;
    // Saved value for connecting hover preview lines at the snapped height
    private bool hoverSnapWasElevated;
    private float hoverSnapRelHeight;

    /// <summary>
    /// Applies straight-extension snapping.
    ///
    /// Placing: snap to the extension of the previous 2 points.
    ///   Extend the direction vector P[n-2] → P[n-1] and project basePos
    ///   onto the closest point on that line.
    ///
    /// Editing: use the 2 neighboring points of the dragged point as references.
    ///   - Non-first points: extension of the preceding or following 2 points,
    ///     or projection onto the segment joining both neighbors for intermediate points.
    ///   - Snap if the closest point on a reference line is within the threshold.
    ///
    /// Calculates in horizontal XZ; Y follows terrain height.
    /// </summary>
    private Vector3 ApplyStraightLineSnap(Vector3 basePos)
    {
        isStraightLineSnapActive = false;

        if (phase == RoadEditorPhase.Placing)
        {
            // Placing: extension of the previous 2 points
            return ApplyPlacingStraightLineSnap(basePos);
        }
        else if (phase == RoadEditorPhase.Editing)
        {
            // Editing: reference neighboring nodes of the dragged point
            return ApplyEditingStraightLineSnap(basePos);
        }

        return basePos;
    }

    /// <summary>
    /// Straight-extension snapping in Placing.
    /// Extend the direction through points[n-2], points[n-1] infinitely
    /// and project basePos onto it. Snap if the XZ distance
    /// to that projection is within snapThreshold.
    /// </summary>
    private Vector3 ApplyPlacingStraightLineSnap(Vector3 basePos)
    {
        if (points.Count < 2) return basePos;
        float distance = float.PositiveInfinity;
        Vector3 snapped = basePos, anchor = default, extension = default;
        bool found = false;
        TrySnapToExtensionLine(basePos, points[points.Count - 2].position, points[points.Count - 1].position,
            ref distance, ref snapped, ref anchor, ref extension, ref found);
        if (!found) return basePos;
        isStraightLineSnapActive = true;
        straightLineSnapAnchor = anchor;
        straightLineSnapExtension = extension;
        straightLineSnapSnappedPos = snapped;
        return snapped;
    }

    /// <summary>
    /// Straight-extension snapping in Editing.
    /// Uses the dragged point index (dragPointIndex)
    /// to choose an appropriate reference line.
    ///
    /// - First point (0): extension of the next 2 points (points[1], points[2]).
    /// - Last point (n-1): extension of the preceding 2 points (points[n-3], points[n-2]).
    /// - Intermediate points: segment connecting neighboring nodes (points[i-1], points[i+1]).
    ///   Also snap to whichever of the preceding/following 2-point extensions is closest.
    /// </summary>
    private Vector3 ApplyEditingStraightLineSnap(Vector3 basePos)
    {
        // Dragged or selected point index
        int idx = isDragging ? dragPointIndex : selectedPointIndex;
        if (idx < 0 || idx >= points.Count || points.Count < 2) return basePos;

        int n = points.Count;
        float bestDist = float.MaxValue;
        Vector3 bestSnapped = basePos;
        Vector3 bestAnchor = Vector3.zero;
        Vector3 bestExtension = Vector3.zero;
        bool found = false;

        // Candidate 1: extension of the preceding 2 points (idx >= 2)
        if (idx >= 2)
        {
            TrySnapToExtensionLine(basePos, points[idx - 2].position, points[idx - 1].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }

        // Candidate 2: extension of the following 2 points (idx <= n - 3)
        if (idx <= n - 3)
        {
            TrySnapToExtensionLine(basePos, points[idx + 2].position, points[idx + 1].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }

        // Candidate 3: for intermediate points, project onto the segment between both neighbors.
        if (idx > 0 && idx < n - 1)
        {
            Vector3 prev = points[idx - 1].position;
            Vector3 next = points[idx + 1].position;
            Vector3 segDir = new Vector3(next.x - prev.x, 0f, next.z - prev.z);
            float segLen = segDir.magnitude;
            if (segLen > 1e-6f)
            {
                Vector3 segDirNorm = segDir / segLen;
                Vector3 toBase = new Vector3(basePos.x - prev.x, 0f, basePos.z - prev.z);
                float t = Vector3.Dot(toBase, segDirNorm);
                // Project within the segment (0 <= t <= segLen).
                t = Mathf.Clamp(t, 0f, segLen);
                Vector3 projected = prev + segDirNorm * t;
                projected.y = Utils.GetGroundHeight(projected);
                float dist = Vector2.Distance(
                    new Vector2(basePos.x, basePos.z),
                    new Vector2(projected.x, projected.z)
                );
                if (dist <= snapThreshold && dist < bestDist)
                {
                    bestDist = dist;
                    bestSnapped = projected;
                    bestAnchor = prev;
                    bestExtension = next;
                    found = true;
                }
            }
        }

        // For a 2-point road, use the line through the opposite anchor and original position.
        if (n == 2)
            TrySnapToExtensionLine(basePos, points[1 - idx].position, points[idx].position,
                ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);

        if (found)
        {
            isStraightLineSnapActive = true;
            straightLineSnapAnchor = bestAnchor;
            straightLineSnapExtension = bestExtension;
            straightLineSnapSnappedPos = bestSnapped;
            return bestSnapped;
        }

        return basePos;
    }

    /// <summary>
    /// Projects basePos onto the extension in direction extB → extA,
    /// recording a candidate if within the snap threshold.
    /// Projects onto the infinite line through extA in direction (extA - extB).
    /// </summary>
    private void TrySnapToExtensionLine(
        Vector3 basePos, Vector3 extB, Vector3 extA,
        ref float bestDist, ref Vector3 bestSnapped,
        ref Vector3 bestAnchor, ref Vector3 bestExtension,
        ref bool found)
    {
        Vector3 dir = new Vector3(extA.x - extB.x, 0f, extA.z - extB.z);
        if (dir.sqrMagnitude < 1e-6f) return;

        Vector3 dirNorm = dir.normalized;
        Vector3 toBase = new Vector3(basePos.x - extA.x, 0f, basePos.z - extA.z);
        float t = Vector3.Dot(toBase, dirNorm);
        Vector3 projected = extA + dirNorm * t;
        projected.y = Utils.GetGroundHeight(projected);

        float dist = Vector2.Distance(
            new Vector2(basePos.x, basePos.z),
            new Vector2(projected.x, projected.z)
        );

        if (dist <= snapThreshold && dist < bestDist)
        {
            bestDist = dist;
            bestSnapped = projected;
            bestAnchor = extA;
            bestExtension = extB;
            found = true;
        }
    }

    /// <summary>
    /// Applies shared connection/grid snapping and road-specific parallel/end alignment/angle assistance.
    /// Parallel mode prioritizes connection targets and does not add angle correction after snapping to a parallel line.
    /// </summary>
    private Vector3 ApplySnapping(Vector3 basePos)
    {
        if (TutorialRuntime.PathRestricted)
        {
            lastSnapWasElevated = false;
            lastSnapRelHeight = 0;
            return TutorialRuntime.TrySnap(basePos, points.Count == 0 ? 0 : 1, out var guided) ? guided : basePos;
        }
        lastRawSnapPosition = basePos;
        hasRawSnapPosition = true;
        Vector3 rawPosition = basePos;
        parallelSnapActive = false;
        parallelGuideVisible = false;
        lastSnapWasElevated = false;
        lastSnapRelHeight = 0f;

        // Station building paths never snap to existing features. Accept only alignment
        // to a grid explicitly displayed by the player.
        if (SnappingSuppressed)
        {
            isStraightLineSnapActive = false;
            isRightAngleSnapActive = false;
            if (TerrainInputHandler.EnableGridSnapping && GridOverlay.Instance != null)
            {
                return GridOverlay.Instance.SnapPosition(basePos);
            }
            return basePos;
        }

        // First apply shared TerrainInputHandler snapping (grid/node/path/district vertices).
        // Filter by the current variant; tracks/monorails snap only to the same path type.
        // Infrastructure paths snap only to targets compatible with the same infrastructure network.
        // terrainInputHandler belongs to terrain tiles; LOD tile creation/destruction can destroy
        // a cached reference (Unity pseudo-null). No tiles exist at startup, so
        // resolve it when snapping is needed, and resolve again after destruction.
        // Snapping logic is instance-independent (static flags, pathManager and threshold overrides),
        // so any live handler produces the same result.
        if (terrainInputHandler == null)
        {
            terrainInputHandler = FindAnyObjectByType<TerrainInputHandler>();
        }

        // During parallel track construction (nonzero lateral offsets), replace node/path snapping
        // with the offset-aware version. Simply snapping the group centerline to existing tracks shifts all tracks sideways,
        // preventing connection; instead snap the centerline so one group track lands on the target.
        // Normally prioritize the grid; parallel mode prioritizes connection target positions.
        bool offsetSnapApplied = false;
        if (HasNonZeroLateralOffsets()
            && (ParallelSnapEnabled || !(TerrainInputHandler.EnableGridSnapping && GridOverlay.Instance != null))
            && TryOffsetAwareRailSnap(basePos, out Vector3 offsetSnapped, out float offsetSnapRelHeight))
        {
            basePos = offsetSnapped;
            lastSnapWasElevated = true;
            lastSnapRelHeight = offsetSnapRelHeight;
            offsetSnapApplied = true;
        }

        // Skip shared node/path snapping after an offset-aware hit
        // (the snapped group center is separated from the existing track by a lateral offset;
        // shared snapping would pull it back onto that track's centerline).
        if (!offsetSnapApplied && terrainInputHandler != null)
        {
            basePos = terrainInputHandler.ApplySnapping(basePos, PathSubtype, out lastSnapWasElevated, out lastSnapRelHeight, snapThreshold, skipGrid: ParallelSnapEnabled);
        }

        if (!offsetSnapApplied && IsRailJunctionTarget && lastSnapWasElevated
            && TryProjectRailTarget(basePos, snapThreshold, out Vector3 railPoint, out _, out var railTarget))
        {
            basePos.x = railPoint.x;
            basePos.z = railPoint.z;
            lastSnapRelHeight = railTarget.GetRelativeHeightAtPoint(basePos.x, basePos.z);
        }

        // Also prioritize connection position/elevation in parallel mode; angle assistance must not change them again.
        if (lastSnapWasElevated && (IsRailJunctionTarget || ParallelSnapEnabled))
        {
            isStraightLineSnapActive = false;
            isRightAngleSnapActive = false;
            return basePos;
        }

        if (TryApplyParallelSnap(rawPosition, out Vector3 parallelPosition))
        {
            isStraightLineSnapActive = false;
            isRightAngleSnapActive = false;
            lastSnapWasElevated = false;
            return parallelPosition;
        }

        if (ParallelSnapEnabled && TerrainInputHandler.EnableGridSnapping && GridOverlay.Instance != null)
        {
            isStraightLineSnapActive = false;
            isRightAngleSnapActive = false;
            return GridOverlay.Instance.SnapPosition(rawPosition);
        }

        // Straight-extension snapping (higher priority than right-angle snapping)
        if (TerrainInputHandler.EnableStraightLineSnapping && points.Count >= 2)
        {
            Vector3 straightSnapped = ApplyStraightLineSnap(basePos);
            if (isStraightLineSnapActive)
            {
                isRightAngleSnapActive = false;
                // Straight-extension snapping flattens Y to ground, so disable elevated height inheritance.
                lastSnapWasElevated = false;
                return straightSnapped;
            }
        }
        else
        {
            isStraightLineSnapActive = false;
        }

        // Return shared snapping results if right-angle snapping is disabled or no points exist.
        if (!TerrainInputHandler.EnableRightAngleSnapping || points.Count == 0)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }

        // Previous point (last placed point)
        Vector3 lastPoint = points[points.Count - 1].position;

        // Check whether at least one of lastPoint and basePos lies on an existing path/node.
        // Detection: look for existing path segments around lastPoint or basePos.
        bool lastOnExisting = false;
        bool currentOnExisting = false;
        Vector3 existingSegA = Vector3.zero;
        Vector3 existingSegB = Vector3.zero;

        if (pathManager != null)
        {
            lastOnExisting = TryGetRightAngleReference(lastPoint, out existingSegA, out existingSegB);
            currentOnExisting = TryGetRightAngleReference(basePos, out Vector3 currentA, out Vector3 currentB);
            if (currentOnExisting) { existingSegA = currentA; existingSegB = currentB; }
        }

        // Do not right-angle snap unless one of the points lies on an existing path.
        if (!lastOnExisting && !currentOnExisting)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }

        // Existing path segment direction (XZ)
        Vector3 pathDir = existingSegB - existingSegA;
        pathDir.y = 0;
        if (pathDir.sqrMagnitude < 1e-6f)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }
        pathDir.Normalize();

        // Direction perpendicular to pathDir (XZ)
        Vector3 perpDir = new Vector3(-pathDir.z, 0, pathDir.x);

        // Reference: the point on the existing path
        Vector3 anchor = lastOnExisting ? lastPoint : basePos;
        Vector3 other = lastOnExisting ? basePos : lastPoint;

        // Project the vector from other to anchor onto perpDir.
        Vector3 toOther = other - anchor;
        toOther.y = 0;
        float projLen = Vector3.Dot(toOther, perpDir);

        // Snapped position = anchor + perpDir * projLen
        Vector3 snapped = anchor + perpDir * projLen;
        snapped.y = Utils.GetGroundHeight(snapped);
        // Right-angle snapping flattens Y to ground, so disable elevated height inheritance.
        lastSnapWasElevated = false;

        // Save state for drawing guides.
        isRightAngleSnapActive = true;
        rightAngleSnapOrigin = anchor;
        rightAngleSnapTarget = other;
        rightAngleSnapSnappedPos = snapped;

        return snapped;
    }

    // =====================================================================
    // Offset-aware snapping for parallel tracks
    // =====================================================================

    /// <summary>Nearby segment search radius for finding point-target (node/endpoint) tangents in offset-aware snapping (m).</summary>
    private const float OFFSET_SNAP_TANGENT_SEARCH_RADIUS = 2.0f;

    /// <summary>XZ distance for treating a segment endpoint as touching a point target (m).</summary>
    private const float OFFSET_SNAP_TOUCH_TOLERANCE = 0.5f;

    /// <summary>
    /// Whether parallel tracks with nonzero lateral offsets are being laid.
    /// Determines whether to use TryOffsetAwareRailSnap.
    /// Excludes single tracks ([0] only), roads, and existing path editing (empty array), which use shared snapping
    /// (single tracks have offset 0, so both methods give identical results).
    /// </summary>
    private bool HasNonZeroLateralOffsets()
    {
        if (LateralOffsets == null) return false;
        foreach (float offset in LateralOffsets)
        {
            if (Mathf.Abs(offset) > 0.01f) return true;
        }
        return false;
    }

    /// <summary>
    /// Offset-aware snapping for parallel track construction.
    ///
    /// A track group is internally represented by one group-center polyline, but confirmation
    /// expands it into parallel single-track paths, leaving no existing-path group object.
    /// Snapping the group center directly to an existing track centerline would shift every track sideways
    /// by half the spacing, preventing any connection. For target node/construction endpoint/segment
    /// position E and tangent t, enumerate center candidates where a group track with lateral offset o lands exactly on E:
    /// C = E ± perp(t)·o. Snap to the candidate nearest the raw cursor.
    ///
    /// - Near an existing group center, candidates derived from left/right tracks
    ///   coincide, so extending an identically configured group aligns all endpoints.
    /// - The sign of tangent ±t cannot be decided on the first click before construction direction is known,
    ///   so enumerate both signs and let the actual direction resolve it (identical sets for symmetric offsets).
    /// - Match shared TerrainInputHandler.ApplySnapping for node-before-segment priority, network compatibility,
    ///   node/path snap flags, and elevated relative-height inheritance.
    ///   Exclude district vertex snapping, which has no practical use for track construction.
    /// </summary>
    private bool TryOffsetAwareRailSnap(Vector3 basePos, out Vector3 snapped, out float snapRelHeight)
    {
        snapped = basePos;
        snapRelHeight = 0f;
        if (pathManager == null) return false;
        if (!TerrainInputHandler.EnableNodeSnapping && !TerrainInputHandler.EnablePathSnapping) return false;

        // Expand search radius by the maximum offset because targets lie that far from candidate centers.
        float maxOffset = 0f;
        foreach (float offset in LateralOffsets)
        {
            maxOffset = Mathf.Max(maxOffset, Mathf.Abs(offset));
        }
        float searchRadius = snapThreshold + maxOffset;

        float bestDist = snapThreshold;
        bool found = false;

        // Nodes/construction endpoints (prioritize node targets as in shared snapping; skip segments on a hit).
        if (TerrainInputHandler.EnableNodeSnapping)
        {
            var nodes = pathManager.QueryNearbyNodes(basePos, searchRadius);
            if (nodes != null)
            {
                foreach (var node in nodes)
                {
                    if (!InfraNetworkUtil.IsNodeSnappable(node, PathSubtype, pathManager)) continue;
                    if (!TerrainInputHandler.IsNodeSnapVisible(node, pathManager)) continue;
                    EvaluatePointTargetOffsetCandidates(basePos, pathManager.GetNodeSnapPosition(node), ref bestDist, ref snapped, ref snapRelHeight, ref found);
                }
            }

            // Nodes are created only after construction completes, so directly test
            // start_point / end_point of planned/under-construction paths
            // (same reason as shared FindNearestConstructionEndpoint).
            EvaluateConstructionEndpointOffsetCandidates(basePos, searchRadius, ref bestDist, ref snapped, ref snapRelHeight, ref found);

            if (found) return true;
        }

        // Path segments (snap by projection onto a laterally shifted line).
        if (TerrainInputHandler.EnablePathSnapping)
        {
            var segments = TerrainInputHandler.GetVisiblePathSnapSegments(pathManager.QueryNearbySegments(basePos, searchRadius));
            if (segments != null)
            {
                var projectedRails = new HashSet<int>();
                foreach (var seg in segments)
                {
                    if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
                    if (IsRailJunctionTarget && seg.path != null && !projectedRails.Add(seg.path.path_id)) continue;
                    if (IsRailJunctionTarget && seg.path != null
                        && RailJunctionGeometry.TryProject(seg.path, basePos, out Vector3 railPosition, out Vector3 railTangent))
                    {
                        if (!IsRailSnapPointVisible(seg.path, railPosition)) continue;
                        // Analytic projections have Y=0, so assign surface elevation before building candidates.
                        railPosition.y = RailSurfaceHeight(seg.path, railPosition);
                        EvaluateTangentOffsetCandidates(basePos, railPosition, railTangent,
                            ref bestDist, ref snapped, ref snapRelHeight, ref found);
                        continue;
                    }
                    Vector3 dir = seg.b - seg.a;
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 1e-6f) continue;
                    dir.Normalize();
                    Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
                    foreach (float offset in LateralOffsets)
                    {
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            Vector3 shift = perp * (offset * sign);
                            Vector3 candidate = PolylineUtils.ClosestPointOnSegmentXZ(basePos, seg.a - shift, seg.b - shift);
                            float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(candidate.x, candidate.z));
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                snapped = candidate;
                                // Inherit elevation from anchor relative-Y interpolation at the corresponding original centerline point
                                // (candidate + shift), avoiding noisy inversion of rendered Y minus terrain samples.
                                snapRelHeight = seg.path != null
                                    ? seg.path.GetRelativeHeightAtPoint(candidate.x + shift.x, candidate.z + shift.z)
                                        + Utils.GetGroundHeight(candidate + shift) - Utils.GetGroundHeight(candidate)
                                    : 0f;
                                found = true;
                            }
                        }
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Evaluates offset-aware candidates for point targets (existing nodes/construction endpoints).
    /// For every tangent touching the target, enumerate C = target ± perp(tangent)·offset
    /// and update the best result with the candidate nearest the raw cursor. Use nearby segments whose endpoints
    /// match the target (try all intersection arms), falling back to the nearest segment if none match.
    /// </summary>
    private void EvaluatePointTargetOffsetCandidates(Vector3 basePos, Vector3 targetPos, ref float bestDist, ref Vector3 bestPos, ref float bestRelHeight, ref bool found)
    {
        var segs = TerrainInputHandler.GetVisiblePathSnapSegments(pathManager.QueryNearbySegments(targetPos, OFFSET_SNAP_TANGENT_SEARCH_RADIUS));

        Vector2 targetXZ = new Vector2(targetPos.x, targetPos.z);

        bool anyTouching = false;
        Vector3 fallbackDir = Vector3.zero;
        float fallbackDist = float.MaxValue;
        foreach (var seg in segs)
        {
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
            Vector3 dir = seg.b - seg.a;
            if (IsRailJunctionTarget && seg.path != null
                && RailJunctionGeometry.TryProject(seg.path, targetPos, out _, out Vector3 railTangent))
                dir = railTangent;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) continue;

            float distA = Vector2.Distance(targetXZ, new Vector2(seg.a.x, seg.a.z));
            float distB = Vector2.Distance(targetXZ, new Vector2(seg.b.x, seg.b.z));
            if (Mathf.Min(distA, distB) <= OFFSET_SNAP_TOUCH_TOLERANCE)
            {
                anyTouching = true;
                EvaluateTangentOffsetCandidates(basePos, targetPos, dir, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
            }
            else if (!anyTouching)
            {
                Vector3 closest = PolylineUtils.ClosestPointOnSegment(targetPos, seg.a, seg.b);
                float d = Vector2.Distance(targetXZ, new Vector2(closest.x, closest.z));
                if (d < fallbackDist)
                {
                    fallbackDist = d;
                    fallbackDir = dir;
                }
            }
        }
        if (!anyTouching && fallbackDir.sqrMagnitude > 1e-6f)
        {
            EvaluateTangentOffsetCandidates(basePos, targetPos, fallbackDir, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
        }
    }

    /// <summary>
    /// Enumerates C = target ± perp·offset for one point target/tangent and updates the best result.
    /// Preserve absolute target elevation (targetPos.y) during lateral movement; calculate relative height from destination terrain.
    /// </summary>
    private void EvaluateTangentOffsetCandidates(Vector3 basePos, Vector3 targetPos, Vector3 tangent, ref float bestDist, ref Vector3 bestPos, ref float bestRelHeight, ref bool found)
    {
        Vector3 dir = tangent.normalized;
        Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
        foreach (float offset in LateralOffsets)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 candidate = targetPos + perp * (offset * sign);
                float dist = Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(candidate.x, candidate.z));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    candidate.y = targetPos.y;
                    bestPos = candidate;
                    bestRelHeight = candidate.y - Utils.GetGroundHeight(candidate);
                    found = true;
                }
            }
        }
    }

    /// <summary>
    /// Evaluates offset-aware candidates for planned/under-construction path endpoints (start_point / end_point).
    /// </summary>
    private void EvaluateConstructionEndpointOffsetCandidates(Vector3 basePos, float searchRadius, ref float bestDist, ref Vector3 bestPos, ref float bestRelHeight, ref bool found)
    {
        var allPaths = pathManager.GetAllPaths();
        if (allPaths == null) return;

        foreach (var path in allPaths)
        {
            if (path.status != Constants.STATUS_SCHEDULED_CONSTRUCTION &&
                path.status != Constants.STATUS_PENDING &&
                path.status != Constants.STATUS_CONSTRUCTING)
            {
                continue;
            }
            if (!InfraNetworkUtil.IsPathSnappable(path.path_variant, PathSubtype)) continue;

            EvaluateEndpointOffsetCandidates(basePos, path, GeneratedModelUtils.ToFloatList(path.start_point), searchRadius, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
            EvaluateEndpointOffsetCandidates(basePos, path, GeneratedModelUtils.ToFloatList(path.end_point), searchRadius, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
        }
    }

    /// <summary>Converts one [x, relative height, z] endpoint to absolute coordinates and evaluates point-target candidates.</summary>
    private void EvaluateEndpointOffsetCandidates(Vector3 basePos, PathSummary path, List<float> pointList, float searchRadius, ref float bestDist, ref Vector3 bestPos, ref float bestRelHeight, ref bool found)
    {
        if (pointList == null || pointList.Count < 2) return;
        float x = pointList[0];
        float z = pointList[pointList.Count - 1];
        float rel = path.GetRelativeHeightAtPoint(x, z);
        if (Vector2.Distance(new Vector2(basePos.x, basePos.z), new Vector2(x, z)) > searchRadius) return;
        Vector3 endpoint = new Vector3(x, Utils.GetGroundHeight(new Vector3(x, 0f, z)) + rel, z);
        if (!TerrainInputHandler.IsPathSnapPointVisible(path, endpoint)) return;
        EvaluatePointTargetOffsetCandidates(basePos, endpoint, ref bestDist, ref bestPos, ref bestRelHeight, ref found);
    }

    /// <summary>
    /// Returns the first nearby segment network-compatible with the current variant (PathSubtype).
    /// Used for right-angle reference direction so road construction does not pick up
    /// nearby infrastructure path directions (power lines, etc.).
    /// </summary>
    private bool TryGetRightAngleReference(Vector3 position, out Vector3 a, out Vector3 b)
    {
        bool connected = TerrainInputHandler.FindNearestNode(position, snapThreshold, pathManager, out _, PathSubtype) != null
            || TerrainInputHandler.FindNearestPathSegment(position, snapThreshold, pathManager, out _, out _, out _, PathSubtype);
        a = b = default;
        return connected && TryGetFirstSnappableSegment(position, out a, out b);
    }

    private bool TryGetFirstSnappableSegment(Vector3 pos, out Vector3 segA, out Vector3 segB)
    {
        segA = Vector3.zero;
        segB = Vector3.zero;
        if (pathManager == null) return false;

        var segs = TerrainInputHandler.GetVisiblePathSnapSegments(pathManager.QueryNearbySegments(pos, snapThreshold * 2f));
        if (segs == null) return false;

        foreach (var seg in segs)
        {
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype)) continue;
            segA = seg.a;
            segB = seg.b;
            return true;
        }
        return false;
    }
}
