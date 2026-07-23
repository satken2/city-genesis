/// <summary>
/// 道路・交差点の各パーツの高さオフセット定数。
///
/// 道路の描画は次の2段の層構造になっている。オフセット値が生成器ごとにバラバラだと、
/// 交差点と道路の継ぎ目に段差ができたり、パーツ同士がZファイティングを起こしたりするため、
/// 高さに関わる値は必ずここの定数から組み立てること。
///
/// 【第1層: スプライン点】
///   地形追従する道路（相対Y=0）のスプライン点は「地形標高 + ROAD_SPLINE_OFFSET」に置かれる
///   （PathManagerのサンプリング）。高架・地下など相対Y≠0の道路のスプライン点は
///   データのY（地形+相対Y）そのままで、このオフセットは加算されない。
///
/// 【第2層: スプラインからのパーツオフセット】
///   各パーツは SplineMesh の offset.y でスプライン点からさらに浮かせる。
///   路面の上面 = スプライン + BODY_ABOVE_SPLINE（地表道路なら 地形+0.2）。
///
/// 交差点はこの2層を再現する形で高さを決める:
///   切断点（道路端点に代入されるスプライン点） = 基準面（地形+ROAD_SPLINE_OFFSET、
///   高架ノードならノード絶対Y）
///   交差点アスファルト = 基準面 + BODY_ABOVE_SPLINE（路面上面と同一平面。領域は互いに
///   切断線で分かれているため重なりによるZファイティングは起きない）
///   交差点の歩道コネクタ = 基準面 + SIDEWALK_ABOVE_SPLINE（通常道路の歩道下端と面一）
///
/// プレハブの高さ方向のピボット規約（この規約が崩れると定数が意味を失う）:
///   - body: ピボット＝路面の上面（厚みは下方向に伸びる）
///   - line / sidewalk / wall: ピボット＝下端（上方向に伸びる。縁石の立ち上がりはプレハブ形状）
/// </summary>
public static class RoadPartHeights
{
    /// <summary>
    /// 地形追従スプライン点の地形からの浮かせ量。地形メッシュの解像度限界による埋没を避ける。
    /// </summary>
    public const float ROAD_SPLINE_OFFSET = 0.1f;

    /// <summary>道路本体（body）のスプラインからの高さ。路面上面はこの平面に一致する（ピボット規約）。</summary>
    public const float BODY_ABOVE_SPLINE = 0.1f;

    /// <summary>区画線・センターライン・車線境界線のスプラインからの高さ。body上面の2cm上。</summary>
    public const float LINE_ABOVE_SPLINE = 0.12f;

    /// <summary>
    /// センターライン・車線境界線を側線よりさらにわずかに浮かせる量。
    /// 側線と中央の線が交差点付近で重なった場合のZファイティングを避ける。
    /// </summary>
    public const float CENTER_LINE_EXTRA = 0.005f;

    /// <summary>歩道の下端のスプラインからの高さ。body上面と同じ平面に置く。</summary>
    public const float SIDEWALK_ABOVE_SPLINE = 0.1f;

    /// <summary>防音壁・ガードレールの下端のスプラインからの高さ。</summary>
    public const float WALL_ABOVE_SPLINE = 0.1f;

    /// <summary>
    /// 軌道パーツ（track: レール2本+枕木の1軌道分）と、交差点内のレールコネクタ（rail）の
    /// 下端のスプラインからの高さ。道床（body）上面（BODY_ABOVE_SPLINE）の2cm上に置き、
    /// 道床天端とのZファイティングを回避する。
    /// プレハブのピボット規約: track / rail は「枕木の下面（道床に接する面）」がピボット。
    /// レール踏面までの立ち上がりはプレハブ形状が持つ。
    /// </summary>
    public const float TRACK_ABOVE_SPLINE = 0.12f;

    /// <summary>
    /// 架線（wire）・架線柱（pole）・架線ビーム（beam）の基準面のスプラインからの高さ。
    /// 軌道パーツと同じ面を基準にする。
    /// プレハブのピボット規約: いずれも「軌道下端と同じ基準面」がピボットで、
    /// トロリ線の高さ（レール面上約5m）や柱の立ち上がりはプレハブ形状が持つ
    /// （コードは架線高さのパラメータを持たない）。
    /// </summary>
    public const float OVERHEAD_ABOVE_SPLINE = 0.12f;

    /// <summary>
    /// 駅パスのプラットフォーム（platform）の下端のスプラインからの高さ。
    /// 道床（body）の天端と同じ平面に置く（交差点の道床ハルとも面一）。
    /// プレハブのピボット規約: platform は「設置面（下端）」がピボットで、ホーム面までの
    /// 立ち上がり（レール面上約1.1m相当）はプレハブ形状が持つ（コードはホーム高さの
    /// パラメータを持たない）。「右側用」として作り、乗降縁端は -X（軌道中心）方向を
    /// 向ける。左側はXミラーで配置される（防音壁 wall と同じ規約）。
    /// </summary>
    public const float PLATFORM_ABOVE_SPLINE = 0.1f;
}
