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
///   - line / sidewalk / wall / center: ピボット＝下端（上方向に伸びる。縁石の立ち上がりはプレハブ形状）
///   路面高を含む旧モデル系列の body / line / sidewalk は生成用メッシュで路面原点に揃える。
///   歩道・ラインの補正には、採用したパーツと同系列の body 上面Yを使う。
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
    /// モノレールの軌道パーツ（track / rail。軌道桁）の天端の、軌道パーツの原点（スプライン + TRACK_ABOVE_SPLINE）からの高さ。
    /// 桁の天端はスプライン+1.2m、下端はスプライン-0.3mで、相対高さ0mの線路でも桁が地面に埋まらない。
    /// 車両は原点（走行タイヤの接地面）をこの面に置き、桁天端+0.3mの床面がホーム面（PLATFORM_SURFACE_ABOVE_SPLINE）に揃う。
    /// 鉄道のレール踏面と同じく、天端までの立ち上がりはプレハブ形状が持つ。
    /// </summary>
    public const float MONORAIL_BEAM_TOP_ABOVE_TRACK = 1.08f;

    /// <summary>
    /// 架線（wire）・架線柱（pole）・架線ビーム（beam）の基準面のスプラインからの高さ。
    /// 軌道パーツと同じ面を基準にする。
    /// プレハブのピボット規約: いずれも「軌道下端と同じ基準面」がピボットで、
    /// トロリ線の高さ（レール面上約5m）や柱の立ち上がりはプレハブ形状が持つ
    /// （コードは架線高さのパラメータを持たない）。
    /// </summary>
    public const float OVERHEAD_ABOVE_SPLINE = 0.12f;

    /// <summary>
    /// 一方通行線路の進行方向矢印（TrackDirectionArrows）の面のスプラインからの高さ。
    /// レール踏面（軌道基準面＋プレハブ形状の立ち上がり約0.2m）より上に浮かせ、
    /// 上空視点の編集モードでレールに隠れず視認できるようにする。
    /// </summary>
    public const float DIRECTION_ARROW_ABOVE_SPLINE = 0.45f;

    /// <summary>
    /// 駅パスのプラットフォームプレハブ（platform / platform_wall）の設置面（原点）の
    /// スプラインからの高さ。プレハブのモデリング規約は**原点＝線路中心線・線路面**
    /// （スプライン中心・スプライン基準面と同一平面）で、縁・外縁への横位置と
    /// ホーム面までの立ち上がりはプレハブ形状が持つ（TrackPath.CreatePlatformSide 参照）。
    /// 地形の微起伏に埋まる余裕として、下端は原点より少し下（-0.5m程度）まで造形しておく。
    /// </summary>
    public const float PLATFORM_BASE_ABOVE_SPLINE = 0.0f;

    /// <summary>
    /// 駅舎パスの連絡橋（body）の設置面（原点）のスプラインからの高さ。
    /// プレハブのモデリング規約は**原点＝地面（スプライン基準面）**で、
    /// デッキの地上高・橋脚の立ち上がりはすべてプレハブ形状が持つ
    /// （架線柱・プラットフォームと同じ考え方。コードは桁の高さを持たない）。
    /// </summary>
    public const float STATION_BRIDGE_BASE_ABOVE_SPLINE = 0.0f;

    /// <summary>
    /// 駅パスのホーム面（プラットフォーム上面の公称高さ）のスプラインからの高さ。
    /// レール踏面（軌道基準面＋プレハブ形状の立ち上がり約0.2m）から約1.2mに相当する。
    /// デッキ（ホーム上面。コードが動的生成する水平面）はこの面から PLATFORM_DECK_SINK
    /// （TrackPath）だけ沈めて張られる。
    /// **淵（platform）のホーム面の上面造形はプレハブ内で原点からこの定数と同じ高さ（1.5m）に
    /// 造形する規約**。ずれるとデッキと淵の上面が食い違う。
    /// </summary>
    public const float PLATFORM_SURFACE_ABOVE_SPLINE = 1.5f;
}
