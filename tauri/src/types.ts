export type EntityRef = {
  type?: string;
  id?: string;
};

export type WindowActionConfig = {
  id: string;
  label: string;
};

export type FetchConfig = {
  method: string;
  path_template: string;
};

export type WindowDefinition = {
  title: string;
  width: number;
  height: number;
  resizable: boolean;
  policy: "single" | "multiple" | "single_per_entity";
  entity_type?: string;
  fetch?: FetchConfig;
  actions: WindowActionConfig[];
  html_path?: string;
  /** 本文を描画せず空の領域だけにする（モック用） */
  empty_shell?: boolean;
};

export type WindowContext = {
  label: string;
  instance_key: string;
  window_type: string;
  title: string;
  definition: WindowDefinition;
  entity?: EntityRef;
  params: Record<string, unknown> | null;
  api_base_url: string;
  /**
   * ウィンドウが「開かれた」世代番号。Rust側が非表示ウィンドウを再表示・転用するたびに増える。
   * WebView2はウィンドウ再オープン時に破棄されず再利用されるため、App.tsxの再マウントキーに
   * この値を含めることで「開き直し=パネル状態のリセット」を保証する。
   */
  open_seq: number;
};

export type QueryState =
  | { status: "idle" }
  | { status: "loading" }
  | { status: "success"; url: string; data: unknown }
  | { status: "error"; url: string; message: string };

/**
 * API /api/v1/agents/{id} のレスポンスに含まれる経済主体データ
 * すべてのtypeで共通するフィールドと、type固有のフィールド(optional)を定義する
 */
export type AgentData = {
  agent_id: number;
  name: string;
  type: number;
  variant: number;
  status: number;
  /** 現在のステータスになった理由(REASON_*)。0(REASON_NONE)は通常遷移 */
  status_reason?: number;
  alert?: number;
  status_from: number;
  reserves: number;
  revenues: number[];
  expenses: number[];
  public_facility: boolean;
  zone_id: number;
  zone_name: string;
  building_agent_id: number;
  building_agent_name: string;
  is_bd: boolean;
  bd_type: number;
  construction_progress: number;
  created_date?: number;
  parcel_id?: number;
  /** 立地する土地区画が買収交渉中か(区画側の under_acquisition をバックエンドが解決した値) */
  under_acquisition?: boolean;
  attraction?: number;
  durability?: number;
  floors?: number;
  height?: number;
  floor_area?: number;
  base_area?: number;
  volume_ratio?: number;
  under_maintenance?: boolean;
  wage?: number;
  quota?: number;
  total_labor?: number;
  required_skill?: number;
  family_type?: number;
  employees?: AgentSummary[];
  members?: CitizenMember[];
  tenants?: AgentSummary[];
  students?: AgentSummary[];
  rent?: number;
  capacity?: Record<string, number>;
  occupied?: Record<string, number>;
  demand_amount?: Record<string, number>;
  supply_amount?: number;
  supply_product_id?: number;
  supply_price?: number;
  supply_production?: number;
  supply_sold_amount?: number[];
  profit_rates?: number[];
  utilization?: number[];
  satis_nutrition?: number;
  satis_healthcare?: number;
  satis_entertainment?: number;
  satis_residential?: number;
  discretionary_income?: number;
  env_score?: number;
  qol_score?: number;
  has_automobile?: boolean;
  education_type?: Record<string, number>;
  /**
   * スキル別の在籍生徒数(スキルID→人数)。教育施設(TYPE_EDUCATION)のみ。
   * endpoints.yaml(自動生成)に未収載のためバックエンドが手動注入する。
   */
  enrolled_by_skill?: Record<string, number>;
  /** 総定員(延床面積由来)。教育施設(TYPE_EDUCATION)のみ手動注入される */
  max_students?: number;
  is_evicted?: boolean;
  realestate_amount?: number;
  user_operations?: unknown[];
  /**
   * 消費後に手元へ残っている在庫(製品ID→量)。調達で増え、生産の原材料消費などで
   * 減るため「獲得量」ではない点に注意(工業原料はエポックをまたいで持ち越される)。
   */
  remain_amount?: Record<string, number>;
  /**
   * 当エポックの獲得量(製品ID→量)。市場調達(自家供給含む)・インフラ配送・
   * 車両配送・天然資源分配の合算で、消費で減らないグロス入荷量。
   * エポック先頭で全財リセットされるため「前エポックにどれだけ手に入れたか」を表す。
   * 「獲得量」表示とインフラ接続セクションの受領量表示はこちらを使う。
   */
  received_amount?: Record<string, number>;
  utilization_target?: number;
  /**
   * 放棄フラグ。true=プレイヤーがこの公共建物の維持を放棄した状態。
   * 放棄された建物は自動修繕の対象外となり、老朽化するに任せる。放棄できるのは
   * 建物(is_bd=true)の公共施設のみ。放棄した時点で稼働中なら稼働停止になる。
   */
  abandoned?: boolean;
  /**
   * インフラ網への接続端子ノードID(接続点)。0 は接続端子を持たない施設。
   * 走行網接続端子(depot_node_id)と同一値の場合は共用接続ノードを指す。
   */
  infra_node_id?: number;
  /**
   * 走行網(公共交通)への接続端子ノードID(路線接続点)。0 は接続端子を持たない施設。
   * 車両基地などが持ち、電化した鉄道車両基地では infra_node_id と同一ノードを指す。
   */
  depot_node_id?: number;
};

/**
 * 経済主体の簡易参照情報(従業員・入居者などの一覧表示用)
 * バックエンドの referrer メソッドが返す {id, name} 形式に対応する
 * (types/generated.ts の RefItem と同じ形状)
 */
export type AgentSummary = {
  id: number;
  name: string | null;
};

/**
 * 世帯構成員の情報(世帯詳細パネルのメンバー一覧表示用)
 * バックエンドから id と name のペアで提供される
 */
export type CitizenMember = {
  id: number;
  name: string;
};

/**
 * API /api/v1/citizens/{id} のレスポンスに含まれる市民データ
 */
export type CitizenData = {
  citizen_id: number;
  name: string;
  citizen_type: number;
  age: number;
  gender: number;
  household_agent_id: number;
  household_agent_name: string;
  employer_agent_id: number;
  employer_agent_name: string;
  school_agent_id: number;
  school_agent_name: string;
  skills: Record<string, number>;
  education_skill: number;
  potential_criminality: number;
  commute_time: number;
};

/**
 * /api/v1/citizens/{id} のレスポンス全体
 */
export type CitizenResponse = {
  citizen: CitizenData;
};

/**
 * /api/v1/agents/{id} のレスポンス全体
 */
export type AgentResponse = {
  economic_agent: AgentData;
};

export type ZoneData = {
  zone_id: number;
  name: string;
  polygon: number[][];
  area: number;
  center_point: number[];
  /** true=自動管理地区(システムが統合しうる) / false=確定地区(ユーザー確定済み) */
  auto_managed: boolean;
  allowed_bd_mask: number;
  limit_coverage_ratio: number | null;
  limit_volume_ratio: number | null;
  limit_height: number | null;
  zone_average_prices: Record<string, number>;
  average_wage: number[];
  average_rent: number[];
  average_land_price: number[];
  zone_population: number[];
  daytime_population: number[];
  zone_unemployment_rate: number[];
  space_demands: Record<string, number>;
  space_supplies: Record<string, number>;
  zone_supply: Record<string, number>;
  zone_demand: Record<string, number>;
};

/**
 * 区画サイズ不足で建設できない入居待ち需要(スロット飢餓の立地不能レポート)の1件分。
 * ConstructionPhase が毎エポック再構築する揮発値のため、ロード直後は空になる。
 */
export type ZoneSlotStarvation = {
  space_type: number;
  variant: number;
  /** 入居待ちテナントの業種名(マスターの display_name) */
  tenant_name: string;
  /** 1棟あたりで必要な床面積(m2) */
  required: number;
  /** 地区内の建築パターンで実現できる1棟あたり最大容量(m2) */
  max_capacity: number;
  /** 入居待ちテナント数 */
  count: number;
};

/**
 * API /api/v1/zones/{id} のレスポンス全体
 */
export type ZoneResponse = {
  zone: ZoneData;
  /** GET /zones/{id} のみが返す付帯情報(一覧系レスポンスには含まれない) */
  slot_starvation?: ZoneSlotStarvation[];
};

/**
 * API /api/v1/parcels/{id} のレスポンス(土地区画データ)
 * single_parcel スキーマに基づくフィールドを定義する
 */
export type ParcelData = {
  parcel_id: number;
  parcel_type: number;
  status: number;
  polygon: number[][];
  center_point: number[];
  usage_id: number;
  zone_id: number;
  zone_name: string | null;
  resource_id: Record<string, number>;
  name_resource?: Record<string, string | null>;
  front_path_id: number;
  front_path_name: string | null;
  facade_edge_index: number;
  owner_agent_id: number;
  owner_agent_name: string | null;
  land_price: number;
  restriction_override: boolean;
  /** 道路建設などのために市政府と買収交渉中か。成立するまで区画は民間所有のまま */
  under_acquisition: boolean;
  allowed_bd_mask: number;
  limit_coverage_ratio: number | null;
  limit_volume_ratio: number | null;
  limit_height: number | null;
  area: number;
  altitude: number;
  /**
   * 区画の直下（浅い地下）または上空を横断するパスがあるか（0/1）。
   * 1 の区画には民間建物がスポーンしない（公共施設は手動設置可能）。
   * バックエンドの endpoints.yaml 再生成前のレスポンスには含まれないため optional。
   */
  has_crossing_path?: number;
  user_operations: unknown[];
};

/**
 * 表示名のみを持つマスターデータの基本エントリ
 * 多くのマスターエンドポイントはこの構造を返す
 */
export type MasterNameEntry = {
  display_name: string;
};

/**
 * 建物種別マスター (bd_types)
 */
export type BdTypeEntry = MasterNameEntry;

/**
 * スキルマスター (skills)
 */
export type SkillEntry = MasterNameEntry;

/**
 * 車両種別マスター (vehicle_types)
 * category は交通手段のカテゴリ（0:徒歩, 1:道路, 2:鉄道）
 */
export type VehicleTypeEntry = {
  display_name: string;
  category: number;
};

/**
 * スペースカテゴリマスター (space_categories)
 */
export type SpaceCategoryEntry = MasterNameEntry;

/**
 * 製品種別マスター (product_types)
 * エントリ4はnull（外部性財は種別として表示名を持たない）
 */
export type ProductTypeEntry = MasterNameEntry | null;

/**
 * 土地区画種別マスター (parcel_types)
 */
export type ParcelTypeEntry = MasterNameEntry;

/**
 * 経路種別マスター (path_types)
 */
export type PathTypeEntry = MasterNameEntry;

/**
 * 天然資源種別マスター (resource_types)
 * product_id は採取される製品のID(資源詳細パネルのアイコン解決等で使用)
 */
export type ResourceTypeEntry = {
  display_name: string;
  product_id: number;
};

/**
 * 地形プロップ種別マスター (prop_types)
 * 森・岩・茂みなどの装飾オブジェクトの種別ごとの定義。
 * prefab はグラフィック(Unity)側で読み込む 3Dモデルのパス。未指定の場合は
 * Unity 側が種別ID(prop_type)から解決する(Resources/Models/Props/{id})。
 */
export type PropMasterEntry = {
  display_name: string;
  prefab?: string;
};

/**
 * 経済主体種別マスター (agent_types)
 */
export type AgentTypeEntry = {
  display_name: string;
  description: string;
};

/**
 * 経済主体マスター (agents)
 * variantごとの詳細定義。typeとbuildingフラグによって存在するフィールドが異なる。
 *
 * - type=0 (地区): 共通フィールドのみ
 * - type=1,2 (市政府,学校): required_skill等の運営パラメータを持つ
 * - type=3 (世帯): 共通フィールドのみ
 * - type=4,5 (輸出入): 投資・需要パラメータを持つ
 * - type=10 (民間施設): supply, materials, 集計パラメータ等を持つ
 *   - building=true の場合: height_per_floor, space_limits等も持つ
 * - type=20 (建物定義): max_floors, min_floors, 建物形状パラメータを持つ
 */
export type AgentMasterEntry = {
  display_name: string;
  type: number;
  building: boolean;
  description: string;
  space_type: number;
  /** 教育施設(type=2)の高等教育フラグ。true=大学(受け入れ枠を編集可能) */
  is_higher_education?: boolean;
  required_skill?: number;
  max_skill?: number;
  skilled_people?: number;
  skill_multiplier?: number;
  investment_factor?: number;
  space_need?: number;
  space_min?: number;
  space_max?: number;
  labor_need?: number;
  labor_min?: number;
  labor_max?: number;
  max_traffic_sales_bonus?: Record<string, number>;
  max_cluster_bonus?: number;
  max_group_cluster_bonus?: number;
  agglomeration_limit?: number;
  cluster_radius?: number;
  supply?: number;
  materials?: Record<string, number>;
  investments?: Record<string, number>;
  cluster_group?: number;
  height_per_floor?: number;
  shared_building?: boolean;
  space_limits?: Record<string, number>;
  bd_type?: number;
  building_shape?: number;
  cost_ratio?: number;
  min_space?: number;
  max_space?: number;
  max_floors?: number;
  min_floors?: number;
  // 余力倍率。正値の施設は「平滑化した直面需要 × 余力倍率 × 稼働率目標」を生産目標とする
  // 公共余力方式で、稼働率目標スライダーの表示対象になる。未設定/0 は通常の需要追従計画。
  production_headroom?: number;
  // 底面形状の可変可否。false のとき底面は default_width × default_height の矩形で固定される
  // （建ぺい率や区画形状による底面生成を行わない）。未設定は可変(従来挙動)。
  resizable?: boolean;
  // 直接配置モードの初期底面（横幅）、および resizable=false の固定底面の横幅(m)。
  default_width?: number;
  // 直接配置モードの初期底面（縦幅）、および resizable=false の固定底面の縦幅(m)。未設定は正方形。
  default_height?: number;
  // 建物メッシュの生成方式(constants.ts の MODEL_* 定数)。MODEL_MASHUP(7)の建物は
  // 複数のサブ建物(subbuildings マスター)で構成され、詳細パネルから配置エディタを開ける。
  model_type?: number;
};

/**
 * サブ建物マスター (subbuildings)
 * マッシュアップ建物(MODEL_MASHUP)を構成する棟(校舎・体育館など)ごとの定義。
 * agent_variant が所属先の施設バリアントを指す。
 * width / height は底面寸法(m)で、未設定の棟はセル1つ分(MASHUP_CELL_SIZE 四方)として扱う。
 */
export type SubBuildingMasterEntry = {
  display_name: string;
  agent_variant: number;
  /** 施設の顔となる主棟かどうか。配置ランダム生成時に前面(接道辺)へ必ず1棟置かれる */
  primary?: boolean;
  /** 配置ランダム生成時に必ず1棟だけ置かれる単独棟かどうか(primaryの次に優先。空きが無ければ置かれない) */
  sole?: boolean;
  model_prefix?: string;
  /** 底面の幅(セル数単位。1単位=MASHUP_CELL_SIZE m。未定義は1セル) */
  width?: number;
  /** 底面の奥行き(同上) */
  height?: number;
  min_floors?: number;
  max_floors?: number;
  height_per_floor?: number;
};

/**
 * 製品マスター (products)
 */
export type ProductMasterEntry = {
  display_name: string;
  name: string;
  unit: string;
  type: number;
  regulated: boolean;
  trade: boolean;
  self_sustain: number;
  transport_amount: number;
  price_elasticity: number;
  demand_tier: number;
  demand_min: number;
  demand_max: number;
  demand_weight: number;
  demand_atom: boolean;
};

/**
 * 経路マスター (paths)
 * sidewalk_width, side_width, tree_spacing は未設定の場合にnullになる
 */
export type PathMasterEntry = {
  streetlight_type_allowed: boolean;
  default_streetlight_type: number;
  fence_type_allowed: boolean;
  default_fence_type: number;
  street_tree_type_allowed: boolean;
  default_street_tree_type: number;
  utility_mode_allowed: boolean;
  default_utility_mode: number;

  display_name: string;
  description: string;
  sidewalk_width: number | null;
  side_width: number | null;
  tree_spacing: number | null;
  lanes: number;
  type: number;
  width: number;
  capacity_base: number;
  /** バリアントのデフォルト制限速度 (km/h)。master.yaml 側で定義される */
  default_speed_limit: number;
  /** 標高が編集可能かどうか。master.yaml の height_editable に対応する */
  height_editable: boolean;
  /**
   * 地形追従のバリアント既定。master.yaml の follow_terrain に対応する(未定義は true=追従)。
   * パス個別の follow_terrain が未設定のときのデフォルトとして使う。
   */
  follow_terrain?: boolean;
  /**
   * 許容最小高さ(m)。master.yaml の min_height に対応する。
   * 負の値を持つバリアント(地下道路・地下鉄用線路など)だけが地下(高さ<0)に建設できる。
   * 未定義は 0(地下禁止)扱い。
   */
  min_height?: number | null;
  /** 幹線道路かどうか。master.yaml の trunk に対応する(都市マップの幹線道路レイヤー等で使用) */
  trunk?: boolean;
  /**
   * メッシュ生成器の種別。PathMeshFactory がこの値で RoadPath / TrackPath / なし を切り替える。
   * "default" → RoadPath, "track" → TrackPath, "none" → メッシュ生成なし。
   * 未定義は "default" 扱い。
   */
  model_type?: string;
  /**
   * カーブ（曲線）敷設を許可するか。false の場合、UI の直線/曲線トグルが非表示になり直線のみになる。
   * 未定義は true（カーブ可）扱い。
   */
  curve_allowed?: boolean;
  /**
   * 駅パスのプラットフォーム幅(m)。master.yaml の platform_width に対応する。
   * 正の値を持つバリアント(駅)だけが建設時にホーム設置側(platform_side)を指定できる。
   * 未定義・null はホームなし(通常の道路・線路)。
   */
  platform_width?: number | null;
  /**
   * 軌道(線路・モノレール・ロープウェイ)の構造種別。master.yaml の track_type に対応する。
   * consts.py の TRACK_* と一致する数値(TRACK_NORMAL=通常 / TRACK_BRIDGE=橋梁 / TRACK_STATION=駅)。
   * 建設パネルの軌道カテゴリが「通常/橋梁/駅」ボタンでこの値を絞り込む。
   * 未定義は TRACK_NORMAL 扱い(軌道以外のバリアントでは参照しない)。
   */
  track_type?: number;
  /**
   * このバリアントが流せるインフラ財の製品IDリスト。master.yaml の infra_products に対応する。
   * 電化軌道は電力財(PRODUCT_HIGH_ELECTRICITY=401)を含む。建設パネルの軌道カテゴリの
   * 電化トグルは、この配列に PRODUCT_HIGH_ELECTRICITY を含むバリアントに絞り込む。
   * 未定義・空配列はインフラ財配送網に参加しない(非電化軌道・一般道路など)。
   */
  infra_products?: number[];
  /**
   * 駅舎パス(type=PATH_STATION_HOUSE)の両終端に置かれる専有エリア(建屋)の寸法(m)と
   * 横方向オフセット(m)。house_width は線と直交方向、house_depth は線に沿う方向で、
   * 矩形は終端から外側へ house_depth ぶん広がる(内側の辺が終端の上に乗る)。
   * house_offset は矩形の横ずれで、終端の外向き方向を向いて右が正。
   * master.yaml の house_width / house_depth / house_offset に対応する
   * (駅舎以外のバリアントでは未定義)。
   */
  house_width?: number | null;
  house_depth?: number | null;
  house_offset?: number | null;
};

/**
 * 複合パスのコンポーネント(1本の並走パス)定義
 * - path_variant: path_master のバリアントID
 * - lateral_offset: 中心線からの横方向オフセット(m)。敷設方向(始点→終点)に向かって右が正
 * - height_offset: 中心線からの高さ方向オフセット(m)
 * - travel_direction: 通行方向。0=双方向 / 1=順方向(始点→終点) / 2=逆方向(終点→始点)
 */
export type CombinedPathComponent = {
  path_variant: number;
  lateral_offset: number;
  height_offset: number;
  travel_direction: number;
  /**
   * 駅パスのホーム設置側(PLATFORM_SIDE_OUTSIDE/INSIDE/BOTH)。
   * 複線駅プリセット(相対式=外側×2、島式=内側×2)のコンポーネントで使う。
   * 未定義なら送信せずバックエンドの既定値(外側)になる。
   */
  platform_side?: number;
};

/**
 * 複合パスマスター (combined_paths)
 * path_master の複数バリアントを1本の中心線から同時に敷設するプリセット(複線線路など)。
 * 敷設時にフロントエンドが中心線を各コンポーネントのオフセットで展開して一括作成する。
 * 複合パス自体はバックエンドのデータとして保存されず、展開後の個別パスのみが存在する。
 */
export type CombinedPathEntry = {
  display_name: string;
  description?: string;
  components: CombinedPathComponent[];
};

/**
 * 公共交通マスター (transit)
 * 公共交通の路線種別(バス路線・メトロ路線など)ごとの定義。
 * - transit_path_id: その種別の区間パスとして使う path_master バリアント(路線種別のキー)。
 *   路線作成 API の path_variant にはこの値を渡す
 * - stop_path_variants: 停留所ノードを設置できる下地パスのバリアント一覧
 * - route_path_variants: 路線の車両が走行(経由)できる下地パスのバリアント一覧
 */
export type TransitMasterEntry = {
  display_name: string;
  description: string;
  transit_path_id: number;
  stop_path_variants: number[];
  route_path_variants: number[];
};

/**
 * 予算カテゴリマスター (budget_categories)
 */
export type BudgetCategoryEntry = MasterNameEntry;

/**
 * nearby_zones の各要素。隣接パスが所属している地区の zone_id と zone_name を持つ。
 */
export type NearbyZoneEntry = {
  zone_id: number;
  zone_name: string;
};

/**
 * API /api/v1/paths/{id} のレスポンス(パスデータ)
 * single_path スキーマに基づくフィールドを定義する
 *
 * max_speed・utilization・construction_progress は method: other のため
 * バックエンドの実装によって値が含まれない場合がある(null を許容)
 */
export type PathData = {
  streetlight_type: number;
  fence_type: number;
  street_tree_type: number;
  utility_mode: number;

  path_id: number;
  name: string;
  path_type: number;
  path_variant: number;
  status: number;
  alert?: number;
  under_maintenance: boolean | null;
  zone_id: number;
  zone_name: string | null;
  nearby_zones?: NearbyZoneEntry[] | null;
  noise: number | null;
  air_pollution: number | null;
  max_speed: number | null;
  override_max_speed: boolean;
  /** 地形追従の個別設定(-1=未設定[バリアント既定]/0=非追従/1=追従)。 */
  follow_terrain?: number;
  path_traffic: Record<string, number> | null;
  utilization: number | null;
  utilization_target?: number | null;
  durability: number | null;
  construction_progress: number | null;
  parcel_id?: number;
  start_node_id?: number;
  end_node_id?: number;
};

/**
 * バックエンドから一括取得する全マスターデータ
 * 各プロパティのキーはバックエンドのID（数値）
 * JSON仕様上キーは文字列として直列化されるが、フェッチ時に数値へ変換している
 */
export type MasterData = {
  bd_types: Record<number, BdTypeEntry>;
  skills: Record<number, SkillEntry>;
  vehicle_types: Record<number, VehicleTypeEntry>;
  space_categories: Record<number, SpaceCategoryEntry>;
  product_types: Record<number, ProductTypeEntry>;
  parcel_types: Record<number, ParcelTypeEntry>;
  path_types: Record<number, PathTypeEntry>;
  agent_types: Record<number, AgentTypeEntry>;
  agents: Record<number, AgentMasterEntry>;
  subbuildings: Record<number, SubBuildingMasterEntry>;
  products: Record<number, ProductMasterEntry>;
  paths: Record<number, PathMasterEntry>;
  transit: Record<number, TransitMasterEntry>;
  combined_paths: Record<number, CombinedPathEntry>;
  budget_categories: Record<number, BudgetCategoryEntry>;
  resource_types: Record<number, ResourceTypeEntry>;
  prop_types: Record<number, PropMasterEntry>;
};

/**
 * 公共施設エージェントの概要情報
 * バックエンド GET /api/v1/city/budgets/public-facilities の各施設エントリ
 *
 * 基本情報は全施設が持つ。メトリクス項目は施設の種類（グループ）によって
 * 付与されるフィールドが異なり、未設定の場合は undefined になる。
 *   - 不動産（TYPE_BUILDING）: supply_floor / occupancy_rate / rent / balance / utilization_rate
 *   - 港湾（TYPE_TRADE）: capacity / import_handling / export_handling / utilization_rate
 *   - サービス・公営（TYPE_STANDARD）: production_capacity / production / sold_amount / price
 *                                     / balance / utilization_rate
 *   - 市政府・教育: メトリクスなし（基本情報のみ）
 */
export type PublicFacilityAgent = {
  agent_id: number;
  name: string;
  variant: number;
  status: number;
  reserves: number;
  // サービス・公営系メトリクス
  // production_capacity はスペース制約なし（space_need<=0）の施設で null になる。
  production_capacity?: number | null;
  production?: number;
  sold_amount?: number;
  price?: number;
  // 不動産系メトリクス
  supply_floor?: number;
  occupancy_rate?: number;
  rent?: number;
  // 港湾系メトリクス（取扱量は輸入・輸出それぞれ）
  capacity?: number;
  import_handling?: number;
  export_handling?: number;
  // 共通メトリクス（複数グループで使用）
  balance?: number;
  utilization_rate?: number;
};

/** 公共サービス価格モード。固定価格 or 原価比率のいずれかで統制する（市場変動は無し）。 */
export type PublicServicePriceMode = "fixed" | "cost_ratio";

/**
 * 公共サービス価格の1行分
 * バックエンド GET/PATCH /api/v1/city/public-service-prices の各エントリ
 *
 * regulated（価格統制対象）な製品ごとに、価格モード（固定価格 / 原価比率）とその設定値・
 * 公共施設の製造原価平均・都市平均価格を保持する。
 * - mode="fixed"      : value = 供給価格そのもの（固定価格）
 * - mode="cost_ratio" : value = 製造原価への乗率（供給価格 = 製造原価 × value）
 */
export type PublicServicePrice = {
  product_id: number;
  mode: PublicServicePriceMode;
  value: number;
  unit_cost: number;
  average_price: number;
  supply_capacity: number;
  max_demand: number;
  demand: number;
  production: number;
};

/**
 * 公共サービスの需給1行分
 * バックエンド GET /api/v1/city/public-service-supply の各エントリ
 *
 * regulated（価格統制対象）な製品ごとの供給キャパシティ・需要量・生産量。
 * 価格の概念を持たないインフラ財（高圧電力・高圧水道）も含まれる点が
 * PublicServicePrice との違いで、需給の表示はすべてこちらを見る。
 */
export type PublicServiceSupply = {
  product_id: number;
  supply_capacity: number;
  demand: number;
  production: number;
};

export type PublicServiceSupplyData = {
  services: PublicServiceSupply[];
};

// ---------------------------------------------------------------------------
// 達成クエスト（quest）
// バックエンドの app/services/crud/crud_quests.py と対になる型定義。
// 判定条件は「メトリクス解決 × 時間派生 × 論理合成」の3層で表現される。
// 仕様の正は genesis-docs/docs/economics/24_達成クエスト.md。
// ---------------------------------------------------------------------------

/** クエスト条件のメトリクス参照。city オブジェクト上の数値の指定方法。 */
export type QuestMetric = {
  /** ドット区切りの属性/辞書パス（例: "population", "budgets.3"） */
  path: string;
  /** path の解決結果が辞書の場合のキー（商品IDなど）。ドット表記と等価な別記法 */
  index?: number | string | null;
  /** path の解決結果が辞書の場合の集約方法 */
  agg?: "sum" | "mean" | "max" | "min" | "count" | null;
};

/** クエスト条件の時間派生モード。メトリクスを「いつと比べて」評価するか。 */
export type QuestClauseMode = "value" | "delta" | "delta_pct" | "moving_avg" | "streak";

/** クエストの比較演算子 */
export type QuestCompareOp = ">" | "<" | ">=" | "<=" | "==" | "!=";

/** クエスト条件の単一節（clause）。1つのメトリクスと1つの比較。 */
export type QuestClause = {
  metric: QuestMetric;
  mode?: QuestClauseMode;
  /** moving_avg の窓幅 / streak の必要連続エポック数 */
  window?: number;
  compare: QuestCompareOp;
  /** 比較右辺の定数。target_metric 指定時は無視される */
  target?: number | null;
  /** 比較右辺を別メトリクスにする（「教育予算 >= 消防予算」等の相対比較） */
  target_metric?: QuestMetric | null;
};

/** クエスト条件の合成ノード。clauses を all / any / not で合成する。 */
export type QuestCondition = {
  op: "all" | "any" | "not";
  clauses: (QuestClause | QuestCondition)[];
};

/** クエストの達成通知方法 */
export type QuestNotify = "modal" | "toast" | "off";

/** クエストの演出強度 */
export type QuestTier = "bronze" | "silver" | "gold";

/** クエストのライフサイクル状態 */
export type QuestStatus = "locked" | "active" | "completed" | "failed";

/** クエストの達成報酬 */
export type QuestReward = {
  /** grant_money: 市政府の現預金へ交付金を注入する */
  type: "grant_money";
  amount: number;
};

/**
 * GET /api/v1/quests が返すクエスト1件分の各条件節の評価状態。
 * 一覧・詳細画面での進捗表示に使う。
 */
export type QuestClauseState = {
  clause_key: string;
  metric: QuestMetric | null;
  mode: QuestClauseMode;
  window: number;
  compare: QuestCompareOp;
  target: number | null;
  current: number | null;
  satisfied: boolean | null;
  /** streak モードのときの現在の連続達成エポック数 */
  streak: number | null;
  progress: number;
};

/** GET /api/v1/quests が返すクエスト1件分 */
export type Quest = {
  id: number;
  name: string;
  description: string;
  condition: QuestCondition;
  latch: boolean;
  notify: QuestNotify;
  deadline_epoch: number | null;
  deadline_within: number | null;
  prerequisites: number[];
  reward: QuestReward | null;
  category: string;
  tier: QuestTier;
  hidden: boolean;
  is_force_completable: boolean;
  status: QuestStatus;
  activated_epoch: number;
  completed_epoch: number | null;
  /** 達成時点の各clauseの実測値（clause_key → {current, target, compare}） */
  achieved_values: Record<string, { current: number | null; target: number | null; compare: string }>;
  /** クエスト全体の進捗率 0..1（サーバ計算） */
  progress: number;
  clause_states: QuestClauseState[];
};

/**
 * エポック実行レスポンスの completed_quests / failed_quests の1件分。
 * notify の値でお祝いウィンドウ（modal）/通知ポップアップ（toast）/無通知（off）を出し分ける。
 */
export type QuestNotification = {
  id: number;
  name: string;
  description: string;
  latch: boolean;
  notify: QuestNotify;
  tier: QuestTier;
  category: string;
  hidden: boolean;
  reward: QuestReward | null;
  achieved_values: Record<string, { current: number | null; target: number | null; compare: string }>;
  completed_epoch: number | null;
  status: string;
};
