using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

public enum BuildingPlacementMode { Parcel, Free, Roadside }

public partial class BuildingPlanManager
{
    public BuildingPlacementMode PlacementMode { get; private set; } = BuildingPlacementMode.Parcel;
    public bool PlacementActive => isBuildingModeActive && !isEditMode && agentVariant > 0 &&
        InputHandler.Instance != null && InputHandler.Instance.GetMode(InputObjectType.TERRAIN) == InputMode.MODE_BUILDING_CONSTRUCTION;
    public bool DirectAllowed { get; set; } = true;
    public bool IsDirectPlacement => PlacementActive && DirectAllowed && PlacementMode != BuildingPlacementMode.Parcel;
    public bool PlacementCommitted => directCommitted;
    public bool HasRoadAnchor => roadAnchorActive;
    public bool SizeEditable => MasterDataStore.Instance?.GetAgentMaster(agentVariant)?.resizable ?? true;
    public float PlacementWidth => directWidth;
    public float PlacementDepth => directDepth;
    public float PlacementAngle => directAngle;
    public bool PlacementVisible => IsDirectPlacement && directPreviewVisible;
    public Vector3 PlacementCenter => directCenter;
    public Vector3 RoadFront => roadFront;
    public Vector3 RoadGuideA => roadGuideA;
    public Vector3 RoadGuideB => roadGuideB;
    public int ActiveSizeAxis { get; private set; }
    public readonly Dictionary<string, string> PlacementLabels = new Dictionary<string, string>();

    private int placementRevision;
    private bool hasRawPlacementPosition;
    private Vector3 lastRawPlacementPosition;
    // Free placement orientation (degrees) when not snapped to a road. The road sets the orientation while snapped; leaving it restores this angle.
    private float freeAngle;
    private PathManager placementPaths;
    private bool roadAnchorActive;
    private int roadAnchorId = -1;
    private int roadAnchorSegment = -1;
    private Vector3 roadFront, roadOutward, roadGuideA, roadGuideB;
    // 1/2=increase/decrease frontage; 3/4=increase/decrease depth. Key repeat uses the shared PlacementInput.cs implementation.
    private readonly KeyRepeat[] sizeKeys = { new KeyRepeat(Key.Digit1), new KeyRepeat(Key.Digit2), new KeyRepeat(Key.Digit3), new KeyRepeat(Key.Digit4) };
    private float maxRoadHalfWidth = 100f;
    private readonly HashSet<long> visitedPlacementSegments = new HashSet<long>();
    private readonly List<RoadCandidate> roadCandidates = new List<RoadCandidate>();
    private struct RoadCandidate
    {
        public PathSegment segment;
        public Vector3 front, outward, center;
        public float angle, score;
    }

    private void InitializePlacement()
    {
        ClearPreview();
        selectedParcelId = -1;
        PlacementMode = BuildingPlacementMode.Parcel;
        ApplyFootprintDefaults();
        if (MasterDataStore.Instance != null)
        {
            maxRoadHalfWidth = 0;
            foreach (var path in MasterDataStore.Instance.GetPathMasterDictionary().Values)
                if (path.type == Constants.PATH_STREET || path.type == Constants.PATH_MOTORWAY)
                    maxRoadHalfWidth = Mathf.Max(maxRoadHalfWidth, path.width * 0.5f);
        }
    }

    /// <summary>
    /// Aligns direct placement footprint dimensions and floors with master defaults.
    /// For landmarks (currentLandmark > 0), landmark_master determines both site dimensions and floors,
    /// and backend direct placement enforces the same values (resizable=false also prevents overriding
    /// frontage/depth). Starting from agent_master default_width / min_floors would make
    /// the ghost differ from the landmark's actual size and break geometry reply generation matching (floor equality).
    /// </summary>
    private void ApplyFootprintDefaults()
    {
        var master = MasterDataStore.Instance?.GetAgentMaster(agentVariant);
        var landmark = currentLandmark > 0 ? MasterDataStore.Instance?.GetLandmarkMaster(currentLandmark) : null;
        directWidth = Mathf.Clamp(landmark?.width ?? master?.default_width ?? 20f, 1f, 500f);
        directDepth = Mathf.Clamp(landmark?.depth ?? master?.default_height ?? directWidth, 1f, 500f);
        currentFloors = landmark?.floors ?? master?.min_floors ?? 1;
        hasDirectParams = true;
    }

    public string PlacementText(string key, string fallback)
        => PlacementLabels.TryGetValue(key, out var text) ? text : fallback;

    private void ApplyPlacementInputMode()
    {
        InputSystemManager.Instance?.SetInputMode(InputObjectType.PARCEL,
            isEditMode || PlacementMode == BuildingPlacementMode.Parcel ? InputMode.MODE_BUILDING_CONSTRUCTION : InputMode.MODE_NONE);
    }

    public void SetPlacementMode(BuildingPlacementMode mode)
    {
        if (!PlacementActive || (!DirectAllowed && mode != BuildingPlacementMode.Parcel) || mode == PlacementMode) return;
        bool keepPosition = directCommitted && mode != BuildingPlacementMode.Parcel;
        PlacementMode = mode;
        if (mode == BuildingPlacementMode.Free) freeAngle = directAngle;
        ResetRoadAnchor();
        middleDrag.Cancel();
        if (!keepPosition) ResetPlacementSelection(false);
        ApplyPlacementInputMode();
        RefreshPlacementPreview();
        EmitPlacementState();
    }

    public void ResetPlacementSelection(bool notify = true)
    {
        directCommitted = false;
        selectedParcelId = -1;
        middleDrag.Cancel();
        ResetRoadAnchor();
        HideDirectGhost();
        ClearPreview();
        effectRangeOverlay?.Clear();
        RefreshPlacementPreview();
        if (notify) EmitPlacementState();
    }

    public void RefreshPlacementPreview()
    {
        if (!directCommitted && hasRawPlacementPosition) OnTerrainHoverDirect(lastRawPlacementPosition);
    }

    private void ResetRoadAnchor()
    {
        roadAnchorActive = false;
        roadAnchorId = roadAnchorSegment = -1;
    }

    // The panel sends only changed fields. It does not echo snap angles or dimensions returned by Unity.
    public void OnPlacementCommand(JToken command)
    {
        if (command == null || !isBuildingModeActive || (int?)command["variant"] != agentVariant) return;
        if (command["labels"] is JObject labels)
            foreach (var pair in labels) PlacementLabels[pair.Key] = (string)pair.Value;
        if ((bool?)command["reset"] == true) { ResetPlacementSelection(); return; }
        if (IsDirectPlacement && (command["width"] != null || command["depth"] != null || command["angle"] != null))
        {
            float width = SizeEditable ? (float?)command["width"] ?? directWidth : directWidth;
            float depth = SizeEditable ? (float?)command["depth"] ?? directDepth : directDepth;
            float? rotation = PlacementMode == BuildingPlacementMode.Free ? (float?)command["angle"] : null;
            float angle = rotation ?? directAngle;
            if (!float.IsNaN(width) && !float.IsInfinity(width) && !float.IsNaN(depth) && !float.IsInfinity(depth) &&
                !float.IsNaN(angle) && !float.IsInfinity(angle))
            {
                directWidth = Mathf.Clamp(width, 1f, 500f);
                directDepth = Mathf.Clamp(depth, 1f, 500f);
                if (rotation.HasValue) RotateFree(angle);
                RebuildAfterSizeChange();
            }
        }
        EmitPlacementState(false, (string)command["request_id"]);
    }

    /// <summary>
    /// Manually changes free placement orientation (panel angle or middle-button drag). While positioning snapped to a road,
    /// the road sets the orientation, so only remember the manual angle for leaving the road. Rotating after fixing
    /// the position breaks road alignment, so unsnap it (later dimension changes must not pull it back to the road-facing edge).
    /// </summary>
    private void RotateFree(float angle)
    {
        freeAngle = angle;
        if (roadAnchorActive && !directCommitted) return;
        directAngle = angle;
        ResetRoadAnchor();
    }

    private void RebuildAfterSizeChange()
    {
        // Preserve the fixed road-facing edge. Do not move it automatically on interference; use the authoritative preview to judge it.
        if (roadAnchorActive) directCenter = BuildingPlacementGeometry.CenterFromFront(roadFront, roadOutward, directDepth);
        else if (!directCommitted) RefreshPlacementPreview();
        if (directPreviewVisible) BuildDirectRectanglePreview();
        placementRevision++;
    }

    private void EmitPlacementState(bool advance = true, string requestId = null)
    {
        if (!isBuildingModeActive || isEditMode) return;
        if (advance) placementRevision++;
        IpcClient.Instance?.EmitEditBuildingPlacementState(new Dictionary<string, object>
        {
            ["variant"] = agentVariant,
            ["revision"] = placementRevision,
            ["mode"] = PlacementMode.ToString().ToLowerInvariant(),
            ["position"] = directCommitted ? new float[] { directCenter.x, 0, directCenter.z } : null,
            ["width"] = directWidth,
            ["depth"] = directDepth,
            ["angle"] = directAngle,
            ["parcel_id"] = selectedParcelId,
            ["request_id"] = requestId,
        });
    }

    private bool UpdatePlacementInput()
    {
        ActiveSizeAxis = 0;
        if (!PlacementActive || PlacementInput.IsBlocked())
        {
            if (isMiddleDragging) { middleDrag.Cancel(); EmitPlacementState(); }
            return false;
        }
        if (InputUtil.WorldEscapeWasPressed) ResetPlacementSelection();
        if (!IsDirectPlacement) return false;
        if (SizeEditable && !isMiddleDragging)
        {
            bool changed = false;
            float step = PlacementInput.ShiftHeld ? 5 : 1;
            for (int i = 0; i < sizeKeys.Length; i++)
            {
                if (!sizeKeys[i].Held) continue;
                ActiveSizeAxis = i < 2 ? 1 : 2;
                if (!sizeKeys[i].Fired()) continue;
                float delta = (i % 2 == 0 ? 1 : -1) * step;
                if (i < 2) directWidth = Mathf.Clamp(directWidth + delta, 1f, 500f);
                else directDepth = Mathf.Clamp(directDepth + delta, 1f, 500f);
                changed = true;
            }
            if (changed) { RebuildAfterSizeChange(); EmitPlacementState(false); }
        }
        return true;
    }

    private bool IsGroundRoad(PathSegment segment, bool requireFrontage)
    {
        if (segment.path == null) return false;
        var master = MasterDataStore.Instance?.GetPathMaster(segment.path.path_variant);
        if (master == null || (master.type != Constants.PATH_STREET && master.type != Constants.PATH_MOTORWAY) ||
            (requireFrontage && !master.frontage_allowed) || !PathManager.GetPathFollowTerrain(segment.path)) return false;
        // Render points use absolute heights. Exclude elevated and underground sections away from ground level.
        return Mathf.Abs(segment.a.y - Utils.GetGroundHeight(segment.a)) < 0.3f &&
            Mathf.Abs(segment.b.y - Utils.GetGroundHeight(segment.b)) < 0.3f;
    }

    /// <summary>
    /// Resolves road snapping. Roadside placement finds a nearby ground-level road; free placement snaps to the shoulder
    /// on the cursor's side only while over the road surface (including sidewalks). Both share snap calculations: align the front
    /// to the road's outer edge facing the road and reject intrusions into curves/intersections. Restore the manual angle when free placement does not snap.
    /// </summary>
    private void ResolveRoadPlacement(Vector3 raw)
    {
        bool roadside = PlacementMode == BuildingPlacementMode.Roadside;
        if (!roadside && PlacementMode != BuildingPlacementMode.Free) { ResetRoadAnchor(); return; }
        if (TryAnchorToRoad(raw, roadside)) return;
        ResetRoadAnchor();
        if (!roadside) directAngle = freeAngle;
    }

    private bool TryAnchorToRoad(Vector3 raw, bool roadside)
    {
        if (placementPaths == null) placementPaths = FindAnyObjectByType<PathManager>();
        if (placementPaths == null) return false;
        Vector3 cursor = new Vector3(raw.x, 0, raw.z);
        float radius = roadside ? directDepth * 0.5f + maxRoadHalfWidth + 12f : maxRoadHalfWidth;
        var candidates = placementPaths.QueryNearbySegments(raw, radius);
        roadCandidates.Clear();
        visitedPlacementSegments.Clear();
        foreach (var segment in candidates)
        {
            if (segment.path == null || !visitedPlacementSegments.Add(SegmentKey(segment))) continue;
            if (!IsGroundRoad(segment, true)) continue;
            Vector3 tangent = segment.b - segment.a;
            tangent.y = 0;
            if (tangent.sqrMagnitude < 0.0001f) continue;
            tangent.Normalize();
            Vector3 closest = BuildingPlacementGeometry.Closest(raw, segment.a, segment.b);
            float halfRoad = placementPaths.GetRoadWidth(segment.path.path_variant) * 0.5f;
            float fromCenterLine = Vector3.Distance(cursor, closest);
            if (!roadside && fromCenterLine > halfRoad) continue;
            Vector3 normal = new Vector3(-tangent.z, 0, tangent.x);
            float side = Vector3.Dot(raw - closest, normal);
            // Near the road center, retain the previous side and flip only when clearly moving to the opposite side.
            if (Mathf.Abs(side) < 1f && roadAnchorActive && roadAnchorId == segment.path.path_id)
                side = Vector3.Dot(roadOutward, normal);
            normal *= side < 0 ? -1 : 1;
            Vector3 front = closest + normal * (halfRoad + 0.05f);
            Vector3 center = BuildingPlacementGeometry.CenterFromFront(front, normal, directDepth);
            float angle = BuildingPlacementGeometry.AngleFromOutward(normal);
            float distance = Vector3.Distance(cursor, center);
            if (roadside && distance > directDepth * 0.5f + 12f) continue;
            // Roadside placement chooses the building candidate nearest the cursor; free placement chooses the road nearest directly beneath it.
            float score = roadside ? distance : fromCenterLine;
            if (roadAnchorActive && roadAnchorId == segment.path.path_id && Vector3.Dot(roadOutward, normal) > 0)
                score -= roadAnchorSegment == segment.segmentIndex ? 1f : 0.6f;
            roadCandidates.Add(new RoadCandidate { segment = segment, front = front, outward = normal,
                center = center, angle = angle, score = score });
        }
        // Deduplicate sections returned from multiple spatial hash cells. Validate nearest candidates first
        // and stop when one is accepted, avoiding interference searches for more distant candidates.
        roadCandidates.Sort((a, b) => a.score.CompareTo(b.score));
        foreach (var candidate in roadCandidates)
        {
            bool overlaps = false;
            float checkRadius = Mathf.Sqrt(directWidth * directWidth + directDepth * directDepth) * 0.5f + maxRoadHalfWidth;
            visitedPlacementSegments.Clear();
            foreach (var other in placementPaths.QueryNearbySegments(candidate.center, checkRadius))
            {
                if (other.path == null || !visitedPlacementSegments.Add(SegmentKey(other))) continue;
                if (!IsGroundRoad(other, false)) continue;
                if (BuildingPlacementGeometry.OverlapsRoad(candidate.center, directWidth, directDepth, candidate.angle, other.a, other.b,
                    placementPaths.GetRoadWidth(other.path.path_variant) * 0.5f)) { overlaps = true; break; }
            }
            if (overlaps) continue;
            roadAnchorActive = true;
            roadAnchorId = candidate.segment.path.path_id;
            roadAnchorSegment = candidate.segment.segmentIndex;
            roadFront = candidate.front;
            roadOutward = candidate.outward;
            float halfWidth = placementPaths.GetRoadWidth(candidate.segment.path.path_variant) * 0.5f;
            roadGuideA = candidate.segment.a + candidate.outward * halfWidth;
            roadGuideB = candidate.segment.b + candidate.outward * halfWidth;
            directCenter = candidate.center;
            directAngle = candidate.angle;
            return true;
        }
        return false;
    }

    private static long SegmentKey(PathSegment segment) => ((long)segment.path.path_id << 32) | (uint)segment.segmentIndex;
}
