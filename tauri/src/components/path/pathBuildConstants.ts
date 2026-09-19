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
  /** 所属アンカー直下の地盤に対する相対高さ。未受信時だけアンカー高さで補完する。 */
  y?: number;
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

/** 敷設エディタのフェーズ(`edit_path:update_shape` の `phase`)。Unity RoadPlanManager と同じ3段 */
export type PathEditorPhase = "placing" | "editing" | "inactive";

/**
 * `edit_path:update_shape` の `alignment`。Unity がローカル推定した線形指標
 * (3Dの赤線・区間ラベルと同じ計算)を約5m間隔に間引いたもの。線形評価の対象外の種別では省略される。
 */
export type PathAlignmentSyncPayload = {
  /** 規格の最小曲線半径(m)。0=制限なし */
  radius_limit: number;
  /** 規格の最大勾配(%)。0=制限なし */
  grade_limit: number;
  /** 全線の最小曲線半径(m)。直線のみなら null */
  min_curve_radius: number | null;
  /** 全線の最大勾配(%) */
  max_grade: number;
  /** 中心線サンプル [x, z, 曲線半径(m。直線は null), 勾配(%), 始点からの距離(m), 路面の絶対標高, 地盤の絶対標高] */
  samples: Array<[number, number, number | null, number, number, number, number]>;
  /** 原因別の連続違反区間の最悪値(3Dの区間ラベルと同じ) */
  labels: Array<{ curve: boolean; x: number; z: number; value: number; limit: number }>;
  /** アンカーと同順の [始点からの距離(m), アンカー直下の地盤標高]。縦断面ストリップの横位置と基準高さ */
  nodes?: Array<[number, number]>;
};

/**
 * `edit_path:update_shape` のペイロード。座標は [x, 地面相対の高さ, z]。
 * 制御点の相対高さは所属アンカー直下の地盤基準(Unity `ControlToRelativeList`)。
 */
export type PathShapeUpdatePayload = {
  /** 編集対象の既存パスID(0=新規)。名前は歴史的経緯 */
  unsent_paths_count: number;
  path_type: number;
  is_curved: boolean;
  follow_terrain?: number;
  /** 右クリックで形状確定(Placing→Editing)した通知のときだけ true */
  is_finalized?: boolean;
  can_confirm?: boolean;
  selected_points?: number[][];
  start_control?: number[];
  end_control?: number[];
  embankment_heights?: number[];
  waypoint_controls?: Array<{ control_in?: number[] | null; control_out?: number[] | null }>;
  phase?: PathEditorPhase;
  /** 3D側で選択中のノード添字。-1=未選択 */
  selected_index?: number;
  /** 複線の各線の横オフセット(m)。単線は空 */
  lateral_offsets?: number[];
  /** バリアントが標高編集可能(高架・地下)か。縦断面ストリップの表示条件 */
  height_editable?: boolean;
  /** 相対高さの下限(m。マスター min_height。負なら地下可) */
  min_height?: number;
  alignment?: PathAlignmentSyncPayload;
  route_proposal?: RouteProposalShape;
};

export type RouteProposalShape = {
  revision: string;
  points: number[][];
  controls_in: number[][];
  controls_out: number[][];
  embankments: number[];
  generated: number[];
  can_undo: boolean;
  enabled: boolean;
  height_editable: boolean;
  path_variant: number;
  follow_terrain: number;
  components: Array<{
    path_variant: number;
    lateral_offset: number;
    height_offset: number;
    travel_direction: number;
  }>;
  platform_side: number;
  align_station_tracks: boolean;
  exclude_path_id: number | null;
};
