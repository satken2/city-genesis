/**
 * UI側で独自に定義する定数
 * constants.ts は自動生成ファイルなので編集せず、UI固有の定数はここに定義する。
 */

/**
 * `tauri build` でバンドルされた本番 UI（配布用 exe に相当）で動作しているとき true。
 * `tauri dev`（Vite 開発サーバ経由の実行）では false。
 * 開発時は Unity との Named Pipe が無くてもバックエンド API だけでトップメニュー／HUDの表示を検証できるようにする。
 */
export const IS_PACKAGED_PRODUCTION_UI = import.meta.env.PROD;

/**
 * 公共交通の新設時の運賃・目標運行間隔(分)の既定値。
 * 路線の種別は path_master の PATH_TRANSPORT バリアントそのものであり、UI 側で種別定数は持たない。
 */
export const TRANSIT_DEFAULT_FARE = 200;
export const TRANSIT_DEFAULT_HEADWAY_MIN = 10;
// 新設時の1編成あたりの車両数(cars_per_train)の既定値。
export const TRANSIT_DEFAULT_CARS_PER_TRAIN = 1;

/**
 * 公共交通路線の種別(path_variant)ごとの既定描画色。
 * 路線の色は路線ごとの `line_color`(バックエンドが作成時にパレットから割り当て、プレイヤーが
 * 変更できる)が正で、ここは line_color が空の路線に対するフォールバックにだけ使う。
 * resolveTransitIcon(utils/pathIcon.ts)と同様にバリアントIDをキーとする。
 * 未定義の種別は TRANSIT_DEFAULT_COLOR にフォールバックする。
 */
export const TRANSIT_VARIANT_COLOR: Record<number, string> = {
  1001: "#fab387", // バス路線 (オレンジ)
  1002: "#89b4fa", // メトロ路線 (青)
  1003: "#a6e3a1", // LRT路線 (緑)
  1004: "#cba6f7", // モノレール路線 (紫)
  1005: "#f38ba8", // 中距離鉄道路線 (赤)
};

/** 種別未定義時のフォールバック色 */
export const TRANSIT_DEFAULT_COLOR = "#f9e2af";

/**
 * 路線カラーの色見本(路線詳細の設定タブ)。バックエンドが新設路線に巡回的に割り当てる
 * パレット(crud_transit.TRANSIT_LINE_COLOR_PALETTE)と同じ12色で、暗い UI と 3D 地表の
 * 両方で見分けやすい実在の地下鉄路線図風の高彩度色。任意色はカラーコード入力で指定できる。
 */
export const TRANSIT_LINE_COLOR_PRESETS: readonly string[] = [
  "#e5171f", // 赤
  "#0079c2", // 青
  "#00a650", // 緑
  "#f39700", // 橙
  "#8f76d6", // 紫
  "#e85298", // 桃
  "#00b2b2", // 青緑
  "#ffd400", // 黄
  "#9c5e31", // 茶
  "#1f2f8c", // 紺
  "#b5b5ac", // 銀
  "#6cbb5a", // 黄緑
];

/**
 * 軌道建設パネルの簡易モード(駅タブ)で、ホーム長を「両数」で指定するための換算値。
 * マスターに車両長の項目が無いため UI 側で持つ。1両20m + 停止位置の余裕を両端に取る。
 * バックエンドはホーム長を m でしか扱わないので、この換算は表示と入力のためだけのもの。
 */
export const STATION_CAR_LENGTH_M = 20;
export const STATION_PLATFORM_MARGIN_M = 10;
export const STATION_MIN_CAR_COUNT = 1;
export const STATION_MAX_CAR_COUNT = 20;
export const STATION_DEFAULT_CAR_COUNT = 4;

/** 両数からホーム長(m)を求める。 */
export function stationPlatformLength(carCount: number): number {
  return carCount * STATION_CAR_LENGTH_M + STATION_PLATFORM_MARGIN_M;
}

/**
 * 複合パス(combined_paths)用のUI専用の擬似パスタイプ。
 * バックエンドの path_type としては存在せず、PathConstructionPanel の
 * タイププルダウンで複合パスカテゴリを表すためだけに使われる。
 * 実際に作成されるパスは各コンポーネントの path_variant のタイプになる。
 */
export const PATH_TYPE_COMBINED: number = 1000;

/** 路線の種別(path_variant)から路線図の描画色を引く */
export function resolveTransitColor(pathVariant: number): string {
  return TRANSIT_VARIANT_COLOR[pathVariant] ?? TRANSIT_DEFAULT_COLOR;
}

import {
  STATUS_TEMPORARY,
  STATUS_ACTIVE,
  STATUS_PAUSED,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
  STATUS_SCHEDULED_DEMOLITION,
  STATUS_DEMOLISHING,
  STATUS_COMPLETED,
  STATUS_CANCELED,
  STATUS_EMPTY,
  STATUS_COLLAPSED,
  PATH_STREET,
  PATH_MOTORWAY,
  PATH_WALKWAY,
  PATH_TRACK,
  PATH_MONORAIL,
  PATH_CONVEYOR,
  PATH_TRANSPORT,
  PATH_ELECTRICITY,
  PATH_WATER,
  PATH_PIPELINE,
  PRODUCT_WATER_SUPPLY,
  PRODUCT_ELECTRICITY,
  PRODUCT_HIGH_ELECTRICITY,
  PRODUCT_HIGH_WATER,
} from "./constants";

/**
 * コックピットの公共サービス充足度メーター(hud/PublicServiceGauges)に並べる製品と、その並び順。
 * バックエンドの GET /api/v1/city/public-service-supply は regulated 製品を全件返すため、
 * 「常時見えるべき公共サービスはどれか」というUI側の判断をここに持つ。
 * 棒を増やしたいときはこの配列に製品IDを足すだけでよい(表示名の i18n キーは
 * publicServiceGauge.services.<製品ID> なので、そちらも ja/en 両方に足すこと)。
 */
export const COCKPIT_PUBLIC_SERVICE_PRODUCTS: readonly number[] = [
  PRODUCT_ELECTRICITY,
  PRODUCT_WATER_SUPPLY,
  PRODUCT_HIGH_ELECTRICITY,
  PRODUCT_HIGH_WATER,
];

/**
 * インフラ・パス種別（送電・導水・パイプライン）の集合と判定ヘルパ。
 * バックエンド consts.PATH_TYPES_INFRA に対応する。constants.ts は自動生成で
 * タプル(PATH_TYPES_INFRA)を出力しないため、UI側ではここに集約する。
 * 財ごとの網の物理的分離はノードビット側で行われるため、この判定は地形非変形など
 * 「財を区別しない共通のインフラ挙動」にのみ使う。
 */
export const PATH_TYPES_INFRA: readonly number[] = [PATH_ELECTRICITY, PATH_WATER, PATH_PIPELINE];

/** path_type がインフラかを返す。 */
export function isInfraPathType(pathType: number | null | undefined): boolean {
  return pathType != null && PATH_TYPES_INFRA.includes(pathType);
}

/**
 * 形状から最小曲線半径・最大勾配を算出するパス種別。
 * バックエンド geo_path_alignment.SPEED_PATH_TYPES に対応する。これ以外の種別の値は
 * 算出されない既定値(0・直線)のままなので、パス詳細に表示しない。
 */
export const PATH_TYPES_WITH_ALIGNMENT: readonly number[] = [PATH_STREET, PATH_MOTORWAY, PATH_TRACK, PATH_MONORAIL];

/**
 * パス詳細で通過速度(base_speed)を表示するパス種別。自動車が走る道路だけ。
 * 線路・モノレールは制限速度を規制で変えない前提(既定300km/h)で通過速度がその天井に張り付き、
 * 実際の列車の速度は曲線半径と路線種別の最高速度で決まるため、表示しても意味を持たない。
 */
export const PATH_TYPES_WITH_BASE_SPEED: readonly number[] = [PATH_STREET, PATH_MOTORWAY];

/**
 * パス詳細で制限速度(標識と個別指定)を扱うパス種別。通過速度を持つ種別に加えて歩道を含める。
 * 歩行者の速度には効かないが、歩行者専用道路(LRT付き)を走るLRTがこの値を上限に走るため。
 * 線路・モノレールはプレイヤーが制限速度を設定する意味が無いので含めない(規制ウィンドウも同じ)。
 * 人員搬送は設備の巡航速度で動き、公共交通の区間パス・インフラ・駅舎は通過時間を持たないので含めない。
 */
export const PATH_TYPES_WITH_SPEED_LIMIT: readonly number[] = [...PATH_TYPES_WITH_BASE_SPEED, PATH_WALKWAY];

/**
 * 利用率(utilization)を算出するパス種別。バックエンド PathUtilizationPhase に対応する。
 * これ以外の種別の利用率は常に0なので、パス詳細に表示しない。
 */
export const PATH_TYPES_WITH_UTILIZATION: readonly number[] = [
  PATH_STREET,
  PATH_MOTORWAY,
  PATH_TRACK,
  PATH_MONORAIL,
  PATH_CONVEYOR,
  PATH_TRANSPORT,
];

/**
 * ステータスごとの表示スタイルを定義する
 * bg は背景色、text は文字色
 * showProgress が true のステータスは、進捗率に応じたプログレスバーを表示する
 */
export const STATUS_STYLE_MAP: Record<
  number,
  { bg: string; text: string; showProgress?: boolean }
> = {
  [STATUS_TEMPORARY]: { bg: "#ffffff", text: "#1a1a1a" },
  [STATUS_ACTIVE]: { bg: "#22c55e", text: "#ffffff" },
  [STATUS_PAUSED]: { bg: "#9ca3af", text: "#ffffff" },
  [STATUS_SCHEDULED_CONSTRUCTION]: { bg: "#ffffff", text: "#1a1a1a" },
  [STATUS_CONSTRUCTING]: {
    bg: "#eab308",
    text: "#1a1a1a",
    showProgress: true,
  },
  [STATUS_SCHEDULED_DEMOLITION]: { bg: "#a16207", text: "#ffffff" },
  [STATUS_DEMOLISHING]: { bg: "#a16207", text: "#ffffff" },
  [STATUS_COMPLETED]: { bg: "#3b82f6", text: "#ffffff" },
  [STATUS_CANCELED]: { bg: "#6b7280", text: "#ffffff" },
  [STATUS_EMPTY]: { bg: "#6b7280", text: "#ffffff" },
  [STATUS_COLLAPSED]: { bg: "#ef4444", text: "#ffffff" },
};

/**
 * 建物断面図におけるスペースカテゴリ別の表示色パレット
 * カテゴリの登場順にインデックスでローテーションして使用する
 */
export const SPACE_CATEGORY_COLORS: readonly string[] = [
  "#4A90D9",
  "#E8A838",
  "#50C878",
  "#E85D75",
  "#9B59B6",
  "#1ABC9C",
  "#F39C12",
  "#34495E",
];
