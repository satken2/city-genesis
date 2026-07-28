using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.VisualBasic;
using UnityEngine;

/// <summary>
/// ゲーム全体で使用する定数を定義するクラス
/// </summary>
public static class LocalConstants
{
    public static readonly Dictionary<int, Color> SPACE_TYPE_COLOR_MAP = new Dictionary<int, Color>
    {
        { Constants.SPACE_RESIDENCE, new Color(0.25f, 0.8f, 0.25f) },
        { Constants.SPACE_RETAIL, new Color(0.25f, 0.5f, 0.95f) },
        { Constants.SPACE_FACTORY, new Color(0.95f, 0.85f, 0.2f) },
        { Constants.SPACE_OFFICE, new Color(0.6f, 0.4f, 0.9f) },
        { Constants.SPACE_OTHER, new Color(0.7f, 0.7f, 0.7f) },
        { Constants.SPACE_FARM, new Color(0.4f, 0.8f, 0.1f) },
    };
    /// <summary>
    /// 1階あたりの高さ(m)のフォールバック値。
    /// マスターデータの height_per_floor が取得できない場合にのみ使用する。
    /// ワールドは 1 Unityユニット = 1m なので、マスターの標準値(4.0)に合わせている。
    /// </summary>
    public const float FLOOR_HEIGHT = 4f;

    /// <summary>
    /// 線路（PATH_TRACK/PATH_MONORAIL）のノードでの最大転回偏向角（度。直進=0°、折り返し=180°）。
    /// 交差点内のレールコネクタは、偏向角がこの値以下の通行可能な線路ペアにのみ描画される。
    /// バックエンドの parameters.yaml の RAIL_MAX_TURN_ANGLE_DEG（公共交通の下地経路探索の
    /// 転回制約）と同じ値を保つこと。値がずれると「レールが敷かれているのに走れない
    /// （またはその逆）」という見た目と経路探索の不一致が起きる。
    /// プレイヤーが parameters.yaml を書き換えた場合にずれるのは既知の制限。
    /// </summary>
    public const float RAIL_MAX_TURN_ANGLE_DEG = 45.0f;

    // ------------------------------------------------------------------
    // 近接線路の結合描画（複線見え化）
    // 内部データに「複線」の単位は無く、複線は一方通行の単線線路2本で表現される。
    // 近接して並走する線路は道床・防音壁・架線柱・架線ビームを結合して描画する
    // （TrackAdjacency / TrackPath。描画専用でバックエンドには影響しない）。
    // ------------------------------------------------------------------

    /// <summary>
    /// 結合とみなす道床エッジ間の最大XZギャップ(m)。
    /// 隣り合う線路の「道床の縁どうし」の隙間がこの値以下なら同一コリドーとして連結する
    /// （中心線間距離ではない点に注意。幅の異なるバリアントどうしでも同じ感覚で繋がる）。
    /// </summary>
    public const float TRACK_MERGE_MAX_EDGE_GAP = 4.0f;

    /// <summary>
    /// 結合とみなす最大Y差(m)。描画点列（地形追従済み）のY座標で比較するため、
    /// 高架と地上・地上と地下のように高さ層が異なる線路は結合されない。
    /// </summary>
    public const float TRACK_MERGE_MAX_DY = 1.0f;

    /// <summary>
    /// 結合とみなす接線方向の最大角度差(度)。上り線・下り線で敷設方向が逆でも
    /// 結合できるよう、判定は接線の内積の絶対値で行う。
    /// </summary>
    public const float TRACK_MERGE_MAX_ANGLE_DEG = 20.0f;

    /// <summary>
    /// 合流・分岐で収束中の線路を結合とみなす最大角度差(度)。
    /// 道床エッジギャップが TRACK_MERGE_MAX_EDGE_GAP 以下まで近づいている場合に限り、
    /// TRACK_MERGE_MAX_ANGLE_DEG を超えていてもこの角度までは同一コリドーとして結合する。
    /// 接線が揃いきる(20°以下になる)のを待つと、複線どうしの合流では道床が重なり始めてから
    /// ようやく路盤結合が始まり、線路間の防音壁が相手の線路本体に被ってしまうため。
    /// 値はノードの転回制約 RAIL_MAX_TURN_ANGLE_DEG（合流として成立し得る最大の進入角）に
    /// 合わせている。
    /// </summary>
    public const float TRACK_MERGE_MAX_CONVERGE_ANGLE_DEG = 45.0f;

    // ------------------------------------------------------------------
    // 工事中(STATUS_CONSTRUCTING)の3D表示
    // 建物: 敷地を覆う土マウンド + 足場リング(scaffold-wall/corner) + クレーン(crane-base/mid/top)
    // パス: 経路を覆う土ストリップ + 高架の柱のみ + トンネル坑口のみ
    // ------------------------------------------------------------------

    /// <summary>クレーンを立てる最小論理階数。これ未満の低層建物の工事現場にはタワークレーンを置かない。</summary>
    public const int CONSTRUCTION_CRANE_MIN_FLOORS = 4;

    /// <summary>クレーンを現在の足場高さ(builtFloors)より何段高く伸ばすか。実際のタワークレーンが建物より高く立つ表現。</summary>
    public const int CONSTRUCTION_CRANE_EXTRA_LEVELS = 2;

    /// <summary>土マウンドの基本厚み(m)。敷地・道路の土は地表からこの高さだけ盛り上がる。</summary>
    public const float CONSTRUCTION_DIRT_THICKNESS = 0.4f;

    /// <summary>土マウンド上面のノイズ凹凸の振幅(m)。ノイズは多オクターブfbm（ConstructionMeshUtil.DirtNoise）。</summary>
    public const float CONSTRUCTION_DIRT_BUMP_AMPLITUDE = 0.35f;

    /// <summary>土ノイズの基本オクターブのワールド周波数(1/m)。座標×この値をノイズ入力に使う（波長 約6.7m）。上位オクターブはこの2.7倍・6.1倍。</summary>
    public const float CONSTRUCTION_DIRT_NOISE_SCALE = 0.15f;

    /// <summary>土マウンド上面の細分化で許容する最大辺長(m)。これより長い辺は中点分割され、凹凸用の内部頂点が作られる。</summary>
    public const float CONSTRUCTION_DIRT_MAX_EDGE = 3f;

    /// <summary>地面パネル(floor)の地形追従細分化で許容する最大辺長(m)。これより長い辺は中点分割され、区画内部の起伏を拾う内部頂点が作られる。平面斜面では線形補間が厳密なので、この値を細かくしすぎても品質は上がらず頂点数だけ増える。</summary>
    public const float SITE_FLOOR_MAX_EDGE = 6f;

    /// <summary>土マウンド外周の凹凸フェード距離(m)。境界からこの距離以内は凹凸を減衰させ、外周頂点では0にする(隣接区画との隙間防止)。</summary>
    public const float CONSTRUCTION_DIRT_EDGE_FADE = 1.5f;

    /// <summary>工事中パスの土ストリップに道路幅へ加算する余白幅(m)。</summary>
    public const float CONSTRUCTION_PATH_DIRT_EXTRA_WIDTH = 1.0f;

    /// <summary>
    /// 全建物共通の「標準パーツ」のprefixフォルダ名。
    /// scaffold/craneパーツはバリアント(a{variant})→建物タイプ(b{bd_type})→このフォルダ
    /// (Models/Buildings/common/)の順にフォールバックする。common は外観バリアント閾値の
    /// 概念を持たないため suffix フォルダを挟まず直接置く。
    /// </summary>
    public const string CONSTRUCTION_COMMON_PREFIX = "common";

    public const bool UNITY_LOG_BUILDING = true; // BuildingMeshBuilder 等
    public const bool UNITY_LOG_ROAD = false;     // RoadPath 等
    public const bool UNITY_LOG_LOADER = false;   // CellDataLoader / 各Manager のロード進行ログ
    public const bool UNITY_LOG_TIMING = false;   // [TIMING] 計測ログ
}

/// <summary>
/// LocalConstants.UNITY_LOG_* と組み合わせて使う Debug.Log ラッパー。
/// </summary>
public static class UnityLog
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void If(bool enabled, object message)
    {
        if (enabled) Debug.Log(message);
    }

    /// <summary>無効時はコールバックが呼ばれない（重い補間を避けたい場合）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void If(bool enabled, Func<object> message)
    {
        if (enabled) Debug.Log(message());
    }
}
