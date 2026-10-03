using UnityEngine;

public partial class RoadPlanManager
{
    // 設定だけをセッション間で保持する。基準道路やガイドは敷設ごとの一時状態。
    public static bool EnableParallelSnapping { get; private set; }
    public static bool EnableEndAlignment { get; private set; }
    public bool ParallelSnapEnabled => EnableParallelSnapping && !Curved && !SnappingSuppressed;
    public bool EndAlignmentEnabled => ParallelSnapEnabled && EnableEndAlignment;

    [SerializeField] private float parallelSearchRadius = 100f;
    private bool parallelSnapActive;
    private bool parallelGuideVisible;
    private bool parallelEndAligned;
    private int parallelReferenceId = -1;
    private int parallelReferenceSegment = -1;
    private int parallelAnchorIndex = -2;
    private Vector3 parallelGuideA, parallelGuideB, parallelGuideAnchor, parallelGuidePosition, parallelGuideEnd;

    public void SetParallelSnapping(bool enabled)
    {
        EnableParallelSnapping = enabled;
        ResetParallelSnap();
        RefreshSnapPreview();
    }

    public void SetEndAlignment(bool enabled)
    {
        EnableEndAlignment = enabled;
        ResetParallelSnap();
        RefreshSnapPreview();
    }

    // メニューを閉じた後、入力側が静止Rayを省略してもプレビューを更新できるよう生座標を保持する。
    private Vector3 lastRawSnapPosition;
    private bool hasRawSnapPosition;

    public void RefreshSnapPreview()
    {
        if (hasRawSnapPosition && phase == RoadEditorPhase.Placing)
        {
            RefreshHoverSnapping();
            UpdateShapePreview();
        }
    }

    private void RefreshHoverSnapping()
    {
        if (hasRawSnapPosition && phase == RoadEditorPhase.Placing)
            UpdateHoverSnapping(lastRawSnapPosition);
    }

    private void ResetParallelSnap()
    {
        parallelSnapActive = false;
        parallelGuideVisible = false;
        parallelReferenceId = -1;
        parallelReferenceSegment = -1;
        parallelAnchorIndex = -2;
    }

    private bool TryApplyParallelSnap(Vector3 raw, out Vector3 snapped)
    {
        snapped = raw;
        if (!ParallelSnapEnabled || pathManager == null) return false;
        int firstAnchor, lastAnchor;
        if (phase == RoadEditorPhase.Placing)
            firstAnchor = lastAnchor = points.Count - 1;
        else if (phase == RoadEditorPhase.Editing && dragTarget == DragTarget.Point && dragPointIndex >= 0)
        {
            firstAnchor = dragPointIndex - 1;
            lastAnchor = dragPointIndex + 1;
        }
        else return false;

        float bestScore = float.PositiveInfinity;
        PathSegment bestSegment = default;
        int bestAnchor = -2;
        Vector3 bestOrigin = raw, bestEnd = default;
        bool bestAligned = false;
        bool bestSnapped = false;
        for (int index = firstAnchor; index <= lastAnchor; index++)
        {
            bool placingStart = phase == RoadEditorPhase.Placing && points.Count == 0;
            if (!placingStart && (index < 0 || index >= points.Count ||
                (phase == RoadEditorPhase.Editing && index == dragPointIndex))) continue;
            Vector3? anchor = placingStart ? (Vector3?)null : points[index].position;
            // 始点近傍も検索することで、長い区間の終端を動かしても基準道路を見失わない。
            for (int search = 0; search < (anchor.HasValue ? 2 : 1); search++)
            {
                Vector3 center = search == 0 ? raw : anchor.Value;
                var segments = TerrainInputHandler.GetVisiblePathSnapSegments(pathManager.QueryNearbySegments(center, parallelSearchRadius));
                foreach (var segment in segments)
                {
                    var path = segment.path;
                    if (path == null || path.path_id == pathId || path.curved ||
                        !InfraNetworkUtil.IsPathSnappable(path.path_variant, PathSubtype)) continue;
                    if (RoadParallelSnapGeometry.DistanceXZ(segment.a, segment.b) < 0.001f) continue;
                    float distance = RoadParallelSnapGeometry.SegmentDistance(center, segment.a, segment.b);
                    if (distance > parallelSearchRadius) continue;
                    var start = GeneratedModelUtils.ToFloatList(path.start_point);
                    var end = GeneratedModelUtils.ToFloatList(path.end_point);
                    if (start == null || start.Count < 2 || end == null || end.Count < 2) continue;
                    Vector3 startPosition = new Vector3(start[0], 0, start[start.Count - 1]);
                    Vector3 endPosition = new Vector3(end[0], 0, end[end.Count - 1]);
                    bool didSnap = RoadParallelSnapGeometry.TrySnap(raw, anchor, segment.a, segment.b,
                        startPosition, endPosition, EndAlignmentEnabled, snapThreshold,
                        out Vector3 candidate, out bool aligned, out Vector3 endpoint);
                    if (!didSnap && !anchor.HasValue) continue;
                    float score = RoadParallelSnapGeometry.DistanceXZ(raw, candidate) + distance * 0.02f;
                    // 同じ道路・区間・隣接点の候補を僅かに優遇し、境界での方向のちらつきを抑える。
                    if (path.path_id == parallelReferenceId && segment.segmentIndex == parallelReferenceSegment && index == parallelAnchorIndex)
                        score -= 0.75f;
                    if (bestSnapped && !didSnap) continue;
                    if (didSnap == bestSnapped && score >= bestScore) continue;
                    bestScore = score;
                    bestSegment = segment;
                    bestAnchor = index;
                    bestOrigin = anchor ?? candidate;
                    snapped = candidate;
                    bestAligned = aligned;
                    bestSnapped = didSnap;
                    bestEnd = endpoint;
                }
            }
        }
        if (bestSegment.path == null)
        {
            ResetParallelSnap();
            return false;
        }
        parallelSnapActive = bestSnapped;
        parallelGuideVisible = true;
        parallelEndAligned = bestAligned;
        parallelReferenceId = bestSegment.path.path_id;
        parallelReferenceSegment = bestSegment.segmentIndex;
        parallelAnchorIndex = bestAnchor;
        parallelGuideA = bestSegment.a;
        parallelGuideB = bestSegment.b;
        parallelGuideAnchor = bestOrigin;
        parallelGuidePosition = snapped;
        parallelGuideEnd = bestEnd;
        return bestSnapped;
    }

    private void DrawParallelSnapGuide()
    {
        if (!parallelGuideVisible || !ParallelSnapEnabled ||
            (phase != RoadEditorPhase.Placing && !isDragging)) return;
        Vector3 direction = parallelGuideB - parallelGuideA;
        direction.y = 0;
        direction.Normalize();
        DrawParallelGuideLine(parallelGuideA, parallelGuideB, new Color(1f, 0.8f, 0.25f, 0.8f));
        float length = Vector3.Dot(parallelGuidePosition - parallelGuideAnchor, direction);
        DrawParallelGuideLine(parallelGuideAnchor + direction * (Mathf.Min(0, length) - 30),
            parallelGuideAnchor + direction * (Mathf.Max(0, length) + 30), new Color(0.3f, 0.8f, 1f, 0.7f));
        if (parallelEndAligned && EndAlignmentEnabled)
            DrawParallelGuideLine(parallelGuideEnd, parallelGuidePosition, new Color(0.7f, 1f, 0.4f, 0.9f));
        if (parallelSnapActive)
        {
            Vector3 marker = parallelGuidePosition;
            marker.y = Utils.GetGroundHeight(marker);
            overlayRenderer.AddCircle(marker, pointCircleRadius * 1.2f, new Color(0.7f, 1f, 0.4f, 0.9f));
        }
    }

    private void DrawParallelGuideLine(Vector3 a, Vector3 b, Color color)
    {
        // 長いガイドも起伏に沿わせる。道路本体の標高をこの描画座標から逆算しない。
        int count = Mathf.Clamp(Mathf.CeilToInt(RoadParallelSnapGeometry.DistanceXZ(a, b) / 10), 1, 256);
        a.y = Utils.GetGroundHeight(a);
        Vector3 previous = a;
        for (int i = 1; i <= count; i++)
        {
            Vector3 next = Vector3.Lerp(a, b, (float)i / count);
            next.y = Utils.GetGroundHeight(next);
            overlayRenderer.AddDashedLine(previous, next, color, 1.5f);
            previous = next;
        }
    }
}
