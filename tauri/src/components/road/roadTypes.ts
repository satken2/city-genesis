/**
 * 道路(名前付き道路)のAPI応答型。
 *
 * 道路は、交差点で分かれた複数のパス(区間)を「青梅街道」のような1本の道路にまとめる単位。
 * 所属の正は区間側の road_id(パスの一覧・詳細APIにも載る)で、road_id が VALUE_NONE(-1)の
 * 区間は名もなき道。道路の記録は区間が1本も無くなった時点でバックエンドが自動で削除する。
 *
 * バックエンド: GET/POST /api/v1/roads/、GET/PATCH/DELETE /api/v1/roads/{id} ほか
 * (genesis-core app/controllers/roads.py。いずれも手書きのプレーンJSON)。
 */

/** 道路名を地図・3Dに置く位置。angle_deg はワールドXZ平面での道路の向き(atan2(dz, dx) を度で表したもの) */
export type RoadLabelPoint = {
  x: number;
  z: number;
  angle_deg: number;
};

/** 一覧 GET /api/v1/roads/ の1件。Unityのラベル・地図のラベル・所属の選択肢に使う */
export type RoadSummary = {
  road_id: number;
  /** 空文字は未命名(表示側が「道路 #id」を補う) */
  name: string;
  /** "#rrggbb" */
  color: string;
  /** 所属する区間のパスID(昇順。計画中・工事中も含む) */
  path_ids: number[];
  segment_count: number;
  /** 所属する全区間の中心線長の合計(m) */
  length_m: number;
  /** [minx, minz, maxx, maxz]。形状が取れない場合は null */
  bbox: [number, number, number, number] | null;
  label_points: RoadLabelPoint[];
};

export type RoadListResponse = {
  /** 道路の集合・名前・色・所属・区間の形状や状態が変わるたびに変わる整数。前回と同じなら再描画を省ける */
  version: number;
  roads: RoadSummary[];
};

/** つながった区間の塊。塊と塊の間は途切れ(未接続)で、キロ程は途切れをまたいで連続する */
export type RoadSection = {
  index: number;
  start_km: number;
  end_km: number;
  /** 前の塊の終端からこの塊の始端までの直線距離(m)。先頭の塊は null */
  gap_before_m: number | null;
};

/**
 * 区間の役割。
 * - main: 基準線(キロ程の軸)を構成する区間
 * - parallel: 基準線と並行する一方通行の区間(上下線が分かれた道路の反対車線)
 * - branch: 基準線から枝分かれした区間
 */
export type RoadSegmentRole = "main" | "parallel" | "branch";

/** down=キロ程が増える向き(起点から遠ざかる=下り) / up=起点へ向かう向き(上り) / both=双方向 */
export type RoadSegmentDirection = "both" | "down" | "up";

export type RoadSegment = {
  path_id: number;
  /** 区間名(橋・トンネル名など)。空文字は無名 */
  name: string;
  path_type: number;
  path_variant: number;
  lanes: number;
  status: number;
  section_index: number;
  role: RoadSegmentRole;
  from_km: number;
  to_km: number;
  /**
   * branch の区間が基準線に取り付くキロ程(路線図で枝を描き出す位置)。基準線か並行区間につながる端点の位置で、
   * つながる端点が無ければ基準線に近い方の端を基準線へ投影した位置。main / parallel は null
   */
  attach_km: number | null;
  length_m: number;
  direction: RoadSegmentDirection;
  zone_id: number;
  zone_name: string | null;
  /** VEHICLE_* を文字列にしたキー → 1日あたりの通過トリップ数。値が0より大きい歩行者・道路車両だけ */
  traffic: Record<string, number>;
  /** 道路交通(自動車類)の1日あたりの交通量 */
  road_traffic: number;
  /** 道路交通の1日あたりの容量。0以下なら null */
  capacity: number | null;
  /** 混雑度 = road_traffic / capacity(1で頭打ちにしない)。容量が無ければ null */
  vc: number | null;
  /** 制限速度(km/h) */
  speed_limit: number | null;
  /** 形状から決まる通過速度(km/h) */
  base_speed: number | null;
  /** 自動車の自由走行の所要時間(分)。稼働中でない・通れない区間は null */
  travel_time_free_min: number | null;
  /** 自動車の混雑込みの所要時間(分) */
  travel_time_min: number | null;
  durability: number;
  noise: number;
  air_pollution: number;
  parked_cars: number;
  street_parking_capacity: number;
  /** 当エポックに交通事故が起きた */
  accident: boolean;
  under_maintenance: boolean;
  rebuilding: boolean;
  structure: "tunnel" | "bridge" | null;
};

/** 交差点で交わる相手。同じ道路が交差点を貫いている場合は1件にまとめ side="both" */
export type RoadCrossing = {
  path_id: number;
  path_type: number;
  road_id: number | null;
  road_name: string | null;
  road_color: string | null;
  /** 相手の区間名(空文字は無名) */
  path_name: string;
  /** キロ程の進む向きに対して、相手がどちら側へ伸びるか */
  side: "left" | "right" | "both";
};

export type RoadEvent =
  | { kind: "terminal"; km: number; section_index: number; node_id: number | null; position: "start" | "end" }
  | { kind: "crossing"; km: number; node_id: number; crossings: RoadCrossing[] }
  | { kind: "interchange"; km: number; node_id: number; interchange_id: number; name: string; interchange_kind: number }
  | { kind: "stop"; km: number; node_id: number; name: string | null; line_agent_id: number };

export type RoadStats = {
  segment_count: number;
  length_m: number;
  /** 稼働中の区間の延長(m) */
  active_length_m: number;
  /** 計画中・発注済み・工事中の区間の延長(m) */
  planned_length_m: number;
  lanes_composition: { lanes: number; length_m: number }[];
  /** 平均断面交通量(台/日) = 稼働中の基準線区間の road_traffic を区間長で重み付けした平均 */
  avg_daily_traffic: number | null;
  /** 走行台キロ(台km/日) = Σ road_traffic × 区間長(km)。稼働中の基準線・並行区間 */
  vehicle_km: number;
  max_vc: { value: number; path_id: number; from_km: number; to_km: number } | null;
  avg_vc: number | null;
  /** 混雑度0.8以上の稼働中区間の延長(m) */
  congested_length_m: number;
  /** 基準線を起点から終点まで走る所要時間(分)。自由走行 / 混雑込み。稼働中でない区間を含むと null */
  travel_time_free_min: number | null;
  travel_time_min: number | null;
  travel_speed_free_kmh: number | null;
  travel_speed_kmh: number | null;
  /** 渋滞損失(台時/日) = Σ road_traffic × (混雑込み − 自由走行の所要時間) / 60 */
  delay_vehicle_hours: number;
  avg_durability: number | null;
  low_durability_count: number;
  accident_segment_count: number;
  speed_limit_min: number | null;
  speed_limit_max: number | null;
};

/** 詳細 GET /api/v1/roads/{id} */
export type RoadDetail = RoadSummary & {
  version: number;
  /** 起点の基準位置 [x, z]。起点と終点を入れ替えると更新される。未設定は null(自動で決まる) */
  origin: [number, number] | null;
  stats: RoadStats;
  sections: RoadSection[];
  segments: RoadSegment[];
  events: RoadEvent[];
  /** 通過する地区(キロ程順に初出の順) */
  zones: { zone_id: number; name: string }[];
  /** 所属区間を走る公共交通路線 */
  transit_lines: { line_agent_id: number; name: string; color: string }[];
};

export type RoadMutationResponse = { road: RoadSummary };
export type RoadMembersResponse = { road: RoadSummary | null; deleted: boolean };
export type RoadDeleteResponse = { deleted: true; cleared_path_ids: number[] };
/** bbox は候補の区間の範囲 [minx, minz, maxx, maxz](候補が無ければ null)。古いバックエンドは返さない */
export type RoadTraceResponse = { path_ids: number[]; bbox?: [number, number, number, number] | null };
export type RoadExtensionCandidate = { road_id: number; name: string; color: string; point_index: number };
export type RoadExtensionCandidatesResponse = { candidates: RoadExtensionCandidate[] };
