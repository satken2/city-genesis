using UnityEngine;
using System.Collections.Generic;
using Dreamteck.Splines;
using GeneratedModels;

/// <summary>
/// 線路（PATH_TRACK）・モノレール（PATH_MONORAIL）向けのメッシュ生成 (Dreamteck Splines使用)。
///
/// 道路（RoadPath）と同じ「プレハブパーツを SplineMesh でスプラインに沿って曲げる」方式だが、
/// パーツ構成が異なる（プレハブは Models/Paths/p{variant}/{パーツ名}、無ければ p{type} にフォールバック）:
///
///   body  ... 道床。地上線路ではバラスト（砕石）の台形断面、高架・橋梁バリアントでは
///             プレハブ自体を「桁+道床」の断面にすることで高架床を表現する（deck等の専用スロットは
///             持たない。バリアントは地上/高架で固定なのでプレハブの断面で表せる）。
///             全幅（マスターの width）に引き伸ばして配置する。
///   track ... 1軌道分（レール2本+枕木）。軌間（ゲージ）が変わってはいけないため
///             Xスケールせず実寸で、lanes（軌道数）本を等間隔オフセットに並べる。
///             モノレールでは軌道桁1本のモデルを同じスロットに置くだけで同じ仕組みに乗る。
///   wire  ... 架線（トロリ線+吊架線）。実寸で各軌道の中心に配置する。トロリ線の高さ
///             （レール面上約5m）はプレハブ形状が持ち、コードは高さパラメータを持たない。
///   pole / beam ... 架線柱（柱）と架線ビーム（門型梁）。wire_spacing 間隔で左右に Instantiate 配置。
///   wall / pillar ... 防音壁・高架橋脚。道路とまったく同じ仕組み（RoadPathの共通ヘルパー）を使う。
///   platform ... 駅パスのプラットフォーム。プレハブの原点を線路中心線に合わせ、モデル実寸のまま
///             片側（または両側）に配置する（張り出し幅はプレハブ形状が持つ。マスターの
///             platform_width はホームの有無と敷地・コライダー・柱/壁位置の算出にのみ使う）。
///
/// 【駅パスのプラットフォーム】
/// 駅パスの width は通常線路と同じ「線路コリドーの幅」であり、道床・軌道・交差点ハル・
/// レールコネクタは通常線路とまったく同じに描かれる（前後の線路とシームレスに繋がる）。
/// プラットフォームは platform_width ぶん道床の外へ張り出す追加パーツで、設置側はパスの
/// platform_side（内側/外側/両側）で決まる。内側/外側→左右の解決は都市の通行方向
/// （CityInfoStore.TrafficHand）を基準に行う（ResolvePlatformExtents）。この解決ロジックは
/// バックエンドの敷地ポリゴン算出（geo_paths.get_platform_extension_widths）と鏡写しであり、
/// 片方だけ変えると敷地と見た目が食い違う。
/// プラットフォームがある側では、架線柱をホーム外縁の外側に立て、近接線路の結合描画
/// （バラスト充填・壁省略）を無効化し、クリック判定コライダーをホーム込みの非対称幅にする。
///
/// 電化（wire / pole / beam の配置）はマスターデータの wire_spacing で制御する
/// （null または 0 以下なら非電化としてスキップ）。
///
/// すべてのパーツはプレハブ未配置ならスキップされ、道床も軌道も無い場合は白いストリップに
/// フォールバックするため、アートアセットが揃う前でもゲームプレイは継続できる。
///
/// ノード（分岐器・平面交差）のレール描画は TrackIntersectionMeshGenerator が
/// RailConnectorSpec として幾何情報を計算し、本クラスの CreateRailConnectorObject が実体化する。
///
/// 【近接線路の結合描画（複線見え化）】
/// 内部データに「複線」の単位は無く、複線は一方通行の単線線路を2本並べて表現される。
/// XZ・Yともに近接して並走する線路（TrackAdjacency.Compute が判定。閾値は
/// LocalConstants.TRACK_MERGE_*）は1つのコリドーとみなし、以下を結合して描画する。
/// 合流・分岐で収束中の線路（接線が揃う前）も、道床エッジギャップが閾値以下まで
/// 近づいた時点から結合が始まる（TRACK_MERGE_MAX_CONVERGE_ANGLE_DEG）:
///   - 道床（body）: 道床どうしの隙間をバラスト帯（ギャップフィラー）で埋めて一体に見せる。
///     充填はpath_idの小さい側が担当する（両側から二重生成しないため）。
///   - 防音壁（wall）: 結合している側（線路間）の壁を区間クリップで省略する。
///     分岐などで間隔が開き結合が解けた区間では壁が復活する。加えて並走判定とは独立に、
///     他線路の道床が壁の位置に物理的に掛かる区間（合流の喉元・浅い角度の平面交差）でも
///     壁を省略する（TrackCorridorInfo.Left/RightWallBlocked）。
///   - 架線柱（pole）・架線ビーム（beam）: コリドー内で最小path_idの線路（主線路）だけが、
///     コリドー全幅の左右端に柱を立てて梁を渡す。他の線路は結合区間では柱・梁を置かない。
/// 軌道（track）・架線（wire）は各線路が従来どおり自分の分を描く。
/// この結合は描画専用で、バックエンドのデータ・経路探索には影響しない。
/// 隣接線路の情報は TrackAdjacency.NeighborProvider（PathManager が設定）から取得し、
/// 未設定（テストシーン等）なら従来どおり単独描画になる。
/// </summary>
public class TrackPath : IPathMeshGenerator
{
    /// <summary>架線柱を道床の端からどれだけ外側に立てるか（m）。</summary>
    private const float POLE_MARGIN = 0.5f;

    /// <summary>
    /// 道床ギャップフィラーを両側の道床の内側に食い込ませる量(m)。
    /// エッジをぴったり合わせると計算誤差で線状の裂け目が出るため、少し重ねる。
    /// </summary>
    private const float MERGE_FILLER_EDGE_INSET = 0.5f;

    /// <summary>
    /// 道床ギャップフィラーを道床天端（BODY_ABOVE_SPLINE）からどれだけ沈めるか(m)。
    /// 道床の上面と同一平面に置くと重なり部分がZファイティングするため、わずかに下げて
    /// バラストの肩の下に潜り込ませる（バラスト同士なので段差は視認されにくい）。
    /// </summary>
    private const float MERGE_FILLER_SINK = 0.02f;

    /// <summary>これより短い道床ギャップフィラーの連続区間は生成しない(m)。スリバー防止。</summary>
    private const float MERGE_FILLER_MIN_LENGTH = 1.0f;

    /// <summary>これより短い防音壁の描画区間は生成しない(m)。結合境界での細切れ壁の防止。</summary>
    private const float MERGE_WALL_MIN_RUN_LENGTH = 1.0f;

    // 線路に歩道はないため、歩道セットバック（エンドキャップ用）は受け取るだけで使用しない。
    public PathMeshResult Generate(PathSummary pathData, List<Vector3> points, SidewalkSetback startSidewalkSetback = default, SidewalkSetback endSidewalkSetback = default)
    {
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] Generate開始: path_id={pathData?.path_id ?? -1}, path_variant={pathData?.path_variant ?? -1}, path_type={pathData?.path_type ?? -1}, points={points?.Count ?? 0}");

        if (points == null || points.Count < 2)
        {
            Debug.LogWarning($"[TrackPath] ポイント不足 ({points?.Count ?? 0}件)。生成スキップ。");
            return new PathMeshResult(null, false);
        }

        GetTrackMasterParams(pathData, out float width, out int lanes, out float wallWidth, out float? pillarSpacing, out bool heightEditable, out float? wireSpacing, out float baseHeight, out float platformWidth);

        // プラットフォームの左右の張り出し幅（無い側・ホーム無しバリアントは0）
        ResolvePlatformExtents(pathData, platformWidth, out float platformLeft, out float platformRight);

        bool isCurved = pathData.curved;

        // curved=False の場合、CalculatePathPoints はアンカー点のみを返すため地形追従が機能しない。
        // 一定間隔で中間点を補間し、地形高さを反映させる。
        // ウェイポイントの角は Broken 点として維持する（道路のような角の切断・充填は行わない。
        // 線路は通常 curved=true で滑らかに敷設される想定で、折れ線線路の角は簡易表現とする）。
        if (!isCurved)
        {
            bool followTerrain = PathManager.GetPathFollowTerrain(pathData)
                && AreAllAnchorsOnGround(pathData);
            points = PathManager.SubdividePointsWithTerrain(points, PathManager.TERRAIN_SAMPLE_INTERVAL, followTerrain);
        }

        // 近接して並走する線路との結合コリドー（複線見え化）。
        // 結合区間が無い・プロバイダ未設定（テストシーン等）なら null で、従来どおり単独描画になる。
        TrackCorridorInfo corridor = null;
        if (TrackAdjacency.NeighborProvider != null)
        {
            List<TrackNeighborInfo> trackNeighbors = TrackAdjacency.NeighborProvider(pathData);
            if (trackNeighbors != null && trackNeighbors.Count > 0)
            {
                corridor = TrackAdjacency.Compute(pathData.path_id, points, width, wallWidth, trackNeighbors);
            }
        }

        // プラットフォームがある側では結合描画（バラスト充填・壁の省略）を無効化する。
        // ホームの下にギャップフィラーが張られたり、ホーム側の壁だけ省略されたりするのを防ぐ
        // （通常、隣接線路はホーム幅より外に離れるため結合自体が成立しないが、
        //  狭いホーム＋近接敷設の組み合わせで発動し得る）。
        if (corridor != null)
        {
            if (platformLeft > 0f) SuppressCorridorSide(corridor, leftSide: true);
            if (platformRight > 0f) SuppressCorridorSide(corridor, leftSide: false);
        }

        GameObject container = new GameObject($"Track_{pathData.path_id}");
        SplineComputer spline = RoadPath.BuildSpline(container, points, isCurved);

        CreateTrackParts(container, spline, width, lanes, wallWidth, pillarSpacing, heightEditable, baseHeight, wireSpacing, pathData, corridor, platformLeft, platformRight);

        // コライダーはクリック判定用のストリップ（道路と同じ）。プラットフォームがある場合は
        // ホーム込みの全幅にし、中心をホーム側へずらした非対称ストリップにする。
        float colliderWidth = width + platformLeft + platformRight;
        float colliderOffset = (platformRight - platformLeft) * 0.5f;
        Mesh colliderMesh = RoadPath.BuildColliderMeshFromSpline(spline, colliderWidth, colliderOffset);
        if (colliderMesh != null)
        {
            colliderMesh.name = $"TrackCollider_{pathData.path_id}";
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] Generate完了: 子オブジェクト数={container.transform.childCount}, colliderMesh={(colliderMesh != null ? "あり" : "なし")}");
        return new PathMeshResult(colliderMesh, true, container);
    }

    /// <summary>
    /// 始点・終点・全ウェイポイントの相対Yがすべて0（地面張り付き）かどうか。
    /// 地形追従の細分化を行うかの判定に使う。
    /// </summary>
    private static bool AreAllAnchorsOnGround(PathSummary pathData)
    {
        var startList = pathData.GetStartPointList();
        var endList = pathData.GetEndPointList();
        float startRelY = (startList != null && startList.Count >= 3) ? startList[1] : 0f;
        float endRelY = (endList != null && endList.Count >= 3) ? endList[1] : 0f;
        if (startRelY != 0f || endRelY != 0f) return false;

        var waypointList = pathData.GetWaypointList();
        if (waypointList != null)
        {
            foreach (var wp in waypointList)
            {
                float relY = (wp.position != null && wp.position.Length >= 3) ? wp.position[1] : 0f;
                if (relY != 0f) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// パスの platform_side（内側/外側/両側）と都市の通行方向（CityInfoStore.TrafficHand）から、
    /// プラットフォームの左右の張り出し幅(m)を解決する。左右はスプライン進行方向
    /// （start→end。right = Cross(up, forward)）基準。
    ///
    /// 「内側」とは複線（一方通行の単線線路2本）における相手線路側のことで、
    /// 左側通行では相手線路が進行方向の右側に来るため 内側=右、右側通行では 内側=左。
    /// 上下線の両方に内側を指定すると線路間にホームが向き合う島式ホームになる。
    ///
    /// この解決ロジックはバックエンドの敷地ポリゴン算出
    /// （geo_paths.get_platform_extension_widths）の鏡写し。片方だけ変えると
    /// 敷地（土地の占有・クリック判定）と見た目が食い違う。
    /// </summary>
    public static void ResolvePlatformExtents(PathSummary pathData, float platformWidth, out float leftWidth, out float rightWidth)
    {
        leftWidth = 0f;
        rightWidth = 0f;
        if (platformWidth <= 0f || pathData == null) return;

        int side = pathData.GetPlatformSide();
        if (side == Constants.PLATFORM_SIDE_BOTH)
        {
            leftWidth = platformWidth;
            rightWidth = platformWidth;
            return;
        }

        bool insideIsRight = CityInfoStore.TrafficHand == Constants.TRAFFIC_HAND_LEFT;
        bool platformIsRight = (side == Constants.PLATFORM_SIDE_INSIDE) == insideIsRight;
        if (platformIsRight) rightWidth = platformWidth;
        else leftWidth = platformWidth;
    }

    /// <summary>
    /// 結合コリドーの指定側の結合情報を打ち消す（プラットフォームがある側用）。
    /// その側の壁クリップ・バラスト充填が行われなくなり、柱の張り出しも自幅基準に戻る。
    /// 反対側の結合描画には影響しない。
    /// </summary>
    private static void SuppressCorridorSide(TrackCorridorInfo corridor, bool leftSide)
    {
        int n = corridor.Points.Count;
        bool[] covered = leftSide ? corridor.LeftCovered : corridor.RightCovered;
        bool[] wallBlocked = leftSide ? corridor.LeftWallBlocked : corridor.RightWallBlocked;
        float[] fillTo = leftSide ? corridor.LeftFillTo : corridor.RightFillTo;
        float[] extent = leftSide ? corridor.LeftExtent : corridor.RightExtent;
        for (int i = 0; i < n; i++)
        {
            covered[i] = false;
            // 壁の物理ブロックも解除する。プラットフォーム側の壁はホームの外縁のさらに外に
            // 立つため、道床基準の壁ゾーンで判定したブロックは位置が合わない。
            wallBlocked[i] = false;
            fillTo[i] = float.NaN;
            extent[i] = corridor.OwnHalfWidth;
        }
    }

    /// <summary>
    /// 指定した軌道インデックスの中心オフセット（スプライン中心線からの横方向距離）を返す。
    /// 道路の車線と同じ導出で、全幅を軌道数で等分した各帯の中央に軌道を置く。
    /// 例: 単線(width5, lanes1)=0、複線(width11, lanes2)=±2.75、複々線(width16, lanes4)=±2, ±6。
    /// </summary>
    public static float GetTrackCenterOffset(float width, int lanes, int trackIndex)
    {
        int laneCount = Mathf.Max(1, lanes);
        float laneWidth = width / laneCount;
        return -width * 0.5f + laneWidth * (trackIndex + 0.5f);
    }

    /// <summary>
    /// 線路パーツ（道床、軌道、架線、架線柱、防音壁、橋脚、プラットフォーム）をスプラインに沿って配置する。
    /// corridor（近接線路の結合コリドー。null可）が渡された場合、結合区間では
    /// 道床のギャップ充填・防音壁の省略・架線柱/ビームのコリドー全幅化を行う。
    /// platformLeft / platformRight は駅パスのプラットフォームの左右の張り出し幅
    /// （ResolvePlatformExtents で解決済み。0なら無し）。
    /// </summary>
    private static void CreateTrackParts(GameObject container, SplineComputer spline, float width, int lanes, float wallWidth, float? pillarSpacing, bool heightEditable, float baseHeight, float? wireSpacing, PathSummary pathData, TrackCorridorInfo corridor = null, float platformLeft = 0f, float platformRight = 0f)
    {
        string variantBase = $"Models/Paths/p{pathData.path_variant}";
        string typeBase = $"Models/Paths/p{pathData.path_type}";
        int laneCount = Mathf.Max(1, lanes);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] CreateTrackParts開始: variant={variantBase}, type={typeBase}, width={width}, lanes={laneCount}, wallWidth={wallWidth}, wireSpacing={wireSpacing}");

        // 道床（body）: 全幅に引き伸ばして配置。高さは道路の body と同じ体系で、
        // 交差点の道床ハル（基準面 + BODY_ABOVE_SPLINE）とも同一平面になる。
        GameObject ballast = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/body", width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body");
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] body(道床): {(ballast != null ? "生成成功" : "プレハブなし")}");

        // 結合区間では隣の線路との道床の隙間をバラスト帯で埋め、一体の道床に見せる。
        // 道床プレハブが無いバリアント（モノレール等）は道床自体が無いため充填もしない。
        if (corridor != null && ballast != null)
        {
            MeshRenderer ballastRenderer = ballast.GetComponent<MeshRenderer>();
            Material bodyMaterial = ballastRenderer != null ? ballastRenderer.sharedMaterial : null;
            CreateBodyGapFillers(container, corridor, bodyMaterial);
        }

        // 軌道（track）: 各軌道の中心に実寸配置（stretchToWidth=false。ゲージ維持）。
        int placedTracks = 0;
        for (int i = 0; i < laneCount; i++)
        {
            float offset = GetTrackCenterOffset(width, laneCount, i);
            GameObject track = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/track", width / laneCount, offset, RoadPartHeights.TRACK_ABOVE_SPLINE, false, $"{typeBase}/track", isDashed: false, stretchToWidth: false);
            if (track != null) placedTracks++;
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] track(軌道): {placedTracks}/{laneCount}本配置");

        // 道床も軌道も無いバリアントは白いストリップにフォールバックする（ゲームプレイ継続用）。
        // 軌道モデルだけがある構成（モノレール等、道床が不要なバリアント）では道床なしが正常。
        if (ballast == null && placedTracks == 0)
        {
            RoadPath.CreateFallbackRoadBody(container, spline, width);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] 道床・軌道ともにモデルなし。白いストリップにフォールバック");
        }

        // プラットフォーム（駅パスのみ: platform_width が設定されているバリアント）。
        // プレハブの原点を線路中心線（スプライン中心）に一致させ、モデル実寸のまま配置する
        // （幅スケールもピボット補正もしない＝ホームの張り出し量はプレハブ形状が持つ）。
        // 「右側用パーツ」規約: 原型は原点から +X 側（進行方向右）へ張り出すよう作り、左側はXミラー。
        // platformLeft / platformRight は「その側にホームがあるか」の判定にのみ使う（描画幅には使わない）。
        // プレハブ未配置ならスキップ（オプショナルパーツ。道床だけの駅として描かれる）。
        if (platformLeft > 0f)
        {
            GameObject p = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/platform", 0f, 0f, RoadPartHeights.PLATFORM_ABOVE_SPLINE, true, $"{typeBase}/platform", stretchToWidth: false, compensatePivot: false);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] platform(左): {(p != null ? "生成成功" : "プレハブなし")}");
        }
        if (platformRight > 0f)
        {
            GameObject p = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/platform", 0f, 0f, RoadPartHeights.PLATFORM_ABOVE_SPLINE, false, $"{typeBase}/platform", stretchToWidth: false, compensatePivot: false);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] platform(右): {(p != null ? "生成成功" : "プレハブなし")}");
        }

        // 架線系（電化バリアントのみ: wire_spacing が設定されている場合）
        if (wireSpacing.HasValue && wireSpacing.Value > 0f)
        {
            for (int i = 0; i < laneCount; i++)
            {
                float offset = GetTrackCenterOffset(width, laneCount, i);
                RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/wire", width / laneCount, offset, RoadPartHeights.OVERHEAD_ABOVE_SPLINE, false, $"{typeBase}/wire", isDashed: false, stretchToWidth: false);
            }
            GeneratePolesAndBeams(container, spline, wireSpacing.Value, width, variantBase, typeBase, corridor, platformLeft, platformRight);
        }
        else
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] wire_spacing未設定: 非電化（架線・架線柱なし）");
        }

        // 防音壁: 道路と同じ「右側用パーツ」規約（左側はXミラー）で道床の外側に立てる。
        // 近接線路と結合している側は、線路間に壁を立てないよう結合区間を区間クリップで省略する
        // （分岐などで間隔が開き結合が解けた区間では壁が復活する）。
        // プラットフォームがある側では、壁はホームの外縁のさらに外側に立てる。
        if (wallWidth > 0f)
        {
            float wallCenterOffsetLeft = (width * 0.5f) + platformLeft + (wallWidth * 0.5f);
            float wallCenterOffsetRight = (width * 0.5f) + platformRight + (wallWidth * 0.5f);
            foreach (var range in GetWallPercentRanges(corridor, leftSide: true))
            {
                RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, -wallCenterOffsetLeft, RoadPartHeights.WALL_ABOVE_SPLINE, true, $"{typeBase}/wall", clipFrom: range.From, clipTo: range.To);
            }
            foreach (var range in GetWallPercentRanges(corridor, leftSide: false))
            {
                RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, wallCenterOffsetRight, RoadPartHeights.WALL_ABOVE_SPLINE, false, $"{typeBase}/wall", clipFrom: range.From, clipTo: range.To);
            }
        }

        // 高架橋脚（道路と共通処理）。地下パスには配置しない（RoadPath側の理由コメント参照）。
        if (heightEditable && pillarSpacing.HasValue && pillarSpacing.Value > 0f
            && !TunnelSectionUtil.IsUndergroundPath(pathData))
        {
            RoadPath.GeneratePillars(container, spline, pillarSpacing.Value, baseHeight, pathData, variantBase, typeBase);
        }
    }

    /// <summary>
    /// 架線柱（pole）と架線ビーム（beam）を wire_spacing 間隔でスプラインに沿って配置する。
    /// 交差点の切断面ぎりぎりに柱が立たないよう、両端から間隔の半分だけ内側に寄せる
    /// （街路樹の配置と同じ端部クリアランスの考え方）。
    ///
    /// 近接線路との結合区間（corridor）では、コリドー内で最小path_idの線路（主線路）だけが
    /// コリドー全幅の左右端に柱を立てて梁を渡し、それ以外の線路はその地点の柱・梁を置かない。
    /// 主線路と他線路の柱位相（spacing の起点）は揃わないが、結合が解けた区間では
    /// 各線路が自分の柱を従来どおり立てる。
    ///
    /// プラットフォームがある側（platformLeft / platformRight > 0）では、柱がホームを
    /// 突き抜けないよう、ホーム外縁のさらに外側（+POLE_MARGIN）に立てる。
    /// 現実の駅でもホーム外縁に架線柱が並ぶため見た目も正しい。梁は柱間全幅に引き伸ばされる。
    /// </summary>
    private static void GeneratePolesAndBeams(GameObject container, SplineComputer spline, float spacing, float width, string variantBase, string typeBase, TrackCorridorInfo corridor = null, float platformLeft = 0f, float platformRight = 0f)
    {
        GameObject polePrefab = LoadPrefab($"{variantBase}/pole", $"{typeBase}/pole");
        GameObject beamPrefab = LoadPrefab($"{variantBase}/beam", $"{typeBase}/beam");
        if (polePrefab == null && beamPrefab == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] pole/beam プレハブなし: 架線柱スキップ");
            return;
        }

        float length = spline.CalculateLength();
        if (length <= 0f || spline.sampleCount == 0) return;

        float ownHalfWidth = width * 0.5f;
        float endClearance = spacing * 0.5f;
        if (length <= endClearance * 2f)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] 架線柱: 線路が短すぎるため配置なし");
            return;
        }

        double currentPercent = spline.Travel(0.0, endClearance, Spline.Direction.Forward);
        double endPercent = spline.Travel(1.0, endClearance, Spline.Direction.Backward);
        if (endPercent <= currentPercent) return;

        // Travel が進まなくなった場合の無限ループ防止（街路樹配置と同じ安全策）
        int maxCount = Mathf.FloorToInt(length / spacing) + 2;
        int count = 0;
        int placedCount = 0;

        // コリドー情報の参照用に、スプライン始点からの走行距離を追跡する
        // （スプラインは SampleMode.Uniform のため、点列の累積弧長とほぼ一致する）。
        float currentDistance = endClearance;

        while (currentPercent <= endPercent && count < maxCount)
        {
            bool place = true;
            float leftHalf = ownHalfWidth;
            float rightHalf = ownHalfWidth;
            if (corridor != null)
            {
                int idx = corridor.SampleIndexAtLength(currentDistance);
                if (corridor.SuppressPoleSet[idx])
                {
                    // この地点では主線路（最小path_id）がコリドー全幅分の柱・梁を配置する
                    place = false;
                }
                else
                {
                    leftHalf = corridor.LeftExtent[idx];
                    rightHalf = corridor.RightExtent[idx];
                }
            }

            // プラットフォームがある側は、柱をホーム外縁の外側へ追い出す
            // （コリドーの張り出しとどちらか大きい方を採用する）。
            leftHalf = Mathf.Max(leftHalf, ownHalfWidth + platformLeft);
            rightHalf = Mathf.Max(rightHalf, ownHalfWidth + platformRight);

            if (place)
            {
                SplineSample sample = spline.Evaluate(currentPercent);
                PlacePoleSet(container, polePrefab, beamPrefab, sample, leftHalf + POLE_MARGIN, rightHalf + POLE_MARGIN);
                placedCount++;
            }

            double next = spline.Travel(currentPercent, spacing, Spline.Direction.Forward);
            if (next <= currentPercent) break;
            currentPercent = next;
            currentDistance += spacing;
            count++;
        }

        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] 架線柱配置完了: {placedCount}組");
    }

    /// <summary>
    /// 1箇所分の架線柱セット（左右の柱 + 門型梁）を配置する。
    ///
    /// プレハブのピボット規約:
    ///   pole ... 原点=設置面（軌道と同じ基準面）、+Y方向に柱が伸びる。「右側用」として作り、
    ///            腕金・張り出しは -X（軌道中心）方向を向ける。左側はY軸180°回転で対称に配置する。
    ///   beam ... 原点=スプライン中心の設置面。梁本体はプレハブ内で柱の高さに造形する。
    ///            X幅が柱間（左右オフセットの和）に一致するよう引き伸ばされ、左右非対称の場合は
    ///            柱間の中央へ横移動する（近接線路の結合コリドーでは自線路が中央に来ないため）。
    ///
    /// 柱の設置面は路面（スプライン）基準なので、高架では桁の上に立つ。
    /// 地面まで伸びる橋脚（pillar）とは役割が異なる。
    /// </summary>
    private static void PlacePoleSet(GameObject container, GameObject polePrefab, GameObject beamPrefab, SplineSample sample, float leftPoleOffset, float rightPoleOffset)
    {
        // 架線柱・架線ビームは地形の傾斜に追従して斜めに立たず、常にワールド上方向に対して垂直に立つ。
        // 進行方向の水平成分からYawのみの回転を導出し、横方向オフセットも水平面上の右方向を使う。
        Vector3 forward = sample.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        Quaternion yawRotation = Quaternion.LookRotation(forward, Vector3.up);

        Vector3 basePos = sample.position + Vector3.up * RoadPartHeights.OVERHEAD_ABOVE_SPLINE;

        if (polePrefab != null)
        {
            GameObject rightPole = Object.Instantiate(polePrefab, container.transform);
            rightPole.transform.position = basePos + right * rightPoleOffset;
            rightPole.transform.rotation = yawRotation;

            GameObject leftPole = Object.Instantiate(polePrefab, container.transform);
            leftPole.transform.position = basePos - right * leftPoleOffset;
            leftPole.transform.rotation = yawRotation * Quaternion.Euler(0f, 180f, 0f);
        }

        if (beamPrefab != null)
        {
            GameObject beam = Object.Instantiate(beamPrefab, container.transform);
            // 左右非対称（結合コリドー）の場合は柱間の中央に置く
            beam.transform.position = basePos + right * ((rightPoleOffset - leftPoleOffset) * 0.5f);
            beam.transform.rotation = yawRotation;

            MeshFilter mf = beamPrefab.GetComponent<MeshFilter>();
            float meshWidth = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.bounds.size.x : 0f;
            if (meshWidth > 0.001f)
            {
                Vector3 scale = beam.transform.localScale;
                scale.x = (leftPoleOffset + rightPoleOffset) / meshWidth;
                beam.transform.localScale = scale;
            }
        }
    }

    /// <summary>
    /// 防音壁を描画するスプライン percent 区間の一覧を返す。
    /// 結合コリドーが無ければ全長1区間、あれば「指定側に隣接線路がおらず、かつ
    /// 他線路の道床が壁の位置に掛かっていない区間」のみ。
    /// </summary>
    private static List<TrackCorridorInfo.PercentRange> GetWallPercentRanges(TrackCorridorInfo corridor, bool leftSide)
    {
        if (corridor == null)
        {
            return new List<TrackCorridorInfo.PercentRange>
            {
                new TrackCorridorInfo.PercentRange { From = 0.0, To = 1.0 }
            };
        }
        return corridor.GetWallPercentIntervals(leftSide, MERGE_WALL_MIN_RUN_LENGTH);
    }

    /// <summary>
    /// 結合コリドーの左右それぞれについて、自分が充填担当（path_idが隣より小さい）の区間に
    /// 道床ギャップフィラー（バラスト帯）を生成する。マテリアルは自分の道床と共有する。
    /// </summary>
    private static void CreateBodyGapFillers(GameObject container, TrackCorridorInfo corridor, Material bodyMaterial)
    {
        if (bodyMaterial == null) return;
        CreateBodyGapFillersForSide(container, corridor, bodyMaterial, leftSide: true);
        CreateBodyGapFillersForSide(container, corridor, bodyMaterial, leftSide: false);
    }

    private static void CreateBodyGapFillersForSide(GameObject container, TrackCorridorInfo corridor, Material bodyMaterial, bool leftSide)
    {
        float[] fillTo = leftSide ? corridor.LeftFillTo : corridor.RightFillTo;
        int n = corridor.Points.Count;
        // フィラーの自分側エッジ（道床に少し食い込ませた位置）
        float nearLat = corridor.OwnHalfWidth - MERGE_FILLER_EDGE_INSET;

        int runIndex = 0;
        int i = 0;
        while (i < n)
        {
            if (!IsFillerSampleValid(fillTo[i], nearLat))
            {
                i++;
                continue;
            }
            int s = i;
            while (i < n && IsFillerSampleValid(fillTo[i], nearLat)) i++;
            int e = i - 1;

            if (e - s < 1) continue;
            if (corridor.CumulativeLength[e] - corridor.CumulativeLength[s] < MERGE_FILLER_MIN_LENGTH) continue;

            BuildFillerStrip(container, corridor, s, e, leftSide, nearLat, fillTo, bodyMaterial, runIndex++);
        }
    }

    /// <summary>この点にフィラーを張れるか（充填担当であり、かつ幅が正になるか）。</summary>
    private static bool IsFillerSampleValid(float fillTo, float nearLat)
    {
        return !float.IsNaN(fillTo) && (fillTo + MERGE_FILLER_EDGE_INSET) > nearLat + 0.05f;
    }

    /// <summary>
    /// 点列区間 [s, e] に沿った道床ギャップフィラーのストリップメッシュを生成する。
    /// 高さは道床天端（BODY_ABOVE_SPLINE）から MERGE_FILLER_SINK だけ沈めた水平帯で、
    /// 両側の道床のバラスト肩の下に潜り込む（エッジは EDGE_INSET 分重ねて裂け目を防ぐ）。
    /// </summary>
    private static void BuildFillerStrip(GameObject container, TrackCorridorInfo corridor, int s, int e, bool leftSide, float nearLat, float[] fillTo, Material bodyMaterial, int runIndex)
    {
        int count = e - s + 1;
        var vertices = new Vector3[count * 2];
        var normals = new Vector3[count * 2];
        var uvs = new Vector2[count * 2];
        var triangles = new int[(count - 1) * 6];

        for (int k = 0; k < count; k++)
        {
            int idx = s + k;
            Vector3 basePos = corridor.Points[idx] + Vector3.up * (RoadPartHeights.BODY_ABOVE_SPLINE - MERGE_FILLER_SINK);
            Vector3 right = corridor.RightDir[idx];
            float farLat = fillTo[idx] + MERGE_FILLER_EDGE_INSET;

            // 頂点は「横方向オフセットの小さい側 → 大きい側」の順に並べ、上向き面の巻き順を保つ
            float lat0 = leftSide ? -farLat : nearLat;
            float lat1 = leftSide ? -nearLat : farLat;
            vertices[k * 2] = basePos + right * lat0;
            vertices[k * 2 + 1] = basePos + right * lat1;
            normals[k * 2] = Vector3.up;
            normals[k * 2 + 1] = Vector3.up;

            // バラストテクスチャの概ねのタイリング。横=オフセット・縦=弧長のメートル基準
            float v = corridor.CumulativeLength[idx] * 0.25f;
            uvs[k * 2] = new Vector2(lat0 * 0.25f, v);
            uvs[k * 2 + 1] = new Vector2(lat1 * 0.25f, v);
        }

        int t = 0;
        for (int k = 0; k < count - 1; k++)
        {
            int v0 = k * 2;       // 手前・小オフセット側
            int v1 = k * 2 + 1;   // 手前・大オフセット側
            int v2 = k * 2 + 2;   // 先・小オフセット側
            int v3 = k * 2 + 3;   // 先・大オフセット側
            triangles[t++] = v0; triangles[t++] = v2; triangles[t++] = v1;
            triangles[t++] = v1; triangles[t++] = v2; triangles[t++] = v3;
        }

        Mesh mesh = new Mesh { name = $"TrackMergeFiller_{(leftSide ? "L" : "R")}_{runIndex}" };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        GameObject go = new GameObject(mesh.name);
        go.transform.SetParent(container.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = bodyMaterial;
    }

    /// <summary>
    /// 交差点（分岐器・平面交差）のレールコネクタ（RailConnectorSpec）を、
    /// レールプレハブを SplineMesh で曲げて実体化する共通処理。
    /// PathManager（ノードメッシュ更新）から使う。歩道コネクタの
    /// RoadPath.CreateSidewalkConnectorObject と同じ役割分担（幾何計算は生成器側、実体化はここ）。
    /// </summary>
    public static void CreateRailConnectorObject(Transform parent, RailConnectorSpec connector, int index)
    {
        if (connector == null || connector.Points == null || connector.Points.Count < 2)
        {
            return;
        }

        // レール専用パーツ（枕木なし）を優先し、無ければ通常の軌道パーツ（track）で代用する。
        // どちらも無ければスキップ（オプショナルパーツ。道床ハルだけが描かれる）。
        GameObject prefab = LoadPrefab($"Models/Paths/p{connector.PrefabVariant}/rail", $"Models/Paths/p{connector.FallbackType}/rail");
        if (prefab == null)
        {
            prefab = LoadPrefab($"Models/Paths/p{connector.PrefabVariant}/track", $"Models/Paths/p{connector.FallbackType}/track");
        }

        MeshFilter prefabMf = prefab != null ? prefab.GetComponent<MeshFilter>() : null;
        if (prefabMf == null || prefabMf.sharedMesh == null)
        {
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] レールコネクタ: rail/track プレハブなし (variant={connector.PrefabVariant}, type={connector.FallbackType})");
            return;
        }

        GameObject go = new GameObject($"RailConnector_{index}");
        go.transform.SetParent(parent, false);

        // 計算済みのローカル点列（Yは基準面+TRACK_ABOVE_SPLINE込み）をそのままスプライン制御点にする。
        SplineComputer spline = go.AddComponent<SplineComputer>();
        SplinePoint[] splinePoints = new SplinePoint[connector.Points.Count];
        for (int i = 0; i < connector.Points.Count; i++)
        {
            SplinePoint point = new SplinePoint(connector.Points[i]);
            point.size = 1f;
            point.color = Color.white;
            point.type = SplinePoint.Type.SmoothMirrored;
            splinePoints[i] = point;
        }

        spline.SetPoints(splinePoints, SplineComputer.Space.Local);
        spline.type = Spline.Type.CatmullRom;
        spline.sampleRate = 24;
        spline.sampleMode = SplineComputer.SampleMode.Uniform;
        spline.RebuildImmediate();

        SplineMesh splineMesh = go.AddComponent<SplineMesh>();
        splineMesh.spline = spline;
        SplineMesh.Channel channel = splineMesh.AddChannel(prefabMf.sharedMesh, $"RailConnector_{index}");

        // レールは実寸配置（ゲージが伸縮してはいけない）。
        // ピボット補正のみ行い、bounds 中心をスプライン中心に合わせる。
        channel.minScale = Vector3.one;
        channel.maxScale = Vector3.one;
        splineMesh.offset = new Vector3(-prefabMf.sharedMesh.bounds.center.x, 0f, 0f);

        // 短い曲線でも滑らかに曲がるよう、タイル長の上限からタイル数を明示する
        // （RoadPath.MAX_EXTRUDE_TILE_LENGTH と同じ弦誤差対策）。
        float len = spline.CalculateLength();
        float meshLenZ = Mathf.Max(0.001f, prefabMf.sharedMesh.bounds.size.z);
        float tileLen = Mathf.Min(meshLenZ, RoadPath.MAX_EXTRUDE_TILE_LENGTH);
        channel.autoCount = false;
        channel.count = Mathf.Max(2, Mathf.RoundToInt(len / tileLen));

        // マテリアルは通常軌道とキャッシュを共有し、交差点内だけ色味が変わるのを防ぐ。
        MeshRenderer prefabMr = prefab.GetComponent<MeshRenderer>();
        MeshRenderer targetMr = go.GetComponent<MeshRenderer>();
        if (targetMr == null) targetMr = go.AddComponent<MeshRenderer>();
        if (prefabMr != null)
        {
            Material[] originalMaterials = prefabMr.sharedMaterials;
            Material[] cachedMaterials = new Material[originalMaterials.Length];
            for (int i = 0; i < originalMaterials.Length; i++)
            {
                cachedMaterials[i] = RoadPath.GetOrCreateRoadMaterial(originalMaterials[i]);
            }
            targetMr.sharedMaterials = cachedMaterials;
        }

        if (go.GetComponent<MeshFilter>() == null) go.AddComponent<MeshFilter>();
        splineMesh.updateMethod = SplineUser.UpdateMethod.Update;
        splineMesh.Rebuild();
    }

    /// <summary>
    /// マスターデータから線路の描画パラメータを取得する。
    /// lanes は線路では「軌道数」（単線=1、複線=2、複々線=4）を意味する。
    /// platformWidth は駅パスのプラットフォーム幅（ホーム無しバリアントは0）。
    /// </summary>
    public static void GetTrackMasterParams(PathSummary pathData, out float width, out int lanes, out float wallWidth, out float? pillarSpacing, out bool heightEditable, out float? wireSpacing, out float baseHeight, out float platformWidth)
    {
        width = 5.0f;
        lanes = 1;
        wallWidth = 0f;
        pillarSpacing = null;
        heightEditable = false;
        wireSpacing = null;
        baseHeight = 0f;
        platformWidth = 0f;

        var master = MasterDataStore.Instance != null ? MasterDataStore.Instance.GetPathMaster(pathData.path_variant) : null;
        if (master == null)
        {
            Debug.LogWarning($"[TrackPath] マスターデータなし (variant={pathData.path_variant})。デフォルト値を使用。");
            return;
        }

        if (master.width > 0f) width = master.width;
        if (master.lanes.HasValue && master.lanes.Value > 0) lanes = master.lanes.Value;
        if (master.wall_width.HasValue) wallWidth = master.wall_width.Value;
        if (master.pillar_spacing.HasValue) pillarSpacing = master.pillar_spacing.Value;
        if (master.height_editable.HasValue) heightEditable = master.height_editable.Value;
        if (master.wire_spacing.HasValue) wireSpacing = master.wire_spacing.Value;
        if (master.base_height.HasValue) baseHeight = master.base_height.Value;
        if (master.platform_width.HasValue && master.platform_width.Value > 0f) platformWidth = master.platform_width.Value;
    }

    private static GameObject LoadPrefab(string primaryPath, string fallbackPath)
    {
        if (ModelManager.Instance == null) return null;
        GameObject prefab = ModelManager.Instance.GetModel(primaryPath);
        if (prefab == null && !string.IsNullOrEmpty(fallbackPath))
        {
            prefab = ModelManager.Instance.GetModel(fallbackPath);
        }
        return prefab;
    }
}
