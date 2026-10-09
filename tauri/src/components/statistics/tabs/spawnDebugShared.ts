/**
 * スポーンデバッグタブ（開発者専用）の共有定義
 *
 * GET /api/v1/stats/spawn_debug のレスポンス型と、DebugAgentSpawnTab /
 * DebugBuildingSpawnTab の両タブで使うチャート共通定数・数値フォーマッタを
 * ここに集約する。両タブは同じエンドポイントを参照するため
 * （useEpochCachedStats がエポック単位でメモ化するので実リクエストは1回）、
 * 型がずれないように単一の定義を共有する。
 */

/** GET /api/v1/stats/spawn_debug のバリアント別建設統計。
 *  best_* の内訳4項目は「最終スコアが最大の（パーセル×階数）パターン」における生値。 */
export type ConstructionVariantEntry = {
  best_score: number;
  spawned: number;
  /** 最良パターンのNPV（賃料収入の現在価値） */
  best_npv: number;
  /** 最良パターンの建設コスト */
  best_construction_cost: number;
  /** 最良パターンの土地取得コスト（デベロッパーマージン控除後） */
  best_land_cost: number;
  /** 最良パターンの既存建物買収+解体コスト */
  best_extra_cost: number;
};

/** GET /api/v1/stats/spawn_debug の建物スポーン履歴1レコード */
export type ConstructionRecord = {
  date: number;
  candidates: number;
  positive_patterns: number;
  spawned: number;
  variants: Record<string, ConstructionVariantEntry>;
};

/** 経済主体スポーンの業種別評価メトリクス（SpawnPhase が毎エポック記録）。
 *  需要系（demand_* / effective_demand）は全地区の合算、best_* は全地区の最良値、
 *  capital_need は全地区の最小値。
 *  評価が早期打ち切りされた業種ではキー自体が存在しないことがある。 */
export type AgentSpawnMetrics = {
  /** 国内潜在需要の合算（既存供給を差し引いた残余需要ベース） */
  demand_domestic?: number;
  /** 輸出潜在需要の合算 */
  demand_export?: number;
  /** 競争力割引後の実効需要の合算（スポーン規模の上限になる値） */
  effective_demand?: number;
  /** 国内価格競争力係数の最良値（0〜1） */
  best_domestic_competitiveness?: number;
  /** 輸出競争力係数の最良値（0〜1） */
  best_export_competitiveness?: number;
  /** 推定利益率の最良値（スポーン実行順のソートキーに相当） */
  best_profit_rate?: number;
  /** 最低稼働量（マスター由来で地区非依存） */
  min_qty?: number;
  /** 最低稼働規模の想定初期資本（運転資金＋専用建物の建設費）の最小値。
   *  レコードの credit_limit を超える業種は「資本調達不能」で参入できない
   *  （それ以上の規模は与信限度内へ自動縮小されるため、参入可否はこの値で決まる） */
  capital_need?: number;
  /** 資本・労働・物理上限の全クリップ適用後の評価規模（エポック生産量、全地区の最良値）。
   *  採算評価とスポーン実行規模の基礎になる値 */
  assumed_qty?: number;
};

/** GET /api/v1/stats/spawn_debug の経済主体スポーン履歴1レコード。
 *  variants は阻害要因カウンタ（件数）、metrics は評価の内部数値。 */
export type AgentSpawnRecord = {
  date: number;
  variants: Record<string, Record<string, number>>;
  metrics: Record<string, AgentSpawnMetrics>;
  /** 案件ごとの与信集中限度（円）。業種によらない都市共通のスカラーで、
   *  max(民間預金総額 × SPAWN_CREDIT_CONCENTRATION_LIMIT, SPAWN_CREDIT_FLOOR) */
  credit_limit: number;
  /** 与信集中限度の分母である民間預金総額（円）。
   *  一般施設・共有建物・世帯の正の現金残高の合計 */
  private_deposits: number;
};

/** GET /api/v1/stats/spawn_debug のレスポンス全体 */
export type SpawnDebugStats = {
  enabled: boolean;
  construction: ConstructionRecord[];
  agent_spawn: AgentSpawnRecord[];
};

/** 両タブが参照するエンドポイント（useEpochCachedStats がエポック単位でメモ化する） */
export const SPAWN_DEBUG_ENDPOINT = "/api/v1/stats/spawn_debug";

// recharts の共通スタイル定数（他統計タブと同じダークテーマ）
export const CHART_MARGIN = { top: 12, right: 16, left: 0, bottom: 0 };
export const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
export const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

/** 大きな数値を短縮表記する（スコアや需要量は非常に大きくなり得るため） */
export function formatCompact(v: number): string {
  const abs = Math.abs(v);
  if (abs >= 1_000_000_000) return `${(v / 1_000_000_000).toFixed(1)}G`;
  if (abs >= 1_000_000) return `${(v / 1_000_000).toFixed(1)}M`;
  if (abs >= 1_000) return `${(v / 1_000).toFixed(1)}k`;
  return v.toFixed(0);
}

/** 係数（競争力0〜1・利益率など）の表示用フォーマッタ */
export function formatCoefficient(v: number): string {
  return v.toFixed(3);
}
