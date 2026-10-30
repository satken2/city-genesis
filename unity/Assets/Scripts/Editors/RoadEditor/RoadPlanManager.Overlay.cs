using UnityEngine;
using GeneratedModels;

public partial class RoadPlanManager
{
    // Background overlay (existing path edges/nodes). Rebuild on camera movement, node updates, or underground display changes.
    private OverlayMeshRenderer backgroundRenderer;
    private GameObject backgroundMeshObj;
    private Vector3 lastBgCameraPos = Vector3.zero;
    private bool bgDirty = true;
    private const float BG_REBUILD_THRESHOLD_SQ = 25f;

    /// <summary>
    /// PathManager.NodeDataVersion at the last background overlay rebuild.
    /// Nodes also arrive asynchronously after entering edit mode, so detect counter changes
    /// and rebuild the background (existing nodes) even without camera movement.
    /// </summary>
    private int lastBgNodeDataVersion = -1;
    private bool lastBgUndergroundView;

    // Distance fade (same constants as ParcelManager)
    private const float DISTANCE_START_FADE = 280f;
    private const float DISTANCE_INVISIBLE = 480f;

    private void UpdateOverlay()
    {
        if (overlayRenderer == null) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        UpdateBackgroundOverlay(cam);

        overlayRenderer.BeginFrame();

        // Grid points specify an area; do not draw polyline edges, controls, or local interference estimates.
        if (IsGridBuild)
        {
            DrawGridOverlay(cam);
            overlayRenderer.EndFrame(cam);
            return;
        }

        DrawPlacedEdges(cam);

        // Infrastructure terminal markers (draw here every frame instead of in the background mesh
        // to animate rotation and bobbing).
        DrawInfraTerminalMarkers(cam);

        if (phase == RoadEditorPhase.Placing)
        {
            if (points.Count > 0)
            {
                DrawPreviewLine(cam);
            }
            DrawPlacingMarker(cam);
        }

        DrawPoints(cam);
        DrawControlHandles(cam);
        DrawParallelSnapGuide();

        if (hoveredEdgeIndex >= 0)
        {
            DrawHoverHighlight(cam);
        }

        if (isRightAngleSnapActive)
        {
            DrawRightAngleSnapGuide(cam);
        }

        if (isStraightLineSnapActive)
        {
            DrawStraightLineSnapGuide(cam);
        }

        DrawTunnelSections(cam);
        DrawMaxSpanWarnings(cam);
        DrawAlignmentWarnings(cam);
        DrawElevationIntersections(cam);
        DrawBuildingInterference(cam);

        overlayRenderer.EndFrame(cam);
    }

    /// <summary>
    /// Overlays tunnel sections of the edited underground path (depth &gt; clearance height) in orange.
    /// Tunnel sections have no mesh after construction and are hidden by terrain, so
    /// visualize where tunnels will be during editing with this _ZTest=Always overlay.
    /// Terrain is not excavated before construction, so depth against current terrain matches backend classification.
    /// </summary>
    private void DrawTunnelSections(Camera cam)
    {
        if (overlayRenderer == null || points.Count < 2) return;
        if (!IsHeightEditable()) return;

        // Negative relative heights indicate an underground path. Also visualize effective non-following paths
        // (follow_terrain=0), which may enter hills and form tunnels even at relative height 0.
        bool followTerrain = EffectiveFollowTerrain();
        bool underground = false;
        if (followTerrain)
        {
            foreach (var pt in points)
            {
                if (pt.position.y - Utils.GetPathSurfaceHeight(pt.position) < -TunnelSectionUtil.UNDERGROUND_REL_Y_EPS)
                {
                    underground = true;
                    break;
                }
            }
            if (!underground) return;
        }

        float clearance = GetPathHeightFromMaster(PathSubtype);
        var tunnelRuns = TunnelSectionUtil.CollectTunnelRuns(GetRenderPoints(), clearance, followTerrain);
        if (tunnelRuns.Count == 0) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        Color tunnelColor = new Color(1f, 0.55f, 0.1f, 0.85f);

        foreach (var run in tunnelRuns)
        {
            for (int i = 0; i < run.Count - 1; i++)
            {
                Vector3 a = run[i];
                Vector3 b = run[i + 1];
                if (Vector3.Dot(a - camPos, camFwd) < 0f && Vector3.Dot(b - camPos, camFwd) < 0f)
                    continue;
                overlayRenderer.AddSolidLine(a, b, tunnelColor, dashedLineThickness * 1.5f);
            }
        }
    }

    /// <summary>
    /// Updates the background overlay (existing path edges/nodes).
    /// Rebuilds the mesh when the camera moves beyond the threshold, nodes update, or underground display changes.
    /// Otherwise reuses the existing mesh.
    /// </summary>
    private void UpdateBackgroundOverlay(Camera cam)
    {
        if (backgroundRenderer == null) return;

        Vector3 camPos = cam.transform.position;
        // Also rebuild on actual node cache changes (asynchronous arrivals/deletions).
        bool nodeDataChanged = pathManager != null && pathManager.NodeDataVersion != lastBgNodeDataVersion;
        bool undergroundChanged = lastBgUndergroundView != UndergroundView.IsActive;
        if (!bgDirty && !nodeDataChanged && !undergroundChanged
            && (camPos - lastBgCameraPos).sqrMagnitude < BG_REBUILD_THRESHOLD_SQ)
            return;

        bgDirty = false;
        lastBgCameraPos = camPos;
        lastBgNodeDataVersion = pathManager != null ? pathManager.NodeDataVersion : -1;
        lastBgUndergroundView = UndergroundView.IsActive;

        backgroundRenderer.BeginFrame();
        DrawExistingEdges(cam);
        DrawExistingNodes(cam);
        backgroundRenderer.EndFrame(cam);
    }



    /// <summary>
    /// Draws existing path edges (thin white lines).
    /// Pass world coordinates directly to OverlayMeshRenderer.
    /// Called only inside UpdateBackgroundOverlay's backgroundRenderer.BeginFrame/EndFrame,
    /// so always target backgroundRenderer. Output to overlayRenderer would be cleared
    /// by BeginFrame at the start of each UpdateOverlay and disappear in the same frame.
    /// </summary>
    private void DrawExistingEdges(Camera cam)
    {
        if (pathManager == null) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        var segments = TerrainInputHandler.GetVisiblePathSnapSegments(pathManager.QueryNearbySegments(camPos, DISTANCE_INVISIBLE));
        if (segments == null) return;

        Color lineColor = Color.white;

        foreach (var seg in segments)
        {
            Vector3 mid = (seg.a + seg.b) * 0.5f;
            float dist = Vector3.Distance(camPos, mid);
            float alpha = CalculateAlphaByDistance(dist);
            if (alpha <= 0.01f) continue;

            Vector3 sA = seg.a;
            Vector3 sB = seg.b;
            sA.y = Utils.GetPathSurfaceHeight(sA) + 0.05f;
            sB.y = Utils.GetPathSurfaceHeight(sB) + 0.05f;

            // Cull behind the near plane.
            if (Vector3.Dot(sA - camPos, camFwd) < 0f && Vector3.Dot(sB - camPos, camFwd) < 0f)
                continue;

            Color c = lineColor;
            c.a *= alpha;

            // Fade unsnappable paths (infrastructure during road construction, roads or other infrastructure networks during infrastructure construction)
            // to light gray to indicate they are not connection targets (same convention as faded nodes).
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype))
            {
                c = new Color(0.5f, 0.5f, 0.5f, alpha * 0.3f);
            }

            backgroundRenderer.AddSolidLine(sA, sB, c, solidLineThickness);
        }
    }

    /// <summary>
    /// Draws existing nodes.
    /// Visually emphasizes connectable nodes (infrastructure or road nodes) according to the path being laid,
    /// fading incompatible targets to light gray.
    /// Called only inside UpdateBackgroundOverlay's backgroundRenderer.BeginFrame/EndFrame,
    /// so always target backgroundRenderer. Output to overlayRenderer would be cleared
    /// by BeginFrame at the start of each UpdateOverlay and disappear in the same frame.
    /// </summary>
    private void DrawExistingNodes(Camera cam)
    {
        if (pathManager == null) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;
        var nodes = pathManager.QueryNearbyNodes(camPos, DISTANCE_INVISIBLE);
        if (nodes == null) return;

        bool drawingInfra = InfraNetworkUtil.IsInfraPathType(PathType);

        foreach (var node in nodes)
        {
            if (!TerrainInputHandler.IsNodeSnapVisible(node, pathManager)) continue;
            Vector3 worldPos = node.GetAbsolutePosition();
            float dist = Vector3.Distance(camPos, worldPos);
            float alpha = CalculateAlphaByDistance(dist);
            if (alpha <= 0.01f) continue;

            // Cull behind the near plane.
            if (Vector3.Dot(worldPos - camPos, camFwd) < 0f) continue;

            worldPos.y += 0.1f;

            // Whether snapping is allowed (includes network compatibility: road × infrastructure is invalid, as are
            // distinct infrastructure networks such as power lines × water conduits; see InfraNetworkUtil.IsNodeSnappable).
            bool snappable = InfraNetworkUtil.IsNodeSnappable(node, PathSubtype, pathManager);
            bool isInfraNode = InfraNetworkUtil.IsInfraMask(node.type);

            if (!snappable)
            {
                // Fade unsnappable nodes to light gray to indicate they are not connection targets.
                Color c = new Color(0.5f, 0.5f, 0.5f, alpha * 0.3f);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 0.8f, c);
            }
            else if (drawingInfra && isInfraNode)
            {
                // During infrastructure construction, emphasize same-network infrastructure nodes
                // (including facility terminals) in large cyan with an inner white border.
                Color c = new Color(0f, 0.8f, 1f, alpha);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 1.8f, c);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 0.8f, new Color(1f, 1f, 1f, alpha));
            }
            else
            {
                // Draw ordinary snappable nodes at standard size in white.
                Color c = Color.white;
                c.a = alpha;
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize, c);
            }
        }
    }

    /// <summary>
    /// During infrastructure path construction, draw inverted red square pyramid markers above connectable facility terminals
    /// (slow rotation + vertical bobbing).
    /// Since they animate, draw them in overlayRenderer, rebuilt each frame, rather than backgroundRenderer,
    /// which rebuilds only on camera movement (terminal counts are small, so the cost is negligible).
    /// Show only facility terminals network-compatible with the current variant. Leave other-network terminals
    /// to DrawExistingNodes' gray fading, and show none during road construction.
    /// </summary>
    private void DrawInfraTerminalMarkers(Camera cam)
    {
        if (pathManager == null) return;

        // Show only during infrastructure path construction (also derive from master data
        // in case PathType is unset).
        bool drawingInfra = InfraNetworkUtil.IsInfraPathType(PathType)
            || InfraNetworkUtil.IsInfraPathVariant(PathSubtype);
        if (!drawingInfra) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        // Search radius matches DrawExistingNodes at 480m. Do not increase it:
        // huge-radius SpatialHash queries can freeze the application.
        var nodes = pathManager.QueryNearbyNodes(camPos, DISTANCE_INVISIBLE);
        if (nodes == null) return;

        foreach (var node in nodes)
        {
            // Centralize display eligibility here:
            // an infrastructure node that is a facility terminal (BuildingManager infra_node_id reverse lookup)
            // and network-compatible (snappable) with the current variant.
            if (!InfraNetworkUtil.IsInfraMask(node.type)) continue;
            if (BuildingManager.Instance == null
                || !BuildingManager.Instance.TryGetInfraTerminalOwner(node.node_id, out _)) continue;
            if (!InfraNetworkUtil.IsNodeSnappable(node, PathSubtype, pathManager)) continue;
            if (!TerrainInputHandler.IsNodeSnapVisible(node, pathManager)) continue;

            Vector3 worldPos = node.GetAbsolutePosition();
            float dist = Vector3.Distance(camPos, worldPos);
            float alpha = CalculateAlphaByDistance(dist);
            if (alpha <= 0.01f) continue;

            // Cull behind the near plane.
            if (Vector3.Dot(worldPos - camPos, camFwd) < 0f) continue;

            // Call the shared drawing utility to use identical logic and material.
            InfrastructureMarkerUtility.DrawPyramid(overlayRenderer, worldPos, InfrastructureMarkerUtility.COLOR_BRIGHT, alpha);
        }
    }

    /// <summary>
    /// Draws point connections as blue solid lines (Editing) or dotted lines (Placing).
    /// Passes world coordinates directly to OverlayMeshRenderer.
    /// </summary>
    private void DrawPlacedEdges(Camera cam)
    {
        if (points.Count < 2) return;

        Color edgeColor = new Color(0.3f, 0.6f, 1f);
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 p1 = points[i].position;
            Vector3 p2 = points[i + 1].position;

            if (Vector3.Dot(p1 - camPos, camFwd) < 0f && Vector3.Dot(p2 - camPos, camFwd) < 0f)
                continue;

            if (phase == RoadEditorPhase.Editing)
            {
                overlayRenderer.AddSolidLine(p1, p2, edgeColor, dashedLineThickness);
            }
            else
            {
                overlayRenderer.AddDashedLine(p1, p2, edgeColor, dashedLineThickness);
            }
        }
    }

    /// <summary>
    /// Draws a preview line from the last point to the mouse during Placing.
    /// Passes world coordinates directly to OverlayMeshRenderer.
    /// </summary>
    private void DrawPreviewLine(Camera cam)
    {
        Vector3 lastPos = points[points.Count - 1].position;
        Vector3 hoverPos = hoverWorldPosition;
        hoverPos.y = Utils.GetPathSurfaceHeight(hoverPos) + currentHeight;

        Color previewColor = new Color(0.3f, 0.6f, 1f, 0.5f);
        overlayRenderer.AddDashedLine(lastPos, hoverPos, previewColor, dashedLineThickness);
    }

    /// <summary>
    /// Draws a placement marker following the mouse during Placing.
    /// Shows grid-snapped hoverWorldPosition as a green circle
    /// to indicate where the next node will be placed.
    /// Projects the marker onto the terrain surface.
    /// </summary>
    private void DrawPlacingMarker(Camera cam)
    {
        Vector3 markerPos = hoverWorldPosition;
        markerPos.y = Utils.GetPathSurfaceHeight(markerPos);

        if (Vector3.Dot(markerPos - cam.transform.position, cam.transform.forward) < 0f)
            return;

        Color markerColor = new Color(0.65f, 0.89f, 0.63f);
        overlayRenderer.AddCircle(markerPos, pointCircleRadius * 1.2f, markerColor);
    }

    /// <summary>
    /// Colors placement points by role:
    /// Start=green, End=red, Waypoint=blue.
    /// Projects markers onto terrain even when elevation is set.
    /// </summary>
    private void DrawPoints(Camera cam)
    {
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 groundPos = points[i].position;
            groundPos.y = Utils.GetPathSurfaceHeight(groundPos);

            if (Vector3.Dot(groundPos - camPos, camFwd) < 0f) continue;

            Color c;
            switch (points[i].role)
            {
                case PathPointRole.Start:
                    c = new Color(0.65f, 0.89f, 0.63f);
                    break;
                case PathPointRole.End:
                    c = new Color(0.95f, 0.55f, 0.66f);
                    break;
                default:
                    c = new Color(0.54f, 0.71f, 0.98f);
                    break;
            }

            if (i == selectedPointIndex)
            {
                // Draw an outer white ring to emphasize the selected node.
                Color ringColor = new Color(1f, 1f, 1f, 0.9f);
                overlayRenderer.AddCircle(groundPos, pointCircleRadius * 1.5f, ringColor);
                c = new Color(0.2f, 1f, 0.4f);
            }

            overlayRenderer.AddCircle(groundPos, pointCircleRadius, c);
        }
    }

    /// <summary>
    /// Draws square control handles, connected to anchors with dashed lines.
    /// Visible only in Editing.
    /// Each point has up to 2 handles (control_in / control_out).
    /// Projects both anchors and controls onto terrain height.
    /// </summary>
    private void DrawControlHandles(Camera cam)
    {
        if (!CanEditControls) return;

        Color outColor = new Color(0.976f, 0.886f, 0.686f); // Outgoing control (yellow)
        Color inColor = new Color(0.878f, 0.686f, 0.976f);  // Incoming control (purple)
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 anchorGround = points[i].position;
            anchorGround.y = Utils.GetPathSurfaceHeight(anchorGround);

            // Outgoing control (control_out)
            if (points[i].control_out.HasValue)
            {
                Vector3 coGround = points[i].control_out.Value;
                coGround.y = Utils.GetPathSurfaceHeight(coGround);

                if (Vector3.Dot(anchorGround - camPos, camFwd) >= 0f || Vector3.Dot(coGround - camPos, camFwd) >= 0f)
                {
                    overlayRenderer.AddDashedLine(anchorGround, coGround, outColor, 1.5f);
                    overlayRenderer.AddSquare(coGround, controlHandleHalfSize, outColor);
                }
            }

            // Incoming control (control_in)
            if (points[i].control_in.HasValue)
            {
                Vector3 ciGround = points[i].control_in.Value;
                ciGround.y = Utils.GetPathSurfaceHeight(ciGround);

                if (Vector3.Dot(anchorGround - camPos, camFwd) >= 0f || Vector3.Dot(ciGround - camPos, camFwd) >= 0f)
                {
                    overlayRenderer.AddDashedLine(anchorGround, ciGround, inColor, 1.5f);
                    overlayRenderer.AddSquare(ciGround, controlHandleHalfSize, inColor);
                }
            }
        }
    }

    /// <summary>
    /// Highlights the hovered edge with a thick yellow line.
    /// Projects the highlight onto terrain height.
    /// </summary>
    private void DrawHoverHighlight(Camera cam)
    {
        if (hoveredEdgeIndex < 0 || hoveredEdgeIndex >= points.Count - 1) return;

        Vector3 p1Ground = points[hoveredEdgeIndex].position;
        p1Ground.y = Utils.GetPathSurfaceHeight(p1Ground);
        Vector3 p2Ground = points[hoveredEdgeIndex + 1].position;
        p2Ground.y = Utils.GetPathSurfaceHeight(p2Ground);

        if (Vector3.Dot(p1Ground - cam.transform.position, cam.transform.forward) < 0f
            && Vector3.Dot(p2Ground - cam.transform.position, cam.transform.forward) < 0f)
            return;

        Color highlightColor = new Color(1f, 1f, 0.3f, 0.8f);
        overlayRenderer.AddSolidLine(p1Ground, p2Ground, highlightColor, dashedLineThickness * 2.5f);
    }

    /// <summary>
    /// Converts pixel size to world size at the specified world position.
    /// </summary>
    private float GetWorldPixelScale(Camera cam, Vector3 worldPos, float pixelSize)
    {
        float distance = Vector3.Distance(cam.transform.position, worldPos);
        float tanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float worldPerPixel = distance * tanHalfFov / (Screen.height * 0.5f);
        return pixelSize * worldPerPixel;
    }

    /// <summary>
    /// Draws right-angle snap guides.
    /// <summary>
    /// Draws straight-extension snap guides.
    /// Shows the extension through the two reference points as a faint orange dashed line,
    /// with a green circle at the snap position.
    /// </summary>
    private void DrawStraightLineSnapGuide(Camera cam)
    {
        if (!isStraightLineSnapActive) return;

        Color guideLineColor = new Color(1f, 0.7f, 0.3f, 0.5f);
        Color markerColor = new Color(0.65f, 0.89f, 0.63f, 0.8f);

        // Two extension reference points (projected onto terrain height)
        Vector3 anchor = straightLineSnapAnchor;
        anchor.y = Utils.GetPathSurfaceHeight(anchor);
        Vector3 ext = straightLineSnapExtension;
        ext.y = Utils.GetPathSurfaceHeight(ext);

        // Draw the extension sufficiently long.
        Vector3 dir = new Vector3(anchor.x - ext.x, 0f, anchor.z - ext.z);
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        // Extend the reference segment 50m on each side.
        Vector3 guideStart = anchor - dir * 50f;
        guideStart.y = Utils.GetPathSurfaceHeight(guideStart);
        Vector3 guideEnd = anchor + dir * 50f;
        guideEnd.y = Utils.GetPathSurfaceHeight(guideEnd);

        if (Vector3.Dot(guideStart - cam.transform.position, cam.transform.forward) >= 0f
            || Vector3.Dot(guideEnd - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddDashedLine(guideStart, guideEnd, guideLineColor, 1.5f);
        }

        // Marker at the snap position
        Vector3 snappedPos = straightLineSnapSnappedPos;
        snappedPos.y = Utils.GetPathSurfaceHeight(snappedPos);
        if (Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddCircle(snappedPos, pointCircleRadius * 1.2f, markerColor);
        }
    }

    /// <summary>
    /// Highlights the target existing path segment with a thick yellow line,
    /// marks the predicted snap position with a green circle,
    /// and draws a dashed line from the original mouse position to the snapped position.
    /// </summary>
    private void DrawRightAngleSnapGuide(Camera cam)
    {
        if (!isRightAngleSnapActive) return;

        Color targetLineColor = new Color(1f, 0.92f, 0.016f, 0.9f); // Yellow
        Color guideLineColor = new Color(0.65f, 0.89f, 0.63f, 0.6f); // Green dashed line
        Color markerColor = new Color(0.65f, 0.89f, 0.63f, 0.8f);   // Green marker

        Vector3 origin = rightAngleSnapOrigin;
        origin.y = Utils.GetPathSurfaceHeight(origin);

        // Draw a small cross at the snap reference (convert pixel length to world space).
        if (Vector3.Dot(origin - cam.transform.position, cam.transform.forward) >= 0f)
        {
            float crossLen = GetWorldPixelScale(cam, origin, 12f);
            Vector3 right = Vector3.Cross(cam.transform.forward, Vector3.up).normalized;
            if (right.sqrMagnitude < 0.0001f) right = Vector3.Cross(cam.transform.forward, Vector3.right).normalized;
            Vector3 up = Vector3.Cross(right, cam.transform.forward).normalized;
            overlayRenderer.AddSolidLine(origin - right * crossLen, origin + right * crossLen, targetLineColor, 2f);
            overlayRenderer.AddSolidLine(origin - up * crossLen, origin + up * crossLen, targetLineColor, 2f);
        }

        // Draw a dashed line from the original mouse position to the snapped position.
        Vector3 targetPos = rightAngleSnapTarget;
        targetPos.y = Utils.GetPathSurfaceHeight(targetPos);
        Vector3 snappedPos = rightAngleSnapSnappedPos;
        snappedPos.y = Utils.GetPathSurfaceHeight(snappedPos);

        if (Vector3.Dot(targetPos - cam.transform.position, cam.transform.forward) >= 0f
            || Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddDashedLine(targetPos, snappedPos, guideLineColor, 1.5f);
        }

        // Show a green circle at the predicted snapped position.
        if (Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddCircle(snappedPos, pointCircleRadius * 1.2f, markerColor);
        }
    }
}
