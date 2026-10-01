using UnityEngine;
using System.Collections.Generic;
using GeneratedModels;

/// <summary>
/// 近接線路の結合描画（TrackPath）に渡す、隣接候補1本分の情報。
/// PathManager が TrackAdjacency.NeighborProvider 経由で提供する。
/// </summary>
public class TrackNeighborInfo
{
    /// <summary>隣接候補のパスID。</summary>
    public int PathId;

    /// <summary>描画点列（ワールド座標・地形追従済み。約5m間隔サンプル）。</summary>
    public List<Vector3> Points;

    /// <summary>マスターデータの全幅（道床幅、m）。</summary>
    public float Width;

    /// <summary>
    /// プラットフォームの左右の張り出し幅(m)。左右はこの線路自身の幾何フレーム
    /// （点列の start→end 方向）基準で、TrackPath.ResolvePlatformExtents で解決済み。
    /// 駅パスでなければ両方0。島式ホームの結合判定（相手のホームが自分側を向いているか）に使う。
    /// </summary>
    public float PlatformLeft;

    public float PlatformRight;

    /// <summary>
    /// ホームの淵ライン（デッキの線路側の縁）の中心線からの距離（TrackPath.PlatformEdgeDistance）。
    /// 島式結合で相手の淵までデッキを張るのに使う。軌道数2の駅は外側の軌道から測るので単線より遠い。
    /// </summary>
    public float PlatformEdge = TrackPath.PLATFORM_EDGE_OFFSET;

    /// <summary>
    /// LRT停留所のホーム区間(Points の始点からの3D累積距離。LrtPlatformParts.ZoneRanges)。無ければ null。
    /// 結合コリドーの主線路は、相手のこの区間では相手のホーム外縁(LrtPlatformOuter)の外に架線柱を立てる。
    /// </summary>
    public List<Vector2> LrtPlatformZones;

    /// <summary>LRT停留所のホーム外縁の中心線からの距離(LrtRoadParts.PlatformOuterOffset。両側)。</summary>
    public float LrtPlatformOuter;

    /// <summary>
    /// 点ごとの道床の外端のずれ(Points と同じ添字。外へ広がると正、左右対称)。つなぎ目のS字(TrackEndTransition)で
    /// 軌道とホーム・道床が外へ動く区間を表す。null は全長で0。
    /// </summary>
    public float[] OuterShifts;

    /// <summary>
    /// 完成前の仮の相手(計画中・着工保留中・建設中・敷設プレビュー)か。完成した線路は仮の相手に自分の部品
    /// (島式のデッキ・架線柱)を譲らず、間を埋める部品(道床の隙間・島のデッキの残り)は仮の相手が自分の見た目で描く。
    /// 架線柱は完成した線路と仮の相手の間では共有しない。
    /// </summary>
    public bool Provisional;
}

/// <summary>
/// 自線路の描画点列に沿った、近接線路との「結合コリドー」情報。
///
/// 複線は軌道数2の1本のパス（まとめた複線）のほか、一方通行の単線線路を2本並べても表現される。
/// 後者はそのままでは単線が2本並ぶだけの見た目になるため、
/// XZ平面・Y方向ともに近接して並走する線路どうし（まとめた複線を含む）を1つのコリドー（廊下）とみなし、
/// 道床（body）・防音壁（wall）・架線柱（pole）・架線ビーム（beam）を結合して描画する。
///
/// 添字はすべて自線路の点列（スプライン制御点）のインデックスに対応する。
/// 左右はスプライン進行方向に対する向き（right = Cross(up, forward) の水平右方向）。
/// 2本の単線が互いに逆向きに敷かれていても（上り線・下り線）、判定は接線の絶対平行度で
/// 行うため結合される。
/// </summary>
public class TrackCorridorInfo
{
    /// <summary>スプライン percent（0〜1）の区間。</summary>
    public struct PercentRange
    {
        public double From;
        public double To;
    }

    /// <summary>自線路の描画点列（スプライン制御点と同一）。</summary>
    public List<Vector3> Points;

    /// <summary>各点の水平右方向（単位ベクトル）。</summary>
    public Vector3[] RightDir;

    /// <summary>点列に沿った累積弧長(m)。スプラインは SampleMode.Uniform のため percent ≒ 弧長比。</summary>
    public float[] CumulativeLength;

    public float TotalLength;

    /// <summary>自線路の半幅（マスター width の半分）。</summary>
    public float OwnHalfWidth;

    /// <summary>自線路の点ごとの道床の外端のずれ(つなぎ目のS字。外へ広がると正、左右対称)。null は全長で0。</summary>
    public float[] OwnOuterShift;

    /// <summary>点 i での自線路の半幅(S字のずれを含む)。</summary>
    public float OwnHalfAt(int i) => OwnHalfWidth + (OwnOuterShift != null ? OwnOuterShift[i] : 0f);

    /// <summary>左側に隣接線路（コリドー内の直接隣）が存在するか。</summary>
    public bool[] LeftCovered;

    public bool[] RightCovered;

    /// <summary>
    /// この地点で自分がコリドーの「主線路（コリドー内最小path_id）」でないため、
    /// 架線柱・架線ビームを配置しない（主線路がコリドー全幅分を配置する）。
    /// </summary>
    public bool[] SuppressPoleSet;

    /// <summary>
    /// 左側の防音壁の位置に他線路の道床が物理的に掛かっているため、壁を立てられない点。
    /// 並走判定（角度・ギャップ）とは独立の角度不問の判定で、合流の喉元やシザーズ状の
    /// 平面交差で、収束・横断してくる線路の本体の上に壁が残るのを防ぐ。
    /// </summary>
    public bool[] LeftWallBlocked;

    public bool[] RightWallBlocked;

    /// <summary>コリドー左端までの距離（自中心線からの正の距離。単独区間では OwnHalfWidth）。</summary>
    public float[] LeftExtent;

    public float[] RightExtent;

    /// <summary>
    /// 左隣接線路の手前側（自分に近い側）道床エッジまでの距離（正）。
    /// 道床ギャップの充填はpath_idの小さい側が担当し（二重生成の防止）、
    /// 担当しない・隣接なしの点は NaN。
    /// </summary>
    public float[] LeftFillTo;

    public float[] RightFillTo;

    /// <summary>
    /// 左側プラットフォームのデッキ（ホーム上面）の外縁までの距離（自中心線からの正の距離）。
    /// NaN はこの点でデッキを張らないことを意味する（その側にホームが無い、または
    /// 島式結合区間でデッキの充填担当が相手線路の側）。
    /// 通常値は「自半幅 + platform_width」で、島式結合区間の充填担当側では相手線路の
    /// 淵ライン（相手中心線から TrackNeighborInfo.PlatformEdge）まで、他線路の道床が
    /// ホームに掛かる区間ではその手前までクランプされる。
    /// なお淵側（線路側）のエッジは自分の淵ライン（TrackPath.PlatformEdgeDistance。
    /// TrackPath 側が持つ）で、この配列は外縁側だけを表す。
    /// </summary>
    public float[] LeftDeckFar;

    public float[] RightDeckFar;

    /// <summary>
    /// 左側デッキの外縁が島式結合で相手線路の淵に接続している点。
    /// この点ではデッキの外側スカート（垂直面）を張らない（相手の淵パーツが面を持つ）。
    /// </summary>
    public bool[] LeftDeckJoined;

    public bool[] RightDeckJoined;

    /// <summary>
    /// 左側の platform_wall（ホーム外縁の壁・柵・ベンチ）を省略する点。
    /// 島式結合区間（ホームが相手と繋がる）と、他線路の道床がホーム外縁に掛かる区間。
    /// </summary>
    public bool[] LeftPlatformWallOmit;

    public bool[] RightPlatformWallOmit;

    /// <summary>
    /// 左側のLRT停留所のホーム(自道床の外〜ホーム外縁+余裕)の帯に他線路の道床が掛かる点。
    /// ホームは停留所の位置で決まり線路の配置では動かせないので、この点ではその側のホームを描かない
    /// (分岐の喉元や並走する線路の本体の上にデッキ・スロープを載せない)。LRTの線路でなければ常に false。
    /// </summary>
    public bool[] LeftLrtPlatformBlocked;

    public bool[] RightLrtPlatformBlocked;

    /// <summary>点列のどこかに結合区間が存在するか。false のコリドーは生成されない。</summary>
    public bool AnyCoverage;

    /// <summary>累積弧長 length(m) に最も近い点列インデックスを返す（二分探索）。</summary>
    public int SampleIndexAtLength(float length)
    {
        float[] cum = CumulativeLength;
        int hi = cum.Length - 1;
        if (length <= 0f || hi <= 0) return 0;
        if (length >= TotalLength) return hi;

        int lo = 0;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (cum[mid] < length) lo = mid + 1;
            else hi = mid;
        }
        // lo は length 以上となる最初の点。前後で近い方を返す。
        if (lo > 0 && length - cum[lo - 1] < cum[lo] - length) return lo - 1;
        return lo;
    }

    /// <summary>
    /// 指定側に防音壁を立ててよい連続区間（隣接線路が無く、かつ他線路の道床が壁の位置に
    /// 掛かっていない区間）をスプライン percent の区間として返す。区間境界はサンプル点の中間位置。
    /// minRunLength(m) 未満の細切れ区間はスリバー防止のため出力しない。
    /// </summary>
    public List<PercentRange> GetWallPercentIntervals(bool leftSide, float minRunLength)
    {
        bool[] covered = leftSide ? LeftCovered : RightCovered;
        bool[] blocked = leftSide ? LeftWallBlocked : RightWallBlocked;
        return GetClearPercentIntervals(i => covered[i] || blocked[i], minRunLength);
    }

    /// <summary>
    /// 指定側の platform_wall（ホーム外縁）を配置してよい連続区間をスプライン percent の
    /// 区間として返す（島式結合区間・他線路の道床がホーム外縁に掛かる区間を除いた残り）。
    /// </summary>
    public List<PercentRange> GetPlatformWallPercentIntervals(bool leftSide, float minRunLength)
    {
        bool[] omit = leftSide ? LeftPlatformWallOmit : RightPlatformWallOmit;
        return GetClearPercentIntervals(i => omit[i], minRunLength);
    }

    /// <summary>
    /// 指定側でLRT停留所のホームを描けない連続区間を、スプライン始点からの距離(m。x の昇順)で返す。
    /// 区間境界はサンプル点の中間位置で、端の点が塞がれていればその先へ無限に伸ばす
    /// (切断端の外へはみ出すスロープも他線路の上には置かない)。
    /// </summary>
    public List<Vector2> GetLrtPlatformBlockedRanges(bool leftSide)
    {
        bool[] blocked = leftSide ? LeftLrtPlatformBlocked : RightLrtPlatformBlocked;
        var result = new List<Vector2>();
        int n = Points.Count;
        int i = 0;
        while (i < n)
        {
            if (!blocked[i]) { i++; continue; }
            int s = i;
            while (i < n && blocked[i]) i++;
            int e = i - 1;
            float from = s == 0 ? float.NegativeInfinity : (CumulativeLength[s - 1] + CumulativeLength[s]) * 0.5f;
            float to = e == n - 1 ? float.PositiveInfinity : (CumulativeLength[e] + CumulativeLength[e + 1]) * 0.5f;
            result.Add(new Vector2(from, to));
        }
        return result;
    }

    private List<PercentRange> GetClearPercentIntervals(System.Func<int, bool> isOmitted, float minRunLength)
        => ClearPercentIntervals(CumulativeLength, TotalLength, isOmitted, minRunLength);

    /// <summary>
    /// isOmitted が false の連続区間をスプライン percent の区間として返す共通処理(道路の結合区間 RoadCorridorInfo も使う)。
    /// 区間境界はサンプル点の中間位置。minRunLength(m) 未満の細切れ区間は
    /// スリバー防止のため出力しない。
    /// </summary>
    public static List<PercentRange> ClearPercentIntervals(float[] cumulativeLength, float totalLength, System.Func<int, bool> isOmitted, float minRunLength)
    {
        var result = new List<PercentRange>();
        int n = cumulativeLength.Length;
        if (totalLength <= 0f)
        {
            result.Add(new PercentRange { From = 0.0, To = 1.0 });
            return result;
        }

        int i = 0;
        while (i < n)
        {
            if (isOmitted(i)) { i++; continue; }
            int s = i;
            while (i < n && !isOmitted(i)) i++;
            int e = i - 1;

            float fromLen = (s == 0) ? 0f : (cumulativeLength[s - 1] + cumulativeLength[s]) * 0.5f;
            float toLen = (e == n - 1) ? totalLength : (cumulativeLength[e] + cumulativeLength[e + 1]) * 0.5f;
            if (toLen - fromLen < minRunLength) continue;

            result.Add(new PercentRange { From = fromLen / totalLength, To = toLen / totalLength });
        }
        return result;
    }
}

/// <summary>
/// 近接して並走する線路どうしの隣接解析。閾値は LocalConstants の TRACK_MERGE_* を使う。
/// 判定はすべてUnity側の描画専用で、バックエンドのデータ・経路探索には影響しない。
/// </summary>
public static class TrackAdjacency
{
    /// <summary>
    /// 完成前の仮の線路か(計画中・着工予定・着工保留中・建設中。敷設プレビューの仮パスは状態0=計画中)。
    /// 完成した線路は仮の線路に部品を譲らず、架線柱も共有しない(TrackNeighborInfo.Provisional)。
    /// </summary>
    public static bool IsProvisional(PathSummary path) => path != null
        && (path.status == Constants.STATUS_TEMPORARY || path.status == Constants.STATUS_SCHEDULED_CONSTRUCTION
            || path.status == Constants.STATUS_PENDING || path.status == Constants.STATUS_CONSTRUCTING);

    /// <summary>
    /// 隣接線路の取得デリゲート。PathManager.Awake が設定する（キャッシュ済みパスデータと
    /// セル空間インデックスへのアクセスが必要なため）。null なら結合描画は行われない
    /// （メッシュ確認用テストシーンなど）。
    /// </summary>
    public static System.Func<PathSummary, List<TrackNeighborInfo>> NeighborProvider;

    /// <summary>コリドー連結で走査する自中心線からの最大距離(m)。これより遠い候補は無視する。</summary>
    private const float MAX_SCAN_HALF_WIDTH = 40f;

    /// <summary>
    /// 最近接点の進行方向成分の許容量(m)。隣接線路の端点で射影がクランプされた場合の
    /// 誤検出（線路の端の先まで並走していると誤認する）を防ぐ。
    /// </summary>
    private const float LONGITUDINAL_TOLERANCE = 2.5f;

    /// <summary>
    /// 防音壁の物理ブロック判定（他線路の道床が壁の位置に掛かるか）で、壁ゾーンの外側に
    /// 追加する余裕(m)。道床エッジと壁がぴったり接する見た目を避ける。
    /// </summary>
    private const float WALL_BLOCK_CLEARANCE = 0.3f;

    /// <summary>
    /// 他線路の道床がプラットフォームに掛かる判定（島式結合が成立しない近接）の余裕(m)。
    /// この距離まで道床が迫った区間は platform_wall を省略し、デッキを道床の手前までクランプする。
    /// </summary>
    private const float PLATFORM_ENCROACH_CLEARANCE = 0.3f;

    // 上面を片側の高さで共有できる誤差だけを許し、道床結合の高さ許容値と区別する。
    private const float PLATFORM_JOIN_MAX_DY = 0.02f;

    /// <summary>
    /// コリドーの1メンバー（自分自身または隣接線路）。O は自中心線からの符号付き横位置。
    /// Reach はコリドーの外端とする半幅で、通常は道床の半幅。LRT停留所のホーム区間では、外端+POLE_MARGIN に
    /// 立つ架線柱がホーム外縁+PLATFORM_POLE_CLEARANCE に来るよう広げる（連結判定は Half で行う）。
    /// </summary>
    private struct Member
    {
        public float O;
        public float Half;
        public float Reach;
        public int Id;
        public bool Provisional;
    }

    /// <summary>
    /// 隣り合う2本の間の充填(道床の隙間・島のデッキ)を自分が描くか。完成した線路と仮の相手の組では仮の相手が
    /// (自分の見た目で)描き、同じ種類どうしは path_id の小さい側が描く。
    /// </summary>
    private static bool OwnsFill(int ownId, bool ownProvisional, int otherId, bool otherProvisional)
        => ownProvisional != otherProvisional ? ownProvisional : ownId < otherId;

    /// <summary>
    /// 自線路の点列に沿って隣接線路との結合コリドーを計算する。
    /// 結合区間・壁の物理ブロック区間・ホームの結合/干渉区間が1点も無い場合は null を返す
    /// （呼び出し側は従来描画にフォールバック）。
    ///
    /// 各点で「自分＋閾値内の隣接候補」を横位置順に並べ、道床エッジ間ギャップが
    /// TRACK_MERGE_MAX_EDGE_GAP 以下で連なる連結成分（自分を含むもの）をコリドーとする。
    /// チェーン連結のため3本以上の並走（複々線相当）も1つのコリドーになる。
    ///
    /// ownWallWidth は自線路の防音壁の幅（wall_width。null相当は0）。壁の物理ブロック判定
    /// （LeftWallBlocked / RightWallBlocked）の壁ゾーン算出に使う。
    ///
    /// ownPlatformLeft / ownPlatformRight は自線路のプラットフォーム張り出し幅
    /// （TrackPath.ResolvePlatformExtents で解決済み。0なら無し）。ホームがある側では:
    ///   - 道床結合（バラスト充填・壁省略・柱コリドー化のチェーン連結）は行わない
    ///     （ホームの下にギャップフィラーが張られるのを防ぐ）
    ///   - 代わりに島式ホームの結合を判定する: 互いに向き合うホームを持つほぼ平行な
    ///     駅パス同士で、道床エッジ間ギャップが両ホームの張り出し幅の合計以下なら、
    ///     デッキ（ホーム上面）を1枚に繋げて島式ホームとして描画する。
    ///     デッキの充填はpath_idの小さい側が相手の道床エッジ（＝相手の淵）まで担当し、
    ///     platform_wall は両側とも省略、架線柱は最小path_idの線路が島全幅の外側に立てる
    ///     （道床結合の柱コリドー化と同じ主線路方式）
    ///   - 島式が成立しない近接（張り出し幅の合計を超えるが道床がホームに掛かる等）では
    ///     platform_wall を省略し、デッキを相手道床の手前までクランプする
    ///
    /// ownLrtPlatformOuter はLRTの線路(431)の停留所ホーム外縁の中心線からの距離（LRTでなければ0）。
    /// ホームは停留所の位置で決まるので、どの点でもその帯に他線路の道床が掛かるかだけを記録する
    /// （Left/RightLrtPlatformBlocked。道床結合はホームの有無に関わらず行う）。
    /// </summary>
    public static TrackCorridorInfo Compute(int ownPathId, List<Vector3> ownPoints, float ownWidth, float ownWallWidth, float ownPlatformLeft, float ownPlatformRight, List<TrackNeighborInfo> neighbors, float ownLrtPlatformOuter = 0f)
        => ComputeWithShifts(ownPathId, ownPoints, ownWidth, ownWallWidth, ownPlatformLeft, ownPlatformRight, neighbors, ownLrtPlatformOuter, null, false);

    /// <summary>
    /// Compute に、自線路の点ごとの道床の外端のずれ(ownOuterShift。つなぎ目のS字。null は全長で0)を加えたもの。
    /// 自線路・隣接線路の半幅とホームの淵は、点ごとにS字のずれだけ外へ動いた位置で判定する。
    /// 出力の張り出し・デッキの外縁・充填先も、ずれを含んだ自中心線からの距離。
    /// ownProvisional は自線路が完成前の仮の線路(計画中・建設中・敷設プレビュー)か。完成した線路と仮の線路の組では、
    /// 間を埋める部品を仮の線路が描き、架線柱は共有しない(TrackNeighborInfo.Provisional)。
    /// </summary>
    public static TrackCorridorInfo ComputeWithShifts(int ownPathId, List<Vector3> ownPoints, float ownWidth, float ownWallWidth, float ownPlatformLeft, float ownPlatformRight, List<TrackNeighborInfo> neighbors, float ownLrtPlatformOuter, float[] ownOuterShift, bool ownProvisional)
    {
        if (ownPoints == null || ownPoints.Count < 2 || neighbors == null || neighbors.Count == 0) return null;
        if (ownOuterShift != null && ownOuterShift.Length != ownPoints.Count) ownOuterShift = null;

        int n = ownPoints.Count;
        float ownHalf = ownWidth * 0.5f;

        var info = new TrackCorridorInfo
        {
            Points = ownPoints,
            RightDir = new Vector3[n],
            CumulativeLength = new float[n],
            OwnHalfWidth = ownHalf,
            OwnOuterShift = ownOuterShift,
            LeftCovered = new bool[n],
            RightCovered = new bool[n],
            SuppressPoleSet = new bool[n],
            LeftWallBlocked = new bool[n],
            RightWallBlocked = new bool[n],
            LeftExtent = new float[n],
            RightExtent = new float[n],
            LeftFillTo = new float[n],
            RightFillTo = new float[n],
            LeftDeckFar = new float[n],
            RightDeckFar = new float[n],
            LeftDeckJoined = new bool[n],
            RightDeckJoined = new bool[n],
            LeftPlatformWallOmit = new bool[n],
            RightPlatformWallOmit = new bool[n],
            LeftLrtPlatformBlocked = new bool[n],
            RightLrtPlatformBlocked = new bool[n],
        };

        float cum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) cum += Vector3.Distance(ownPoints[i - 1], ownPoints[i]);
            info.CumulativeLength[i] = cum;
            float half = info.OwnHalfAt(i);
            info.LeftExtent[i] = half;
            info.RightExtent[i] = half;
            info.LeftFillTo[i] = float.NaN;
            info.RightFillTo[i] = float.NaN;
            info.LeftDeckFar[i] = ownPlatformLeft > 0f ? half + ownPlatformLeft : float.NaN;
            info.RightDeckFar[i] = ownPlatformRight > 0f ? half + ownPlatformRight : float.NaN;
        }
        info.TotalLength = cum;

        // 隣接候補ごとのXZ AABB（走査距離分マージン込み）。サンプル点ごとの全ポリライン走査を
        // 明らかに遠い候補で行わないための粗い前置フィルタ。
        var nbBounds = new Rect[neighbors.Count];
        var nbCumulative = new float[neighbors.Count][];
        for (int j = 0; j < neighbors.Count; j++)
        {
            nbBounds[j] = ComputeExpandedBoundsXZ(neighbors[j]?.Points, MAX_SCAN_HALF_WIDTH);
            nbCumulative[j] = CumulativeLengths(neighbors[j]);
        }

        float cosMaxAngle = Mathf.Cos(LocalConstants.TRACK_MERGE_MAX_ANGLE_DEG * Mathf.Deg2Rad);
        float cosMaxConvergeAngle = Mathf.Cos(LocalConstants.TRACK_MERGE_MAX_CONVERGE_ANGLE_DEG * Mathf.Deg2Rad);
        bool anyWallBlock = false;
        bool anyPlatform = false;
        bool anyLrtPlatformBlock = false;
        var members = new List<Member>(neighbors.Count + 1);

        for (int i = 0; i < n; i++)
        {
            Vector3 ownPos = ownPoints[i];
            Vector3 fwd = ownPoints[Mathf.Min(i + 1, n - 1)] - ownPoints[Mathf.Max(i - 1, 0)];
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f)
            {
                info.RightDir[i] = i > 0 ? info.RightDir[i - 1] : Vector3.right;
                continue;
            }
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            info.RightDir[i] = right;

            float ownHalfAt = info.OwnHalfAt(i);
            members.Clear();
            members.Add(new Member { O = 0f, Half = ownHalfAt, Reach = ownHalfAt, Id = ownPathId, Provisional = ownProvisional });

            // プラットフォームの結合/干渉のサンプル単位の集計（左右それぞれ）。
            // island*   ... 島式結合の相手がいるか・最寄りの相手道床エッジ・最小path_id・島全幅の外縁
            // encroach* ... 島式が成立しない近接でデッキをクランプする位置（最小値）
            bool islandLeft = false, islandRight = false;
            float islandJoinLeft = float.MaxValue, islandJoinRight = float.MaxValue;
            int islandMinIdLeft = int.MaxValue, islandMinIdRight = int.MaxValue;
            float islandOuterLeft = 0f, islandOuterRight = 0f;
            // 最寄りの島式の相手が仮の線路か(完成した線路との組かで担当が変わる)と、相手の単独のデッキの外縁(自中心線からの距離)。
            bool islandProvisionalLeft = false, islandProvisionalRight = false;
            float islandStandaloneLeft = float.MaxValue, islandStandaloneRight = float.MaxValue;
            float encroachLeft = float.MaxValue, encroachRight = float.MaxValue;

            for (int j = 0; j < neighbors.Count; j++)
            {
                TrackNeighborInfo nb = neighbors[j];
                if (nb?.Points == null || nb.Points.Count < 2) continue;
                if (!nbBounds[j].Contains(new Vector2(ownPos.x, ownPos.z))) continue;
                if (!TryClosestPointOnPolylineXZ(nb.Points, ownPos, out Vector3 cp, out Vector3 nbTan, out bool clampedAtPolylineEnd, out float nbAlong)) continue;

                // Y方向にも近い（同じ高さ層を走っている）ことを要求する。
                // 高架と地上、地上と地下トンネルのように層が違う線路は結合しない。
                if (Mathf.Abs(cp.y - ownPos.y) > LocalConstants.TRACK_MERGE_MAX_DY) continue;

                Vector3 delta = cp - ownPos;
                delta.y = 0f;
                // 端点クランプ（隣接線路がこの地点より手前で終わっている）の誤検出を除外。
                if (Mathf.Abs(Vector3.Dot(fwd, delta)) > LONGITUDINAL_TOLERANCE) continue;

                float o = Vector3.Dot(right, delta);
                if (Mathf.Abs(o) > MAX_SCAN_HALF_WIDTH) continue;

                float nbShift = NeighborShift(nb, nbCumulative[j], nbAlong);
                float nbHalf = nb.Width * 0.5f + nbShift;
                float cosAngle = Mathf.Abs(Vector3.Dot(fwd, nbTan));
                float centerDist = delta.magnitude;

                // 端点クランプの虚像: 最近接点が隣接線路の端点へのクランプで、かつ道床同士が
                // 重なる距離まで近い場合は、交差点ハルで切られた相手の端点が自線路の中心線近傍
                // （反対側＝外側のこともある）へ投影された幾何的な虚像。結合メンバー・島式結合の
                // 判定から除外する（許すと合流ノードの手前で壁・ホームの描画が数サンプル分乱れる）。
                bool endClampVirtual = clampedAtPolylineEnd && centerDist < ownHalfAt + nbHalf;

                // 防音壁の物理ブロック判定（角度不問）。壁は他線路の道床の上に立ててはならない。
                // 並走判定とは独立に、隣接線路の道床の横断面 [o-nbHalf, o+nbHalf] が自分の
                // 壁ゾーン [ownHalf, ownHalf+壁幅+余裕]（左側はミラー）に掛かる点を記録する。
                // 合流の喉元（まだ接線が揃っていない収束区間）や浅い角度の平面交差で、
                // 相手線路の本体を壁が横切るのを防ぐ。
                if (ownWallWidth > 0f)
                {
                    float zoneMin = ownHalfAt;
                    float zoneMax = ownHalfAt + ownWallWidth + WALL_BLOCK_CLEARANCE;
                    if (o + nbHalf > zoneMin && o - nbHalf < zoneMax)
                    {
                        info.RightWallBlocked[i] = true;
                        anyWallBlock = true;
                    }
                    if (-o + nbHalf > zoneMin && -o - nbHalf < zoneMax)
                    {
                        info.LeftWallBlocked[i] = true;
                        anyWallBlock = true;
                    }
                }

                // LRTの停留所ホームの帯（自道床の外〜ホーム外縁+余裕。角度不問）に他線路の道床が掛かるか。
                // 帯の内側を自道床の縁から取るのは、前後へ続く線路の端の像を拾わないため。
                if (ownLrtPlatformOuter > 0f && !endClampVirtual)
                {
                    float zoneMax = ownLrtPlatformOuter + PLATFORM_ENCROACH_CLEARANCE;
                    if (o + nbHalf > ownHalfAt && o - nbHalf < zoneMax)
                    {
                        info.RightLrtPlatformBlocked[i] = true;
                        anyLrtPlatformBlock = true;
                    }
                    if (-o + nbHalf > ownHalfAt && -o - nbHalf < zoneMax)
                    {
                        info.LeftLrtPlatformBlocked[i] = true;
                        anyLrtPlatformBlock = true;
                    }
                }

                // プラットフォームの結合/干渉判定（自分のホームがある側のみ）。
                float myPlatform = o > 0f ? ownPlatformRight : ownPlatformLeft;
                if (myPlatform > 0f)
                {
                    float absO = Mathf.Abs(o);
                    float edgeGap = absO - ownHalfAt - nbHalf;

                    // 相手のホームのうち「自分側を向いている張り出し幅」と「反対側（島の外縁側）の
                    // 張り出し幅」。相手の幾何フレームの左右を、自分が相手のどちら側にいるかで
                    // 自フレームへ読み替える（-delta = 相手の最近接点から自分への水平ベクトル）。
                    Vector3 nbRight = Vector3.Cross(Vector3.up, nbTan);
                    bool ownIsOnNbRight = Vector3.Dot(nbRight, -delta) > 0f;
                    float nbFacingPlatform = ownIsOnNbRight ? nb.PlatformRight : nb.PlatformLeft;
                    float nbOuterPlatform = ownIsOnNbRight ? nb.PlatformLeft : nb.PlatformRight;

                    // 島式結合: 互いに向き合うホームを持つほぼ平行な駅パスで、道床エッジ間
                    // ギャップが両ホームの張り出し幅の合計以下（＝デッキが届き合う距離）。
                    bool island = !endClampVirtual && cosAngle >= cosMaxAngle
                        && Mathf.Abs(cp.y - ownPos.y) <= PLATFORM_JOIN_MAX_DY
                        && nbFacingPlatform > 0f
                        && edgeGap > 0f && edgeGap <= myPlatform + nbFacingPlatform;
                    if (island)
                    {
                        // デッキの充填先は相手の淵ライン（相手の淵パーツの縁の位置と一致し、継ぎ目を相手の淵が覆う）。
                        float joinTo = absO - (nb.PlatformEdge + nbShift);
                        float outerTo = absO + nbHalf + nbOuterPlatform;
                        float standalone = absO - nbHalf - nbFacingPlatform;
                        if (o > 0f)
                        {
                            islandRight = true;
                            if (joinTo < islandJoinRight) { islandJoinRight = joinTo; islandProvisionalRight = nb.Provisional; islandStandaloneRight = standalone; }
                            if (nb.Provisional == ownProvisional && nb.PathId < islandMinIdRight) islandMinIdRight = nb.PathId;
                            if (nb.Provisional == ownProvisional && outerTo > islandOuterRight) islandOuterRight = outerTo;
                        }
                        else
                        {
                            islandLeft = true;
                            if (joinTo < islandJoinLeft) { islandJoinLeft = joinTo; islandProvisionalLeft = nb.Provisional; islandStandaloneLeft = standalone; }
                            if (nb.Provisional == ownProvisional && nb.PathId < islandMinIdLeft) islandMinIdLeft = nb.PathId;
                            if (nb.Provisional == ownProvisional && outerTo > islandOuterLeft) islandOuterLeft = outerTo;
                        }
                    }
                    else if (!endClampVirtual && edgeGap < myPlatform + PLATFORM_ENCROACH_CLEARANCE)
                    {
                        // 島式が成立しないままホーム（外縁の余裕込み）に他線路の道床が掛かる近接。
                        // 接続先の端点へクランプされた虚像は、ホームを横切る道床ではない。
                        // デッキを道床の手前までクランプする位置を記録する（角度不問）。
                        float limit = absO - nbHalf - PLATFORM_ENCROACH_CLEARANCE;
                        if (o > 0f) { if (limit < encroachRight) encroachRight = limit; }
                        else { if (limit < encroachLeft) encroachLeft = limit; }
                    }

                    // 自分のホームがある側の線路は道床結合のチェーン連結に加えない
                    // （ホームの下へのバラスト充填・柱コリドー化を防ぐ。ホーム側の結合は
                    //  上の島式判定が担い、架線柱の島全幅化もそちらで行う）。
                    continue;
                }

                // コリドーメンバー判定。ほぼ平行な並走（上り線・下り線で向きが逆でもよいので
                // 絶対値で判定）に加え、合流・分岐で収束中の線路（角度差が転回制約と同じ45°以下）も、
                // 道床エッジギャップが結合閾値以下まで近づいていればメンバーにする。
                // 角度が揃いきるのを待つと、複線どうしの合流では道床が重なり始めてから
                // ようやく結合が始まり、線路間の壁が相手の本体に被ってしまう。
                bool parallel = cosAngle >= cosMaxAngle;
                bool converging = !parallel && cosAngle >= cosMaxConvergeAngle
                    && (centerDist - nbHalf - ownHalfAt) <= LocalConstants.TRACK_MERGE_MAX_EDGE_GAP;
                if (!parallel && !converging) continue;

                // 端点クランプの虚像ガード（上記 endClampVirtual）: 合流ノードの手前で
                // 「外側に隣接線路がいる」と誤判定され、外側の防音壁が数サンプル分だけ
                // 抜け落ちる（壁の途切れ）のを防ぐ。
                if (endClampVirtual) continue;

                if (Mathf.Abs(o) < 0.01f) continue;

                // 相手のLRT停留所のホーム区間では、主線路が立てる架線柱を相手のホーム外縁の外へ出す。
                float reach = nbHalf;
                if (nb.LrtPlatformOuter > 0f && LrtPlatformParts.Contains(nb.LrtPlatformZones, nbAlong))
                    reach = Mathf.Max(reach, nb.LrtPlatformOuter + LrtRoadParts.PLATFORM_POLE_CLEARANCE - TrackPath.POLE_MARGIN);
                members.Add(new Member { O = o, Half = nbHalf, Reach = reach, Id = nb.PathId, Provisional = nb.Provisional });
            }

            // プラットフォームの結合/干渉の確定（道床結合のメンバー有無に関わらず反映する）。
            if (ownPlatformRight > 0f)
            {
                if (islandRight)
                {
                    anyPlatform = true;
                    info.RightPlatformWallOmit[i] = true;
                    ApplyIslandDeck(info.RightDeckFar, info.RightDeckJoined, i, ownPathId, ownProvisional, islandProvisionalRight,
                        islandMinIdRight, islandJoinRight, islandStandaloneRight, encroachRight);
                    // 架線柱は同じ種類(完成どうし・仮どうし)の最小path_idの線路が島全幅（相手の外縁側ホーム込み）の外側に立てる
                    //（道床結合の柱コリドー化と同じ主線路方式）。完成した線路と仮の線路では共有しない。
                    if (islandMinIdRight != int.MaxValue)
                    {
                        info.RightExtent[i] = Mathf.Max(info.RightExtent[i], islandOuterRight);
                        if (islandMinIdRight < ownPathId) info.SuppressPoleSet[i] = true;
                    }
                }
                else if (encroachRight < float.MaxValue)
                {
                    anyPlatform = true;
                    info.RightPlatformWallOmit[i] = true;
                    info.RightDeckFar[i] = Mathf.Min(info.RightDeckFar[i], encroachRight);
                }
            }
            if (ownPlatformLeft > 0f)
            {
                if (islandLeft)
                {
                    anyPlatform = true;
                    info.LeftPlatformWallOmit[i] = true;
                    ApplyIslandDeck(info.LeftDeckFar, info.LeftDeckJoined, i, ownPathId, ownProvisional, islandProvisionalLeft,
                        islandMinIdLeft, islandJoinLeft, islandStandaloneLeft, encroachLeft);
                    if (islandMinIdLeft != int.MaxValue)
                    {
                        info.LeftExtent[i] = Mathf.Max(info.LeftExtent[i], islandOuterLeft);
                        if (islandMinIdLeft < ownPathId) info.SuppressPoleSet[i] = true;
                    }
                }
                else if (encroachLeft < float.MaxValue)
                {
                    anyPlatform = true;
                    info.LeftPlatformWallOmit[i] = true;
                    info.LeftDeckFar[i] = Mathf.Min(info.LeftDeckFar[i], encroachLeft);
                }
            }

            if (members.Count <= 1) continue;

            members.Sort((a, b) => a.O.CompareTo(b.O));
            int ownIdx = -1;
            for (int m = 0; m < members.Count; m++)
            {
                if (members[m].Id == ownPathId) { ownIdx = m; break; }
            }
            if (ownIdx < 0) continue;

            // 自分を含む連結成分（道床エッジ間ギャップが閾値以下で連なる範囲）を求める
            int lo = ownIdx;
            while (lo - 1 >= 0 && (members[lo].O - members[lo].Half) - (members[lo - 1].O + members[lo - 1].Half) <= LocalConstants.TRACK_MERGE_MAX_EDGE_GAP)
            {
                lo--;
            }
            int hi = ownIdx;
            while (hi + 1 < members.Count && (members[hi + 1].O - members[hi + 1].Half) - (members[hi].O + members[hi].Half) <= LocalConstants.TRACK_MERGE_MAX_EDGE_GAP)
            {
                hi++;
            }
            if (lo == ownIdx && hi == ownIdx) continue;

            info.AnyCoverage = true;
            // 架線柱は同じ種類(完成どうし・仮どうし)のメンバーだけで共有する。完成した線路と仮の線路の間では、
            // それぞれが自分の幅で立てる(仮の相手が非表示・取り消しになっても完成側の柱が欠けない)。
            // 島式ホームの柱コリドー化（上の確定処理）が反対側で先に張り出しを広げている
            // ことがあるため、上書きではなく最大値で合成する。
            int minId = ownPathId;
            for (int m = lo; m <= hi; m++)
            {
                if (members[m].Provisional != ownProvisional) continue;
                info.LeftExtent[i] = Mathf.Max(info.LeftExtent[i], -(members[m].O - members[m].Reach));
                info.RightExtent[i] = Mathf.Max(info.RightExtent[i], members[m].O + members[m].Reach);
                if (members[m].Id < minId) minId = members[m].Id;
            }
            if (minId < ownPathId) info.SuppressPoleSet[i] = true;

            if (lo < ownIdx)
            {
                Member lm = members[ownIdx - 1];
                info.LeftCovered[i] = true;
                if (OwnsFill(ownPathId, ownProvisional, lm.Id, lm.Provisional)) info.LeftFillTo[i] = -(lm.O + lm.Half);
            }
            if (hi > ownIdx)
            {
                Member rm = members[ownIdx + 1];
                info.RightCovered[i] = true;
                if (OwnsFill(ownPathId, ownProvisional, rm.Id, rm.Provisional)) info.RightFillTo[i] = rm.O - rm.Half;
            }
        }

        return (info.AnyCoverage || anyWallBlock || anyPlatform || anyLrtPlatformBlock) ? info : null;
    }

    /// <summary>
    /// 島式結合の片側のデッキの外縁を決める。同じ種類(完成どうし・仮どうし)の相手とは path_id の小さい側が
    /// 相手の淵まで張り、もう一方は張らない。完成した線路と仮の線路の組では、完成側は単独のときの外縁のまま張り
    /// (仮の相手が消えても欠けない)、仮の側が完成側の外縁まで張って島をつなぐ。第三の線路が割り込む位置で止める。
    /// </summary>
    private static void ApplyIslandDeck(float[] deckFar, bool[] deckJoined, int i, int ownPathId, bool ownProvisional, bool partnerProvisional,
        int minSameKindId, float joinTo, float partnerStandalone, float encroach)
    {
        if (partnerProvisional != ownProvisional)
        {
            if (ownProvisional)
            {
                deckFar[i] = Mathf.Min(partnerStandalone, encroach);
                deckJoined[i] = encroach >= partnerStandalone;
            }
            else
            {
                deckFar[i] = Mathf.Min(deckFar[i], encroach);
            }
            return;
        }
        if (ownPathId < minSameKindId)
        {
            deckFar[i] = Mathf.Min(joinTo, encroach);
            deckJoined[i] = encroach >= joinTo;
        }
        else
        {
            deckFar[i] = float.NaN;
        }
    }

    /// <summary>S字のずれを持つ隣接候補の点列の3D累積距離(ずれが無ければ null)。</summary>
    private static float[] CumulativeLengths(TrackNeighborInfo nb)
    {
        if (nb?.OuterShifts == null || nb.Points == null || nb.OuterShifts.Length != nb.Points.Count) return null;
        var cumulative = new float[nb.Points.Count];
        for (int i = 1; i < cumulative.Length; i++) cumulative[i] = cumulative[i - 1] + Vector3.Distance(nb.Points[i - 1], nb.Points[i]);
        return cumulative;
    }

    /// <summary>隣接候補の、点列に沿った距離 along での道床の外端のずれ(点の間は線形補間)。</summary>
    private static float NeighborShift(TrackNeighborInfo nb, float[] cumulative, float along)
    {
        if (cumulative == null) return 0f;
        int hi = System.Array.BinarySearch(cumulative, along);
        if (hi >= 0) return nb.OuterShifts[hi];
        hi = ~hi;
        if (hi <= 0) return nb.OuterShifts[0];
        if (hi >= cumulative.Length) return nb.OuterShifts[cumulative.Length - 1];
        float span = cumulative[hi] - cumulative[hi - 1];
        float t = span > 1e-6f ? (along - cumulative[hi - 1]) / span : 0f;
        return Mathf.Lerp(nb.OuterShifts[hi - 1], nb.OuterShifts[hi], t);
    }

    /// <summary>点列のXZ AABBを margin 分拡張した Rect を返す（点列不正時は空Rect）。</summary>
    internal static Rect ComputeExpandedBoundsXZ(List<Vector3> points, float margin)
    {
        if (points == null || points.Count == 0) return new Rect(0f, 0f, -1f, -1f);
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var p in points)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }
        return new Rect(minX - margin, minZ - margin, (maxX - minX) + margin * 2f, (maxZ - minZ) + margin * 2f);
    }

    /// <summary>
    /// XZ平面上でポリラインへの最近接点を求める。closest のYは線分上の補間値
    /// （Y差の判定に使う）、tangent は最近接線分の水平方向単位ベクトル。
    /// clampedAtPolylineEnd は、最近接点がポリラインの始端・終端の端点への
    /// クランプだった（真の垂線の足が端点上またはポリラインの外にある）場合に true。
    /// 交差点ハルで切り詰められた相手の端点が虚像の隣接として採用されるのを
    /// 呼び出し側が除外するために使う。along は最近接点までの点列の3D累積距離。
    /// </summary>
    internal static bool TryClosestPointOnPolylineXZ(List<Vector3> points, Vector3 query, out Vector3 closest, out Vector3 tangent, out bool clampedAtPolylineEnd, out float along)
    {
        closest = query;
        tangent = Vector3.forward;
        clampedAtPolylineEnd = false;
        along = 0f;
        float bestSq = float.MaxValue;
        bool found = false;
        float start = 0f;

        for (int i = 0; i < points.Count - 1; i++, start += Vector3.Distance(points[i - 1], points[i]))
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];
            float abx = b.x - a.x;
            float abz = b.z - a.z;
            float lenSq = abx * abx + abz * abz;
            if (lenSq < 1e-8f) continue;

            float tRaw = ((query.x - a.x) * abx + (query.z - a.z) * abz) / lenSq;
            float t = Mathf.Clamp01(tRaw);
            Vector3 p = a + (b - a) * t;
            float dx = p.x - query.x;
            float dz = p.z - query.z;
            float dSq = dx * dx + dz * dz;
            if (dSq < bestSq)
            {
                bestSq = dSq;
                closest = p;
                Vector3 tan = b - a;
                tan.y = 0f;
                tangent = tan.sqrMagnitude > 1e-8f ? tan.normalized : Vector3.forward;
                clampedAtPolylineEnd = (i == 0 && tRaw <= 0f) || (i == points.Count - 2 && tRaw >= 1f);
                along = start + Vector3.Distance(a, b) * t;
                found = true;
            }
        }
        return found;
    }
}
