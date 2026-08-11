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
 * 地形の初期標高(メートル)。
 * 新規地形作成時(Rust側 `create_new_terrain_for_editor`)、および地形エディタでの
 * マップ拡張時に新しく追加されるセルの標高として使う。海面(標高0m)と区別できるように、
 * あえて負の値にしている。
 *
 * Rust側(`src-tauri/src/lib.rs` の `create_new_terrain_for_editor`)にも同じ値が
 * float16バイト列として直接埋め込まれているため、この値を変更する場合はそちらも
 * 合わせて変更すること(言語をまたぐため定数の共有はできない)。
 */
export const NEW_TERRAIN_INITIAL_ELEVATION_M = -10;

/**
 * 公共交通の新設時の運賃・目標運行間隔(分)の既定値。
 * 路線の種別は path_master の PATH_TRANSPORT バリアントそのものであり、UI 側で種別定数は持たない。
 */
export const TRANSIT_DEFAULT_FARE = 200;
export const TRANSIT_DEFAULT_HEADWAY_MIN = 10;
// 新設時の1編成あたりの車両数(cars_per_train)の既定値。
export const TRANSIT_DEFAULT_CARS_PER_TRAIN = 1;

/**
 * 公共交通路線の種別(path_variant)ごとの路線図描画色。
 * バックエンドのマスター(transit_master / path_master)に色情報の項目が無いため、UI 側で定義する。
 * resolveTransitIcon(utils/pathIcon.ts)と同様にバリアントIDをキーとする。
 * 未定義の種別は TRANSIT_DEFAULT_COLOR にフォールバックする。
 */
export const TRANSIT_VARIANT_COLOR: Record<number, string> = {
  1001: "#fab387", // バス路線 (オレンジ)
  1002: "#89b4fa", // メトロ路線 (青)
  1003: "#a6e3a1", // LRT路線 (緑)
  1004: "#cba6f7", // モノレール路線 (紫)
  1005: "#f38ba8", // 中距離鉄道路線 (赤)
  1006: "#94e2d5", // ロープウェイ路線 (水色)
};

/** 種別未定義時のフォールバック色 */
export const TRANSIT_DEFAULT_COLOR = "#f9e2af";

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
  STATUS_COLLAPSED_FIRE,
  PATH_ELECTRICITY,
  PATH_WATER,
} from "./constants";

/**
 * インフラ・パス種別（電気・水）の集合と判定ヘルパ。
 * バックエンド consts.PATH_TYPES_INFRA に対応する。constants.ts は自動生成で
 * タプル(PATH_TYPES_INFRA)を出力しないため、UI側ではここに集約する。
 * 電気網/水網の物理的分離はノードビット側で行われるため、この判定は地形非変形など
 * 「財を区別しない共通のインフラ挙動」にのみ使う。
 */
export const PATH_TYPES_INFRA: readonly number[] = [PATH_ELECTRICITY, PATH_WATER];

/** path_type がインフラ（電気・水）かを返す。 */
export function isInfraPathType(pathType: number | null | undefined): boolean {
  return pathType === PATH_ELECTRICITY || pathType === PATH_WATER;
}

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
  [STATUS_COLLAPSED_FIRE]: { bg: "#7f1d1d", text: "#ffffff" },
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
