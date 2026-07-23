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
}

/// <summary>
/// 自線路の描画点列に沿った、近接線路との「結合コリドー」情報。
///
/// ゲームの内部データに「複線線路」という単位は存在せず、複線は一方通行の単線線路を
/// 2本並べて表現される。そのままでは単線が2本並ぶだけの見た目になるため、
/// XZ平面・Y方向ともに近接して並走する線路どうしを1つのコリドー（廊下）とみなし、
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
        var result = new List<PercentRange>();
        bool[] covered = leftSide ? LeftCovered : RightCovered;
        bool[] blocked = leftSide ? LeftWallBlocked : RightWallBlocked;
        int n = covered.Length;
        if (TotalLength <= 0f)
        {
            result.Add(new PercentRange { From = 0.0, To = 1.0 });
            return result;
        }

        int i = 0;
        while (i < n)
        {
            if (covered[i] || blocked[i]) { i++; continue; }
            int s = i;
            while (i < n && !covered[i] && !blocked[i]) i++;
            int e = i - 1;

            float fromLen = (s == 0) ? 0f : (CumulativeLength[s - 1] + CumulativeLength[s]) * 0.5f;
            float toLen = (e == n - 1) ? TotalLength : (CumulativeLength[e] + CumulativeLength[e + 1]) * 0.5f;
            if (toLen - fromLen < minRunLength) continue;

            result.Add(new PercentRange { From = fromLen / TotalLength, To = toLen / TotalLength });
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

    /// <summary>コリドーの1メンバー（自分自身または隣接線路）。O は自中心線からの符号付き横位置。</summary>
    private struct Member
    {
        public float O;
        public float Half;
        public int Id;
    }

    /// <summary>
    /// 自線路の点列に沿って隣接線路との結合コリドーを計算する。
    /// 結合区間も壁の物理ブロック区間も1点も無い場合は null を返す
    /// （呼び出し側は従来描画にフォールバック）。
    ///
    /// 各点で「自分＋閾値内の隣接候補」を横位置順に並べ、道床エッジ間ギャップが
    /// TRACK_MERGE_MAX_EDGE_GAP 以下で連なる連結成分（自分を含むもの）をコリドーとする。
    /// チェーン連結のため3本以上の並走（複々線相当）も1つのコリドーになる。
    ///
    /// ownWallWidth は自線路の防音壁の幅（wall_width。null相当は0）。壁の物理ブロック判定
    /// （LeftWallBlocked / RightWallBlocked）の壁ゾーン算出に使う。
    /// </summary>
    public static TrackCorridorInfo Compute(int ownPathId, List<Vector3> ownPoints, float ownWidth, float ownWallWidth, List<TrackNeighborInfo> neighbors)
    {
        if (ownPoints == null || ownPoints.Count < 2 || neighbors == null || neighbors.Count == 0) return null;

        int n = ownPoints.Count;
        float ownHalf = ownWidth * 0.5f;

        var info = new TrackCorridorInfo
        {
            Points = ownPoints,
            RightDir = new Vector3[n],
            CumulativeLength = new float[n],
            OwnHalfWidth = ownHalf,
            LeftCovered = new bool[n],
            RightCovered = new bool[n],
            SuppressPoleSet = new bool[n],
            LeftWallBlocked = new bool[n],
            RightWallBlocked = new bool[n],
            LeftExtent = new float[n],
            RightExtent = new float[n],
            LeftFillTo = new float[n],
            RightFillTo = new float[n],
        };

        float cum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) cum += Vector3.Distance(ownPoints[i - 1], ownPoints[i]);
            info.CumulativeLength[i] = cum;
            info.LeftExtent[i] = ownHalf;
            info.RightExtent[i] = ownHalf;
            info.LeftFillTo[i] = float.NaN;
            info.RightFillTo[i] = float.NaN;
        }
        info.TotalLength = cum;

        // 隣接候補ごとのXZ AABB（走査距離分マージン込み）。サンプル点ごとの全ポリライン走査を
        // 明らかに遠い候補で行わないための粗い前置フィルタ。
        var nbBounds = new Rect[neighbors.Count];
        for (int j = 0; j < neighbors.Count; j++)
        {
            nbBounds[j] = ComputeExpandedBoundsXZ(neighbors[j]?.Points, MAX_SCAN_HALF_WIDTH);
        }

        float cosMaxAngle = Mathf.Cos(LocalConstants.TRACK_MERGE_MAX_ANGLE_DEG * Mathf.Deg2Rad);
        float cosMaxConvergeAngle = Mathf.Cos(LocalConstants.TRACK_MERGE_MAX_CONVERGE_ANGLE_DEG * Mathf.Deg2Rad);
        bool anyWallBlock = false;
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

            members.Clear();
            members.Add(new Member { O = 0f, Half = ownHalf, Id = ownPathId });

            for (int j = 0; j < neighbors.Count; j++)
            {
                TrackNeighborInfo nb = neighbors[j];
                if (nb?.Points == null || nb.Points.Count < 2) continue;
                if (!nbBounds[j].Contains(new Vector2(ownPos.x, ownPos.z))) continue;
                if (!TryClosestPointOnPolylineXZ(nb.Points, ownPos, out Vector3 cp, out Vector3 nbTan, out bool clampedAtPolylineEnd)) continue;

                // Y方向にも近い（同じ高さ層を走っている）ことを要求する。
                // 高架と地上、地上と地下トンネルのように層が違う線路は結合しない。
                if (Mathf.Abs(cp.y - ownPos.y) > LocalConstants.TRACK_MERGE_MAX_DY) continue;

                Vector3 delta = cp - ownPos;
                delta.y = 0f;
                // 端点クランプ（隣接線路がこの地点より手前で終わっている）の誤検出を除外。
                if (Mathf.Abs(Vector3.Dot(fwd, delta)) > LONGITUDINAL_TOLERANCE) continue;

                float o = Vector3.Dot(right, delta);
                if (Mathf.Abs(o) > MAX_SCAN_HALF_WIDTH) continue;

                float nbHalf = nb.Width * 0.5f;

                // 防音壁の物理ブロック判定（角度不問）。壁は他線路の道床の上に立ててはならない。
                // 並走判定とは独立に、隣接線路の道床の横断面 [o-nbHalf, o+nbHalf] が自分の
                // 壁ゾーン [ownHalf, ownHalf+壁幅+余裕]（左側はミラー）に掛かる点を記録する。
                // 合流の喉元（まだ接線が揃っていない収束区間）や浅い角度の平面交差で、
                // 相手線路の本体を壁が横切るのを防ぐ。
                if (ownWallWidth > 0f)
                {
                    float zoneMin = ownHalf;
                    float zoneMax = ownHalf + ownWallWidth + WALL_BLOCK_CLEARANCE;
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

                // コリドーメンバー判定。ほぼ平行な並走（上り線・下り線で向きが逆でもよいので
                // 絶対値で判定）に加え、合流・分岐で収束中の線路（角度差が転回制約と同じ45°以下）も、
                // 道床エッジギャップが結合閾値以下まで近づいていればメンバーにする。
                // 角度が揃いきるのを待つと、複線どうしの合流では道床が重なり始めてから
                // ようやく結合が始まり、線路間の壁が相手の本体に被ってしまう。
                float cosAngle = Mathf.Abs(Vector3.Dot(fwd, nbTan));
                float centerDist = delta.magnitude;
                bool parallel = cosAngle >= cosMaxAngle;
                bool converging = !parallel && cosAngle >= cosMaxConvergeAngle
                    && (centerDist - nbHalf - ownHalf) <= LocalConstants.TRACK_MERGE_MAX_EDGE_GAP;
                if (!parallel && !converging) continue;

                // 端点クランプの虚像ガード: 最近接点が隣接線路の端点へのクランプで、かつ道床同士が
                // 重なる距離まで近い場合は、交差点ハルで切られた相手の端点が自線路の中心線近傍
                // （反対側＝外側のこともある）へ投影された幾何的な虚像なのでメンバーにしない。
                // これを許すと合流ノードの手前で「外側に隣接線路がいる」と誤判定され、
                // 外側の防音壁が数サンプル分だけ抜け落ちる（壁の途切れ）。
                if (clampedAtPolylineEnd && centerDist < ownHalf + nbHalf) continue;

                if (Mathf.Abs(o) < 0.01f) continue;

                members.Add(new Member { O = o, Half = nbHalf, Id = nb.PathId });
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
            info.LeftExtent[i] = -(members[lo].O - members[lo].Half);
            info.RightExtent[i] = members[hi].O + members[hi].Half;

            int minId = ownPathId;
            for (int m = lo; m <= hi; m++)
            {
                if (members[m].Id < minId) minId = members[m].Id;
            }
            info.SuppressPoleSet[i] = minId < ownPathId;

            if (lo < ownIdx)
            {
                Member lm = members[ownIdx - 1];
                info.LeftCovered[i] = true;
                if (ownPathId < lm.Id) info.LeftFillTo[i] = -(lm.O + lm.Half);
            }
            if (hi > ownIdx)
            {
                Member rm = members[ownIdx + 1];
                info.RightCovered[i] = true;
                if (ownPathId < rm.Id) info.RightFillTo[i] = rm.O - rm.Half;
            }
        }

        return (info.AnyCoverage || anyWallBlock) ? info : null;
    }

    /// <summary>点列のXZ AABBを margin 分拡張した Rect を返す（点列不正時は空Rect）。</summary>
    private static Rect ComputeExpandedBoundsXZ(List<Vector3> points, float margin)
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
    /// クランプだった（＝真の垂線の足がポリラインの外にある）場合に true。
    /// 交差点ハルで切り詰められた相手の端点が虚像の隣接として採用されるのを
    /// 呼び出し側が除外するために使う。
    /// </summary>
    private static bool TryClosestPointOnPolylineXZ(List<Vector3> points, Vector3 query, out Vector3 closest, out Vector3 tangent, out bool clampedAtPolylineEnd)
    {
        closest = query;
        tangent = Vector3.forward;
        clampedAtPolylineEnd = false;
        float bestSq = float.MaxValue;
        bool found = false;

        for (int i = 0; i < points.Count - 1; i++)
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
                clampedAtPolylineEnd = (i == 0 && tRaw < 0f) || (i == points.Count - 2 && tRaw > 1f);
                found = true;
            }
        }
        return found;
    }
}
