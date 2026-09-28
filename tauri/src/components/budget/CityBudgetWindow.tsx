import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { resolveLedgerIcon } from "../../utils/ledgerIcon";
import { STATUS_CONSTRUCTING } from "../../constants";
import type { BudgetCategoryEntry } from "../../types";
import { formatEpochDateRange } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import { Money } from "../common/Money";
import { formatCompactAmount, formatExactAmount } from "../../utils/money";
import { hasRoad, roadDisplayName } from "../road/roadApi";
import {
  ResponsiveContainer,
  BarChart,
  ComposedChart,
  Bar,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  ReferenceLine,
  Tooltip as RechartsTooltip,
  Legend,
} from "recharts";
import {
  buildBusinessRevenueItems,
  TAX_REVENUE_META,
  spendCategoryColor,
} from "../common/FiscalSankey";
import "./CityBudgetWindow.css";
import { useRemPx } from "../../hooks/useRemPx";

type BudgetAgent = {
  kind?: "agent" | "path" | "parcel" | "welfare";
  agent_id?: number;
  path_id?: number;
  parcel_id?: number;
  /** 社会保障カテゴリの給付種別行(実体エンティティなし)。welfare_kind は i18n キー budget.welfare.* に対応 */
  welfare_id?: number;
  welfare_kind?: string;
  /** パスの行は区間名をそのまま返す(既定名は空文字)。表示名は UI が rowDisplayName で補う */
  name: string;
  /** パスの行の所属道路(未所属は VALUE_NONE)とその名前(未所属は空文字) */
  road_id?: number;
  road_name?: string;
  category_id: number;
  variant?: number;
  status: number;
  actual_expense: number;
  projected_expense: number;
  utilization_target?: number;
  construction_cost_per_epoch?: number;
  remaining_epochs?: number | null;
};

/**
 * 行の表示名。社会保障の給付種別行は実体を持たないため i18n(budget.welfare.*)で解決する(バックエンドの name はフォールバック)。
 * パスの行は区間名が空なら、所属道路があれば「道路名(区間 #id)」、無ければ「パス #id」を補う(パス詳細のヘッダと同じ規則)。
 */
function rowDisplayName(a: BudgetAgent, t: TFunction): string {
  if (a.kind === "welfare" && a.welfare_kind) {
    return t(`budget.welfare.${a.welfare_kind}`, { defaultValue: a.name });
  }
  if (a.kind === "path" && a.name === "") {
    return hasRoad(a.road_id)
      ? t("path.titleInRoad", { road: roadDisplayName(a.road_name, a.road_id, t), id: a.path_id })
      : t("path.titleWithId", { id: a.path_id });
  }
  return a.name;
}

/** エージェント/道路/土地区画/給付種別を一意に識別するキー（稼働率の編集state・PATCH対象の識別に使う） */
function rowKey(a: BudgetAgent): string {
  if (a.kind === "path") return `path:${a.path_id}`;
  if (a.kind === "parcel") return `parcel:${a.parcel_id}`;
  if (a.kind === "welfare") return `welfare:${a.welfare_id}`;
  return `agent:${a.agent_id}`;
}

/**
 * 予算比率スライダーの上限(%)。全カテゴリ共通でフル稼働必要額の150%まで
 * (100%超は見積もり誤差やエポック中の価格・賃金変動へのヘッドルーム)。
 */
const RATIO_MAX_PERCENT = 150;

/**
 * 比率を変えてから自動保存(PATCH)するまでの待ち時間(ms)。
 * PATCHはバックエンドで予算の再導出とPlanStdPhaseの再実行を伴うため、スライダーのドラッグ中に
 * 毎ステップ送ると重い。スライダーを離した時点では待たずに送るので、これは
 * キーボード操作など「離す」操作を伴わない変更のための保険。
 */
const AUTO_SAVE_DEBOUNCE_MS = 800;

/** 比率マップが同値か(自動保存の要否判定。キー集合は初期化時に固定されるので値だけ比べる) */
function ratiosEqual(a: Record<string, number>, b: Record<string, number>): boolean {
  const keys = Object.keys(a);
  if (keys.length !== Object.keys(b).length) return false;
  return keys.every((k) => a[k] === b[k]);
}

/** 市政府の現預金のエポック末スナップショット1点（バックエンドの reserves_history の要素） */
type ReservesHistoryPoint = {
  epoch: number;
  reserves: number;
};

type BudgetData = {
  budgets: Record<string, number>;
  prev_budgets: Record<string, number>;
  total_expense: Record<string, number>;
  total_revenue: Record<string, number>;
  budget_tiers?: Record<string, number>;
  // 予算はプレイヤーが「フル稼働必要額に対する比率」で設定し、以下から導出される。
  category_ratio?: Record<string, number>;
  effective_category_ratio?: Record<string, number>;
  // 前エポック実績支出のスナップショット(「前実績」バーの表示用。予算の基準ではない)。
  prev_total_expense?: Record<string, number>;
  // カテゴリ別のフル稼働必要額(予算導出の基準額。比率スライダーの分母)。
  // カテゴリ稼働率100%の前提で、各施設が自身の稼働率目標どおり稼働した場合の
  // 1エポック見込み費用。建設(8)はポーズ中の新規発注も含めてバックエンドが毎回見積もる。
  projected_required?: Record<string, number>;
  savings_target?: number;
  savings?: number;
  budget_auto_adjusted?: boolean;
  // 市政府の現預金のエポック末推移(古い順)と、現在(ユーザー操作フェーズ)の現預金。
  reserves_history?: ReservesHistoryPoint[];
  current_reserves?: number;
};

type CityData = {
  city: {
    city_agent_id: number;
    epoch: number;
    current_date: number;
    simulation_days: number;
    is_early_game?: boolean;
  };
};

const LEDGER_RESERVE_KEY = "10";

/**
 * 市政カテゴリ(1)。現状これに属する実支出主体が存在しない(地区代表エージェントの
 * 建設サービス支出は建設(8)/修繕(9)へ付け替え済み、市政府自体は運営費を持たない)ため、
 * 空カテゴリとしてプレイヤーには表示しない。バックエンドの定数・マスター行は将来の
 * 拡張(市庁舎の運営費など)のため温存されており、予算導出上は常に¥0で無害。
 */
const LEDGER_CITYHALL_KEY = "1";

/** プレイヤーに表示しない予算カテゴリ(貯蓄=支出枠ではない、市政=空カテゴリ) */
const HIDDEN_CATEGORY_KEYS = new Set([LEDGER_RESERVE_KEY, LEDGER_CITYHALL_KEY]);

/** 現金推移グラフに描く過去エポック数の上限(これより古い履歴は横軸が潰れるので切り捨てる) */
const RESERVES_HISTORY_MAX = 24;
/** 将来予測の傾きを最小二乗で求めるのに使う直近の点数(現在の点を含む) */
const FORECAST_LOOKBACK = 6;
/** 将来予測を描く先のエポック数 */
const FORECAST_HORIZON = 4;

/** 現金推移グラフの色。実績=濃い青、現在=アクセント色、予測=灰色の点線。 */
const COLOR_RESERVES_ACTUAL = "#2c5687";
const COLOR_RESERVES_CURRENT = "#4ea1ff";
const COLOR_RESERVES_FORECAST = "#6b8299";

/**
 * グラフの軸・グリッドの色。フラット意匠の共通トークン(App.cssの --flat-*)と同値だが、
 * rechartsはCSS変数を解決できない(SVG属性へ直接渡すため)ので直値で持つ。
 */
const CHART_AXIS_TEXT = "#a4bccc";
const CHART_AXIS_LINE = "#2c3646";
const CHART_GRID_LINE = "#22303f";
const CHART_CURSOR_FILL = "rgba(78, 161, 255, 0.08)";
const CHART_ZERO_LINE = "#ff9a9a";

/** 収支バランスの棒(X軸カテゴリ)の識別子 */
const BALANCE_ROW_REVENUE = "revenue";
const BALANCE_ROW_EXPENSE = "expense";
const BALANCE_ROW_PRE_COMPRESS = "preCompress";

/** 収支バランスの「圧縮前の支出」バーの不透明度(薄く描いて確定支出と区別する) */
const PRE_COMPRESS_OPACITY = 0.3;

/**
 * カテゴリの維持優先度（Tier）を返す。
 * Tier は数字が小さいほど優先的に維持され、大きいほど先に削減される
 * （1=最優先維持〜4=最初に削減。バックエンドと同一の番号体系）。
 * 未設定カテゴリはバックエンド City.budget_tiers の既定値と同じフォールバックを使う。
 */
function getCategoryTier(key: string, tiers: Record<string, number>): number {
  if (tiers[key] !== undefined) {
    return tiers[key];
  }
  const cId = Number(key);
  if (cId === 10) return 4; // 貯蓄: 最初に削減（希望0のため実質無効）
  if (cId === 8 || cId === 14) return 3; // 建設・用地買収
  if ([3, 4, 5, 7, 11].includes(cId)) return 2; // 教育・治安・消防・その他・交通
  return 1; // 医療・インフラ・修繕: 最優先維持
}

/** 左ペインに表示する維持優先度グループの並び（Tier 1=最優先維持 が最上段） */
const TIER_ORDER = [1, 2, 3, 4] as const;

/**
 * 予算カテゴリごとの「必要金額」(＝比率スライダーの分母・カードの表示値)を、予算パネルの
 * 施設別リストと同一の per施設値(projected_expense)の単純合計として求める。これにより
 * カードの必要金額とリストの「次見込み」合計が常に一致する。projected_expense は各施設が
 * 自身の utilization_target どおり稼働した場合の1エポック見込み費用であり、バックエンドの
 * estimate_projected_required と build_budget_rows が同一の算出元を共有するため両者は一致する。
 * 実効稼働率は施設別の utilization_target のみで決まる(カテゴリ全体の稼働率という概念は廃止済み)。
 */
function computeBaseAmounts(
  budgetAgents: BudgetAgent[] | null,
): Record<string, number> {
  const base: Record<string, number> = {};
  if (!budgetAgents) return base;
  for (const agent of budgetAgents) {
    const catKey = String(agent.category_id);
    if (catKey === LEDGER_RESERVE_KEY) continue;
    base[catKey] = (base[catKey] ?? 0) + agent.projected_expense;
  }
  return base;
}

/**
 * プレイヤーが設定した「フル稼働必要額に対する比率(％)」と基準額(computeBaseAmounts)から
 * 実際に配分される予算額(円)を導出する。バックエンドの City.maintain_and_scale_budget を
 * 忠実に反映したプレビュー計算：desired = 基準額 × 比率(0〜150%)、希望合計が配分可能額
 * (現金)を超える場合は維持優先度の低い Tier 4→1 の順に削減して収める
 * （＝資金不足時の自動調整。Tier 1 が最後まで維持される）。
 * 基準額の合計が0の場合(初回エポック等)は全カテゴリ均等配分のブートストラップをミラーする。
 *
 * 返り値の desired は削減前のプレイヤー希望額であり、収支バランスの「圧縮前の支出」バー
 * (現金不足で削られた分を薄く描く3本目の棒)のデータ源になる。
 */
function deriveBudgets(
  ratiosPct: Record<string, number>,
  base: Record<string, number>,
  tiers: Record<string, number>,
  reserves: number,
): {
  budgets: Record<string, number>;
  desired: Record<string, number>;
  autoAdjusted: boolean;
} {
  const keys = Object.keys(base).filter((k) => k !== LEDGER_RESERVE_KEY);

  const budgets: Record<string, number> = {};
  for (const k of keys) budgets[k] = 0;
  budgets[LEDGER_RESERVE_KEY] = 0;

  // プレイヤー希望額(削減前)。現金の多寡に関係なく 基準額 × 比率 で決まる。
  const desired: Record<string, number> = {};
  for (const k of keys) {
    const b = Math.max(base[k] ?? 0, 0);
    const ratio = Math.min(Math.max((ratiosPct[k] ?? 100) / 100, 0), RATIO_MAX_PERCENT / 100);
    desired[k] = b * ratio;
  }

  if (reserves <= 0) {
    // 債務超過: バックエンドは全予算を0にする(希望額はそのまま圧縮前として表示する)。
    return { budgets, desired, autoAdjusted: false };
  }

  const totalBase = keys.reduce((s, k) => s + Math.max(base[k] ?? 0, 0), 0);
  if (totalBase <= 1e-8) {
    // 実績も必要量も未捕捉(初回エポック等): バックエンドと同じく現金を均等配分する。
    const even = keys.length > 0 ? reserves / keys.length : 0;
    for (const k of keys) budgets[k] = even;
    return { budgets, desired: { ...budgets }, autoAdjusted: false };
  }

  for (const k of keys) budgets[k] = desired[k];

  const available = Math.max(reserves, 0);
  const totalDesired = keys.reduce((s, k) => s + budgets[k], 0);

  let autoAdjusted = false;
  if (totalDesired > available + 1e-6) {
    autoAdjusted = true;
    let deficit = totalDesired - available;
    // 維持優先度の低い Tier 4 から順に削減する（Tier 1 が最後まで維持される）
    for (let tier = 4; tier >= 1 && deficit > 1e-6; tier--) {
      const tierKeys = keys.filter((k) => getCategoryTier(k, tiers) === tier);
      const tierSum = tierKeys.reduce((s, k) => s + budgets[k], 0);
      if (tierSum <= 1e-8) continue;
      if (tierSum <= deficit) {
        for (const k of tierKeys) budgets[k] = 0;
        deficit -= tierSum;
      } else {
        const scale = (tierSum - deficit) / tierSum;
        for (const k of tierKeys) budgets[k] *= scale;
        deficit = 0;
      }
    }
  }

  return { budgets, desired, autoAdjusted };
}

/** 現金推移グラフの1行。実績・現在・予測はそれぞれ別系列にして色と線種を分ける。 */
type ReservesChartRow = {
  /** X軸ラベル(エポック番号) */
  label: string;
  /** 過去エポック末の確定現預金 */
  actual?: number;
  /** 現在(ユーザー操作フェーズ)の現預金。棒1本だけを明るい色で描くための別系列。 */
  current?: number;
  /** 直近トレンドの外挿値。現在の点にも同じ値を入れて点線を棒から繋げる。 */
  forecast?: number;
};

/**
 * 現金推移グラフのデータを組み立てる。
 *
 * 「過去エポック末の確定現預金(actual)」＋「現在の現預金(current)」の棒に続けて、
 * 直近 FORECAST_LOOKBACK 点の最小二乗回帰で求めた1エポックあたりの増減(傾き)を
 * FORECAST_HORIZON エポック先まで外挿した点線(forecast)を描く。予測は最後の実測値を
 * 起点にするため、点線は必ず「現在」の棒の高さから始まる。
 * 点が1つしかない(=まだエポックを回していない)場合は傾きを求められないので予測は描かない。
 */
function buildReservesChartData(
  history: ReservesHistoryPoint[],
  currentEpoch: number,
  currentReserves: number,
): ReservesChartRow[] {
  const past = history.slice(-RESERVES_HISTORY_MAX);
  const rows: ReservesChartRow[] = past.map((p) => ({
    label: String(p.epoch),
    actual: p.reserves,
  }));
  const currentRow: ReservesChartRow = {
    label: String(currentEpoch),
    current: currentReserves,
  };
  rows.push(currentRow);

  // 回帰に使う実測値の並び(過去の確定値＋現在)。X座標は等間隔なので添字をそのまま使う。
  const series = [...past.map((p) => p.reserves), currentReserves];
  if (series.length < 2) return rows;

  const window = series.slice(-FORECAST_LOOKBACK);
  const n = window.length;
  const meanX = (n - 1) / 2;
  const meanY = window.reduce((s, v) => s + v, 0) / n;
  let cov = 0;
  let varX = 0;
  for (let i = 0; i < n; i++) {
    cov += (i - meanX) * (window[i] - meanY);
    varX += (i - meanX) * (i - meanX);
  }
  const slope = varX > 0 ? cov / varX : 0;

  // 点線の起点を現在の棒に合わせる(回帰直線の端点ではなく最後の実測値をアンカーにする)
  currentRow.forecast = currentReserves;
  for (let k = 1; k <= FORECAST_HORIZON; k++) {
    rows.push({
      label: String(currentEpoch + k),
      forecast: currentReserves + slope * k,
    });
  }
  return rows;
}

/** 収支バランスの積み上げ系列(1カテゴリ or 1税目) */
type BalanceSeries = {
  /** recharts の dataKey。収入=rev_*、確定支出=exp_*、圧縮前支出=pre_* */
  key: string;
  label: string;
  color: string;
};

/** recharts がツールチップに渡す1系列分のペイロード(必要なフィールドだけを型付けする) */
type ChartTooltipEntry = {
  name?: string;
  value?: number | string;
  color?: string;
  dataKey?: string | number;
};

/**
 * 金額グラフ共通のツールチップ。
 *
 * recharts の既定ツールチップは、そのX位置に値を持たない系列(積み上げの相手側の棒や、
 * 描画されていない予測線など)まで空行として並べてしまう。収支バランスは系列数が多く
 * 空行だらけになるため、値のある系列だけに絞り、必要なら合計行(＝積み上げ棒全体の高さ)を
 * 添える実装に差し替える。recharts が active / payload / label を注入する。
 */
function ChartValueTooltip({
  active,
  payload,
  label,
  locale,
  totalLabel,
  formatLabel,
}: {
  active?: boolean;
  payload?: ChartTooltipEntry[];
  label?: string | number;
  /** 金額整形に使うロケール */
  locale: string;
  /** 指定すると、系列が2つ以上のときに合計行をこのラベルで表示する */
  totalLabel?: string;
  /** 見出し(X軸ラベル)の整形。未指定ならラベルをそのまま出す */
  formatLabel?: (label: string | number | undefined) => string;
}) {
  if (!active || !payload || payload.length === 0) return null;
  const items = payload.filter(
    (p) => typeof p.value === "number" && Number.isFinite(p.value) && p.value !== 0,
  );
  if (items.length === 0) return null;
  const total = items.reduce((sum, p) => sum + Number(p.value), 0);
  return (
    <div className="budget-chart-tooltip">
      <div className="budget-chart-tooltip__label">
        {formatLabel ? formatLabel(label) : label}
      </div>
      {items.map((p) => (
        <div key={String(p.dataKey)} className="budget-chart-tooltip__row">
          <span
            className="budget-chart-tooltip__swatch"
            style={{ background: p.color }}
          />
          <span className="budget-chart-tooltip__name">{p.name}</span>
          <span className="budget-chart-tooltip__value">
            {formatExactAmount(Number(p.value), locale, true)}
          </span>
        </div>
      ))}
      {totalLabel && items.length > 1 && (
        <div className="budget-chart-tooltip__row budget-chart-tooltip__row--total">
          <span className="budget-chart-tooltip__name">{totalLabel}</span>
          <span className="budget-chart-tooltip__value">
            {formatExactAmount(total, locale, true)}
          </span>
        </div>
      )}
    </div>
  );
}

export function CityBudgetWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const remPx = useRemPx();
  const masterData = useMasterData();

  const [budgetData, setBudgetData] = useState<BudgetData | null>(null);
  const [cityReserves, setCityReserves] = useState<number>(0);
  const [currentEpoch, setCurrentEpoch] = useState<number>(0);
  const [currentDate, setCurrentDate] = useState<number>(0);
  const [simulationDays, setSimulationDays] = useState<number>(30);
  // プレイヤーの主入力：カテゴリ別「フル稼働必要額に対する比率(％整数、0〜150)」。
  const [pendingRatios, setPendingRatios] = useState<Record<string, number>>({});
  const [budgetAgents, setBudgetAgents] = useState<BudgetAgent[] | null>(null);
  const [activeCategoryId, setActiveCategoryId] = useState<number>(0);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // 予算比率スライダーの表示トグル。既定は非表示(％のワッペンだけを見る閲覧モード)で、
  // 「編集」ボタンを押している間だけ全カードにスライダーが出る。
  const [isEditingRatios, setIsEditingRatios] = useState(false);

  // 自動保存の作業用。savedRatios=サーバが持っている比率(nullはロード完了前)、
  // latestRatios=画面上の最新値、saveTimer=デバウンス待ちのタイマー、saving=送信中。
  // 保存中に更に動かされた場合は完了後に最新値で送り直す(応答の追い越しでサーバが
  // 古い比率のまま終わるのを防ぐ)。
  const savedRatiosRef = useRef<Record<string, number> | null>(null);
  const latestRatiosRef = useRef<Record<string, number>>({});
  const saveTimerRef = useRef<number | null>(null);
  const savingRef = useRef(false);

  useEffect(() => {
    setHintbarText(t("hint.cityBudget"));
    return () => {
      setHintbarText("");
    };
  }, [t]);

  const categories = useMemo(() => {
    if (!masterData?.budget_categories) return [];
    return Object.entries(masterData.budget_categories).map(([id, entry]) => ({
      id: Number(id),
      display_name: (entry as BudgetCategoryEntry).display_name,
    }));
  }, [masterData?.budget_categories]);

  const sortedCategories = useMemo(() => {
    return [...categories]
      .sort((a, b) => a.id - b.id);
  }, [categories]);

  useEffect(() => {
    const controller = new AbortController();

    async function loadData() {
      try {
        const [budgetsRes, cityRes, agentsRes] = await Promise.all([
          fetchApi<BudgetData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/budgets"),
            { signal: controller.signal },
          ),
          fetchApi<CityData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city"),
            { signal: controller.signal },
          ),
          fetchApi<{ agents: BudgetAgent[] }>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/budgets/agents"),
            { signal: controller.signal },
          ),
        ]);

        setBudgetData(budgetsRes);
        // 現在の現預金は予算APIが市政府エージェントから解決して返すため、
        // エージェント詳細APIを別途叩く必要はない。
        setCityReserves(budgetsRes.current_reserves ?? 0);
        setCurrentEpoch(cityRes.city.epoch);
        setCurrentDate(cityRes.city.current_date);
        setSimulationDays(cityRes.city.simulation_days);
        setBudgetAgents(agentsRes.agents);

        // プレイヤーの比率入力（％）を初期化する。
        // 基準額（必要金額）は施設別リスト（budgetAgents）から computeBaseAmounts で導出する。
        const ratios: Record<string, number> = {};
        if (masterData?.budget_categories) {
          for (const catId of Object.keys(masterData.budget_categories)) {
            if (catId === LEDGER_RESERVE_KEY) continue;
            const frac = budgetsRes.category_ratio?.[catId];
            ratios[catId] = Math.min(
              frac !== undefined ? Math.round(frac * 100) : 100,
              RATIO_MAX_PERCENT,
            );
          }
        }
        // ロード直後はサーバと同値＝未変更。ここを起点に差分だけを自動保存する。
        savedRatiosRef.current = ratios;
        latestRatiosRef.current = ratios;
        setPendingRatios(ratios);
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      }
    }

    loadData();
    return () => controller.abort();
  }, [apiBaseUrl, masterData]);

  // 内訳積み上げバー・カテゴリカードの対象(貯蓄・市政を除く全予算カテゴリ)。
  const visibleCategories = useMemo(
    () => sortedCategories.filter((c) => !HIDDEN_CATEGORY_KEYS.has(String(c.id))),
    [sortedCategories],
  );

  // 施設一覧に表示するカテゴリ。未選択(初期値0)や、カードの無い非表示カテゴリ
  // (市政・貯蓄)を指している場合は表示対象の先頭へフォールバックする
  // (カードが無いカテゴリは選び直せないため、そこへ留まらせない)。
  const activeCategory = useMemo(() => {
    if (visibleCategories.some((c) => c.id === activeCategoryId)) return activeCategoryId;
    return visibleCategories[0]?.id ?? 0;
  }, [visibleCategories, activeCategoryId]);

  // 遅延実行(デバウンス・アンマウント・保存完了後の追い送り)から常に最新の saveRatios を
  // 呼ぶための参照。実体は下の effect で差し込む。
  const saveRatiosRef = useRef<() => void>(() => {});

  /**
   * 画面上の比率をサーバへ反映する。確定ボタンは持たず、この関数がデバウンス・
   * スライダーを離した時・編集終了時・ウィンドウを閉じた時に呼ばれる。
   * 送信は毎回全カテゴリの比率を含む冪等なPATCHなので、取りこぼしても
   * 次の1回で正しい状態に収束する。
   */
  const saveRatios = useCallback(async () => {
    if (savingRef.current) return; // 進行中の保存の完了時に最新値で呼び直される
    const ratios = latestRatiosRef.current;
    const saved = savedRatiosRef.current;
    if (saved === null || ratiosEqual(saved, ratios)) return;

    savingRef.current = true;
    savedRatiosRef.current = ratios;
    setIsSaving(true);
    let ok = false;
    try {
      // 比率(％)を 0〜1.5 の割合に変換して送信する。
      // 実際の予算額と自動調整はバックエンドがフル稼働必要額から導出する。
      // 維持優先度（budget_tiers）は現状UIから編集できないため送信しない
      // （バックエンドの既定値/既存設定がそのまま維持される）。
      const ratioFractions: Record<string, number> = {};
      for (const [k, pct] of Object.entries(ratios)) {
        ratioFractions[k] = Math.max(pct, 0) / 100;
      }
      const res = await fetchApi<BudgetData>(
        buildApiUrl(apiBaseUrl, "/api/v1/city/budgets"),
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ category_ratio: ratioFractions }),
        },
      );
      // サーバが再導出した予算枠・実効比率・自動調整フラグで表示を同期する。
      setBudgetData(res);
      ok = true;
    } catch (err) {
      // 送信できなかった分は未保存に戻し、次の操作で再送されるようにする。
      savedRatiosRef.current = saved;
      showNotification({
        message: t("budget.saveFailed", { defaultValue: "予算の保存に失敗しました" }),
        durationMs: 4000,
      });
      console.error("Failed to save budget ratios", err);
    } finally {
      savingRef.current = false;
      setIsSaving(false);
    }
    // 送信中に動かされた分を送り直す(失敗時は無限リトライになるので送らない)。
    if (ok && !ratiosEqual(savedRatiosRef.current ?? {}, latestRatiosRef.current)) {
      void saveRatiosRef.current();
    }
  }, [apiBaseUrl, t]);

  useEffect(() => {
    saveRatiosRef.current = () => void saveRatios();
  }, [saveRatios]);

  // 比率が変わったら少し待って自動保存する。連続操作のたびにタイマーを張り直すことで
  // ドラッグ中の連続PATCHを1回にまとめる。
  useEffect(() => {
    latestRatiosRef.current = pendingRatios;
    if (savedRatiosRef.current === null) return; // 初期ロード完了前
    if (ratiosEqual(savedRatiosRef.current, pendingRatios)) return;
    if (saveTimerRef.current !== null) window.clearTimeout(saveTimerRef.current);
    saveTimerRef.current = window.setTimeout(() => {
      saveTimerRef.current = null;
      void saveRatiosRef.current();
    }, AUTO_SAVE_DEBOUNCE_MS);
  }, [pendingRatios]);

  /** デバウンスを待たずに即保存する(スライダーを離した・編集を終えた) */
  const commitRatios = useCallback(() => {
    if (saveTimerRef.current !== null) {
      window.clearTimeout(saveTimerRef.current);
      saveTimerRef.current = null;
    }
    void saveRatiosRef.current();
  }, []);

  // アンマウント時の取りこぼし回収。ポップアップウィンドウは×で閉じても非表示になるだけで
  // ここは走らないため、これは別ウィンドウへ使い回されたときの保険（通常は
  // スライダーを離した時点で保存済み）。
  useEffect(
    () => () => {
      if (saveTimerRef.current !== null) window.clearTimeout(saveTimerRef.current);
      void saveRatiosRef.current();
    },
    [],
  );

  // プレイヤーの比率入力（％）を更新する。全カテゴリ 0〜150 でクランプ。
  // latestRatiosRef もここで同時に更新する: スライダーを離した瞬間(onPointerUp)の
  // commitRatios は、直前の onChange による再レンダリング・effect を待たずに走りうるため。
  const handleRatioChange = useCallback((catId: number, valStr: string) => {
    let pct = parseFloat(valStr);
    if (isNaN(pct) || pct < 0) pct = 0;
    pct = Math.min(pct, RATIO_MAX_PERCENT);
    const next = { ...latestRatiosRef.current, [String(catId)]: pct };
    latestRatiosRef.current = next;
    setPendingRatios(next);
  }, []);

  // 各カテゴリの維持優先度（Tier）。現状UIからは編集できないため、サーバ設定の読み取り専用。
  // 左ペインのグループ分けと、資金不足時の自動調整プレビュー（deriveBudgets）に使う。
  const tiers = useMemo(() => budgetData?.budget_tiers ?? {}, [budgetData]);

  // 予算導出の基準額（フル稼働必要額）。予算パネルの施設別リストと
  // 同一の per施設値の合計として求めるため、施設一覧の合計と常に一致する。
  const baseAmounts = useMemo(
    () => computeBaseAmounts(budgetAgents),
    [budgetAgents],
  );

  // 比率 × 基準額から実際の配分予算を導出し、資金不足なら自動調整する（バックエンドと同一ロジック）。
  const derived = useMemo(
    () => deriveBudgets(pendingRatios, baseAmounts, tiers, cityReserves),
    [pendingRatios, baseAmounts, tiers, cityReserves],
  );
  const pendingBudgets = derived.budgets;
  const desiredBudgets = derived.desired;
  // バナーは「編集中のプレビューで自動調整が起きる」場合に加え、バックエンドが直近の配分で
  // 実際に自動調整した場合（budget_auto_adjusted）にも表示する。ロード直後は両者は一致するが、
  // 配分後に現金が変動した場合などはサーバ側の実結果のみが真になるため、両方を見る。
  const autoAdjusted = derived.autoAdjusted || Boolean(budgetData?.budget_auto_adjusted);

  /**
   * 現金推移グラフのデータ。過去エポック末の確定現預金＋現在の現預金の棒に、
   * 直近トレンドを外挿した点線を繋げる。
   */
  const reservesChartData = useMemo(
    () =>
      buildReservesChartData(
        budgetData?.reserves_history ?? [],
        currentEpoch,
        cityReserves,
      ),
    [budgetData?.reserves_history, currentEpoch, cityReserves],
  );

  /**
   * 収支バランスの積み上げ系列とデータ行。
   *
   * X軸は「収入(前エポック実績)」「支出(次エポック予算)」の2本が基本で、現金不足で
   * 希望額が削られている場合だけ「圧縮前(削減前の希望額)」の3本目が加わる。
   * 収入側は税(TAX_REVENUE_META)＋公営事業収入(buildBusinessRevenueItems)、
   * 支出側は編集中の予算枠(pendingBudgets)をカテゴリ別に積み上げる。支出側の色は
   * 施設一覧・カテゴリカードと同じ spendCategoryColor で統一する。
   * 値が0の系列は凡例が無駄に長くなるだけなので、系列ごと落とす。
   */
  const balanceChart = useMemo(() => {
    if (!budgetData) {
      return {
        series: [] as BalanceSeries[],
        preSeries: [] as BalanceSeries[],
        rows: [] as Record<string, number | string>[],
        totalRevenue: 0,
        totalExpense: 0,
        totalDesired: 0,
        compressed: false,
      };
    }

    const revenueRow: Record<string, number | string> = {
      name: t("budget.balanceRevenue", { defaultValue: "収入" }),
      row: BALANCE_ROW_REVENUE,
    };
    const expenseRow: Record<string, number | string> = {
      name: t("budget.balanceExpense", { defaultValue: "支出" }),
      row: BALANCE_ROW_EXPENSE,
    };
    const preRow: Record<string, number | string> = {
      name: t("budget.balancePreCompress", { defaultValue: "圧縮前" }),
      row: BALANCE_ROW_PRE_COMPRESS,
    };

    // --- 収入側: 税目 + 公営事業収入 ---
    const revenueSeries: BalanceSeries[] = [];
    let totalRevenue = 0;
    for (const meta of TAX_REVENUE_META) {
      const value = budgetData.total_revenue[meta.key] ?? 0;
      totalRevenue += value;
      if (value <= 0) continue;
      const key = `rev_tax_${meta.key}`;
      revenueSeries.push({ key, label: t(meta.labelKey), color: meta.color });
      revenueRow[key] = value;
    }
    // 公営事業収入(公共サービス売上・公営住宅家賃)。支出側と同じカテゴリ粒度・同色で描く。
    const businessItems = buildBusinessRevenueItems(
      budgetData.total_revenue,
      masterData?.budget_categories,
      t,
    );
    businessItems.forEach((item, index) => {
      totalRevenue += item.value;
      if (item.value <= 0) return;
      const key = `rev_biz_${index}`;
      revenueSeries.push({ key, label: item.label, color: item.color });
      revenueRow[key] = item.value;
    });

    // --- 支出側: カテゴリ別の確定予算と、削減前の希望額 ---
    const expenseSeries: BalanceSeries[] = [];
    const preSeries: BalanceSeries[] = [];
    let totalExpense = 0;
    let totalDesired = 0;
    for (const c of visibleCategories) {
      const catKey = String(c.id);
      const budget = pendingBudgets[catKey] ?? 0;
      const desired = desiredBudgets[catKey] ?? 0;
      totalExpense += budget;
      totalDesired += desired;
      if (budget <= 0 && desired <= 0) continue;
      const label = t(`budget.category.${c.id}`, { defaultValue: c.display_name });
      const color = spendCategoryColor(c.id);
      expenseSeries.push({ key: `exp_${catKey}`, label, color });
      preSeries.push({ key: `pre_${catKey}`, label, color });
      expenseRow[`exp_${catKey}`] = budget;
      preRow[`pre_${catKey}`] = desired;
    }

    // 希望額が確定予算を上回っている＝現金不足で支出が圧縮された状態。
    const compressed = totalDesired > totalExpense + 1;
    const rows = compressed
      ? [revenueRow, expenseRow, preRow]
      : [revenueRow, expenseRow];

    return {
      series: [...revenueSeries, ...expenseSeries],
      preSeries,
      rows,
      totalRevenue,
      totalExpense,
      totalDesired,
      compressed,
    };
  }, [budgetData, masterData, visibleCategories, pendingBudgets, desiredBudgets, t]);

  const filteredAgents = useMemo(() => {
    if (!budgetAgents) return [];
    const catId = activeCategory;
    return budgetAgents
      .filter((agent) => agent.category_id === catId)
      .map((agent) => ({
        ...agent,
        rowKeyStr: rowKey(agent),
        name: rowDisplayName(agent, t),
        // 施設別の「必要金額」。この行の合計（agentsTotal.projected）はカテゴリの
        // 必要金額（baseAmounts[catId]）と常に一致する（同一の projected_expense を使うため）。
        projected_preview: agent.projected_expense,
      }));
  }, [budgetAgents, activeCategory, t]);

  const agentsTotal = useMemo(() => {
    let actual = 0;
    let projected = 0;
    for (const agent of filteredAgents) {
      actual += agent.actual_expense;
      projected += agent.projected_preview;
    }
    return { actual, projected };
  }, [filteredAgents]);

  const locale = resolveIntlLocale(i18n.language);
  const nextEpochStart = currentDate;
  const nextEpochEnd = currentDate + simulationDays;
  const nextPeriod = formatEpochDateRange(nextEpochStart, nextEpochEnd, locale, { hideDay: true, simulationDays });

  /** 収支差(収入 − 確定支出)。プラスなら黒字、マイナスなら現金の取り崩し。 */
  const netBalance = balanceChart.totalRevenue - balanceChart.totalExpense;

  if (error) {
    return <div className="budget-window__error">{error}</div>;
  }

  if (!budgetData || categories.length === 0 || !budgetAgents) {
    return <div className="budget-window__loading">{t("common.loading")}</div>;
  }

  return (
    <div className="budget-window">
      {/* ヘッダー */}
      <div className="budget-window__header">
        <div className="budget-window__reserves-card">
          <span className="budget-window__reserves-card-label">{t("budget.reservesCardTitle", { defaultValue: "資金" })}</span>
          <span className="budget-window__reserves-card-value"><Money value={cityReserves} unit /></span>
        </div>
        <div className="budget-window__period-info">
          {t("budget.planPeriod", { period: nextPeriod })}
        </div>
      </div>

      {/* 資金不足による自動調整バナー */}
      {autoAdjusted && (
        <div className="budget-window__auto-adjust-banner">
          {t("budget.autoAdjustedNotice", { defaultValue: "資金不足のため予算が自動調整されました" })}
        </div>
      )}

      {/* メインの2分割: 左1/3=予算スライダー / 右2/3=現金推移+施設一覧+収支バランス */}
      <div className="budget-window__split-container">

        {/* 左カラム: 維持優先度(Tier)ごとにグループ化したカテゴリカード。
            Tier 1 が最優先維持、Tier 4 が資金不足時に最初に削減される。
            各カードは右上のワッペンに比率(％)を出し、「編集」トグルON の間だけ
            スライダーを表示する(閲覧時は数字だけで読める)。 */}
        <div className="budget-window__left-pane">
          <div className="budget-window__pane-header">
            <h3 className="budget-window__section-title">
              {t("budget.allocationTitle", { defaultValue: "予算配分" })}
            </h3>
            {/* 自動保存の送信中表示。確定ボタンが無いので、送信が走ったことはここでしか分からない。 */}
            {isSaving && (
              <span className="budget-window__saving-note">
                {t("budget.autoSaving", { defaultValue: "保存中..." })}
              </span>
            )}
            <button
              type="button"
              className={[
                "budget-window__edit-btn",
                isEditingRatios && "budget-window__edit-btn--active",
              ]
                .filter(Boolean)
                .join(" ")}
              aria-pressed={isEditingRatios}
              onClick={() => {
                setIsEditingRatios((prev) => !prev);
                commitRatios(); // 編集を終える操作でデバウンス待ちを打ち切って確定させる
              }}
            >
              {isEditingRatios
                ? t("budget.editRatiosDone", { defaultValue: "編集を終える" })
                : t("budget.editRatios", { defaultValue: "編集" })}
            </button>
          </div>

          <div className="budget-window__tier-list">
            {TIER_ORDER.map((tier) => {
              const tierCategories = visibleCategories.filter(
                (cat) => getCategoryTier(String(cat.id), tiers) === tier,
              );
              if (tierCategories.length === 0) return null;
              return (
                <div key={tier} className={`budget-tier-group budget-tier-group--tier${tier}`}>
                  <div className="budget-tier-group__title">{t(`budget.tier${tier}`)}</div>
                  <div className="budget-tier-group__cards">
                    {tierCategories.map((cat) => {
                      const catId = cat.id;
                      const catKey = String(catId);
                      const ratioPct = pendingRatios[catKey] ?? 100;
                      const isSelected = activeCategory === catId;
                      const iconUrl = resolveLedgerIcon(catId);
                      return (
                        <div
                          key={catId}
                          className={`budget-category-card ${isSelected ? "budget-category-card--active" : ""}`}
                          onClick={() => setActiveCategoryId(catId)}
                        >
                          {/* 右上のワッペン: 比率(％)。スライダー非表示時の唯一の数値表示。 */}
                          <span className="budget-category-card__badge">
                            {Math.round(ratioPct)}%
                          </span>
                          {/* 左: カテゴリアイコン(正方形)。画像が無いカテゴリは空枠。 */}
                          <div className="budget-category-card__icon">
                            {iconUrl && <img src={iconUrl} alt="" draggable={false} />}
                          </div>
                          {/* 右: タイトル + 配分額(+ 編集中はスライダー) */}
                          <div className="budget-category-card__body">
                            <span className="budget-category-card__name">
                              {t(`budget.category.${catId}`, { defaultValue: cat.display_name })}
                            </span>
                            <span className="budget-category-card__amount">
                              <Money value={pendingBudgets[catKey] ?? 0} unit />
                            </span>
                            {/* 予算スライダー: フル稼働必要額に対する比率(0〜150%) */}
                            {isEditingRatios && (
                              <div
                                className="budget-category-card__slider-row"
                                title={t("budget.ratioSliderHint", {
                                  defaultValue: "フル稼働時の見込み費用に対する割合(%)。100%で全施設が稼働率目標どおり稼働できる予算になる",
                                })}
                              >
                                <input
                                  type="range"
                                  min={0}
                                  max={RATIO_MAX_PERCENT}
                                  step={5}
                                  value={Math.round(ratioPct)}
                                  onChange={(e) => handleRatioChange(catId, e.target.value)}
                                  className="budget-category-card__slider"
                                  onClick={(e) => e.stopPropagation()}
                                  /* つまみを離した時点が「確定」。デバウンスを待たずに送る。 */
                                  onPointerUp={commitRatios}
                                  onBlur={commitRatios}
                                />
                              </div>
                            )}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </div>
              );
            })}
          </div>
        </div>

        {/* 右エリア: 上=現金推移、下=施設一覧+収支バランスの左右分割 */}
        <div className="budget-window__right-area">
          {/* 上段: 市政府の現金(reserves)推移。過去エポック末の確定値＋現在を棒で、
              直近トレンドの外挿を点線で描く。 */}
          <div className="budget-window__reserves-section">
            <h3 className="budget-window__section-title">
              {t("budget.reservesTrendTitle", { defaultValue: "現金推移" })}
            </h3>
            <div className="budget-window__reserves-chart">
              <ResponsiveContainer width="100%" height="100%">
                <ComposedChart
                  data={reservesChartData}
                  margin={{ top: 4, right: 16, left: 0, bottom: 0 }}
                >
                  <CartesianGrid strokeDasharray="3 3" stroke={CHART_GRID_LINE} vertical={false} />
                  <XAxis
                    dataKey="label"
                    tick={{ fontSize: "0.93rem", fill: CHART_AXIS_TEXT }}
                    tickLine={false}
                    stroke={CHART_AXIS_LINE}
                  />
                  <YAxis
                    tick={{ fontSize: "0.93rem", fill: CHART_AXIS_TEXT }}
                    tickLine={false}
                    width={remPx(4.81)}
                    tickFormatter={(v) => formatCompactAmount(Number(v), locale, false)}
                    stroke={CHART_AXIS_LINE}
                  />
                  <RechartsTooltip
                    content={
                      <ChartValueTooltip
                        locale={locale}
                        formatLabel={(label) =>
                          t("budget.reservesEpochLabel", {
                            defaultValue: "エポック {{n}}",
                            n: label,
                          })
                        }
                      />
                    }
                    cursor={{ fill: CHART_CURSOR_FILL }}
                  />
                  <Legend wrapperStyle={{ fontSize: "0.93rem" }} />
                  {/* 債務超過(現預金マイナス)が一目でわかるようにゼロ線を引く */}
                  <ReferenceLine y={0} stroke={CHART_ZERO_LINE} />
                  {/* 実績と現在は同じ stackId に載せることで、同じX位置で1本の棒として描かれる */}
                  <Bar
                    dataKey="actual"
                    stackId="reserves"
                    name={t("budget.reservesActual", { defaultValue: "エポック末実績" })}
                    fill={COLOR_RESERVES_ACTUAL}
                    isAnimationActive={false}
                  />
                  <Bar
                    dataKey="current"
                    stackId="reserves"
                    name={t("budget.reservesCurrent", { defaultValue: "現在" })}
                    fill={COLOR_RESERVES_CURRENT}
                    isAnimationActive={false}
                  />
                  <Line
                    type="linear"
                    dataKey="forecast"
                    name={t("budget.reservesForecast", { defaultValue: "予測(直近トレンド)" })}
                    stroke={COLOR_RESERVES_FORECAST}
                    strokeWidth={2}
                    strokeDasharray="5 4"
                    dot={{ r: 2, fill: COLOR_RESERVES_FORECAST }}
                    connectNulls={false}
                    isAnimationActive={false}
                  />
                </ComposedChart>
              </ResponsiveContainer>
            </div>
          </div>

          {/* 下段: 左=選択カテゴリの施設一覧、右=収支バランス */}
          <div className="budget-window__bottom-row">
            {/* 下段左: 選択カテゴリのエージェント明細 */}
            <div className="budget-window__agents-pane">
              <div className="budget-agent-table-wrapper">
                <div className="budget-agent-title-row">
                  <h3 className="budget-agent-title">
                    {t(`budget.category.${activeCategory}`, {
                      defaultValue: categories.find((c) => c.id === activeCategory)?.display_name
                    })} {t("budget.details", { defaultValue: "詳細" })}
                  </h3>
                </div>

                <table className="budget-agent-table">
                  <thead>
                    <tr>
                      <th className="budget-agent-table__header">{t("budget.agentName", { defaultValue: "エージェント" })}</th>
                      <th className="budget-agent-table__header budget-agent-table__header--num">{t("budget.prevActual", { defaultValue: "前実績" })}</th>
                      <th className="budget-agent-table__header budget-agent-table__header--num">{t("budget.nextProjected", { defaultValue: "次見込み" })}</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr className="budget-agent-table__row--total">
                      <td className="budget-agent-table__cell budget-agent-table__cell--total">{t("budget.total", { defaultValue: "合計" })}</td>
                      <td className="budget-agent-table__cell budget-agent-table__cell--num budget-agent-table__cell--total">
                        <Money value={agentsTotal.actual} unit />
                      </td>
                      <td className="budget-agent-table__cell budget-agent-table__cell--num budget-agent-table__cell--total">
                        <Money value={agentsTotal.projected} unit />
                      </td>
                    </tr>
                    {filteredAgents.map((agent) => (
                      <tr key={agent.rowKeyStr} className="budget-agent-table__row">
                        <td className="budget-agent-table__cell budget-agent-table__cell--name" title={agent.name}>
                          <div className="budget-agent-name-line">{agent.name}</div>
                          {agent.status === STATUS_CONSTRUCTING && agent.remaining_epochs != null && (
                            <div className="budget-util-note">
                              {t("budget.remainingEpochs", {
                                defaultValue: "残り約{{n}}エポック",
                                n: Math.ceil(agent.remaining_epochs),
                              })}
                            </div>
                          )}
                        </td>
                        <td className="budget-agent-table__cell budget-agent-table__cell--num">
                          <Money value={agent.actual_expense} unit />
                        </td>
                        <td className="budget-agent-table__cell budget-agent-table__cell--num">
                          <Money value={agent.projected_preview} unit />
                        </td>
                      </tr>
                    ))}
                    {filteredAgents.length === 0 && (
                      <tr>
                        <td colSpan={3} className="budget-agent-table__cell--empty">
                          {t("budget.noAgentsInCategory", { defaultValue: "このカテゴリに所属するエージェントはいません" })}
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            </div>

            {/* 下段右: 収支バランス。左の棒=収入(前エポック実績)、右の棒=支出(次エポック予算)。
                現金不足で支出が圧縮された場合のみ、圧縮前の希望額を薄い3本目の棒で描く。 */}
            <div className="budget-window__balance-section">
              <div className="budget-window__balance-header">
                <h3 className="budget-window__section-title">
                  {t("budget.balanceTitle", { defaultValue: "収支バランス" })}
                </h3>
                <span className="budget-window__balance-note">
                  {t("budget.balanceNote", {
                    defaultValue: "収入=前エポック実績 / 支出=次エポック予算",
                  })}
                </span>
              </div>
              {/* 収支差のサマリ: 収入 − 支出。マイナスなら現金の取り崩しになる。 */}
              <div className="budget-window__balance-summary">
                <span className="budget-window__balance-summary-label">
                  {t("budget.balanceNet", { defaultValue: "収支差" })}
                </span>
                <span
                  className={[
                    "budget-window__balance-summary-value",
                    netBalance < 0
                      ? "budget-window__balance-summary-value--deficit"
                      : "budget-window__balance-summary-value--surplus",
                  ].join(" ")}
                >
                  {netBalance >= 0 ? "+" : "-"}
                  <Money value={Math.abs(netBalance)} unit />
                </span>
              </div>
              <div className="budget-window__balance-chart">
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart
                    data={balanceChart.rows}
                    margin={{ top: 4, right: 8, left: 0, bottom: 0 }}
                    barCategoryGap="28%"
                  >
                    <CartesianGrid strokeDasharray="3 3" stroke={CHART_GRID_LINE} vertical={false} />
                    <XAxis
                      dataKey="name"
                      tick={{ fontSize: "1.02rem", fill: CHART_AXIS_TEXT }}
                      tickLine={false}
                      stroke={CHART_AXIS_LINE}
                    />
                    <YAxis
                      tick={{ fontSize: "0.93rem", fill: CHART_AXIS_TEXT }}
                      tickLine={false}
                      width={remPx(4.81)}
                      tickFormatter={(v) => formatCompactAmount(Number(v), locale, false)}
                      stroke={CHART_AXIS_LINE}
                    />
                    <RechartsTooltip
                      content={
                        <ChartValueTooltip
                          locale={locale}
                          totalLabel={t("budget.total", { defaultValue: "合計" })}
                        />
                      }
                      cursor={{ fill: CHART_CURSOR_FILL }}
                    />
                    <Legend wrapperStyle={{ fontSize: "0.93rem" }} />
                    {balanceChart.series.map((s) => (
                      <Bar
                        key={s.key}
                        dataKey={s.key}
                        stackId="balance"
                        name={s.label}
                        fill={s.color}
                        maxBarSize={72}
                        isAnimationActive={false}
                      />
                    ))}
                    {/* 圧縮前の支出。確定支出と同じ色を薄く描き、凡例には出さない
                        (同じカテゴリが2重に並ぶため)。 */}
                    {balanceChart.compressed &&
                      balanceChart.preSeries.map((s) => (
                        <Bar
                          key={s.key}
                          dataKey={s.key}
                          stackId="balance"
                          name={s.label}
                          fill={s.color}
                          fillOpacity={PRE_COMPRESS_OPACITY}
                          legendType="none"
                          maxBarSize={72}
                          isAnimationActive={false}
                        />
                      ))}
                  </BarChart>
                </ResponsiveContainer>
              </div>
            </div>
          </div>
        </div>

      </div>
    </div>
  );
}
