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
import { formatCompactAmount } from "../../utils/money";
import { hasRoad, roadDisplayName } from "../road/roadApi";
import { FlatTabs } from "../common/FlatTabs";
import { useAutoSave } from "../../hooks/useAutoSave";
import { BudgetOverlay } from "./BudgetOverlay";
import { TaxRatesPane } from "./TaxRatesPane";
import {
  ResponsiveContainer,
  BarChart,
  ComposedChart,
  LineChart,
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
} from "../common/fiscalSeries";
import "./CityBudgetWindow.css";
import { useRemPx } from "../../hooks/useRemPx";
import {
  RATIO_MAX_PERCENT,
  deriveBudgets,
  expectedSpending,
  getCategoryTier,
  ratioPercent,
} from "../../utils/budgetForecast";

type BudgetAgent = {
  kind?: "agent" | "path" | "parcel" | "welfare";
  agent_id?: number;
  path_id?: number;
  parcel_id?: number;
  /** Social security benefit-type row (no actual entity). welfare_kind corresponds to i18n keys budget.welfare.*. */
  welfare_id?: number;
  welfare_kind?: string;
  /** Path rows return the section name unchanged (default empty). The UI supplies a display name through rowDisplayName. */
  name: string;
  /** The road containing the path row (VALUE_NONE if none) and its name (empty if none). */
  road_id?: number;
  road_name?: string;
  category_id: number;
  variant?: number;
  status: number;
  actual_expense: number;
  projected_expense: number;
  /** Expected payment next epoch. Excludes raw materials covered by inventory, so it is at most projected_expense (required amount). */
  projected_payment: number;
  utilization_target?: number;
  construction_cost_per_epoch?: number;
  remaining_epochs?: number | null;
};

/**
 * Row display name. Benefit-type rows have no entity, so resolve through i18n (budget.welfare.*),
 * falling back to the backend name. For paths with an empty section name, supply "Road name (Section #id)"
 * if assigned to a road, otherwise "Path #id", matching the path detail header.
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

/** Key uniquely identifying an agent/road/parcel/benefit type (used for utilization editing state and PATCH targets). */
function rowKey(a: BudgetAgent): string {
  if (a.kind === "path") return `path:${a.path_id}`;
  if (a.kind === "parcel") return `parcel:${a.parcel_id}`;
  if (a.kind === "welfare") return `welfare:${a.welfare_id}`;
  return `agent:${a.agent_id}`;
}

/** Whether ratio maps are equal (autosave check). Keys are fixed at initialization, so compare values only. */
function ratiosEqual(a: Record<string, number>, b: Record<string, number>): boolean {
  const keys = Object.keys(a);
  if (keys.length !== Object.keys(b).length) return false;
  return keys.every((k) => a[k] === b[k]);
}

/** One end-of-epoch snapshot of city government cash (an element of backend reserves_history). */
type ReservesHistoryPoint = {
  epoch: number;
  reserves: number;
};

/** One past epoch's actual tax revenue (backend history element). revenues maps ledger keys (31-35) to actual amounts. */
type TaxRevenueHistoryPoint = {
  epoch: number;
  revenues: Record<string, number>;
};

type BudgetData = {
  budgets: Record<string, number>;
  prev_budgets: Record<string, number>;
  total_expense: Record<string, number>;
  total_revenue: Record<string, number>;
  budget_tiers?: Record<string, number>;
  // The player sets budgets as a ratio of the required amount, derived from the following values.
  category_ratio?: Record<string, number>;
  effective_category_ratio?: Record<string, number>;
  // Snapshot of previous epoch actual spending (for the "previous actual" bar, not the budget basis).
  prev_total_expense?: Record<string, number>;
  // Required amounts per category (the budget derivation basis and ratio slider denominator).
  // Expected cost for one epoch with category utilization at 100% and each facility operating at
  // its own utilization target. The backend estimates construction (8) each time, including new orders while paused.
  projected_required?: Record<string, number>;
  savings_target?: number;
  savings?: number;
  budget_auto_adjusted?: boolean;
  // End-of-epoch city government cash history (oldest first) and current cash during the player operation phase.
  reserves_history?: ReservesHistoryPoint[];
  current_reserves?: number;
  history?: TaxRevenueHistoryPoint[];
};

type CityData = {
  city: {
    city_agent_id: number;
    epoch: number;
    current_date: number;
    simulation_days: number;
  };
};

const LEDGER_RESERVE_KEY = "10";

/**
 * City administration category (1). No actual spending agents currently belong to it: district
 * representative agents' construction service expenses are assigned to construction (8)/repair (9),
 * and the city government has no operating expenses. Hide this empty category from players.
 * Backend constants/master rows remain for future extensions (e.g. City Hall operating expenses),
 * and always contribute ¥0 to budget derivation, so they are harmless.
 */
const LEDGER_CITYHALL_KEY = "1";

/** Hidden budget categories (savings is not a spending allocation; city administration is empty). */
const HIDDEN_CATEGORY_KEYS = new Set([LEDGER_RESERVE_KEY, LEDGER_CITYHALL_KEY]);

/** Maximum past epochs in the cash trend chart (older history would crowd the X axis and is discarded). */
const RESERVES_HISTORY_MAX = 24;
/** Number of recent points, including the current one, used for the least-squares forecast slope. */
const FORECAST_LOOKBACK = 6;
/** Number of future epochs to forecast. */
const FORECAST_HORIZON = 4;

/** Cash trend colors: actual = dark blue; current = accent color; forecast = gray dashed line. */
const COLOR_RESERVES_ACTUAL = "#2c5687";
const COLOR_RESERVES_CURRENT = "#4ea1ff";
const COLOR_RESERVES_FORECAST = "#6b8299";

/**
 * Chart axis/grid colors. Match shared flat design tokens (--flat-* in App.css), but keep literal
 * values because recharts cannot resolve CSS variables passed directly as SVG attributes.
 */
const CHART_AXIS_TEXT = "#a4bccc";
const CHART_AXIS_LINE = "#2c3646";
const CHART_GRID_LINE = "#22303f";
const CHART_CURSOR_FILL = "rgba(78, 161, 255, 0.08)";
const CHART_ZERO_LINE = "#ff9a9a";

/** Identifiers for income/expenditure balance bars (X-axis categories). */
const BALANCE_ROW_REVENUE = "revenue";
const BALANCE_ROW_EXPENSE = "expense";
const BALANCE_ROW_PRE_COMPRESS = "preCompress";

/** Opacity of the pre-reduction spending bar (draw lightly to distinguish it from committed spending). */
const PRE_COMPRESS_OPACITY = 0.3;

/** Maintenance priority group order in the left pane (Tier 1, highest priority, first). */
const TIER_ORDER = [1, 2, 3, 4] as const;

/**
 * Sum per-facility values in the budget panel's facility list by category.
 * The required amount (ratio slider denominator and card value) sums projected_expense,
 * so the card's required amount always equals the list's total "next forecast".
 * projected_expense is the expected cost for one epoch at each facility's own utilization_target;
 * backend estimate_projected_required and build_budget_rows share the same calculation source.
 * Effective utilization depends only on individual utilization_target values;
 * category-wide utilization no longer exists.
 */
function sumByCategory(
  budgetAgents: BudgetAgent[] | null,
  field: "projected_expense" | "projected_payment",
): Record<string, number> {
  const sums: Record<string, number> = {};
  if (!budgetAgents) return sums;
  for (const agent of budgetAgents) {
    const catKey = String(agent.category_id);
    if (catKey === LEDGER_RESERVE_KEY) continue;
    sums[catKey] = (sums[catKey] ?? 0) + agent[field];
  }
  return sums;
}

/** One cash trend chart row. Actual, current, and forecast are separate series with distinct colors/line styles. */
type ReservesChartRow = {
  /** X-axis label (epoch number). */
  label: string;
  /** Committed cash at the end of a past epoch. */
  actual?: number;
  /** Current cash during the player operation phase. Separate series to color only one bar brightly. */
  current?: number;
  /** Extrapolation of the recent trend. Set the same value at the current point to connect the dashed line to the bar. */
  forecast?: number;
};

/**
 * Build cash trend chart data.
 *
 * After bars for committed end-of-epoch cash (actual) and current cash (current), draw a dashed
 * forecast extrapolating the per-epoch change (slope) from least-squares regression over the last
 * FORECAST_LOOKBACK points, out to FORECAST_HORIZON epochs.
 * Anchor the forecast at the last observation so the line always starts at the current bar's height.
 * With only one point (no epoch run yet), no slope is available, so omit the forecast.
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

  // Observed values for regression (past committed values plus current). X spacing is uniform, so use array indices.
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

  // Align the dashed line's start with the current bar (anchor at the latest observation, not the regression endpoint).
  currentRow.forecast = currentReserves;
  for (let k = 1; k <= FORECAST_HORIZON; k++) {
    rows.push({
      label: String(currentEpoch + k),
      forecast: currentReserves + slope * k,
    });
  }
  return rows;
}

/** Total line color in the tax revenue trend chart. Neutral, avoiding tax colors in TAX_REVENUE_META. */
const COLOR_TAX_REVENUE_TOTAL = "#e2e8f0";

/**
 * Tax revenue trend data. One row per epoch with actual amounts by tax (ledger key) and total
 * for the five taxes. Same "total plus five taxes" structure as the analysis screen (data.py).
 */
function buildTaxRevenueChartData(history: TaxRevenueHistoryPoint[]): Record<string, number>[] {
  return history.map((entry) => {
    const row: Record<string, number> = { epoch: entry.epoch };
    let total = 0;
    for (const meta of TAX_REVENUE_META) {
      const value = Number(entry.revenues?.[meta.key] ?? 0);
      const safe = Number.isFinite(value) ? value : 0;
      row[meta.key] = safe;
      total += safe;
    }
    row.total = total;
    return row;
  });
}

/** Stacked income/expenditure balance series (one category or one tax). */
type BalanceSeries = {
  /** recharts dataKey: revenue = rev_*, committed spending = exp_*, pre-reduction spending = pre_*. */
  key: string;
  label: string;
  color: string;
};

/** Payload for one series passed by recharts to the tooltip (type only the required fields). */
type ChartTooltipEntry = {
  name?: string;
  value?: number | string;
  color?: string;
  dataKey?: string | number;
};

/**
 * Shared monetary chart tooltip.
 *
 * The default recharts tooltip includes empty rows for series with no value at the X position
 * (opposite stacked bars, undisplayed forecast lines, etc.). With many balance series this causes
 * mostly empty rows, so filter to series with values and optionally add a total row
 * (the full stacked bar height). recharts injects active / payload / label.
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
  /** Locale used to format amounts. */
  locale: string;
  /** If specified, show a total row with this label when at least two series are present. */
  totalLabel?: string;
  /** Heading (X-axis label) formatter. Display the label unchanged if omitted. */
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
            {formatCompactAmount(Number(p.value), locale, true)}
          </span>
        </div>
      ))}
      {totalLabel && items.length > 1 && (
        <div className="budget-chart-tooltip__row budget-chart-tooltip__row--total">
          <span className="budget-chart-tooltip__name">{totalLabel}</span>
          <span className="budget-chart-tooltip__value">
            {formatCompactAmount(total, locale, true)}
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
  // Primary player input: required-amount ratio by category (integer percentage, 0-150).
  const [pendingRatios, setPendingRatios] = useState<Record<string, number>>({});
  // Also store the latest displayed values in a ref so successive inputs accumulate without waiting for a rerender.
  const latestRatiosRef = useRef<Record<string, number>>({});
  const [budgetAgents, setBudgetAgents] = useState<BudgetAgent[] | null>(null);
  const [activeTab, setActiveTab] = useState<"budget" | "tax">("budget");
  /** Budget category whose facility details are open in the overlay (null means closed). */
  const [detailCategoryId, setDetailCategoryId] = useState<number | null>(null);
  const [isTaxSaving, setIsTaxSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Slider visibility toggle shared by budget and tax tabs. Hidden by default for viewing numeric badges only;
  // all cards show sliders only while the "Edit" button is active.
  const [isEditing, setIsEditing] = useState(false);

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

  /**
 * Apply displayed ratios to the server (autosave request).
 * Each idempotent PATCH includes all category ratios, so a missed update converges to the
 * correct state on the next request.
 */
  const saveRatios = useCallback(
    async (ratios: Record<string, number>) => {
      // Convert percentage ratios to proportions from 0 to 1.5 before sending.
      // The backend derives actual budgets and automatic adjustments from required amounts.
      // Do not send maintenance priorities (budget_tiers), as they cannot currently be edited in the UI;
      // backend defaults/existing settings remain intact.
      const ratioFractions: Record<string, number> = {};
      for (const [k, pct] of Object.entries(ratios)) {
        ratioFractions[k] = Math.max(pct, 0) / 100;
      }
      try {
        const res = await fetchApi<BudgetData>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/budgets"),
          {
            method: "PATCH",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ category_ratio: ratioFractions }),
          },
        );
        // Synchronize the display using server-derived budget allocations, effective ratios, and auto-adjustment flags.
        setBudgetData(res);
      } catch (err) {
        showNotification({
          message: t("budget.saveFailed", { defaultValue: "予算の保存に失敗しました" }),
          durationMs: 4000,
        });
        console.error("Failed to save budget ratios", err);
        throw err;
      }
    },
    [apiBaseUrl, t],
  );
  const {
    isSaving: isBudgetSaving,
    reset: resetRatios,
    update: updateRatios,
    commit: commitRatios,
  } = useAutoSave(saveRatios, ratiosEqual);

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
            buildApiUrl(apiBaseUrl, "/api/v1/city/"),
            { signal: controller.signal },
          ),
          fetchApi<{ agents: BudgetAgent[] }>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/budgets/agents"),
            { signal: controller.signal },
          ),
        ]);

        setBudgetData(budgetsRes);
        // The budget API resolves current cash from the city government agent,
        // so a separate agent detail API request is unnecessary.
        setCityReserves(budgetsRes.current_reserves ?? 0);
        setCurrentEpoch(cityRes.city.epoch);
        setCurrentDate(cityRes.city.current_date);
        setSimulationDays(cityRes.city.simulation_days);
        setBudgetAgents(agentsRes.agents);

        // Initialize the player's percentage inputs.
        // Derive base amounts (required amounts) from the facility list (budgetAgents) through sumByCategory.
        const ratios: Record<string, number> = {};
        if (masterData?.budget_categories) {
          for (const catId of Object.keys(masterData.budget_categories)) {
            if (catId === LEDGER_RESERVE_KEY) continue;
            ratios[catId] = ratioPercent(budgetsRes.category_ratio?.[catId]);
          }
        }
        // Immediately after load, values match the server and are unchanged. Autosave only later differences.
        resetRatios(ratios);
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
  }, [apiBaseUrl, masterData, resetRatios]);

  // Targets for breakdown stacked bars and category cards (all budget categories except savings and city administration).
  const visibleCategories = useMemo(
    () => sortedCategories.filter((c) => !HIDDEN_CATEGORY_KEYS.has(String(c.id))),
    [sortedCategories],
  );

  // Update the player's percentage inputs. Clamp every category to 0-150.
  const handleRatioChange = useCallback(
    (catId: number, valStr: string) => {
      let pct = parseFloat(valStr);
      if (isNaN(pct) || pct < 0) pct = 0;
      pct = Math.min(pct, RATIO_MAX_PERCENT);
      const next = { ...latestRatiosRef.current, [String(catId)]: pct };
      latestRatiosRef.current = next;
      setPendingRatios(next);
      updateRatios(next);
    },
    [updateRatios],
  );

  // Maintenance priority (Tier) per category. Read-only server settings, since the UI does not currently edit them.
  // Used for left-pane grouping and automatic adjustment previews (deriveBudgets) when funds are insufficient.
  const tiers = useMemo(() => budgetData?.budget_tiers ?? {}, [budgetData]);

  // Budget base amounts (required amounts). Sum the same per-facility values as the budget panel's
  // facility list, so these always match the list totals.
  const baseAmounts = useMemo(
    () => sumByCategory(budgetAgents, "projected_expense"),
    [budgetAgents],
  );
  const paymentAmounts = useMemo(
    () => sumByCategory(budgetAgents, "projected_payment"),
    [budgetAgents],
  );

  // Derive allocations from ratio × base amount and adjust automatically if funds are insufficient (same logic as backend).
  const derived = useMemo(
    () => deriveBudgets(pendingRatios, baseAmounts, tiers, cityReserves),
    [pendingRatios, baseAmounts, tiers, cityReserves],
  );
  const pendingBudgets = derived.budgets;
  const desiredBudgets = derived.desired;
  // Show the banner when the editing preview would automatically adjust allocations or when
  // the backend actually adjusted the latest allocation (budget_auto_adjusted). These match after loading,
  // but cash changes after allocation can make only the server result true, so check both.
  const autoAdjusted = derived.autoAdjusted || Boolean(budgetData?.budget_auto_adjusted);

  /**
 * Cash trend chart data. Connect bars for committed end-of-epoch cash and current cash
 * to a dashed line extrapolating the recent trend.
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

  const taxRevenueChartData = useMemo(
    () => buildTaxRevenueChartData(budgetData?.history ?? []),
    [budgetData?.history],
  );

  /**
 * Income/expenditure balance stacked series and data rows.
 *
 * The X axis normally has two bars: income (previous epoch actual) and spending (expected payment
 * next epoch). Only when cash shortages reduce spending, add a third pre-reduction bar at the
 * requested amount. Stack taxes (TAX_REVENUE_META) plus public enterprise income
 * (buildBusinessRevenueItems) for income, and expectedSpending by category for spending
 * (shared with the cockpit budget meter). Use spendCategoryColor as in facility lists/category cards.
 * Drop zero-value series entirely to avoid an unnecessarily long legend.
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
      name: t("budget.balanceExpense", { defaultValue: "支出見込み" }),
      row: BALANCE_ROW_EXPENSE,
    };
    const preRow: Record<string, number | string> = {
      name: t("budget.balancePreCompress", { defaultValue: "圧縮前" }),
      row: BALANCE_ROW_PRE_COMPRESS,
    };

    // --- Income: taxes and public enterprise income ---
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
    // Public enterprise income (public service sales and public housing rent). Same category granularity/colors as spending.
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

    // --- Spending: by category at committed budget and at pre-reduction requested amounts ---
    const expenseSeries: BalanceSeries[] = [];
    const preSeries: BalanceSeries[] = [];
    const expenses = expectedSpending(pendingBudgets, paymentAmounts);
    const desiredExpenses = expectedSpending(desiredBudgets, paymentAmounts);
    let totalExpense = 0;
    let totalDesired = 0;
    for (const c of visibleCategories) {
      const catKey = String(c.id);
      const expense = expenses[catKey] ?? 0;
      const desired = desiredExpenses[catKey] ?? 0;
      totalExpense += expense;
      totalDesired += desired;
      if (expense <= 0 && desired <= 0) continue;
      const label = t(`budget.category.${c.id}`, { defaultValue: c.display_name });
      const color = spendCategoryColor(c.id);
      expenseSeries.push({ key: `exp_${catKey}`, label, color });
      preSeries.push({ key: `pre_${catKey}`, label, color });
      expenseRow[`exp_${catKey}`] = expense;
      preRow[`pre_${catKey}`] = desired;
    }

    // Spending at requested amounts exceeds committed-budget spending when cash shortages have reduced spending.
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
  }, [budgetData, masterData, visibleCategories, pendingBudgets, desiredBudgets, paymentAmounts, t]);

  const filteredAgents = useMemo(() => {
    if (!budgetAgents || detailCategoryId === null) return [];
    return budgetAgents
      .filter((agent) => agent.category_id === detailCategoryId)
      .map((agent) => ({
        ...agent,
        rowKeyStr: rowKey(agent),
        name: rowDisplayName(agent, t),
        // Required amount per facility. This row total (agentsTotal.projected) always equals the category's
        // required amount (baseAmounts[catId]) because both use the same projected_expense values.
        projected_preview: agent.projected_expense,
      }));
  }, [budgetAgents, detailCategoryId, t]);

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

  /** Balance (income minus committed spending). Positive means surplus; negative means drawing down cash. */
  const netBalance = balanceChart.totalRevenue - balanceChart.totalExpense;

  const formatEpochLabel = (label: string | number | undefined) =>
    t("budget.reservesEpochLabel", { defaultValue: "エポック {{n}}", n: label });

  if (error) {
    return <div className="budget-window__error">{error}</div>;
  }

  if (!budgetData || categories.length === 0 || !budgetAgents) {
    return <div className="budget-window__loading">{t("common.loading")}</div>;
  }

  const detailCategory = categories.find((c) => c.id === detailCategoryId);

  return (
    <div className="budget-window">
      {/* Header. */}
      <div className="budget-window__header">
        <div className="budget-window__reserves-card">
          <span className="budget-window__reserves-card-label">{t("budget.reservesCardTitle", { defaultValue: "資金" })}</span>
          <span className="budget-window__reserves-card-value"><Money value={cityReserves} unit /></span>
        </div>
        <div className="budget-window__period-info">
          {t("budget.planPeriod", { period: nextPeriod })}
        </div>
      </div>

      {/* Automatic adjustment banner for insufficient funds. */}
      {autoAdjusted && (
        <div className="budget-window__auto-adjust-banner">
          {t("budget.autoAdjustedNotice", { defaultValue: "資金不足のため予算が自動調整されました" })}
        </div>
      )}

      {/* Main split: left 1/3 = budget/tax tabs; right 2/3 = tax revenue/cash trends and income/expenditure balance. */}
      <div className="budget-window__split-container">

        {/*
 * Left column: budget/tax tabs. Both display values in upper-right badges;
 * show sliders only while the "Edit" toggle is on (values alone suffice for viewing).
 */}
        <div className="budget-window__left-pane">
          <div className="budget-window__pane-header">
            <FlatTabs<"budget" | "tax">
              tabs={[
                { id: "budget", label: t("budget.tabBudget", { defaultValue: "予算" }) },
                { id: "tax", label: t("budget.tabTax", { defaultValue: "税率" }) },
              ]}
              active={activeTab}
              onChange={setActiveTab}
            />
            {/* Autosave sending indicator. Without a commit button, this is the only indication a request was sent. */}
            {(isBudgetSaving || isTaxSaving) && (
              <span className="budget-window__saving-note">
                {t("budget.autoSaving", { defaultValue: "保存中..." })}
              </span>
            )}
            <button
              type="button"
              className={[
                "budget-window__edit-btn",
                isEditing && "budget-window__edit-btn--active",
              ]
                .filter(Boolean)
                .join(" ")}
              aria-pressed={isEditing}
              onClick={() => {
                setIsEditing((prev) => !prev);
                commitRatios(); // End the debounce wait and commit when editing ends (the tax tab handles its own commit).
              }}
            >
              {isEditing
                ? t("budget.editRatiosDone", { defaultValue: "編集を終える" })
                : t("budget.editRatios", { defaultValue: "編集" })}
            </button>
          </div>

          {/*
 * Budget tab: category cards grouped by maintenance priority (Tier).
 * Tier 1 has highest priority; Tier 4 is cut first when funds are insufficient.
 * Click a card to open per-facility details in an overlay.
 */}
          <div className="budget-window__tab-panel" hidden={activeTab !== "budget"}>
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
                      const iconUrl = resolveLedgerIcon(catId);
                      return (
                        <div
                          key={catId}
                          className="budget-category-card"
                          onClick={() => setDetailCategoryId(catId)}
                        >
                          {/* Upper-right badge: ratio (%). The only numeric display while the slider is hidden. */}
                          <span className="budget-category-card__badge">
                            {Math.round(ratioPct)}%
                          </span>
                          {/* Left: square category icon. Leave empty if no image is available. */}
                          <div className="budget-category-card__icon">
                            {iconUrl && <img src={iconUrl} alt="" draggable={false} />}
                          </div>
                          {/* Right: title and allocation (plus slider while editing). */}
                          <div className="budget-category-card__body">
                            <span className="budget-category-card__name">
                              {t(`budget.category.${catId}`, { defaultValue: cat.display_name })}
                            </span>
                            <span className="budget-category-card__amount">
                              <Money value={pendingBudgets[catKey] ?? 0} unit />
                            </span>
                            {/* Budget slider: required-amount ratio (0-150%). */}
                            {isEditing && (
                              <div
                                className="budget-category-card__slider-row"
                                title={t("budget.ratioSliderHint", {
                                  defaultValue: "各施設が生産計画どおり稼働したときの見込み費用に対する割合(%)。100%で全施設が計画どおり稼働できる予算になる",
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
                                  /* Releasing the thumb commits the value. Send immediately without waiting for debounce. */
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

          {/* Tax tab. Keep mounted but hidden so switching tabs preserves editing state and autosave. */}
          <div className="budget-window__tab-panel" hidden={activeTab !== "tax"}>
            <TaxRatesPane
              apiBaseUrl={apiBaseUrl}
              isEditing={isEditing}
              onSavingChange={setIsTaxSaving}
            />
          </div>
        </div>

        {/* Right area: tax revenue/cash trends side by side above; income/expenditure balance below. */}
        <div className="budget-window__right-area">
          <div className="budget-window__charts-row">
            {/* Top left: tax revenue trends by category (past epoch actuals). Lines for total plus five taxes. */}
            <div className="budget-window__chart-section">
              <h3 className="budget-window__section-title">
                {t("tax.revenueHistoryTitle", { defaultValue: "税収推移(カテゴリ別)" })}
              </h3>
              <div className="budget-window__chart">
                {taxRevenueChartData.length === 0 ? (
                  <div className="budget-window__chart-empty">{t("common.noData")}</div>
                ) : (
                  <ResponsiveContainer width="100%" height="100%">
                    <LineChart
                      data={taxRevenueChartData}
                      margin={{ top: 4, right: 16, left: 0, bottom: 0 }}
                    >
                      <CartesianGrid strokeDasharray="3 3" stroke={CHART_GRID_LINE} vertical={false} />
                      <XAxis
                        dataKey="epoch"
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
                        content={<ChartValueTooltip locale={locale} formatLabel={formatEpochLabel} />}
                        cursor={{ stroke: CHART_AXIS_LINE }}
                      />
                      <Legend wrapperStyle={{ fontSize: "0.93rem" }} iconType="plainline" />
                      {/* Draw the total line first as a thicker solid reference line. */}
                      <Line
                        type="monotone"
                        dataKey="total"
                        name={t("tax.revenueTotal", { defaultValue: "合計" })}
                        stroke={COLOR_TAX_REVENUE_TOTAL}
                        strokeWidth={2.5}
                        dot={false}
                        activeDot={{ r: 4 }}
                        isAnimationActive={false}
                      />
                      {TAX_REVENUE_META.map((meta) => (
                        <Line
                          key={meta.key}
                          type="monotone"
                          dataKey={meta.key}
                          name={t(meta.labelKey)}
                          stroke={meta.color}
                          strokeWidth={2}
                          dot={false}
                          activeDot={{ r: 4 }}
                          isAnimationActive={false}
                        />
                      ))}
                    </LineChart>
                  </ResponsiveContainer>
                )}
              </div>
            </div>

            {/*
 * Top right: city government cash (reserves). Bars for committed end-of-epoch values and current cash;
 * a dashed line extrapolates the recent trend.
 */}
            <div className="budget-window__chart-section">
              <h3 className="budget-window__section-title">
                {t("budget.reservesTrendTitle", { defaultValue: "現金推移" })}
              </h3>
              <div className="budget-window__chart">
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
                      content={<ChartValueTooltip locale={locale} formatLabel={formatEpochLabel} />}
                      cursor={{ fill: CHART_CURSOR_FILL }}
                    />
                    <Legend wrapperStyle={{ fontSize: "0.93rem" }} />
                    {/* Draw a zero line to make insolvency (negative cash) immediately visible. */}
                    <ReferenceLine y={0} stroke={CHART_ZERO_LINE} />
                    {/* Use the same stackId for actual and current so they render as one bar at each X position. */}
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
          </div>

          {/*
 * Bottom: income/expenditure balance. Left bar = income (previous epoch actual), right = spending
 * (expected payment next epoch). Only when cash shortages reduce spending, show requested amounts
 * before reduction as a lighter third bar.
 */}
          <div className="budget-window__balance-section">
            <h3 className="budget-window__section-title">
              {t("budget.balanceTitle", { defaultValue: "収支バランス" })}
            </h3>
            {/* Balance summary: income minus spending. A negative value draws down cash. */}
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
                    tick={{ fontSize: "1rem", fill: CHART_AXIS_TEXT }}
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
                  {/*
 * Pre-reduction spending. Draw in lighter versions of committed-spending colors and omit from
 * the legend to avoid listing each category twice.
 */}
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

      {/* Per-facility budget category details (previous epoch actuals and next epoch forecasts). */}
      {detailCategory && (
        <BudgetOverlay
          title={`${t(`budget.category.${detailCategory.id}`, { defaultValue: detailCategory.display_name })} ${t("budget.details", { defaultValue: "詳細" })}`}
          onClose={() => setDetailCategoryId(null)}
        >
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
        </BudgetOverlay>
      )}
    </div>
  );
}
