using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

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
    private PathManager placementPaths;
    private bool roadAnchorActive;
    private int roadAnchorId = -1;
    private int roadAnchorSegment = -1;
    private Vector3 roadFront, roadOutward, roadGuideA, roadGuideB;
    private readonly float[] sizeRepeatTimes = new float[4];
    private static readonly Key[] SizeKeys = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4 };
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
        var master = MasterDataStore.Instance?.GetAgentMaster(agentVariant);
        directWidth = Mathf.Clamp(master?.default_width ?? 20f, 1f, 500f);
        directDepth = Mathf.Clamp(master?.default_height ?? directWidth, 1f, 500f);
        currentFloors = master?.min_floors ?? 1;
        hasDirectParams = true;
        if (MasterDataStore.Instance != null)
        {
            maxRoadHalfWidth = 0;
            foreach (var path in MasterDataStore.Instance.GetPathMasterDictionary().Values)
                if (path.type == Constants.PATH_STREET || path.type == Constants.PATH_MOTORWAY)
                    maxRoadHalfWidth = Mathf.Max(maxRoadHalfWidth, path.width * 0.5f);
        }
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
        ResetRoadAnchor();
        isMiddleDragging = false;
        if (!keepPosition) ResetPlacementSelection(false);
        ApplyPlacementInputMode();
        RefreshPlacementPreview();
        EmitPlacementState();
    }

    public void ResetPlacementSelection(bool notify = true)
    {
        directCommitted = false;
        selectedParcelId = -1;
        isMiddleDragging = false;
        ResetRoadAnchor();
        HideDirectGhost();
        ClearPreview();
        effectRangeOverlay?.Clear();
        if (shapeCoroutine != null) { StopCoroutine(shapeCoroutine); shapeCoroutine = null; }
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

    // パネルは変更したフィールドだけを送る。Unityから返したスナップ角度や寸法のエコーは送らない。
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
            float angle = PlacementMode == BuildingPlacementMode.Free ? (float?)command["angle"] ?? directAngle : directAngle;
            if (!float.IsNaN(width) && !float.IsInfinity(width) && !float.IsNaN(depth) && !float.IsInfinity(depth) &&
                !float.IsNaN(angle) && !float.IsInfinity(angle))
            {
                directWidth = Mathf.Clamp(width, 1f, 500f);
                directDepth = Mathf.Clamp(depth, 1f, 500f);
                directAngle = angle;
                RebuildAfterSizeChange();
            }
        }
        EmitPlacementState(false, (string)command["request_id"]);
    }

    private void RebuildAfterSizeChange()
    {
        // 固定した道路側の辺を保持する。干渉が生じても位置を勝手にずらさず、権威プレビューで判定する。
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
        bool blocked = !Application.isFocused || !PlacementActive || RadialMenuController.IsOpen ||
            (CameraController.Instance != null && (CameraController.Instance.IsFlyMode || CameraController.Instance.IsMouseDragging)) ||
            (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject());
        if (blocked)
        {
            if (isMiddleDragging) { isMiddleDragging = false; EmitPlacementState(); }
            return false;
        }
        if (InputUtil.KeyWasPressedThisFrame(Key.Escape)) ResetPlacementSelection();
        if (!IsDirectPlacement) return false;
        if (SizeEditable && !isMiddleDragging)
        {
            bool changed = false;
            float step = InputUtil.KeyPressed(Key.LeftShift) || InputUtil.KeyPressed(Key.RightShift) ? 5 : 1;
            for (int i = 0; i < SizeKeys.Length; i++)
            {
                if (!InputUtil.KeyPressed(SizeKeys[i])) continue;
                ActiveSizeAxis = i < 2 ? 1 : 2;
                bool pressed = InputUtil.KeyWasPressedThisFrame(SizeKeys[i]);
                if (!pressed && Time.unscaledTime < sizeRepeatTimes[i]) continue;
                sizeRepeatTimes[i] = Time.unscaledTime + (pressed ? 0.35f : 0.075f);
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
        // 描画点は絶対高さ。地表から離れた高架・地下区間を対象にしない。
        return Mathf.Abs(segment.a.y - Utils.GetGroundHeight(segment.a)) < 0.3f &&
            Mathf.Abs(segment.b.y - Utils.GetGroundHeight(segment.b)) < 0.3f;
    }

    private void ResolveRoadPlacement(Vector3 raw)
    {
        if (PlacementMode != BuildingPlacementMode.Roadside) { ResetRoadAnchor(); return; }
        if (placementPaths == null) placementPaths = FindAnyObjectByType<PathManager>();
        if (placementPaths == null) { ResetRoadAnchor(); return; }
        float radius = directDepth * 0.5f + maxRoadHalfWidth + 12f;
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
            Vector3 normal = new Vector3(-tangent.z, 0, tangent.x);
            float side = Vector3.Dot(raw - closest, normal);
            // 道路中心付近では直前の側を維持し、明確に反対側へ動かしたときだけ反転する。
            if (Mathf.Abs(side) < 1f && roadAnchorActive && roadAnchorId == segment.path.path_id)
                side = Vector3.Dot(roadOutward, normal);
            normal *= side < 0 ? -1 : 1;
            float halfRoad = placementPaths.GetRoadWidth(segment.path.path_variant) * 0.5f;
            Vector3 front = closest + normal * (halfRoad + 0.05f);
            Vector3 center = BuildingPlacementGeometry.CenterFromFront(front, normal, directDepth);
            float angle = BuildingPlacementGeometry.AngleFromOutward(normal);
            float distance = Vector3.Distance(new Vector3(raw.x, 0, raw.z), center);
            if (distance > directDepth * 0.5f + 12f) continue;
            float score = distance;
            if (roadAnchorActive && roadAnchorId == segment.path.path_id && Vector3.Dot(roadOutward, normal) > 0)
                score -= roadAnchorSegment == segment.segmentIndex ? 1f : 0.6f;
            roadCandidates.Add(new RoadCandidate { segment = segment, front = front, outward = normal,
                center = center, angle = angle, score = score });
        }
        // 空間ハッシュは同じ区間を複数セルから返すため重複を除く。近い候補から検証し、
        // 採用できた時点で止めて遠い候補の干渉検索を省く。
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
            return;
        }
        ResetRoadAnchor();
    }

    private static long SegmentKey(PathSegment segment) => ((long)segment.path.path_id << 32) | (uint)segment.segmentIndex;
}
