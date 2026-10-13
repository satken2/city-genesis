using System.Collections.Generic;
using UnityEngine;
using GeneratedModels;

public partial class RoadPlanManager
{
    // 直角スナップ補助線描画用の一時状態
    private bool isRightAngleSnapActive;
    private Vector3 rightAngleSnapOrigin;
    private Vector3 rightAngleSnapTarget;
    private Vector3 rightAngleSnapSnappedPos;

    // 直線延長スナップ補助線描画用の一時状態
    private bool isStraightLineSnapActive;
    private Vector3 straightLineSnapAnchor;
    private Vector3 straightLineSnapExtension;
    private Vector3 straightLineSnapSnappedPos;

    // 直近の ApplySnapping が高架のノード/パス/建設中端点へ吸着したか、その相対高さ（標高）。
    // 高架エッジに吸着したとき配置ノードの高さを引き継ぐために使う。
    // グリッド/直線延長/直角スナップなど Y を地面へ潰す経路では false にリセットされる。
    private bool lastSnapWasElevated;
    private float lastSnapRelHeight;
    // ホバープレビュー線を吸着高さで接続するための保存値
    private bool hoverSnapWasElevated;
    private float hoverSnapRelHeight;

    /// <summary>
    /// 直線延長スナップを適用する。
    ///
    /// Placingフェーズ: 直前の2点の延長線上に吸着する。
    ///   P[n-2] → P[n-1] の方向ベクトルを延長し、basePos をその延長線上の
    ///   最も近い点に射影する。
    ///
    /// Editingフェーズ: 現在ドラッグ中のポイントの隣接2点を基準に吸着する。
    ///   - 先頭以外のポイント: 手前2点の延長線、または後2点の延長線、
    ///     または中間点の場合は両脇のノードを結ぶ線分への射影
    ///   - 基準線上の最も近い点がスナップ閾値内であれば吸着する。
    ///
    /// XZ平面（水平面）で計算し、Y座標は地形高さを反映する。
    /// </summary>
    private Vector3 ApplyStraightLineSnap(Vector3 basePos)
    {
        isStraightLineSnapActive = false;

        if (phase == RoadEditorPhase.Placing)
        {
            // Placingフェーズ: 直前の2点の延長線
            return ApplyPlacingStraightLineSnap(basePos);
        }
        else if (phase == RoadEditorPhase.Editing)
        {
            // Editingフェーズ: ドラッグ中ポイントの隣接ノードを基準
            return ApplyEditingStraightLineSnap(basePos);
        }

        return basePos;
    }

    /// <summary>
    /// Placingフェーズでの直線延長スナップ。
    /// 直前の2点(points[n-2], points[n-1])の方向ベクトルを無限に延長し、
    /// basePos からその延長線への射影点を求める。射影点とのXZ距離が
    /// snapThreshold以内であればその点に吸着する。
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
    /// Editingフェーズでの直線延長スナップ。
    /// 現在ドラッグ対象のインデックス(dragPointIndex)に基づいて、
    /// 適切な基準線を決定し吸着する。
    ///
    /// - 先頭ポイント(0): 次の2点(points[1], points[2])の延長線に吸着
    /// - 末尾ポイント(n-1): 手前2点(points[n-3], points[n-2])の延長線に吸着
    /// - 中間ポイント: 前後の隣接ノード(points[i-1], points[i+1])を結ぶ線分に吸着。
    ///   また手前2点の延長線、後2点の延長線のうち最も近いものにも吸着。
    /// </summary>
    private Vector3 ApplyEditingStraightLineSnap(Vector3 basePos)
    {
        // ドラッグまたは選択中のポイントインデックス
        int idx = isDragging ? dragPointIndex : selectedPointIndex;
        if (idx < 0 || idx >= points.Count || points.Count < 2) return basePos;

        int n = points.Count;
        float bestDist = float.MaxValue;
        Vector3 bestSnapped = basePos;
        Vector3 bestAnchor = Vector3.zero;
        Vector3 bestExtension = Vector3.zero;
        bool found = false;

        // 候補1: 手前2点の延長線（idx >= 2 の場合）
        if (idx >= 2)
        {
            TrySnapToExtensionLine(basePos, points[idx - 2].position, points[idx - 1].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }

        // 候補2: 後2点の延長線（idx <= n - 3 の場合）
        if (idx <= n - 3)
        {
            TrySnapToExtensionLine(basePos, points[idx + 2].position, points[idx + 1].position, ref bestDist, ref bestSnapped, ref bestAnchor, ref bestExtension, ref found);
        }

        // 候補3: 中間点の場合、両脇のノードを結ぶ線分に射影
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
                // 線分の範囲内（0 <= t <= segLen）に射影
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

        // 2点の道路では反対側のアンカーと元の位置を結ぶ直線を基準にする。
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
    /// 指定した2点(extB → extA)の方向の延長線上に basePos を射影し、
    /// スナップ閾値以内であれば候補として記録する。
    /// extA を通る (extA - extB) 方向の無限直線に射影する。
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
    /// 共通の接続・グリッド吸着と、道路編集固有の並行・終端揃え・角度補助を適用する。
    /// 並行モードでは接続先を優先し、並行線への吸着後に別の角度補正を重ねない。
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

        // 駅舎パスは既存の地物へ一切吸着しない。プレイヤーが明示的に出したグリッドへの
        // 整列だけは受け付ける。
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

        // まずTerrainInputHandlerの共通スナップ（グリッド/ノード/パス/ゾーン頂点）を適用。
        // 建設中バリアントを渡すことでインフラ網の互換性フィルタが働く
        // （道路はインフラノード/パスに吸着せず、インフラパスは同一網にのみ吸着する）。
        // terrainInputHandler は地形タイル上のコンポーネントで、LODによるタイルの生成・破棄で
        // キャッシュした参照が破棄済み(Unityの疑似null)になりうる。起動時はタイルが無いため、
        // 吸着が必要になった時点で解決し、破棄後も再解決する。
        // 吸着ロジックはインスタンス非依存(フラグはstatic・pathManagerとthreshold override付き)
        // なので、生きているどのハンドラを引いても結果は同じ。
        if (terrainInputHandler == null)
        {
            terrainInputHandler = FindAnyObjectByType<TerrainInputHandler>();
        }

        // 軌道複線(0以外の横オフセットあり)の敷設中は、ノード/パス吸着をオフセット考慮版に
        // 差し替える。素朴に中心線を既存線路へ吸着させると全線路が横にズレて接続できないため、
        // 「グループ内のいずれかの線路が吸着対象に乗る」中心線位置へ吸着させる。
        // 通常はグリッドを優先し、並行モードでは接続先の位置を優先する。
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

        // オフセット考慮吸着がヒットしたら共通のノード/パス吸着は行わない
        // (吸着後の中心線位置は既存線路から横オフセット分離れており、共通スナップに通すと
        //  その既存線路の中心線へ引き戻されてしまうため)。
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

        // 並行モードでも接続先の位置と標高を優先し、角度補助では再変更しない。
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

        // 直線延長スナップ（直角スナップより優先）
        if (TerrainInputHandler.EnableStraightLineSnapping && points.Count >= 2)
        {
            Vector3 straightSnapped = ApplyStraightLineSnap(basePos);
            if (isStraightLineSnapActive)
            {
                isRightAngleSnapActive = false;
                // 直線延長スナップは Y を地面へ潰すため高架高さの引き継ぎは無効化する
                lastSnapWasElevated = false;
                return straightSnapped;
            }
        }
        else
        {
            isStraightLineSnapActive = false;
        }

        // 直角スナップが無効、またはポイントが1つもない場合は共通スナップ結果を返す
        if (!TerrainInputHandler.EnableRightAngleSnapping || points.Count == 0)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }

        // 直前のポイント（最後に配置済みのポイント）
        Vector3 lastPoint = points[points.Count - 1].position;

        // lastPoint と basePos のうち、少なくとも一方が既存パス/ノード上にあるかを判定
        // 判定方法: lastPoint または basePos の周辺に既存パスセグメントがあるか
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

        // どちらか一方が既存パス上にない場合は直角スナップしない
        if (!lastOnExisting && !currentOnExisting)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }

        // 既存パスセグメントの方向ベクトル（XZ平面）
        Vector3 pathDir = existingSegB - existingSegA;
        pathDir.y = 0;
        if (pathDir.sqrMagnitude < 1e-6f)
        {
            isRightAngleSnapActive = false;
            return basePos;
        }
        pathDir.Normalize();

        // pathDir に垂直な方向（XZ平面）
        Vector3 perpDir = new Vector3(-pathDir.z, 0, pathDir.x);

        // 基準点: 既存パス上にある方の点
        Vector3 anchor = lastOnExisting ? lastPoint : basePos;
        Vector3 other = lastOnExisting ? basePos : lastPoint;

        // other から anchor に向かうベクトルを perpDir に射影
        Vector3 toOther = other - anchor;
        toOther.y = 0;
        float projLen = Vector3.Dot(toOther, perpDir);

        // スナップ後の位置 = anchor + perpDir * projLen
        Vector3 snapped = anchor + perpDir * projLen;
        snapped.y = Utils.GetGroundHeight(snapped);
        // 直角スナップは Y を地面へ潰すため高架高さの引き継ぎは無効化する
        lastSnapWasElevated = false;

        // 補助線描画用に状態を保存
        isRightAngleSnapActive = true;
        rightAngleSnapOrigin = anchor;
        rightAngleSnapTarget = other;
        rightAngleSnapSnappedPos = snapped;

        return snapped;
    }

    // =====================================================================
    // 軌道複線のオフセット考慮吸着
    // =====================================================================

    /// <summary>オフセット考慮吸着で点ターゲット(ノード/端点)の接線を探す近傍セグメント検索半径(m)。</summary>
    private const float OFFSET_SNAP_TANGENT_SEARCH_RADIUS = 2.0f;

    /// <summary>セグメント端点が点ターゲットに接しているとみなすXZ距離(m)。</summary>
    private const float OFFSET_SNAP_TOUCH_TOLERANCE = 0.5f;

    /// <summary>
    /// 軌道複線(0以外の横オフセットを持つ平行敷設)の最中かどうか。
    /// オフセット考慮吸着(TryOffsetAwareRailSnap)を使うかの判定。
    /// 単線([0]のみ)・道路・既存パス編集(空配列)は対象外で、従来の共通スナップを使う
    /// (単線はオフセット0なので両者は同じ結果になる)。
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
    /// 軌道複線敷設用のオフセット考慮吸着。
    ///
    /// 複線グループの内部表現は「グループ中心線」1本のポリラインだが、確定時には
    /// 平行な単線パス群に分解されて保存され、既存パス側に「グループ」の実体は残らない。
    /// そのため中心線をそのまま既存線路の中心線へ吸着させると、全線路が横に
    /// 半間隔ズレて1本も接続できない。ここでは吸着対象(ノード/建設中端点/セグメント)の
    /// 位置Eとその接線tから「グループ内のいずれかの線路(横オフセットo)がEにちょうど乗る
    /// 中心線位置 C = E ± perp(t)·o」を候補として列挙し、生カーソルに最も近い候補へ吸着する。
    ///
    /// - 既存グループの中心付近にカーソルを置くと、左右の線路それぞれから導かれる候補が
    ///   同じ位置に重なるため、同構成のグループ延長では全線路の端点が一致する。
    /// - 接線の向き(±t)は始点クリック時点では敷設方向が未確定で決められないため、
    ///   両符号の候補を列挙して実際の敷設方向に委ねる(対称なオフセット配列では同一集合)。
    /// - ノード系優先→セグメントの序列、網互換フィルタ、ノード/パス吸着フラグ、
    ///   高架相対高さの引き継ぎは共通スナップ(TerrainInputHandler.ApplySnapping)と揃える。
    ///   ゾーン頂点吸着だけは対象外(軌道敷設では実用場面がないため)。
    /// </summary>
    private bool TryOffsetAwareRailSnap(Vector3 basePos, out Vector3 snapped, out float snapRelHeight)
    {
        snapped = basePos;
        snapRelHeight = 0f;
        if (pathManager == null) return false;
        if (!TerrainInputHandler.EnableNodeSnapping && !TerrainInputHandler.EnablePathSnapping) return false;

        // 吸着対象は候補位置からオフセット分離れているため、検索半径を最大オフセットぶん広げる
        float maxOffset = 0f;
        foreach (float offset in LateralOffsets)
        {
            maxOffset = Mathf.Max(maxOffset, Mathf.Abs(offset));
        }
        float searchRadius = snapThreshold + maxOffset;

        float bestDist = snapThreshold;
        bool found = false;

        // ノード・建設中端点(共通スナップと同じくノード系を優先し、当たればセグメントは見ない)
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

            // 建設が完了するまで端点にノードが作られないため、計画中/建設中パスの
            // start_point / end_point を直接判定対象とする(共通スナップの
            // FindNearestConstructionEndpoint と同じ理由)。
            EvaluateConstructionEndpointOffsetCandidates(basePos, searchRadius, ref bestDist, ref snapped, ref snapRelHeight, ref found);

            if (found) return true;
        }

        // パスセグメント(横オフセット分平行移動した線への射影として吸着)
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
                        // 解析射影の点はYを持たない(0)ので、路面の標高を与えてから候補を作る
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
                                // 標高は元パス中心線上の対応点(candidate + shift)のアンカー相対Y補間から
                                // 引き継ぐ(描画Y−地形サンプルの逆算はノイズが乗るため使わない)
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
    /// 点ターゲット(既存ノード/建設中パス端点)に対するオフセット考慮吸着の候補を評価する。
    /// ターゲットに接するパスの接線ごとに候補 C = ターゲット ± perp(接線)·オフセット を列挙し、
    /// 生カーソルに最も近い候補で最良値を更新する。接線はターゲットの近傍セグメントのうち
    /// 端点が一致するものを使い(交差点では全アームを試す)、無ければ最寄りセグメントで代用する。
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
    /// 点ターゲットと接線方向1本に対する候補 C = ターゲット ± perp·オフセット を列挙し最良値を更新する。
    /// 横移動してもターゲットの絶対標高(targetPos.y)を保ち、相対高さは移動先の地形から求める。
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
    /// 計画中/建設中パスの端点(start_point / end_point)に対するオフセット考慮吸着の候補を評価する。
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

    /// <summary>[x, 相対高さ, z] 形式の端点1つ分を絶対座標に組み立てて点ターゲット候補の評価へ渡す。</summary>
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
    /// 指定位置の近傍セグメントのうち、建設中バリアント（PathSubtype）と網互換な最初のものを返す。
    /// 直角スナップの基準方向を決めるために使う。道路建設中に近くのインフラパス（送電線等）の
    /// 方向を拾ってしまうのを防ぐ。
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
