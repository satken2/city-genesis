/**
 * endpoints.yaml から自動生成された型定義。
 *
 * このファイルは手動で編集しないでください。
 * 変更する場合はスプレッドシートを編集し、tools/generate_models.py を再実行してください。
 */

/** referrer メソッドが返す {id, name} オブジェクト */
export type RefItem = {
  id: number;
  name: string | null;
};

export type CitizenSummary = {
  /** 市民の一意なID */
  citizen_id: number;
  /** 市民名（ランダム生成） */
  name: string;
  /** 世帯内での立場（世帯主・配偶者・子など。consts.py の MEMBER_* を参照） */
  citizen_type: number;
  /** 年齢（歳） */
  age: number;
  /** 性別（0=男/1=女） */
  gender: number;
};

export type CitizenData = {
  /** 市民の一意なID */
  citizen_id: number;
  /** 市民名（ランダム生成） */
  name: string;
  /** 世帯内での立場（世帯主・配偶者・子など。consts.py の MEMBER_* を参照） */
  citizen_type: number;
  /** 年齢（歳） */
  age: number;
  /** 性別（0=男/1=女） */
  gender: number;
  /** 通勤にかかる片道時間（分） */
  commute_time: number;
  /** 都市外から流入した移民かどうか（0/1） */
  is_immigrant: number;
  /** 所属する世帯エージェントのID */
  household_agent_id: number;
  /** 所属世帯の名称（household_agent_idの参照解決） */
  household_agent_name?: string;
  /** 勤務先エージェントのID（無職はVALUE_NONE） */
  employer_agent_id: number;
  /** 勤務先の名称（employer_agent_idの参照解決） */
  employer_agent_name?: string;
  /** 在籍中の学校エージェントのID（非通学はVALUE_NONE） */
  school_agent_id: number;
  /** 在籍校の名称（school_agent_idの参照解決） */
  school_agent_name?: string;
  /** スキルID→習熟度の辞書。実質労働力や賃金ティアの算定に使う */
  skills: Record<string, number>;
  /** 高等教育を修了したかどうか（0/1） */
  educated: boolean;
  /** 高等教育で習得中のスキルID（非就学はVALUE_NONE） */
  education_skill: number;
  /** 犯罪性向（0.0〜1.0）。犯罪発生確率の元データ */
  potential_criminality: number;
};

export type AgentSearchItem = {
  /** 経済主体の一意なID */
  agent_id: number;
  /** 経済主体名 */
  name: string;
  /** 経済主体の大分類（世帯・一般施設・公共施設など。consts.py の TYPE_* を参照） */
  type: number;
  /** 経済主体の小分類（agent_master のバリアントID） */
  variant: number;
  /** 公共施設かどうか（0=民間/1=公共） */
  public_facility: boolean;
  /** 所属地区の名称（zone_idの参照解決） */
  zone_name?: string;
};

export type AgentSummary = {
  /** 経済主体の一意なID */
  agent_id: number;
  /** 経済主体名 */
  name: string;
  /** 所属地区ID */
  zone_id: number;
  /** 見た目の更新版数。フロントの差分描画トリガーとして使う */
  version: number;
  /** 経済主体の大分類（世帯・一般施設・公共施設など。TYPE_*） */
  type: number;
  /** 経済主体の小分類（agent_master のバリアントID） */
  variant: number;
  /** 建物底面ポリゴンの座標列 [[x, z], ...] */
  polygon?: any[];
  /** 中心点座標 [x, z] */
  center_point?: any[];
  /** ステータス（計画中・建設中・稼働中など。consts.py の STATUS_* を参照） */
  status: number;
  /** マッシュアップ建物のサブ建物配置リスト。棟ごとの subbuilding_id / model_prefix / position[x,z] / rotation / floors / height_per_floor (サーバーサイドで計算されるフィールド。型は動的。) */
  sub_buildings?: any;
  /** 階数 */
  floors?: number;
  /** 建物カテゴリ（住宅・農場など。2の冪のカテゴリ値） */
  bd_type?: number;
  /** 建物が提供する床用途カテゴリのビットマスク（SPACE_* のOR） (サーバーサイドで計算されるフィールド。型は動的。) */
  space_types?: any;
  /** 耐久度（0.0〜1.0 1.0=新品） */
  durability?: number;
  /** 稼働率（直近エポックのスロット） */
  utilization?: number[];
  /** 建設進捗率（0〜100%） (サーバーサイドで計算されるフィールド。型は動的。) */
  construction_progress?: any;
  /** インフラ網への接続端子ノードのID */
  infra_node_id: number;
  /** 車両基地など運輸系の接続ノードID */
  depot_node_id: number;
  /** 外観をランダム化するためのシード値（0.0〜1.0） */
  visual_variant?: number;
  /** 外観をランダム化するためのシード値（0.0〜1.0） */
  visual_variant2?: number;
  /** 外観をランダム化するためのシード値（0.0〜1.0） */
  visual_variant3?: number;
};

export type AgentData = {
  /** 経済主体の一意なID */
  agent_id: number;
  /** 経済主体名 */
  name: string;
  /** 見た目の更新版数。フロントの差分描画トリガーとして使う */
  version: number;
  /** 経済主体の大分類（世帯・一般施設・公共施設など。TYPE_*） */
  type: number;
  /** 経済主体の小分類（agent_master のバリアントID） */
  variant: number;
  /** ステータス（計画中・建設中・稼働中など。STATUS_*） */
  status: number;
  /** 警告フラグのビット和（int16。consts.py の ALERT_AGENT_* を参照） */
  alert: number;
  /** 作成日（YYYYMMDD） */
  created_date: number;
  /** 稼働を開始した日（YYYYMMDD） */
  active_from: number;
  /** 手持ち資金（内部通貨建て） */
  reserves: number;
  /** 収入の履歴（エポック別） */
  revenues: number[];
  /** 支出の履歴（エポック別） */
  expenses: number[];
  /** 集客力・魅力度 */
  attraction: number;
  /** 公共建物の放棄フラグ（0/1）。放棄すると自動修繕の対象外になる */
  abandoned?: boolean;
  /** 建物が立地する区画が買収交渉中であるか (サーバーサイドで計算されるフィールド。型は動的。) */
  under_acquisition?: any;
  /** 公共施設かどうか（0=民間/1=公共） */
  public_facility: boolean;
  /** インフラ網への接続端子ノードのID */
  infra_node_id: number;
  /** 車両基地など運輸系の接続ノードID */
  depot_node_id: number;
  /** プレイヤーが予約した操作のリスト（解体など） */
  user_operations?: any[];
  /** 当エポックの製品別需要量（製品ID→量） */
  demand_amount: Record<string, number>;
  /** 当エポックに獲得した製品別数量（製品ID→量） */
  received_amount?: Record<string, number>;
  /** 手持ち在庫として残っている製品別数量（製品ID→量） */
  remain_amount: Record<string, number>;
  /** 立地する土地区画のID */
  parcel_id: number;
  /** 所属地区ID */
  zone_id: number;
  /** 所属地区の名称（zone_idの参照解決） */
  zone_name?: string;
  /** 入居先の建物エージェントのID（テナントの場合） */
  building_agent_id?: number;
  /** 入居先建物の名称（building_agent_idの参照解決） */
  building_agent_name?: string;
  /** 占有・利用している床面積（不動産枠 m2） */
  realestate_amount?: number;
  /** 立ち退き済みかどうか（0/1） */
  is_evicted?: boolean;
  /** 建物かどうか（0/1） */
  is_bd: boolean;
  /** 建物カテゴリ（住宅・農場など。2の冪のカテゴリ値） */
  bd_type?: number;
  /** 耐久度（0.0〜1.0 1.0=新品） */
  durability?: number;
  /** 階数 */
  floors?: number;
  /** 建物の高さ（m） */
  height?: number;
  /** 延床面積（m2） */
  floor_area?: number;
  /** 建築面積（1フロア分の面積 m2） */
  base_area?: number;
  /** 容積率（延床面積/敷地面積） */
  volume_ratio?: number;
  /** 修繕中かどうか（0/1） */
  under_maintenance?: boolean;
  /** 建設進捗率（0〜100%） (サーバーサイドで計算されるフィールド。型は動的。) */
  construction_progress?: any;
  /** 賃料（単価） */
  rent?: number;
  /** 床用途カテゴリ別の総収容量（カテゴリ→量） */
  capacity?: Record<string, number>;
  /** 床用途カテゴリ別の使用済み収容量（カテゴリ→量） */
  occupied?: Record<string, number>;
  /** この建物に入居しているエージェントのリスト */
  tenants?: RefItem[];
  /** 世帯の栄養充足度（0.0〜1.0） */
  satis_nutrition?: number;
  /** 世帯の医療充足度（0.0〜1.0） */
  satis_healthcare?: number;
  /** 世帯の娯楽充足度（0.0〜1.0） */
  satis_entertainment?: number;
  /** 世帯の住環境満足度（0.0〜1.0） */
  satis_residential?: number;
  /** 世帯タイプ（単身・夫婦・子ありなど。consts.py の FAMILY_* を参照） */
  family_type?: number;
  /** この世帯に所属する市民のリスト */
  members?: RefItem[];
  /** 世帯の可処分所得 */
  discretionary_income?: number;
  /** 立地の環境スコア */
  env_score?: number;
  /** 世帯の生活の質（QOL）スコア */
  qol_score?: number;
  /** 世帯が自家用車を保有するか（0/1） */
  has_automobile?: boolean;
  /** 稼働率（直近エポックのスロット） */
  utilization?: number[];
  /** 稼働率の目標値（プレイヤー設定。0.0〜1.5） */
  utilization_target?: number;
  /** 無教育ティアの提示賃金 */
  wage_uneducated?: number;
  /** 高度ティアの提示賃金 */
  wage_high?: number;
  /** 標準ティアの提示賃金 */
  wage?: number;
  /** 1エポックの生産・サービス提供の目標数量 */
  quota?: number;
  /** 稼働に必要な総労働力 */
  total_labor?: number;
  /** 雇用に要求されるスキルID */
  required_skill?: number;
  /** この施設に雇用されている市民のリスト */
  employees?: RefItem[];
  /** 当エポックに出荷可能な供給量 */
  supply_amount?: number;
  /** この施設が供給する製品のID */
  supply_product_id?: number;
  /** 製品の販売価格（税込み） */
  supply_price?: number;
  /** 当エポックの生産量 */
  supply_production?: number;
  /** 販売量の履歴（エポック別） */
  supply_sold_amount?: number[];
  /** 利益率の履歴（エポック別） */
  profit_rates?: number[];
  /** 受け入れスキル別の定員（スキルID→人数） */
  education_type?: Record<string, any>;
  /** この学校に在籍している市民のリスト */
  students?: RefItem[];
};

export type PathSummary = {
  /** パスの一意なID */
  path_id: number;
  /** パス名 */
  name: string;
  /** 見た目の更新版数。フロントの差分描画トリガーとして使う */
  version: number;
  /** 幹線道路かどうか（0/1。マスター由来の読み取り専用値） */
  trunk: boolean;
  /** パスの大分類（道路・街路・線路・公共交通・インフラなど。PATH_*） */
  path_type: number;
  /** パスの小分類（幅や規格を決めるバリアントID） */
  path_variant: number;
  /** 所属地区ID */
  zone_id: number;
  /** パスの占有ポリゴンの座標列 */
  polygon: any[];
  /** ステータス（計画中・建設中・稼働中など。STATUS_*） */
  status: number;
  /** ベジエ曲線かどうか（1=曲線/0=直線・折れ線） */
  curved: boolean;
  /** 交通種別ごとの交通量（道路交通・鉄道交通など） */
  path_traffic: Record<string, number>;
  /** 通行方向（双方向/start→end/end→start） */
  travel_direction: number;
  house_path_id?: number;
  /** 駅ホームの設置側（外側/内側/両側） */
  platform_side: number;
  /** 地形に追従してパスを変形させるかどうか */
  follow_terrain: boolean;
  /** 始点ノードのID */
  start_node_id: number;
  /** 終点ノードのID */
  end_node_id: number;
  /** 始点アンカー座標 [x, y, z] */
  start_point: any[];
  /** 終点アンカー座標 [x, y, z] */
  end_point: any[];
  /** 始点の出口制御点（3次ベジエのP1）[x, y, z]。未指定は自動補完値 */
  start_control: any[];
  /** 終点の入口制御点（3次ベジエのP2）[x, y, z]。未指定は自動補完値 */
  end_control: any[];
  /** 中間アンカー点と制御点のリスト */
  waypoints: any[];
  /** 各アンカーの盛土オフセット（m -5〜+5）。着工時の地形変形に使う */
  embankment_heights: any[];
  /** 中心点座標 [x, z] */
  center_point: any[];
  /** 幅（m） */
  width: number;
  /** 騒音レベル */
  noise: number;
  /** 利用率（交通量/キャパシティ） */
  utilization: number;
  /** 耐久度（0.0〜1.0 1.0=新品） */
  durability: number;
};

export type PathData = {
  /** パスの一意なID */
  path_id: number;
  /** パス名 */
  name: string;
  /** 見た目の更新版数。フロントの差分描画トリガーとして使う */
  version: number;
  /** パスの大分類（道路・街路・線路・公共交通・インフラなど。PATH_*） */
  path_type: number;
  /** パスの小分類（幅や規格を決めるバリアントID） */
  path_variant: number;
  /** ステータス（計画中・建設中・稼働中など。STATUS_*） */
  status: number;
  /** 警告フラグのビット和（int16。consts.py の ALERT_PATH_* を参照） */
  alert: number;
  /** この区間パスが属する公共交通路線エージェントのID */
  line_agent_id: number;
  /** パスの占有ポリゴンの座標列 */
  polygon: any[];
  /** ベジエ曲線かどうか（1=曲線/0=直線・折れ線） */
  curved: boolean;
  /** 通行方向（双方向/start→end/end→start） */
  travel_direction: number;
  /** 修繕中かどうか（0/1） */
  under_maintenance: number;
  /** 駅ホームの設置側（外側/内側/両側） */
  platform_side: number;
  /** 地形に追従してパスを変形させるかどうか */
  follow_terrain: boolean;
  /** 所属地区ID */
  zone_id: number;
  /** 所属地区の名称（zone_idの参照解決） */
  zone_name?: string;
  /** インフラ財の当エポック流量（符号付き。正=start→end方向） */
  infra_flow: number;
  /** 騒音レベル */
  noise: number;
  /** 大気汚染レベル */
  air_pollution: number;
  /** 制限速度(km/h) (サーバーサイドで計算されるフィールド。型は動的。) */
  max_speed?: any;
  /** 制限速度をプレイヤー指定で上書きしているか（0/1） */
  override_max_speed: boolean;
  /** 移動種別ごとの実通過時間（混雑込み） */
  travel_time: Record<string, number>;
  /** 交通種別ごとの交通量（道路交通・鉄道交通など） */
  path_traffic: Record<string, number>;
  /** 利用率（交通量/キャパシティ） (サーバーサイドで計算されるフィールド。型は動的。) */
  utilization?: any;
  /** 耐久度。デフォルトは 1.0 とする。 */
  durability: number;
  /** 建設進捗率（0〜100%） (サーバーサイドで計算されるフィールド。型は動的。) */
  construction_progress?: any;
  /** ノードを共有する隣接街路が属する地区の一覧 (サーバーサイドで計算されるフィールド。型は動的。) */
  nearby_zones?: any;
  /** 始点ノードのID */
  start_node_id: number;
  /** 終点ノードのID */
  end_node_id: number;
  /** 始点アンカー座標 [x, y, z] */
  start_point: any[];
  /** 終点アンカー座標 [x, y, z] */
  end_point: any[];
  /** 始点の出口制御点（3次ベジエのP1）[x, y, z]。未指定は自動補完値 */
  start_control: any[];
  /** 終点の入口制御点（3次ベジエのP2）[x, y, z]。未指定は自動補完値 */
  end_control: any[];
  /** 中間アンカー点と制御点のリスト */
  waypoints: any[];
  /** 各アンカーの盛土オフセット（m -5〜+5）。着工時の地形変形に使う */
  embankment_heights: any[];
  /** 中心点座標 [x, z] */
  center_point: any[];
  /** 幅（m） */
  width: number;
  /** パスの物理長（m） */
  length: number;
};

export type ParcelSummary = {
  /** 土地区画の一意なID */
  parcel_id: number;
  /** 見た目の更新版数。フロントの差分描画トリガーとして使う */
  version: number;
  /** 区画タイプ（陸地/海岸など。PARCEL_*） */
  parcel_type: number;
  /** ステータス（計画中/稼働中など。STATUS_*） */
  status: number;
  /** 基準標高（m） */
  altitude: number;
  /** 区画ポリゴンの座標列 [[x, z], ...] */
  polygon: any[];
  /** 中心点座標 [x, z] */
  center_point: any[];
  /** 接道している街路のID */
  front_path_id: number;
  /** 土地価格 */
  land_price: number;
  /** 地区の法規制を区画側で上書きしているか（0/1） */
  restriction_override: boolean;
  /** 建築可能な建物カテゴリのビットフラグ（bd_type値のOR） */
  allowed_bd_mask: number;
  /** 建蔽率の上限 */
  limit_coverage_ratio: number;
  /** 容積率の上限 */
  limit_volume_ratio: number;
  /** 高さの上限（m） */
  limit_height: number;
  /** この区画を使用している建物エージェントのID（0=空き地） */
  usage_id: number;
};

export type ParcelData = {
  /** 土地区画の一意なID */
  parcel_id: number;
  /** 見た目の更新版数。フロントの差分描画トリガーとして使う */
  version: number;
  /** 区画タイプ（陸地/海岸など。PARCEL_*） */
  parcel_type: number;
  /** ステータス（計画中/稼働中など。STATUS_*） */
  status: number;
  /** 区画ポリゴンの座標列 [[x, z], ...] */
  polygon: any[];
  /** 中心点座標 [x, z] */
  center_point: any[];
  /** この区画を使用している建物エージェントのID（0=空き地） */
  usage_id: number;
  /** 所属地区ID */
  zone_id: number;
  /** 所属地区の名称（zone_idの参照解決） */
  zone_name?: string;
  /** 区画に重なる天然資源のID（資源種別→資源ID） */
  resource_id: Record<string, number>;
  /** 地形の平均勾配（無次元）。建設難易度の元データ */
  terrain_slope: number;
  /** 区画に重なる天然資源の名称（資源種別→名称） (サーバーサイドで計算されるフィールド。型は動的。) */
  name_resource?: any;
  /** 接道している街路のID */
  front_path_id: number;
  /** 接道街路の名称（front_path_idの参照解決） */
  front_path_name?: string;
  /** 道路に面する辺のインデックス */
  facade_edge_index: number;
  /** 所有者エージェントのID（民間または市政府） */
  owner_agent_id: number;
  /** 所有者の名称（owner_agent_idの参照解決） */
  owner_agent_name?: string;
  /** 土地価格 */
  land_price: number;
  /** バリアント別の立地倍率（agentバリアントID→倍率） */
  agent_multiplier: Record<string, number>;
  /** 地区の法規制を区画側で上書きしているか（0/1） */
  restriction_override: boolean;
  under_acquisition: boolean;
  /** 建築可能な建物カテゴリのビットフラグ（bd_type値のOR） */
  allowed_bd_mask: number;
  /** 建蔽率の上限 */
  limit_coverage_ratio: number;
  /** 容積率の上限 */
  limit_volume_ratio: number;
  /** 高さの上限（m） */
  limit_height: number;
  /** 区画面積（m2） */
  area: number;
  /** 基準標高（m） */
  altitude: number;
  /** プレイヤーが予約した操作のリスト（買収など） */
  user_operations: any[];
};

export type ZoneData = {
  /** 地区の一意なID */
  zone_id: number;
  /** 見た目の更新版数。フロントの差分描画トリガーとして使う */
  version: number;
  /** 地区ポリゴン（所属区画の形状を合算して導出） */
  polygon: any[];
  /** 地区名 */
  name: string;
  /** 地区の識別色（#RRGGBB） */
  color: string;
  /** ユーザーが形状確定前の地区か */
  auto_managed: boolean;
  /** 地区面積（m2） */
  area: number;
  /** 中心点座標 [x, z] */
  center_point: any[];
  /** 建築可能な建物カテゴリのビットフラグ（bd_type値のOR） */
  allowed_bd_mask: number;
  /** 建蔽率の上限 */
  limit_coverage_ratio: number;
  /** 容積率の上限 */
  limit_volume_ratio: number;
  /** 高さの上限（m） */
  limit_height: number;
  /** 製品別の地区平均価格（税込み。製品ID→価格） */
  zone_average_prices: Record<string, number>;
  /** 標準ティア平均賃金の履歴 */
  average_wage: number[];
  /** 平均賃料の履歴 */
  average_rent: number[];
  /** 平均土地価格の履歴 */
  average_land_price: number[];
  /** 地区人口の履歴 */
  zone_population: number[];
  /** 昼間人口の履歴 */
  daytime_population: number[];
  /** 失業率の履歴 */
  zone_unemployment_rate: number[];
  /** 床用途カテゴリ別の需要量（カテゴリ→量） */
  space_demands: Record<string, number>;
  /** 床用途カテゴリ別の供給量（カテゴリ→量） */
  space_supplies: Record<string, number>;
  /** 製品別の地区供給量（製品ID→量） */
  zone_supply: Record<string, number>;
  /** 製品別の地区需要量（製品ID→量） */
  zone_demand: Record<string, number>;
};

export type ResourceData = {
  /** 天然資源の一意なID */
  resource_id: number;
  /** 資源名 */
  name: string;
  /** 採取される製品のID */
  product_id: number;
  /** 資源エリアのポリゴンの座標列 */
  polygon: any[];
  /** 中心点座標 [x, z] */
  center_point: any[];
  /** 資源種別（master.yaml の resource_master キー） */
  resource_type: number;
  /** 採取量の履歴（新しい順） */
  consumption_amount: number[];
  /** 埋蔵上限量 */
  max_amount: number;
  /** 埋蔵量の履歴（新しい順） */
  current_amount: number[];
  /** 1エポックあたりの埋蔵量回復率 */
  recovery_rate: number;
};

export type CitizenListResponse = {
  citizens: CitizenSummary[];
};

export type CitizenResponse = {
  citizen: CitizenData;
};

export type AgentSearchItemListResponse = {
  agents: AgentSearchItem[];
};

export type AgentListResponse = {
  agents: AgentSummary[];
};

export type AgentResponse = {
  economic_agent: AgentData;
};

export type PathListResponse = {
  paths: PathSummary[];
};

export type PathResponse = {
  path: PathData;
};

export type ParcelListResponse = {
  parcels: ParcelSummary[];
};

export type ParcelResponse = {
  parcel: ParcelData;
};

export type ZoneResponse = {
  zone: ZoneData;
};

export type ResourceResponse = {
  resource: ResourceData;
};
