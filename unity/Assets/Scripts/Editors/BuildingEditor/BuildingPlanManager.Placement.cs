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
    // 自由配置で道路に吸着していないときの向き(度)。吸着中は道路が向きを決め、道路から外れるとこの向きに戻る。
    private float freeAngle;
    private PathManager placementPaths;
    private bool roadAnchorActive;
    private int roadAnchorId = -1;
    private int roadAnchorSegment = -1;
    private Vector3 roadFront, roadOutward, roadGuideA, roadGuideB;
    // 1/2=間口の増減、3/4=奥行きの増減。長押しリピートは PlacementInput.cs の共通実装。
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
    /// 直接配置の底面寸法と階数をマスターの既定値へ揃える。
    /// 観光名所(currentLandmark > 0)は敷地寸法も階数も landmark_master が正で、
    /// バックエンドの直接配置も同じ値を強制する(resizable=false のため間口・奥行きの
    /// 上書き経路も無い)。agent_master の default_width / min_floors から始めると、
    /// ゴーストが名所の実寸にならず、形状応答の世代照合(階数一致)も成立しない。
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
    /// 自由配置の向きを手動で変える(パネルの回転角・中ボタンドラッグ)。位置決め中に道路へ吸着している間は
    /// 道路が向きを決めるので、道路から外れたときの向きとして覚えるだけにする。位置の固定後に回すと
    /// 道路に沿った向きではなくなるので吸着を外す(以後の寸法変更で道路側の辺に引き戻さない)。
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
        // 描画点は絶対高さ。地表から離れた高架・地下区間を対象にしない。
        return Mathf.Abs(segment.a.y - Utils.GetGroundHeight(segment.a)) < 0.3f &&
            Mathf.Abs(segment.b.y - Utils.GetGroundHeight(segment.b)) < 0.3f;
    }

    /// <summary>
    /// 道路への吸着を解決する。道路沿い配置は近くの地上道路を探して吸着し、自由配置はカーソルが路面(歩道込み)の
    /// 上にあるときだけ、カーソルのある側の路肩へ吸着する。吸着の計算(正面を道路の外縁に合わせて道路側へ向け、
    /// 曲線の内側や交差点に食い込む候補を除く)は共通。自由配置で吸着しないときは手動で決めた向きに戻す。
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
            // 道路中心付近では直前の側を維持し、明確に反対側へ動かしたときだけ反転する。
            if (Mathf.Abs(side) < 1f && roadAnchorActive && roadAnchorId == segment.path.path_id)
                side = Vector3.Dot(roadOutward, normal);
            normal *= side < 0 ? -1 : 1;
            Vector3 front = closest + normal * (halfRoad + 0.05f);
            Vector3 center = BuildingPlacementGeometry.CenterFromFront(front, normal, directDepth);
            float angle = BuildingPlacementGeometry.AngleFromOutward(normal);
            float distance = Vector3.Distance(cursor, center);
            if (roadside && distance > directDepth * 0.5f + 12f) continue;
            // 道路沿い配置は建物がカーソルに近い候補、自由配置はカーソルの真下に近い道路を選ぶ。
            float score = roadside ? distance : fromCenterLine;
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
            return true;
        }
        return false;
    }

    private static long SegmentKey(PathSegment segment) => ((long)segment.path.path_id << 32) | (uint)segment.segmentIndex;
}
