/**
 * 規制ウィンドウで共有する型定義
 *
 * ウィンドウ本体（CityRegulationWindow）と各タブコンポーネントの双方が参照する型だけを置く。
 * 単位変換やキー一覧といったロジックは、それを所有するタブ側のファイルに持たせる。
 */

/**
 * GET /api/v1/city レスポンスのうち、規制ウィンドウが参照する項目
 * 制限速度と補修閾値のみを使う
 */
export type CityData = {
  city: {
    max_speed: Record<string, number>;
    repair_threshold?: number;
    repair_threshold_path?: number;
  };
};

/**
 * GET /api/v1/city/regulations レスポンスの型
 * 建築規制（容積率・高さ制限・建ぺい率）と移民規制を含む。
 */
export type RegulationsData = {
  work_hours: number;
  limit_volume_ratio: number;
  limit_height: number;
  limit_coverage_ratio: number;
  // 移民規制（プレイヤーが政策として設定する受け入れ条件）
  immigration_age_min: number;
  immigration_age_max: number;
  immigration_enabled: boolean;
  immigration_allow_single: boolean;
  immigration_allow_married_no_children: boolean;
  immigration_allow_with_children: boolean;
  immigration_gender_policy: number; // 0=制限なし, 1=男性のみ(世帯主), 2=女性のみ(世帯主)
  immigration_skill_thresholds: Record<string, number>; // skill_id(文字列) → 最低値
  immigration_quota: number; // 1エポックあたり上限人数。0=無制限
  // 社会保障政策（給付率は 0〜1 の比率）
  retirement_age: number;
  unemployment_benefit_ratio: number;
  pension_benefit_ratio: number;
  livelihood_assistance_ratio: number;
};

/**
 * 社会保障政策の編集中値を保持する型。
 * 退職年齢（歳）と、失業給付・年金（地区標準賃金に対する比率）、生活扶助（不足額に対する比率）。
 */
export type WelfareRegulations = {
  retirement_age: number;
  unemployment_benefit_ratio: number;
  pension_benefit_ratio: number;
  livelihood_assistance_ratio: number;
};

/**
 * 移民規制の編集中値を保持する型。
 * 建築規制と異なり bool / dict 型のフィールドを含むため、建築規制とは別に管理する。
 */
export type ImmigrationRegulations = {
  immigration_age_min: number;
  immigration_age_max: number;
  immigration_enabled: boolean;
  immigration_allow_single: boolean;
  immigration_allow_married_no_children: boolean;
  immigration_allow_with_children: boolean;
  immigration_gender_policy: number;
  immigration_skill_thresholds: Record<string, number>;
  immigration_quota: number;
};

/**
 * 建築規制（数値型）のキー。
 * 移民規制は bool/dict 型を含むため、建築規制の数値ハンドラはこのキーのみを扱う。
 */
export type BuildingRegKey = "limit_volume_ratio" | "limit_height" | "limit_coverage_ratio";

/**
 * 編集中の制限速度を保持する型
 * キーは path_variant の ID（数値文字列）、値は km/h
 */
export type SpeedMap = Record<string, number>;

/**
 * マスターデータから取り出したパスバリアントの情報
 * 保存ペイロードの構築（ウィンドウ本体）と一覧表示（制限速度タブ）で共有する。
 */
export type PathVariantInfo = {
  id: number;
  display_name: string;
  type: number;
  default_speed_limit: number;
};
