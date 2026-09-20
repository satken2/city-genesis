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
///   platform / platform_wall / platform_panel ... 駅パスのプラットフォーム（3パーツ構成。下記）。
///
/// 【駅パスのプラットフォーム】
/// 駅パスの width は通常線路と同じ「線路コリドーの幅」であり、道床・軌道・交差点ハル・
/// レールコネクタは通常線路とまったく同じに描かれる（前後の線路とシームレスに繋がる）。
/// プラットフォームは platform_width ぶん道床の外へ張り出し、3つの要素で構成される:
///   - platform（淵・線路側）  ... 縁（中心線から ±PLATFORM_EDGE_OFFSET＝1.5mの固定位置。
///     道床コリドー端より線路側に突き出す）の造形を持つ実寸パーツ。黄線・点字ブロック・
///     ホームドア・縁の垂直面・**ホームを支える支柱**。原点＝線路中心線・線路面で配置し、
///     縁までの横位置とホーム面までの立ち上がりはプレハブ形状が持つ
///   - デッキ（ホーム上面）    ... コードが動的生成する厚さ PLATFORM_DECK_THICKNESS（15cm）の
///     宙に浮いた板（上面＝ホーム面 PLATFORM_SURFACE_ABOVE_SPLINE から PLATFORM_DECK_SINK
///     沈めた高さ）。淵からホーム外縁まで platform_width ぶん張る。マテリアルは
///     platform_panel プレハブ（マテリアル参照専用パーツ）から採取し、未配置なら共通コンクリート
///   - platform_wall（外縁）   ... ホーム外縁（±(width/2+platform_width)）の壁・柵・ベンチを持つ
///     実寸パーツ。原点＝線路中心線・線路面で配置し、外縁までの横位置はプレハブ形状が持つ。
///     島式結合区間・他線路近接区間では省略される
/// デッキの幅の正はマスターの platform_width、高さの正はコードの高さ定数
/// （敷地・コライダーと描画が構造的に一致する）。淵・外縁プレハブは造形の横位置・
/// 立ち上がり高さをマスター値・PLATFORM_SURFACE_ABOVE_SPLINE に一致させて作る規約
/// （ずれるとデッキと食い違う。詳細は CreatePlatformSide のコメント参照）。
/// 設置側はパスの platform_side（内側/外側/両側）で決まる。内側/外側→左右の解決は都市の
/// 通行方向（CityInfoStore.TrafficHand）と線路の走行方向（travel_direction）を基準に行う
/// （ResolvePlatformExtents。DIR_BACKWARD の線路は幾何フレームで左右が反転する）。
/// この解決ロジックはバックエンドの敷地ポリゴン算出（geo_paths.get_platform_extension_widths）
/// と鏡写しであり、片方だけ変えると敷地と見た目が食い違う。
/// プラットフォームがある側では、防音壁を立てず（ホーム外縁は platform_wall が担う）、
/// 架線柱をホーム外縁の外側に立て、クリック判定コライダーをホーム込みの非対称幅にする。
///
/// 【島式ホームの結合描画】
/// 互いに向き合うホーム（上下線とも PLATFORM_SIDE_INSIDE 等）を持つほぼ平行な駅パスで、
/// 道床エッジ間ギャップが両ホームの張り出し幅の合計以下なら、TrackAdjacency がホームの
/// 結合を判定し（TrackCorridorInfo の Deck* / PlatformWallOmit）、
///   - デッキ: path_idの小さい側が相手の淵まで全幅デッキを張り、相手側は張らない
///     （1枚の島に見える。線路間隔の調整にも追従する）
///   - platform_wall: 両側とも省略
///   - 架線柱・架線ビーム: 最小path_idの線路が島全幅の外側に柱を立てて梁を渡す
///     （道床結合の柱コリドー化と同じ主線路方式）
/// として描画する。島式が成立しない近接（他線路の道床がホームに掛かる等）では
/// platform_wall を省略し、デッキを相手道床の手前までクランプする。
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
/// 一方通行の線路（travel_direction が DIR_FORWARD / DIR_BACKWARD）には進行方向を示す
/// 矢印オーバーレイ（TrackDirectionArrows）を子として付ける。表示は道路敷設モード
/// （STATE_EDIT_PATH）のみ（詳細は TrackDirectionArrows のクラスコメント参照）。
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
    /// <summary>ノード逆引きによる接続パス。未設定時は車止めの配置を保留する。</summary>
    public static System.Func<int, List<PathSummary>> ConnectedPathsProvider;
    /// <summary>共用インフラ接続ノード。施設内へ軌道が続くため車止めを置かない。</summary>
    public static System.Func<int, bool> InfrastructureConnectionProvider;
    /// <summary>架線柱を道床の端からどれだけ外側に立てるか（m）。</summary>
    private const float POLE_MARGIN = 0.5f;

    /// <summary>
    /// プラットフォームの淵（デッキの線路側エッジ）の線路中心線からの距離(m)。
    /// バリアントの width に依存しない固定値で、道床コリドー端（width/2）より線路側へ
    /// 突き出す（車両限界ぎりぎりまで寄せた乗降縁）。淵（platform）プレハブの縁の造形位置と
    /// デッキの線路側エッジ、島式結合での相手淵ラインの算出（TrackAdjacency）がこの値を共有する。
    /// </summary>
    public const float PLATFORM_EDGE_OFFSET = 1.5f;

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

    /// <summary>
    /// デッキ（ホーム上面）をホーム面の公称高さ（PLATFORM_SURFACE_ABOVE_SPLINE）から沈める量(m)。
    /// 淵（platform）パーツの上面造形（黄線・点字ブロック）は公称面に置かれるため、
    /// わずかに下げてZファイティングを避ける（点字ブロックが2cm浮き上がって見えるのは実物どおり）。
    /// </summary>
    private const float PLATFORM_DECK_SINK = 0.02f;

    /// <summary>これより幅の狭いデッキは生成しない(m)。クランプで潰れた区間のスリバー防止。</summary>
    private const float PLATFORM_DECK_MIN_WIDTH = 0.05f;

    /// <summary>これより短いデッキの連続区間は生成しない(m)。</summary>
    private const float PLATFORM_DECK_MIN_RUN_LENGTH = 1.0f;

    /// <summary>
    /// デッキ（ホーム上面の板）の厚さ(m)。デッキは地面まで覆う構造ではなく宙に浮いた
    /// スラブとして張られ、ホームを支える支柱・立ち上がりは platform（淵）パーツの造形が持つ。
    /// </summary>
    private const float PLATFORM_DECK_THICKNESS = 0.15f;

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
        if (!isCurved && (pathData.station_grade_profile == null || pathData.station_grade_profile.Count < 2)
            && (points.Count == 2 || (pathData.GetWaypointList()?.Count ?? 0) > 0))
        {
            bool followTerrain = PathManager.GetPathFollowTerrain(pathData)
                && AreAllAnchorsOnGround(pathData);
            points = PathManager.SubdividePointsWithTerrain(points, PathManager.TERRAIN_SAMPLE_INTERVAL, followTerrain);
        }

        // 近接して並走する線路との結合コリドー（複線見え化）と、駅パスのホームの
        // 結合/干渉情報。結合区間が無い・プロバイダ未設定（テストシーン等）なら null で、
        // 従来どおり単独描画になる（ホームは自幅の単独デッキ）。
        // ホームがある側の道床結合（バラスト充填等）の抑止と島式ホームの結合判定は
        // Compute 側が platformLeft / platformRight を見て行う。
        var corridor = ResolveCorridor(pathData, points, width, wallWidth, platformLeft, platformRight);

        GameObject container = new GameObject($"Track_{pathData.path_id}");
        SplineComputer spline = RoadPath.BuildSpline(container, points, isCurved);
        // 道床・架線・防音壁の反復タイル長を曲率から決める(track/platform は著作長固定で影響しない)。
        PathDrawContext.SetAdaptiveTileLength(PathDrawContext.ComputeAdaptiveTileLength(spline, RoadPath.MAX_EXTRUDE_TILE_LENGTH));

        CreateTrackParts(container, spline, points, width, lanes, wallWidth, pillarSpacing, heightEditable, baseHeight, wireSpacing, pathData, corridor, platformLeft, platformRight);

        // 一方通行線路には進行方向を示す矢印オーバーレイを付ける（双方向は何も出ない）。
        // 表示は道路敷設モード（STATE_EDIT_PATH）のみで、切替は TrackDirectionArrows が一括制御する。
        TrackDirectionArrows.Create(container, spline, pathData.travel_direction);

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
    /// パスの platform_side（内側/外側/両側）・都市の通行方向（CityInfoStore.TrafficHand）・
    /// 線路の走行方向（travel_direction）から、プラットフォームの左右の張り出し幅(m)を解決する。
    /// 左右はスプライン進行方向（start→end。right = Cross(up, forward)）基準。
    ///
    /// 「内側」とは複線（一方通行の単線線路2本）における相手線路側のことで、
    /// 左側通行では相手線路が走行方向の右側に来るため 内側=走行方向右、右側通行では 内側=走行方向左。
    /// 上下線の両方に内側を指定すると線路間にホームが向き合う島式ホームになる。
    ///
    /// 内側/外側は走行方向基準の概念なので、走行フレームで解決した左右を幾何フレーム
    /// （スプライン進行方向）へ変換する。DIR_BACKWARD の線路は幾何の end→start 方向へ
    /// 走行するため左右が反転する（複線の上下線は幾何方向が同じで走行方向だけ逆のため、
    /// 反転しないと両線のホームが同じ側に出てしまう）。双方向（DIR_BOTH）の単線は
    /// 敷設方向に運行上の意味がないため、幾何方向をそのまま走行方向とみなす。
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
        if (pathData.travel_direction == Constants.DIR_BACKWARD) platformIsRight = !platformIsRight;
        if (platformIsRight) rightWidth = platformWidth;
        else leftWidth = platformWidth;
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

    private static TrackCorridorInfo ResolveCorridor(PathSummary pathData, List<Vector3> points,
        float width, float wallWidth, float platformLeft, float platformRight)
    {
        TrackCorridorInfo corridor = null;
        if (TrackAdjacency.NeighborProvider != null)
        {
            List<TrackNeighborInfo> trackNeighbors = TrackAdjacency.NeighborProvider(pathData);
            if (trackNeighbors != null && trackNeighbors.Count > 0)
            {
                corridor = TrackAdjacency.Compute(pathData.path_id, points, width, wallWidth, platformLeft, platformRight, trackNeighbors);
            }
        }
        return corridor;
    }

    /// <summary>施工済み区間の道床・軌道・高欄。高架判定と隣接線路側の壁の省略は完成時と共通。</summary>
    public static void CreateConstructionParts(GameObject container, SplineComputer spline,
        List<Vector3> points, PathSummary pathData)
    {
        GetTrackMasterParams(pathData, out float width, out int lanes, out float wallWidth,
            out float? pillarSpacing, out bool heightEditable, out float? wireSpacing,
            out float baseHeight, out float platformWidth);
        ResolvePlatformExtents(pathData, platformWidth, out float platformLeft, out float platformRight);
        var corridor = ResolveCorridor(pathData, points, width, wallWidth, platformLeft, platformRight);
        CreateTrackParts(container, spline, points, width, lanes, wallWidth, pillarSpacing,
            heightEditable, baseHeight, wireSpacing, pathData, corridor, platformLeft, platformRight,
            constructionOnly: true);
    }

    /// <summary>
    /// 線路パーツ（道床、軌道、架線、架線柱、防音壁、橋脚、プラットフォーム）をスプラインに沿って配置する。
    /// corridor（近接線路の結合コリドー。null可）が渡された場合、結合区間では
    /// 道床のギャップ充填・防音壁の省略・架線柱/ビームのコリドー全幅化と、
    /// 島式ホームの結合描画（デッキ充填・platform_wall省略）を行う。
    /// points はスプライン制御点と同一の描画点列（デッキの動的生成に使う）。
    /// platformLeft / platformRight は駅パスのプラットフォームの左右の張り出し幅
    /// （ResolvePlatformExtents で解決済み。0なら無し）。
    /// </summary>
    private static void CreateTrackParts(GameObject container, SplineComputer spline, List<Vector3> points, float width, int lanes, float wallWidth, float? pillarSpacing, bool heightEditable, float baseHeight, float? wireSpacing, PathSummary pathData, TrackCorridorInfo corridor = null, float platformLeft = 0f, float platformRight = 0f, bool constructionOnly = false)
    {
        string variantBase = $"Models/Paths/p{pathData.path_variant}";
        string typeBase = $"Models/Paths/p{pathData.path_type}";
        int laneCount = Mathf.Max(1, lanes);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] CreateTrackParts開始: variant={variantBase}, type={typeBase}, width={width}, lanes={laneCount}, wallWidth={wallWidth}, wireSpacing={wireSpacing}");

        // 地上・高架・地下はバリアントではなくアンカーの高さで決まるため、桁・スラブ軌道・高欄の
        // 描き分けは「地表より高い区間」の percent 区間で行う。地表区間（開削区間を含む）は無印パーツ、
        // 高架区間は `{part}_elevated`（無ければ無印）を使う。
        List<TrackCorridorInfo.PercentRange> elevatedRanges = GetElevatedPercentRanges(points, baseHeight);
        List<TrackCorridorInfo.PercentRange> groundRanges = ComplementRanges(elevatedRanges);

        // 道床（body）: 全幅に引き伸ばして配置。高さは道路の body と同じ体系で、
        // 交差点の道床ハル（基準面 + BODY_ABOVE_SPLINE）とも同一平面になる。
        GameObject ballast = null;
        foreach (var range in groundRanges)
        {
            GameObject part = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/body", width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body", clipFrom: range.From, clipTo: range.To);
            if (ballast == null) ballast = part;
        }
        foreach (var range in elevatedRanges)
        {
            GameObject part = RoadPath.CreateSplineMesh(container, spline, ElevatedPartName(variantBase, "body"), width, 0f, RoadPartHeights.BODY_ABOVE_SPLINE, false, $"{typeBase}/body", clipFrom: range.From, clipTo: range.To);
            if (ballast == null) ballast = part;
        }
        if (!constructionOnly) BrickArcadeGenerator.Generate(container, spline, pathData, width);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] body(道床): {(ballast != null ? "生成成功" : "プレハブなし")} 高架区間={elevatedRanges.Count}");

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
            foreach (var range in groundRanges)
            {
                GameObject track = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/track", width / laneCount, offset, RoadPartHeights.TRACK_ABOVE_SPLINE, false, $"{typeBase}/track", isDashed: false, stretchToWidth: false, clipFrom: range.From, clipTo: range.To, maxTileLength: 0f);
                if (track != null) placedTracks++;
            }
            foreach (var range in elevatedRanges)
            {
                GameObject track = RoadPath.CreateSplineMesh(container, spline, ElevatedPartName(variantBase, "track"), width / laneCount, offset, RoadPartHeights.TRACK_ABOVE_SPLINE, false, $"{typeBase}/track", isDashed: false, stretchToWidth: false, clipFrom: range.From, clipTo: range.To, maxTileLength: 0f);
                if (track != null) placedTracks++;
            }
        }
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] track(軌道): {placedTracks}/{laneCount}本配置");
        if (!constructionOnly) CreateTerminations(container, points, corridor, pathData, width, laneCount, platformLeft, platformRight, variantBase, typeBase);

        // 道床も軌道も無いバリアントは白いストリップにフォールバックする（ゲームプレイ継続用）。
        // 軌道モデルだけがある構成（モノレール等、道床が不要なバリアント）では道床なしが正常。
        if (ballast == null && placedTracks == 0)
        {
            RoadPath.CreateFallbackRoadBody(container, spline, width);
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] 道床・軌道ともにモデルなし。白いストリップにフォールバック");
        }

        // プラットフォーム（駅パスのみ: platform_width が設定されているバリアント）。
        // 淵（platform）・デッキ（動的生成）・外縁（platform_wall）の3要素で構成する
        // （クラスコメント参照）。張り出し幅は platform_width、デッキの高さは
        // RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE が正。淵・外縁プレハブは
        // 原点＝線路面で、ホーム面までの立ち上がりをプレハブ形状が持つ。
        if (!constructionOnly && (platformLeft > 0f || platformRight > 0f))
        {
            CreatePlatformParts(container, spline, points, corridor, width, platformLeft, platformRight, variantBase, typeBase);
        }

        // 架線系（電化バリアントのみ: wire_spacing が設定されている場合）
        if (!constructionOnly && wireSpacing.HasValue && wireSpacing.Value > 0f)
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

        // 高欄（wall）: 高架区間だけに、道路と同じ「右側用パーツ」規約（左側はXミラー）で道床の外側に立てる。
        // 近接線路と結合している側は、線路間に壁を立てないよう結合区間を区間クリップで省略する
        // （分岐などで間隔が開き結合が解けた区間では壁が復活する）。
        // プラットフォームがある側には壁を立てない（乗降空間を防音壁で塞がないため。
        // ホーム外縁の壁・柵は platform_wall が担う）。ホームの無い側は通常どおり立てる。
        if (wallWidth > 0f && elevatedRanges.Count > 0)
        {
            float wallCenterOffset = (width * 0.5f) + (wallWidth * 0.5f);
            if (platformLeft <= 0f)
            {
                foreach (var range in IntersectRanges(GetWallPercentRanges(corridor, leftSide: true), elevatedRanges))
                {
                    RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, -wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, true, $"{typeBase}/wall", clipFrom: range.From, clipTo: range.To);
                }
            }
            if (platformRight <= 0f)
            {
                foreach (var range in IntersectRanges(GetWallPercentRanges(corridor, leftSide: false), elevatedRanges))
                {
                    RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/wall", wallWidth, wallCenterOffset, RoadPartHeights.WALL_ABOVE_SPLINE, false, $"{typeBase}/wall", clipFrom: range.From, clipTo: range.To);
                }
            }
        }

        // 高架橋脚（道路と共通処理）。地下パスには配置しない（RoadPath側の理由コメント参照）。
        if (!constructionOnly && heightEditable && pillarSpacing.HasValue && pillarSpacing.Value > 0f
            && !TunnelSectionUtil.IsUndergroundPath(pathData))
        {
            RoadPath.GeneratePillars(container, spline, pillarSpacing.Value, baseHeight, pathData, variantBase, typeBase);
        }
    }

    /// <summary>
    /// 接続ノード単位で終端を解決する。近接するだけの並走線は車止めの有無に影響しない。
    /// ホーム端は島式デッキの描画担当が実幅に合わせて生成する。
    /// </summary>
    private static void CreateTerminations(GameObject container, List<Vector3> points, TrackCorridorInfo corridor,
        PathSummary path, float width, int lanes, float leftWidth, float rightWidth, string variantBase, string typeBase)
    {
        if (path.path_type != Constants.PATH_TRACK) return;
        ComputeStripFrames(points, out var rights, out var lengths);
        for (int end = 0; end < 2; end++)
        {
            int idx = end == 0 ? 0 : points.Count - 1;
            int nodeId = end == 0 ? path.start_node_id : path.end_node_id;
            var connections = ConnectedPathsProvider?.Invoke(nodeId);
            bool trackContinues = InfrastructureConnectionProvider?.Invoke(nodeId) == true;
            bool leftContinues = false, rightContinues = false;
            if (connections != null)
                foreach (var other in connections)
                {
                    if (other.path_id == path.path_id || other.path_type != path.path_type) continue;
                    trackContinues = true;
                    GetTrackMasterParams(other, out _, out _, out _, out _, out _, out _, out _, out float pfWidth);
                    ResolvePlatformExtents(other, pfWidth, out float otherLeft, out float otherRight);
                    // 同じ端点どうしの接続では幾何方向が逆なので左右を交換する。
                    bool reverse = (end == 0) == (other.start_node_id == nodeId);
                    leftContinues |= (reverse ? otherRight : otherLeft) > 0f;
                    rightContinues |= (reverse ? otherLeft : otherRight) > 0f;
                }
            Vector3 forward = Vector3.Cross(rights[idx], Vector3.up);
            Vector3 inward = end == 0 ? forward : -forward;
            var anchor = end == 0 ? path.GetStartPointList() : path.GetEndPointList();
            bool atAnchor = anchor == null || anchor.Count < 2 ||
                Vector2.Distance(new Vector2(points[idx].x, points[idx].z),
                    new Vector2(anchor[0], anchor.Count >= 3 ? anchor[2] : anchor[1])) < .5f;
            // 未ロードや敷設中の仮ノードを終端と断定しない。
            // トンネルで分割された描画区間の坑口も、線路自体の終端とは区別する。
            if (atAnchor && connections != null && connections.Count > 0 && nodeId > 0 && !trackContinues && lengths[points.Count - 1] >= 3f)
            {
                for (int lane = 0; lane < lanes; lane++)
                    PlaceEndPart(container, LoadPrefab($"{variantBase}/buffer_stop", $"{typeBase}/buffer_stop"),
                        "TrackBufferStop", points[idx] + inward * 1.0f + rights[idx] * GetTrackCenterOffset(width, lanes, lane)
                            + Vector3.up * RoadPartHeights.TRACK_ABOVE_SPLINE,
                        Quaternion.LookRotation(inward), Vector3.one);
            }
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                float pf = left ? leftWidth : rightWidth;
                if (pf <= 0f || (left ? leftContinues : rightContinues)) continue;
                float[] farValues = corridor == null ? null : (left ? corridor.LeftDeckFar : corridor.RightDeckFar);
                float far = farValues == null ? width * .5f + pf : farValues[idx];
                if (float.IsNaN(far) || far <= PLATFORM_EDGE_OFFSET + PLATFORM_DECK_MIN_WIDTH) continue;
                var prefab = LoadPrefab($"{variantBase}/platform_end", $"{typeBase}/platform_end");
                float span = far - PLATFORM_EDGE_OFFSET;
                int panels = Mathf.Max(1, Mathf.CeilToInt(span / 1.2f));
                for (int panel = 0; panel < panels; panel++)
                {
                    float lateral = (left ? -1f : 1f) * (PLATFORM_EDGE_OFFSET + span * (panel + .5f) / panels);
                    PlaceEndPart(container, prefab, "TrackPlatformEnd", points[idx] + rights[idx] * lateral
                        + inward * .12f + Vector3.up * (RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE - PLATFORM_DECK_SINK),
                        Quaternion.LookRotation(inward), new Vector3(span / panels, 1f, 1f));
                }
            }
        }
    }

    private static void PlaceEndPart(GameObject parent, GameObject prefab, string name, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (prefab == null) return;
        var part = UnityEngine.Object.Instantiate(prefab, position, rotation, parent.transform);
        part.name = name;
        part.transform.localScale = scale;
        foreach (var renderer in part.GetComponentsInChildren<MeshRenderer>())
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) materials[i] = RoadPath.GetOrCreateRoadMaterial(materials[i]);
            renderer.sharedMaterials = materials;
        }
    }

    /// <summary>
    /// 駅パスのプラットフォーム一式（淵・デッキ・外縁）を左右それぞれに生成する。
    /// デッキ用マテリアルは platform_panel プレハブ（マテリアル参照専用パーツ）から採取し、
    /// 未配置なら共通コンクリートへフォールバックする（ホームは駅の視認性の本体のため、
    /// 他のオプショナルパーツと違い「未配置＝スキップ」にはしない）。
    /// </summary>
    private static void CreatePlatformParts(GameObject container, SplineComputer spline, List<Vector3> points, TrackCorridorInfo corridor, float width, float platformLeft, float platformRight, string variantBase, string typeBase)
    {
        Material deckMaterial = null;
        GameObject panelPrefab = LoadPrefab($"{variantBase}/platform_panel", $"{typeBase}/platform_panel");
        MeshRenderer panelRenderer = panelPrefab != null ? panelPrefab.GetComponent<MeshRenderer>() : null;
        if (panelRenderer != null && panelRenderer.sharedMaterial != null)
        {
            deckMaterial = RoadPath.GetOrCreateRoadMaterial(panelRenderer.sharedMaterial);
        }
        if (deckMaterial == null)
        {
            deckMaterial = BuildingMeshBuilder.GetCommonConcreteMaterial();
            UnityLog.If(LocalConstants.UNITY_LOG_ROAD, "[TrackPath] platform_panel プレハブなし: デッキは共通コンクリートで描画");
        }

        // デッキの動的生成にはコリドーと同じ「右方向・累積弧長」のフレームが必要。
        // コリドーがあればその配列を共有し、無ければ（隣接線路なし）ここで計算する。
        Vector3[] rightDir;
        float[] cumLength;
        if (corridor != null)
        {
            rightDir = corridor.RightDir;
            cumLength = corridor.CumulativeLength;
        }
        else
        {
            ComputeStripFrames(points, out rightDir, out cumLength);
        }

        float halfWidth = width * 0.5f;
        if (platformLeft > 0f)
        {
            CreatePlatformSide(container, spline, corridor, points, rightDir, cumLength, leftSide: true, halfWidth, platformLeft, deckMaterial, variantBase, typeBase);
        }
        if (platformRight > 0f)
        {
            CreatePlatformSide(container, spline, corridor, points, rightDir, cumLength, leftSide: false, halfWidth, platformRight, deckMaterial, variantBase, typeBase);
        }
    }

    /// <summary>
    /// 片側分のプラットフォーム（淵・デッキ・外縁）を生成する。
    ///
    /// プレハブのモデリング規約（RoadPartHeights のコメントも参照）:
    /// 淵（platform）・外縁（platform_wall）とも**原点＝線路中心線（スプライン中心）・線路面**で、
    /// 横方向の位置もプレハブ形状が持つ（コードはオフセットしない）。「右側用」として作り、
    /// 左側はXミラー（原点が中心線にあるためミラーは中心線対称になる）:
    ///   platform（淵）     ... 縁の造形をプレハブ内で X=+PLATFORM_EDGE_OFFSET（1.5m固定。
    ///                        バリアントの width に依存しない）に置く。ホーム面までの
    ///                        立ち上がり（縁の垂直面・ホームを支える支柱）と黄線・
    ///                        点字ブロック等の上面造形もプレハブ形状が持ち、
    ///                        上面造形の高さは PLATFORM_SURFACE_ABOVE_SPLINE と一致させる
    ///                        （デッキは厚さ15cmの浮いた板で、支柱を生成しない）
    ///   platform_wall（外縁）... 壁・柵・ベンチの本体をプレハブ内で X=+(width/2+platform_width)
    ///                        （ホーム外縁）、ホーム面の高さから上に造形する。デッキの外縁の
    ///                        小口（板の側面）と同一平面になる垂直面は持たせない（Zファイティング防止）
    /// プレハブのX座標と PLATFORM_EDGE_OFFSET・マスターの width / platform_width がずれると
    /// 淵・外縁がデッキと食い違うため、モデルはこれらの値に合わせて作る。
    /// どちらも実寸配置（Xスケール・ピボット補正なし）で、未配置ならスキップ。
    /// </summary>
    private static void CreatePlatformSide(GameObject container, SplineComputer spline, TrackCorridorInfo corridor, List<Vector3> points, Vector3[] rightDir, float[] cumLength, bool leftSide, float halfWidth, float platformWidth, Material deckMaterial, string variantBase, string typeBase)
    {
        string sideLabel = leftSide ? "左" : "右";

        // 支柱・柵の周期を保つため著作タイル長で並べる。曲線追従用の分割はモデル内に持つ。
        // 淵（platform）: 原点を線路中心線に合わせ、設置面（線路面）で配置する。
        GameObject edge = RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/platform", 0f, 0f, RoadPartHeights.PLATFORM_BASE_ABOVE_SPLINE, leftSide, $"{typeBase}/platform", stretchToWidth: false, compensatePivot: false, maxTileLength: 0f);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] platform 淵({sideLabel}): {(edge != null ? "生成成功" : "プレハブなし")}");

        // デッキ（ホーム上面）: 淵（中心線から PLATFORM_EDGE_OFFSET）から外縁
        // （width/2 + platform_width）まで水平の板を動的生成する。
        int deckRuns = BuildPlatformDeckRuns(container, corridor, points, rightDir, cumLength, leftSide, PLATFORM_EDGE_OFFSET, halfWidth + platformWidth, deckMaterial);
        UnityLog.If(LocalConstants.UNITY_LOG_ROAD, $"[TrackPath] platform デッキ({sideLabel}): {deckRuns}区間");

        // 外縁（platform_wall）: 原点を線路中心線に合わせ、設置面（線路面）で配置する
        // （外縁位置への横移動はプレハブ形状が持つ）。
        // 島式結合区間・他線路近接区間は percent 区間クリップで省略する。
        List<TrackCorridorInfo.PercentRange> ranges = corridor != null
            ? corridor.GetPlatformWallPercentIntervals(leftSide, MERGE_WALL_MIN_RUN_LENGTH)
            : new List<TrackCorridorInfo.PercentRange> { new TrackCorridorInfo.PercentRange { From = 0.0, To = 1.0 } };
        foreach (var range in ranges)
        {
            RoadPath.CreateSplineMesh(container, spline, $"{variantBase}/platform_wall", 0f, 0f, RoadPartHeights.PLATFORM_BASE_ABOVE_SPLINE, leftSide, $"{typeBase}/platform_wall", stretchToWidth: false, compensatePivot: false, clipFrom: range.From, clipTo: range.To, maxTileLength: 0f);
            PlatformFoundationGenerator.Generate(container, spline, variantBase, typeBase, leftSide, range.From, range.To);
        }
    }

    /// <summary>
    /// 点列に沿った「水平右方向・累積弧長」を計算する（結合コリドーが無い場合のデッキ生成用。
    /// TrackAdjacency.Compute のフレーム計算と同じ導出）。
    /// </summary>
    private static void ComputeStripFrames(List<Vector3> points, out Vector3[] rightDir, out float[] cumLength)
    {
        int n = points.Count;
        rightDir = new Vector3[n];
        cumLength = new float[n];
        float cum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) cum += Vector3.Distance(points[i - 1], points[i]);
            cumLength[i] = cum;
            Vector3 fwd = points[Mathf.Min(i + 1, n - 1)] - points[Mathf.Max(i - 1, 0)];
            fwd.y = 0f;
            rightDir[i] = fwd.sqrMagnitude < 1e-6f
                ? (i > 0 ? rightDir[i - 1] : Vector3.right)
                : Vector3.Cross(Vector3.up, fwd.normalized);
        }
    }

    /// <summary>
    /// 指定側のデッキ（ホーム上面）を連続区間ごとに生成する。戻り値は生成した区間数。
    /// nearLat は淵ライン（中心線から PLATFORM_EDGE_OFFSET の固定値）、defaultFar は
    /// ホーム外縁（width/2 + platform_width）。外縁位置はコリドーの DeckFar
    /// （NaN=張らない。島式の充填担当・クランプ反映済み）に従い、コリドーが無ければ
    /// 全長を defaultFar で張る。
    /// </summary>
    private static int BuildPlatformDeckRuns(GameObject container, TrackCorridorInfo corridor, List<Vector3> points, Vector3[] rightDir, float[] cumLength, bool leftSide, float nearLat, float defaultFar, Material deckMaterial)
    {
        if (deckMaterial == null || points == null || points.Count < 2) return 0;

        int n = points.Count;
        float[] deckFar = corridor != null ? (leftSide ? corridor.LeftDeckFar : corridor.RightDeckFar) : null;
        bool[] joined = corridor != null ? (leftSide ? corridor.LeftDeckJoined : corridor.RightDeckJoined) : null;

        int runIndex = 0;
        int i = 0;
        while (i < n)
        {
            float far = deckFar != null ? deckFar[i] : defaultFar;
            if (float.IsNaN(far) || far - nearLat < PLATFORM_DECK_MIN_WIDTH)
            {
                i++;
                continue;
            }
            int s = i;
            while (i < n)
            {
                float f = deckFar != null ? deckFar[i] : defaultFar;
                if (float.IsNaN(f) || f - nearLat < PLATFORM_DECK_MIN_WIDTH) break;
                i++;
            }
            int e = i - 1;

            if (e - s < 1) continue;
            if (cumLength[e] - cumLength[s] < PLATFORM_DECK_MIN_RUN_LENGTH) continue;

            BuildPlatformDeckMesh(container, points, rightDir, cumLength, s, e, leftSide, nearLat, deckFar, joined, defaultFar, deckMaterial, runIndex++);
        }
        return runIndex;
    }

    /// <summary>
    /// 点列区間 [s, e] に沿ったデッキのメッシュを生成する。デッキは厚さ
    /// PLATFORM_DECK_THICKNESS の宙に浮いた板（スラブ）で、構成は
    /// 「上面＋底面＋外縁の小口（垂直面）＋両端の妻面キャップ」。
    /// 上面はホーム面（PLATFORM_SURFACE_ABOVE_SPLINE）から PLATFORM_DECK_SINK 沈めた水平面。
    /// ホームを支える支柱・立ち上がりは platform（淵）パーツの造形が持つ（ここでは生成しない）。
    /// 上面の淵側エッジは淵ライン（nearLat＝中心線から PLATFORM_EDGE_OFFSET の固定値）
    /// ちょうどに置き、淵パーツの上面造形が2cm上から継ぎ目を覆う
    /// （淵側の小口も淵パーツが覆うため生成しない）。
    /// 外縁の小口は島式結合区間（joined）では張らない（相手線路の淵パーツが面を持つ）。
    /// </summary>
    private static void BuildPlatformDeckMesh(GameObject container, List<Vector3> points, Vector3[] rightDir, float[] cumLength, int s, int e, bool leftSide, float nearLat, float[] deckFar, bool[] joined, float defaultFar, Material material, int runIndex)
    {
        int count = e - s + 1;
        float deckTop = RoadPartHeights.PLATFORM_SURFACE_ABOVE_SPLINE - PLATFORM_DECK_SINK;
        float deckBottom = deckTop - PLATFORM_DECK_THICKNESS;
        float sideSign = leftSide ? -1f : 1f;

        var vertices = new List<Vector3>(count * 4 + 16);
        var normals = new List<Vector3>(count * 4 + 16);
        var uvs = new List<Vector2>(count * 4 + 16);
        var triangles = new List<int>((count - 1) * 18 + 24);

        // --- 上面・底面ストリップ ---
        // 頂点は「横方向オフセットの小さい側 → 大きい側」の順に並べ、上向き面の巻き順を
        // 道床ギャップフィラーと同じ規約で保つ（底面は巻き順を裏返して下向きに）。
        // UVは横=オフセット・縦=弧長のメートル基準。
        for (int k = 0; k < count; k++)
        {
            int idx = s + k;
            float far = deckFar != null ? deckFar[idx] : defaultFar;
            Vector3 basePos = points[idx] + Vector3.up * deckTop;
            Vector3 right = rightDir[idx];
            float lat0 = leftSide ? -far : nearLat;
            float lat1 = leftSide ? -nearLat : far;
            vertices.Add(basePos + right * lat0);
            vertices.Add(basePos + right * lat1);
            normals.Add(Vector3.up);
            normals.Add(Vector3.up);
            float v = cumLength[idx] * 0.25f;
            uvs.Add(new Vector2(lat0 * 0.25f, v));
            uvs.Add(new Vector2(lat1 * 0.25f, v));
        }
        int bottomBase = count * 2;
        for (int k = 0; k < count; k++)
        {
            int idx = s + k;
            float far = deckFar != null ? deckFar[idx] : defaultFar;
            Vector3 basePos = points[idx] + Vector3.up * deckBottom;
            Vector3 right = rightDir[idx];
            float lat0 = leftSide ? -far : nearLat;
            float lat1 = leftSide ? -nearLat : far;
            vertices.Add(basePos + right * lat0);
            vertices.Add(basePos + right * lat1);
            normals.Add(Vector3.down);
            normals.Add(Vector3.down);
            float v = cumLength[idx] * 0.25f;
            uvs.Add(new Vector2(lat0 * 0.25f, v));
            uvs.Add(new Vector2(lat1 * 0.25f, v));
        }
        for (int k = 0; k < count - 1; k++)
        {
            int v0 = k * 2;       // 手前・小オフセット側
            int v1 = k * 2 + 1;   // 手前・大オフセット側
            int v2 = k * 2 + 2;   // 先・小オフセット側
            int v3 = k * 2 + 3;   // 先・大オフセット側
            triangles.Add(v0); triangles.Add(v2); triangles.Add(v1);
            triangles.Add(v1); triangles.Add(v2); triangles.Add(v3);
            // 底面は同じ並びで巻き順だけ裏返す
            int b0 = bottomBase + v0, b1 = bottomBase + v1, b2 = bottomBase + v2, b3 = bottomBase + v3;
            triangles.Add(b0); triangles.Add(b1); triangles.Add(b2);
            triangles.Add(b1); triangles.Add(b3); triangles.Add(b2);
        }

        // --- 外縁の小口（垂直面） ---
        // 両端点とも島式結合でないセグメントにのみ張る（結合区間は相手の淵が面を持つ）。
        for (int k = 0; k < count - 1; k++)
        {
            int a = s + k;
            int b = s + k + 1;
            if (joined != null && (joined[a] || joined[b])) continue;

            float farA = deckFar != null ? deckFar[a] : defaultFar;
            float farB = deckFar != null ? deckFar[b] : defaultFar;
            Vector3 latA = rightDir[a] * (sideSign * farA);
            Vector3 latB = rightDir[b] * (sideSign * farB);
            Vector3 topA = points[a] + Vector3.up * deckTop + latA;
            Vector3 topB = points[b] + Vector3.up * deckTop + latB;
            Vector3 botA = points[a] + Vector3.up * deckBottom + latA;
            Vector3 botB = points[b] + Vector3.up * deckBottom + latB;

            float u0 = cumLength[a] * 0.25f;
            float u1 = cumLength[b] * 0.25f;
            float vTop = deckTop * 0.25f;
            float vBot = deckBottom * 0.25f;
            AddDeckQuad(vertices, normals, uvs, triangles,
                topA, topB, botB, botA,
                rightDir[a] * sideSign,
                new Vector2(u0, vTop), new Vector2(u1, vTop), new Vector2(u1, vBot), new Vector2(u0, vBot),
                flip: leftSide);
        }

        // --- 妻面（両端のキャップ） ---
        AddDeckEndCap(vertices, normals, uvs, triangles, points, rightDir, s, leftSide, nearLat, deckFar, defaultFar, deckTop, deckBottom, isEnd: false);
        AddDeckEndCap(vertices, normals, uvs, triangles, points, rightDir, e, leftSide, nearLat, deckFar, defaultFar, deckTop, deckBottom, isEnd: true);

        Mesh mesh = new Mesh { name = $"TrackPlatformDeck_{(leftSide ? "L" : "R")}_{runIndex}" };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        GameObject go = new GameObject(mesh.name);
        go.transform.SetParent(container.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    /// <summary>
    /// デッキの妻面キャップ1枚を追加する。淵ラインから外縁までの矩形を、板の上面から
    /// 下面まで（厚さ分だけ）垂直に張る。法線は進行方向の外向き（始端=-forward、終端=+forward）。
    /// </summary>
    private static void AddDeckEndCap(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, List<Vector3> points, Vector3[] rightDir, int idx, bool leftSide, float nearLat, float[] deckFar, float defaultFar, float deckTop, float deckBottom, bool isEnd)
    {
        int n = points.Count;
        Vector3 fwd = points[Mathf.Min(idx + 1, n - 1)] - points[Mathf.Max(idx - 1, 0)];
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd.Normalize();

        float sideSign = leftSide ? -1f : 1f;
        float far = deckFar != null ? deckFar[idx] : defaultFar;
        Vector3 nearOff = rightDir[idx] * (sideSign * nearLat);
        Vector3 farOff = rightDir[idx] * (sideSign * far);
        Vector3 nearTop = points[idx] + Vector3.up * deckTop + nearOff;
        Vector3 farTop = points[idx] + Vector3.up * deckTop + farOff;
        Vector3 nearBot = points[idx] + Vector3.up * deckBottom + nearOff;
        Vector3 farBot = points[idx] + Vector3.up * deckBottom + farOff;

        float vTop = deckTop * 0.25f;
        float vBot = deckBottom * 0.25f;
        AddDeckQuad(vertices, normals, uvs, triangles,
            nearTop, farTop, farBot, nearBot,
            isEnd ? fwd : -fwd,
            new Vector2(nearLat * 0.25f, vTop), new Vector2(far * 0.25f, vTop), new Vector2(far * 0.25f, vBot), new Vector2(nearLat * 0.25f, vBot),
            flip: leftSide != isEnd);
    }

    /// <summary>
    /// 四角形1枚を追加する。normal には表側の法線（外向き）をそのまま渡し、
    /// flip は巻き順だけを裏返す（a,b,c,d の並びが法線側から見て反時計回りになる
    /// 配置＝左側ミラー等の場合に true にして表裏を合わせる）。
    /// </summary>
    private static void AddDeckQuad(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, bool flip)
    {
        int baseIdx = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        for (int i = 0; i < 4; i++) normals.Add(normal);
        uvs.Add(uvA); uvs.Add(uvB); uvs.Add(uvC); uvs.Add(uvD);
        if (!flip)
        {
            triangles.Add(baseIdx); triangles.Add(baseIdx + 1); triangles.Add(baseIdx + 2);
            triangles.Add(baseIdx); triangles.Add(baseIdx + 2); triangles.Add(baseIdx + 3);
        }
        else
        {
            triangles.Add(baseIdx); triangles.Add(baseIdx + 2); triangles.Add(baseIdx + 1);
            triangles.Add(baseIdx); triangles.Add(baseIdx + 3); triangles.Add(baseIdx + 2);
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
    /// 各線路が自分の柱を従来どおり立てる。島式ホームの結合区間も同じ主線路方式で、
    /// コリドーの張り出し（Left/RightExtent）に島全幅（相手線路の外縁側ホーム込み）が
    /// 反映済みのため、柱は島をまたいだ左右の外側に立ち、梁が駅全幅に渡る。
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
    /// <summary>
    /// 高架区間用のパーツ名を返す。`{part}_elevated` プレハブがあればそれ、無ければ無印。
    /// バリアント→タイプのフォールバックは呼び出し側が無印のタイプ名で行う。
    /// </summary>
    private static string ElevatedPartName(string variantBase, string part)
    {
        string elevated = $"{variantBase}/{part}_elevated";
        bool exists = ModelManager.Instance != null && ModelManager.Instance.GetModel(elevated, true) != null;
        return exists ? elevated : $"{variantBase}/{part}";
    }

    /// <summary>
    /// 点列のうち地表より GROUND_SNAP_EPSILON を超えて高い（高架の）連続区間を、
    /// 弧長比のスプライン percent 区間として返す。区間境界はサンプル点の中間位置。
    /// 地表・開削区間は含まない（地下パスのトンネル区間は点列に来ない）。
    /// </summary>
    private static List<TrackCorridorInfo.PercentRange> GetElevatedPercentRanges(List<Vector3> points, float baseHeight)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        int n = points.Count;
        var cumulative = new float[n];
        for (int i = 1; i < n; i++)
        {
            cumulative[i] = cumulative[i - 1] + Vector2.Distance(new Vector2(points[i - 1].x, points[i - 1].z), new Vector2(points[i].x, points[i].z));
        }
        float total = cumulative[n - 1];
        if (total <= 0f) return result;

        bool Elevated(int i) => points[i].y - baseHeight - Utils.GetGroundHeight(points[i]) > TunnelSectionUtil.GROUND_SNAP_EPSILON;

        int idx = 0;
        while (idx < n)
        {
            if (!Elevated(idx)) { idx++; continue; }
            int s = idx;
            while (idx < n && Elevated(idx)) idx++;
            int e = idx - 1;
            float fromLen = (s == 0) ? 0f : (cumulative[s - 1] + cumulative[s]) * 0.5f;
            float toLen = (e == n - 1) ? total : (cumulative[e] + cumulative[e + 1]) * 0.5f;
            result.Add(new TrackCorridorInfo.PercentRange { From = fromLen / total, To = toLen / total });
        }
        return result;
    }

    /// <summary>昇順・非重複の percent 区間列の補集合（[0,1] 内）を返す。</summary>
    private static List<TrackCorridorInfo.PercentRange> ComplementRanges(List<TrackCorridorInfo.PercentRange> ranges)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        double cursor = 0.0;
        foreach (var range in ranges)
        {
            if (range.From > cursor) result.Add(new TrackCorridorInfo.PercentRange { From = cursor, To = range.From });
            cursor = range.To;
        }
        if (cursor < 1.0) result.Add(new TrackCorridorInfo.PercentRange { From = cursor, To = 1.0 });
        return result;
    }

    /// <summary>2つの percent 区間列の共通部分を返す（空区間は含めない）。</summary>
    private static List<TrackCorridorInfo.PercentRange> IntersectRanges(List<TrackCorridorInfo.PercentRange> a, List<TrackCorridorInfo.PercentRange> b)
    {
        var result = new List<TrackCorridorInfo.PercentRange>();
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                double from = System.Math.Max(x.From, y.From);
                double to = System.Math.Min(x.To, y.To);
                if (to > from) result.Add(new TrackCorridorInfo.PercentRange { From = from, To = to });
            }
        }
        return result;
    }

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

        var points = new List<Vector3>(connector.Points);
        // 端部の微小区間を断面の接線へ合わせ、再補間によるレール端の回転を防ぐ。
        float head = Vector3.Distance(points[0], points[1]);
        float tail = Vector3.Distance(points[points.Count - 1], points[points.Count - 2]);
        if (connector.StartDirection.sqrMagnitude > 0f)
            points[1] = new Vector3(points[0].x + connector.StartDirection.x * head, points[0].y + connector.StartSlope * head, points[0].z + connector.StartDirection.y * head);
        if (connector.EndDirection.sqrMagnitude > 0f)
            points[points.Count - 2] = new Vector3(points[points.Count - 1].x - connector.EndDirection.x * tail, points[points.Count - 1].y - connector.EndSlope * tail, points[points.Count - 1].z - connector.EndDirection.y * tail);
        // BuildSpline はワールド点を受け取る。コネクタはノードローカル点なのでここで変換する。
        for (int i = 0; i < points.Count; i++) points[i] = parent.TransformPoint(points[i]);
        SplineComputer spline = RoadPath.BuildSpline(go, points, true);
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

        // 頂点数バジェットガード(RoadPath.CreateSplineMesh と同じ理由)。コネクタは通常軌道の
        // 生成経路を通らないため、ここでもタイル数をクランプして16bitメッシュの上限超過を防ぐ。
        int partVerts = prefabMf.sharedMesh.vertexCount;
        if (partVerts > 0 && (long)partVerts * channel.count > RoadPath.SPLINE_MESH_VERTEX_BUDGET)
        {
            int clamped = Mathf.Max(1, RoadPath.SPLINE_MESH_VERTEX_BUDGET / partVerts);
            Debug.LogWarning($"[TrackPath] レールコネクタのタイル数 {channel.count} は頂点数上限を超えるため {clamped} にクランプします(1個あたり頂点{partVerts})。");
            channel.count = clamped;
        }

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
