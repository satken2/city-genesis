using UnityEngine;

/// <summary>
/// 各カテゴリ専用のビジュアルモード定数。
/// VisualModeHandler はオブジェクトタイプごとにモードを保持するため、
/// 値の重複は問題ありません。
/// </summary>
public static class VisualModePath
{
    public const int UTILIZATION = 100;   // 利用率 0..1 -> 白→赤
    public const int SPEED = 101;         // 通過速度(PathSummary.base_speed。カーブ・勾配・交差点での加減速込み)0..100km/h -> 赤→緑
    public const int PEDESTRIAN = 102;    // 人流 0..100 -> グレー→青
    public const int DURABILITY = 103;    // 耐久性 0..1 -> 赤→緑
    public const int ZONE_COLOR = 104;    // 所属地区色分け (道路を地区ごとの色で塗る。色はバックエンドの zone.color)
}

public static class VisualModeParcel
{
    public const int LAND_PRICE = 200;      // 地価 0..10000 -> 白→赤
    public const int FILTER_HOUSE = 201;    // 一戸建て
    public const int FILTER_APARTMENT = 202;// アパート
    public const int FILTER_COMMERCIAL = 203;// 商業
    public const int FILTER_OFFICE = 204;   // オフィス
    public const int FILTER_FACTORY = 205;  // 工場
    public const int FILTER_OTHER = 206;    // その他
    public const int FILTER_FARM = 207;     // 農地
    public const int FILTER_MINE = 208;     // 鉱山
    public const int FILTER_PORT = 209;     // 港湾
    public const int ZONE_COLOR = 210;      // 所属地区色分け (区画を地区ごとの色で塗る)
    public const int NOISE = 211;           // 騒音 0..1 -> 薄黄→濃橙
    public const int AIR_POLLUTION = 212;   // 大気汚染 0..1 -> 薄緑灰→濃紫
    public const int CRIME = 213;           // 治安の悪さ 0..1 -> 薄赤→濃赤
    public const int SUNSHINE = 214;        // 日照の悪さ(影時間割合) 0..1 -> 薄黄→濃青灰
}

public static class VisualModeBuilding
{
    public const int POPULATION = 300;   // 人口 0..100 -> 白→緑
    public const int RENT = 301;         // 家賃 0..10000 -> 緑→赤
    public const int DURABILITY = 302;   // 耐久性 0..1 -> 赤→緑
    public const int SPACE_OTHER = 303;     // その他スペース
    public const int SPACE_RESIDENCE = 304; // 住居スペース
    public const int SPACE_RETAIL = 305;    // 小売店舗スペース
    public const int SPACE_FACTORY = 306;   // 工場スペース
    public const int SPACE_OFFICE = 307;    // オフィススペース
    public const int SPACE_FARM = 308;      // 農場スペース
    public const int ZONE_COLOR = 309;      // 所属地区色分け (建物を地区ごとの色で塗る)
}

public static class VisualModeTerrain
{
    public const int TRANSPARENT = 400;  // 透過表示
    public const int ELEVATION = 401;    // 標高による色分け
}

