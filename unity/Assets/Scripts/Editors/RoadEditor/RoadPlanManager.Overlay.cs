using UnityEngine;
using GeneratedModels;

public partial class RoadPlanManager
{
    // 背景オーバーレイ（既存パスの辺・ノード）。カメラ移動・ノード更新・地下表示の切替で再構築する。
    private OverlayMeshRenderer backgroundRenderer;
    private GameObject backgroundMeshObj;
    private Vector3 lastBgCameraPos = Vector3.zero;
    private bool bgDirty = true;
    private const float BG_REBUILD_THRESHOLD_SQ = 25f;

    /// <summary>
    /// 前回の背景オーバーレイ再構築時点の PathManager.NodeDataVersion。
    /// ノードは編集モード進入後にも非同期ロードで到着するため、カメラが動かなくても
    /// カウンタの変化を検知して背景（既存ノード表示）を再構築する。
    /// </summary>
    private int lastBgNodeDataVersion = -1;
    private bool lastBgUndergroundView;

    // 距離フェード（ParcelManagerと同じ定数）
    private const float DISTANCE_START_FADE = 280f;
    private const float DISTANCE_INVISIBLE = 480f;

    private void UpdateOverlay()
    {
        if (overlayRenderer == null) return;

        Camera cam = gameCamera != null ? gameCamera : Camera.main;
        if (cam == null) return;

        UpdateBackgroundOverlay(cam);

        overlayRenderer.BeginFrame();

        // 碁盤状は置いた点が範囲の指定なので、折れ線の辺・制御点・ローカルの干渉推定は描かない。
        if (IsGridBuild)
        {
            DrawGridOverlay(cam);
            overlayRenderer.EndFrame(cam);
            return;
        }

        DrawPlacedEdges(cam);

        // インフラ敷設時の接続端子マーカー（回転＋ボビングのアニメーションを行うため
        // 背景メッシュではなく毎フレームのこのパスで描画する）
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
    /// 編集中の地下パスのトンネル区間（深さ &gt; クリアランス高さ）をオレンジの線で
    /// オーバーレイ表示する。トンネル区間は建設後メッシュが生成されず地形にも隠れるため、
    /// 編集中はどこがトンネルになるかをこのオーバーレイ（_ZTest=Always）で可視化する。
    /// 建設前で地形が未掘削のため、現在地形との深さ判定はバックエンドの区間分類と一致する。
    /// </summary>
    private void DrawTunnelSections(Camera cam)
    {
        if (overlayRenderer == null || points.Count < 2) return;
        if (!IsHeightEditable()) return;

        // 編集中の点に負の相対高さがあれば地下パス。加えて、実効非追従（follow_terrain=0）の
        // パスは相対高さ0でも丘に潜ってトンネルになり得るため、可視化対象とする。
        bool followTerrain = EffectiveFollowTerrain();
        bool underground = false;
        if (followTerrain)
        {
            foreach (var pt in points)
            {
                if (pt.position.y - Utils.GetGroundHeight(pt.position) < -TunnelSectionUtil.UNDERGROUND_REL_Y_EPS)
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
    /// 背景オーバーレイ（既存パスの辺・ノード）を更新する。
    /// カメラが閾値以上に移動した場合、ノード更新時、地下表示の切替時にメッシュを再構築する。
    /// それ以外は既存のメッシュをそのまま使い回す。
    /// </summary>
    private void UpdateBackgroundOverlay(Camera cam)
    {
        if (backgroundRenderer == null) return;

        Vector3 camPos = cam.transform.position;
        // ノードキャッシュの実変更（非同期ロードの到着・削除）も再構築条件に含める
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
    /// 既存パスの辺を描画する（薄い白線）。
    /// ワールド座標をそのまま OverlayMeshRenderer に渡す。
    /// UpdateBackgroundOverlay の backgroundRenderer.BeginFrame/EndFrame 括り内からのみ
    /// 呼ばれるため、出力先は必ず backgroundRenderer にすること。overlayRenderer へ出力すると
    /// 毎フレームの UpdateOverlay 冒頭の BeginFrame でクリアされ、同フレームで消えてしまう。
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
            sA.y = Utils.GetGroundHeight(sA) + 0.05f;
            sB.y = Utils.GetGroundHeight(sB) + 0.05f;

            // ニアプレーン後方カリング
            if (Vector3.Dot(sA - camPos, camFwd) < 0f && Vector3.Dot(sB - camPos, camFwd) < 0f)
                continue;

            Color c = lineColor;
            c.a *= alpha;

            // 吸着できない相手（道路建設中のインフラパス、インフラ建設中の道路や別網のインフラパス等）は
            // 薄いグレーに減色して「接続対象ではない」ことを示す（ノード表示の減色と同じ規約）
            if (seg.path != null && !InfraNetworkUtil.IsPathSnappable(seg.path.path_variant, PathSubtype))
            {
                c = new Color(0.5f, 0.5f, 0.5f, alpha * 0.3f);
            }

            backgroundRenderer.AddSolidLine(sA, sB, c, solidLineThickness);
        }
    }

    /// <summary>
    /// 既存のノードを描画する。
    /// 現在敷設中のパスがインフラ用か道路用かに応じて、接続可能な対象（インフラノードまたは道路ノード）を
    /// 視覚的に強調し、接続できない対象は薄いグレーに減色して表示する。
    /// UpdateBackgroundOverlay の backgroundRenderer.BeginFrame/EndFrame 括り内からのみ
    /// 呼ばれるため、出力先は必ず backgroundRenderer にすること。overlayRenderer へ出力すると
    /// 毎フレームの UpdateOverlay 冒頭の BeginFrame でクリアされ、同フレームで消えてしまう。
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

            // ニアプレーン後方カリング
            if (Vector3.Dot(worldPos - camPos, camFwd) < 0f) continue;

            worldPos.y += 0.1f;

            // 吸着可能かどうか（網互換判定込み。道路×インフラは不可、インフラ同士でも
            // 別網（送電線×導水管）は不可。詳細は InfraNetworkUtil.IsNodeSnappable）
            bool snappable = InfraNetworkUtil.IsNodeSnappable(node, PathSubtype, pathManager);
            bool isInfraNode = InfraNetworkUtil.IsInfraMask(node.type);

            if (!snappable)
            {
                // 吸着できないノードは薄いグレーにして「接続対象ではない」ことを示す
                Color c = new Color(0.5f, 0.5f, 0.5f, alpha * 0.3f);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 0.8f, c);
            }
            else if (drawingInfra && isInfraNode)
            {
                // インフラ敷設時は、同一網のインフラノード（施設の接続端子ノード含む）を
                // シアンで大きく強調表示し、内側に白枠を描画する
                Color c = new Color(0f, 0.8f, 1f, alpha);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 1.8f, c);
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize * 0.8f, new Color(1f, 1f, 1f, alpha));
            }
            else
            {
                // 通常の吸着可能ノードは標準サイズかつ白色で描画する
                Color c = Color.white;
                c.a = alpha;
                backgroundRenderer.AddSquare(worldPos, existingNodeHalfSize, c);
            }
        }
    }

    /// <summary>
    /// インフラパス敷設中に、接続先となる施設の接続端子ノードの上空へ
    /// 「反転した赤い四角錐」マーカーを描画する（ゆっくり回転＋上下ボビング）。
    /// アニメーションするため、カメラ移動時にしか再構築されない backgroundRenderer ではなく
    /// 毎フレーム再構築される overlayRenderer 側に描画する（端子ノードは少数のため負荷は無視できる）。
    /// 表示対象は「敷設中バリアントと同一網（網互換）の施設端子」のみ。別網の端子は
    /// DrawExistingNodes の灰色減色表示に任せ、道路敷設中は一切表示しない。
    /// </summary>
    private void DrawInfraTerminalMarkers(Camera cam)
    {
        if (pathManager == null) return;

        // インフラパス敷設中のみ表示する（PathType が未設定のケースに備えて
        // マスターからの導出判定も併用する）
        bool drawingInfra = InfraNetworkUtil.IsInfraPathType(PathType)
            || InfraNetworkUtil.IsInfraPathVariant(PathSubtype);
        if (!drawingInfra) return;

        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        // 検索半径は DrawExistingNodes と同じ 480m。SpatialHash の巨大半径 Query は
        // フリーズを招くため、これ以上拡大しないこと。
        var nodes = pathManager.QueryNearbyNodes(camPos, DISTANCE_INVISIBLE);
        if (nodes == null) return;

        foreach (var node in nodes)
        {
            // 表示対象の判定はここに集約する:
            // インフラノード、かつ施設の接続端子（BuildingManager の infra_node_id 逆引き）、
            // かつ敷設中バリアントと網互換（吸着可能）であること。
            if (!InfraNetworkUtil.IsInfraMask(node.type)) continue;
            if (BuildingManager.Instance == null
                || !BuildingManager.Instance.TryGetInfraTerminalOwner(node.node_id, out _)) continue;
            if (!InfraNetworkUtil.IsNodeSnappable(node, PathSubtype, pathManager)) continue;
            if (!TerrainInputHandler.IsNodeSnapVisible(node, pathManager)) continue;

            Vector3 worldPos = node.GetAbsolutePosition();
            float dist = Vector3.Distance(camPos, worldPos);
            float alpha = CalculateAlphaByDistance(dist);
            if (alpha <= 0.01f) continue;

            // ニアプレーン後方カリング
            if (Vector3.Dot(worldPos - camPos, camFwd) < 0f) continue;

            // 共通の描画ユーティリティを呼び出し、同一ロジック・同一マテリアルで描画する。
            InfrastructureMarkerUtility.DrawPyramid(overlayRenderer, worldPos, InfrastructureMarkerUtility.COLOR_BRIGHT, alpha);
        }
    }

    /// <summary>
    /// 配置中のポイント間を青い実線（Editing）または点線（Placing）で描画する。
    /// ワールド座標をそのまま OverlayMeshRenderer に渡す。
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
    /// Placingフェーズで、最後のポイントからマウス位置へのプレビュー線を描画する。
    /// ワールド座標をそのまま OverlayMeshRenderer に渡す。
    /// </summary>
    private void DrawPreviewLine(Camera cam)
    {
        Vector3 lastPos = points[points.Count - 1].position;
        Vector3 hoverPos = hoverWorldPosition;
        hoverPos.y = Utils.GetGroundHeight(hoverPos) + currentHeight;

        Color previewColor = new Color(0.3f, 0.6f, 1f, 0.5f);
        overlayRenderer.AddDashedLine(lastPos, hoverPos, previewColor, dashedLineThickness);
    }

    /// <summary>
    /// Placingフェーズで、マウスカーソル位置に追従する配置マーカーを描画する。
    /// グリッドスナップ後の hoverWorldPosition を緑色の円として表示し、
    /// ユーザーがどこに次のノードが配置されるかを視覚的に示す。
    /// マーカーは地形表面の高さに投影して表示する。
    /// </summary>
    private void DrawPlacingMarker(Camera cam)
    {
        Vector3 markerPos = hoverWorldPosition;
        markerPos.y = Utils.GetGroundHeight(markerPos);

        if (Vector3.Dot(markerPos - cam.transform.position, cam.transform.forward) < 0f)
            return;

        Color markerColor = new Color(0.65f, 0.89f, 0.63f);
        overlayRenderer.AddCircle(markerPos, pointCircleRadius * 1.2f, markerColor);
    }

    /// <summary>
    /// 配置ポイントをロールに応じて色分けして描画する。
    /// Start=緑, End=赤, Waypoint=青。
    /// 標高が設定されていても、マーカーは地形表面に投影して表示する。
    /// </summary>
    private void DrawPoints(Camera cam)
    {
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 groundPos = points[i].position;
            groundPos.y = Utils.GetGroundHeight(groundPos);

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
                // 選択中のノードは外側に白色のリングを描画して目立たせる。
                Color ringColor = new Color(1f, 1f, 1f, 0.9f);
                overlayRenderer.AddCircle(groundPos, pointCircleRadius * 1.5f, ringColor);
                c = new Color(0.2f, 1f, 0.4f);
            }

            overlayRenderer.AddCircle(groundPos, pointCircleRadius, c);
        }
    }

    /// <summary>
    /// 各ポイントの制御点ハンドルを四角で描画し、アンカーポイントへ破線で接続する。
    /// Editingフェーズでのみ表示する。
    /// 各ポイントは最大2つのハンドル（control_in / control_out）を持つ。
    /// アンカーポイントと制御点ともに、地形表面の高さに投影して表示する。
    /// </summary>
    private void DrawControlHandles(Camera cam)
    {
        if (!CanEditControls) return;

        Color outColor = new Color(0.976f, 0.886f, 0.686f); // 出口制御点（黄色）
        Color inColor = new Color(0.878f, 0.686f, 0.976f);  // 入口制御点（紫色）
        Vector3 camPos = cam.transform.position;
        Vector3 camFwd = cam.transform.forward;

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 anchorGround = points[i].position;
            anchorGround.y = Utils.GetGroundHeight(anchorGround);

            // 出口制御点（control_out）
            if (points[i].control_out.HasValue)
            {
                Vector3 coGround = points[i].control_out.Value;
                coGround.y = Utils.GetGroundHeight(coGround);

                if (Vector3.Dot(anchorGround - camPos, camFwd) >= 0f || Vector3.Dot(coGround - camPos, camFwd) >= 0f)
                {
                    overlayRenderer.AddDashedLine(anchorGround, coGround, outColor, 1.5f);
                    overlayRenderer.AddSquare(coGround, controlHandleHalfSize, outColor);
                }
            }

            // 入口制御点（control_in）
            if (points[i].control_in.HasValue)
            {
                Vector3 ciGround = points[i].control_in.Value;
                ciGround.y = Utils.GetGroundHeight(ciGround);

                if (Vector3.Dot(anchorGround - camPos, camFwd) >= 0f || Vector3.Dot(ciGround - camPos, camFwd) >= 0f)
                {
                    overlayRenderer.AddDashedLine(anchorGround, ciGround, inColor, 1.5f);
                    overlayRenderer.AddSquare(ciGround, controlHandleHalfSize, inColor);
                }
            }
        }
    }

    /// <summary>
    /// ホバー中の辺を黄色い太線でハイライトする。
    /// ハイライトは地形表面の高さに投影して表示する。
    /// </summary>
    private void DrawHoverHighlight(Camera cam)
    {
        if (hoveredEdgeIndex < 0 || hoveredEdgeIndex >= points.Count - 1) return;

        Vector3 p1Ground = points[hoveredEdgeIndex].position;
        p1Ground.y = Utils.GetGroundHeight(p1Ground);
        Vector3 p2Ground = points[hoveredEdgeIndex + 1].position;
        p2Ground.y = Utils.GetGroundHeight(p2Ground);

        if (Vector3.Dot(p1Ground - cam.transform.position, cam.transform.forward) < 0f
            && Vector3.Dot(p2Ground - cam.transform.position, cam.transform.forward) < 0f)
            return;

        Color highlightColor = new Color(1f, 1f, 0.3f, 0.8f);
        overlayRenderer.AddSolidLine(p1Ground, p2Ground, highlightColor, dashedLineThickness * 2.5f);
    }

    /// <summary>
    /// 指定ピクセルサイズを、指定ワールド位置におけるワールド空間のサイズに換算する。
    /// </summary>
    private float GetWorldPixelScale(Camera cam, Vector3 worldPos, float pixelSize)
    {
        float distance = Vector3.Distance(cam.transform.position, worldPos);
        float tanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float worldPerPixel = distance * tanHalfFov / (Screen.height * 0.5f);
        return pixelSize * worldPerPixel;
    }

    /// <summary>
    /// 直角スナップ補助線を描画する。
    /// <summary>
    /// 直線延長スナップの補助線を描画する。
    /// 基準となる2点を結ぶ方向の延長線を薄いオレンジ色の破線で示し、
    /// スナップ位置に緑色の円マーカーを表示する。
    /// </summary>
    private void DrawStraightLineSnapGuide(Camera cam)
    {
        if (!isStraightLineSnapActive) return;

        Color guideLineColor = new Color(1f, 0.7f, 0.3f, 0.5f);
        Color markerColor = new Color(0.65f, 0.89f, 0.63f, 0.8f);

        // 延長線の基準2点（地形高さに投影）
        Vector3 anchor = straightLineSnapAnchor;
        anchor.y = Utils.GetGroundHeight(anchor);
        Vector3 ext = straightLineSnapExtension;
        ext.y = Utils.GetGroundHeight(ext);

        // 延長線を十分な長さで描画
        Vector3 dir = new Vector3(anchor.x - ext.x, 0f, anchor.z - ext.z);
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();

        // 基準セグメントの両側に50m延長した線を描画
        Vector3 guideStart = anchor - dir * 50f;
        guideStart.y = Utils.GetGroundHeight(guideStart);
        Vector3 guideEnd = anchor + dir * 50f;
        guideEnd.y = Utils.GetGroundHeight(guideEnd);

        if (Vector3.Dot(guideStart - cam.transform.position, cam.transform.forward) >= 0f
            || Vector3.Dot(guideEnd - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddDashedLine(guideStart, guideEnd, guideLineColor, 1.5f);
        }

        // スナップ位置にマーカー
        Vector3 snappedPos = straightLineSnapSnappedPos;
        snappedPos.y = Utils.GetGroundHeight(snappedPos);
        if (Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddCircle(snappedPos, pointCircleRadius * 1.2f, markerColor);
        }
    }

    /// <summary>
    /// スナップ対象の既存パスセグメントを黄色の太線でハイライトし、
    /// スナップ後の予想位置に緑色の円マーカーを表示し、
    /// 元のマウス位置からスナップ後の位置へ破線を引く。
    /// </summary>
    private void DrawRightAngleSnapGuide(Camera cam)
    {
        if (!isRightAngleSnapActive) return;

        Color targetLineColor = new Color(1f, 0.92f, 0.016f, 0.9f); // 黄色
        Color guideLineColor = new Color(0.65f, 0.89f, 0.63f, 0.6f); // 緑の破線
        Color markerColor = new Color(0.65f, 0.89f, 0.63f, 0.8f);   // 緑のマーカー

        Vector3 origin = rightAngleSnapOrigin;
        origin.y = Utils.GetGroundHeight(origin);

        // スナップ基準点を中心に小さな十字を描画（ピクセル長さをワールド空間に換算）
        if (Vector3.Dot(origin - cam.transform.position, cam.transform.forward) >= 0f)
        {
            float crossLen = GetWorldPixelScale(cam, origin, 12f);
            Vector3 right = Vector3.Cross(cam.transform.forward, Vector3.up).normalized;
            if (right.sqrMagnitude < 0.0001f) right = Vector3.Cross(cam.transform.forward, Vector3.right).normalized;
            Vector3 up = Vector3.Cross(right, cam.transform.forward).normalized;
            overlayRenderer.AddSolidLine(origin - right * crossLen, origin + right * crossLen, targetLineColor, 2f);
            overlayRenderer.AddSolidLine(origin - up * crossLen, origin + up * crossLen, targetLineColor, 2f);
        }

        // 元のマウス位置からスナップ後の位置へ破線を描画
        Vector3 targetPos = rightAngleSnapTarget;
        targetPos.y = Utils.GetGroundHeight(targetPos);
        Vector3 snappedPos = rightAngleSnapSnappedPos;
        snappedPos.y = Utils.GetGroundHeight(snappedPos);

        if (Vector3.Dot(targetPos - cam.transform.position, cam.transform.forward) >= 0f
            || Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddDashedLine(targetPos, snappedPos, guideLineColor, 1.5f);
        }

        // スナップ後の予想位置に緑色の円マーカーを表示
        if (Vector3.Dot(snappedPos - cam.transform.position, cam.transform.forward) >= 0f)
        {
            overlayRenderer.AddCircle(snappedPos, pointCircleRadius * 1.2f, markerColor);
        }
    }
}
