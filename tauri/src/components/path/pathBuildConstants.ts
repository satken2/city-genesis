/**
 * 道路種別・色・バリアントタイル用のローカル定数
 *
 * 道路種別(path_type)の数値と表示名・色のマッピングを定義する。
 * 色はバリアントタイルのアイコン背景色として使用する。
 */

/** 道路種別の数値 → 表示名マッピング */
export const PATH_TYPE_LABELS: Record<number, string> = {
  0: "仮想道路",
  1: "幹線道路",
  2: "生活道路",
  3: "歩行者道路",
  4: "線路",
  5: "公共交通",
};

/** 道路種別ごとのタイルアイコン背景色 */
export const PATH_TYPE_COLORS: Record<number, string> = {
  0: "#585b70",
  1: "#fab387",
  2: "#89b4fa",
  3: "#a6e3a1",
  4: "#f9e2af",
  5: "#cba6f7",
};

/** ポイントの役割 */
export type PointRole = "start" | "waypoint" | "end";

/** 建設モード */
export type BuildMode = "IDLE" | "PLACING" | "EDITING";

/** 描画モード */
export type CurveMode = "straight" | "curved";

/** 建設中のポイント情報 */
export type BuildPoint = {
  x: number;
  z: number;
  role: PointRole;
};

/** ドラッグ中の対象情報 */
export type DragTarget =
  | { type: "point"; index: number }
  | { type: "control"; which: "start" | "end" };

/** 制御点情報 */
export type ControlPoint = {
  x: number;
  z: number;
} | null;

/** レイヤー設定 */
export type LayerConfig = {
  id: string;
  url: string;
  key: string;
  color: string;
  type: "polygon" | "path" | "node";
  fill?: boolean;
  lineWidth?: number;
};
