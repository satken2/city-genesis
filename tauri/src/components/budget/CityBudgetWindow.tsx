import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { setHintbarText } from "../../utils/hintbar";
import { resolveLedgerIcon } from "../../utils/ledgerIcon";
import { STATUS_CONSTRUCTING } from "../../constants";
import type { BudgetCategoryEntry } from "../../types";
import { formatEpochDateRange } from "../../utils/date";
import { Money } from "../common/Money";
import { formatCompactAmount } from "../../utils/money";
import {
  ResponsiveContainer,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip as RechartsTooltip,
  Legend,
} from "recharts";
import {
  FiscalSankey,
  buildFiscalSankeyData,
  buildBusinessRevenueItems,
  buildLandAcquisitionSpendItem,
  getActualSpendingValue,
  TAX_REVENUE_META,
  spendCategoryColor,
  type FiscalFlowItem,
} from "../common/FiscalSankey";
import "./CityBudgetWindow.css";

type BudgetAgent = {
  kind?: "agent" | "path" | "parcel";
  agent_id?: number;
  path_id?: number;
  parcel_id?: number;
  name: string;
  category_id: number;
  variant?: number;
  status: number;
  actual_expense: number;
  projected_expense: number;
  utilization_target?: number;
  construction_cost_per_epoch?: number;
  remaining_epochs?: number | null;
};

/** エージェント/道路/土地区画を一意に識別するキー（稼働率の編集state・PATCH対象の識別に使う） */
function rowKey(a: BudgetAgent): string {
  if (a.kind === "path") return `path:${a.path_id}`;
  if (a.kind === "parcel") return `parcel:${a.parcel_id}`;
  return `agent:${a.agent_id}`;
}

/**
 * 予算比率スライダーの上限(%)。全カテゴリ共通でフル稼働必要額の150%まで
 * (100%超は見積もり誤差やエポック中の価格・賃金変動へのヘッドルーム)。
 */
const RATIO_MAX_PERCENT = 150;

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
};

type CityData = {
  city: {
    city_agent_id: number;
    current_date: number;
    simulation_days: number;
    is_early_game?: boolean;
  };
};

type CityAgentData = {
  economic_agent: {
    reserves: number;
  };
};

const LEDGER_RESERVE_KEY = "10";



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
 */
function deriveBudgets(
  ratiosPct: Record<string, number>,
  base: Record<string, number>,
  tiers: Record<string, number>,
  reserves: number,
): { budgets: Record<string, number>; autoAdjusted: boolean } {
  const keys = Object.keys(base).filter((k) => k !== LEDGER_RESERVE_KEY);

  const budgets: Record<string, number> = {};
  for (const k of keys) budgets[k] = 0;
  budgets[LEDGER_RESERVE_KEY] = 0;

  if (reserves <= 0) {
    return { budgets, autoAdjusted: false };
  }

  const totalBase = keys.reduce((s, k) => s + Math.max(base[k] ?? 0, 0), 0);
  if (totalBase <= 1e-8) {
    // 実績も必要量も未捕捉(初回エポック等): バックエンドと同じく現金を均等配分する。
    const even = keys.length > 0 ? reserves / keys.length : 0;
    for (const k of keys) budgets[k] = even;
    return { budgets, autoAdjusted: false };
  }

  for (const k of keys) {
    const b = Math.max(base[k] ?? 0, 0);
    const ratio = Math.min(Math.max((ratiosPct[k] ?? 100) / 100, 0), RATIO_MAX_PERCENT / 100);
    budgets[k] = b * ratio;
  }

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

  return { budgets, autoAdjusted };
}

export function CityBudgetWindow({
  apiBaseUrl,
  onClose,
}: {
  apiBaseUrl: string;
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();

  const [budgetData, setBudgetData] = useState<BudgetData | null>(null);
  const [cityReserves, setCityReserves] = useState<number>(0);
  const [currentDate, setCurrentDate] = useState<number>(0);
  const [simulationDays, setSimulationDays] = useState<number>(30);
  // プレイヤーの主入力：カテゴリ別「フル稼働必要額に対する比率(％整数、0〜150)」。
  const [pendingRatios, setPendingRatios] = useState<Record<string, number>>({});
  const [budgetAgents, setBudgetAgents] = useState<BudgetAgent[] | null>(null);
  const [activeCategoryId, setActiveCategoryId] = useState<number>(1);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // 収支フローSankeyのタブ選択（"projected"=来期見込み、"actual"=前期実績）。
  const [sankeyTab, setSankeyTab] = useState<"projected" | "actual">("projected");

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

        const cityAgentId = cityRes.city.city_agent_id;
        const agentData = await fetchApi<CityAgentData>(
          buildApiUrl(apiBaseUrl, `/api/v1/agents/${cityAgentId}`),
          { signal: controller.signal },
        );

        setBudgetData(budgetsRes);
        const reserves = agentData.economic_agent.reserves;
        setCityReserves(reserves);
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

  const handleConfirmBudget = useCallback(async () => {
    setIsSaving(true);
    try {
      // 比率(％)を 0〜1.5 の割合に変換して送信する。
      // 実際の予算額と自動調整はバックエンドがフル稼働必要額から導出する。
      // 維持優先度（budget_tiers）は現状UIから編集できないため送信しない
      // （バックエンドの既定値/既存設定がそのまま維持される）。
      const ratioFractions: Record<string, number> = {};
      for (const [k, pct] of Object.entries(pendingRatios)) {
        ratioFractions[k] = Math.max(pct, 0) / 100;
      }
      const res = await fetch(buildApiUrl(apiBaseUrl, "/api/v1/city/budgets"), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          category_ratio: ratioFractions,
        }),
      });

      if (!res.ok) {
        throw new Error(`HTTP ${res.status}`);
      }

      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save");
    } finally {
      setIsSaving(false);
    }
  }, [apiBaseUrl, pendingRatios, onClose]);

  // プレイヤーの比率入力（％）を更新する。全カテゴリ 0〜150 でクランプ。
  const handleRatioChange = useCallback((catId: number, valStr: string) => {
    let pct = parseFloat(valStr);
    if (isNaN(pct) || pct < 0) pct = 0;
    pct = Math.min(pct, RATIO_MAX_PERCENT);
    setPendingRatios((prev) => ({ ...prev, [String(catId)]: pct }));
  }, []);

  // 各カテゴリの維持優先度（Tier）。現状UIからは編集できないため、サーバ設定の読み取り専用。
  // 左ペインのグループ分けと、資金不足時の自動調整プレビュー（deriveBudgets）に使う。
  const tiers = useMemo(() => budgetData?.budget_tiers ?? {}, [budgetData]);

  // 予算導出の基準額（フル稼働必要額＝棒グラフの「必要金額」）。予算パネルの施設別リストと
  // 同一の per施設値の合計として求めるため、グラフとリスト合計は常に一致する。
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
  // バナーは「編集中のプレビューで自動調整が起きる」場合に加え、バックエンドが直近の配分で
  // 実際に自動調整した場合（budget_auto_adjusted）にも表示する。ロード直後は両者は一致するが、
  // 配分後に現金が変動した場合などはサーバ側の実結果のみが真になるため、両方を見る。
  const autoAdjusted = derived.autoAdjusted || Boolean(budgetData?.budget_auto_adjusted);

  // 内訳積み上げバーの列となるカテゴリ(貯蓄を除く全予算カテゴリ)。
  const breakdownCategories = useMemo(
    () => sortedCategories.filter((c) => String(c.id) !== LEDGER_RESERVE_KEY),
    [sortedCategories],
  );

  // 「必要金額内訳」と「確定予算内訳」の横向き積み上げバー用データ(行=2本、列=カテゴリ別金額)。
  // 上段=必要金額(フル稼働必要額の単純積み上げ)、下段=確定予算(比率×基準額をTier自動調整で
  // 現金内に収めた配分結果)。同一X軸で上下に並ぶため、資金不足時にどのカテゴリが
  // どれだけ削られたかを見比べられる。スライダー操作には確定予算側が即座に追随する。
  const breakdownChartData = useMemo(() => {
    const requiredRow: Record<string, number | string> = {
      name: t("budget.nextProjectedShort", { defaultValue: "必要金額" }),
    };
    const budgetRow: Record<string, number | string> = {
      name: t("budget.confirmedBudgetLabel", { defaultValue: "確定予算" }),
    };
    for (const c of breakdownCategories) {
      const key = String(c.id);
      requiredRow[key] = baseAmounts[key] ?? 0;
      budgetRow[key] = pendingBudgets[key] ?? 0;
    }
    return [requiredRow, budgetRow];
  }, [breakdownCategories, baseAmounts, pendingBudgets, t]);

  const filteredAgents = useMemo(() => {
    if (!budgetAgents) return [];
    const catId = activeCategoryId;
    return budgetAgents
      .filter((agent) => agent.category_id === catId)
      .map((agent) => ({
        ...agent,
        rowKeyStr: rowKey(agent),
        // 施設別の「必要金額」。この行の合計（agentsTotal.projected）はカードの
        // 必要金額（baseAmounts[catId]）と常に一致する（同一の projected_expense を使うため）。
        projected_preview: agent.projected_expense,
      }));
  }, [budgetAgents, activeCategoryId]);

  const agentsTotal = useMemo(() => {
    let actual = 0;
    let projected = 0;
    for (const agent of filteredAgents) {
      actual += agent.actual_expense;
      projected += agent.projected_preview;
    }
    return { actual, projected };
  }, [filteredAgents]);

  /**
   * 財政Sankey用データ(タブ「来期見込み」: 前エポック歳入 → 資金プール → 次エポック支出予定)。
   * 歳入は税(31〜35)に加え、公営事業収入(コストカテゴリキー1〜7に計上される公共サービス
   * 売上・公営住宅家賃)も描く。
   * 収用補償は予算枠で計画される支出ではない実支出のため、支出側が予算枠(pendingBudgets)の
   * この予算ウィンドウでは描かない(前エポック実績を描く「前期実績」タブでのみ描く)。
   * 右側(支出)は編集中の pendingBudgets を参照するため、予算バーを操作すると即座に再描画される。
   */
  const projectedSankeyChart = useMemo(() => {
    if (!budgetData) return null;
    const revenues: FiscalFlowItem[] = [
      ...TAX_REVENUE_META.map((m) => ({
        label: t(m.labelKey),
        value: budgetData.total_revenue[m.key] ?? 0,
        color: m.color,
      })),
      ...buildBusinessRevenueItems(
        budgetData.total_revenue,
        masterData?.budget_categories,
        t,
      ),
    ];
    // 貯蓄(10)は支出カテゴリではないため支出側ノードに含めない（余剰は surplus ノードが表す）。
    const spends: FiscalFlowItem[] = sortedCategories
      .filter((c) => String(c.id) !== LEDGER_RESERVE_KEY)
      .map((c) => ({
        label: t(`budget.category.${c.id}`, { defaultValue: c.display_name }),
        value: pendingBudgets[String(c.id)] ?? 0,
        color: spendCategoryColor(c.id),
      }));
    return buildFiscalSankeyData(
      revenues,
      { label: t("fiscalSankey.pool", { defaultValue: "資金" }) },
      spends,
      {
        surplus: t("fiscalSankey.surplus", { defaultValue: "繰越" }),
        deficit: t("fiscalSankey.deficit", { defaultValue: "貯蓄取り崩し(赤字)" }),
      },
    );
  }, [budgetData, pendingBudgets, sortedCategories, masterData, t]);

  /**
   * 財政Sankey用データ(タブ「前期実績」: 前エポック歳入 → 資金プール → 前エポック支出実績)。
   * 税率ウィンドウ(CityTaxWindow)のSankeyと同一内容。歳入側は「来期見込み」タブと同じだが、
   * 支出側は予算枠(pendingBudgets)ではなく実支出(budgetData.total_expense、
   * getActualSpendingValue)を使い、予算カテゴリ外の収用補償(buildLandAcquisitionSpendItem)も
   * 前エポック実績として右側に加える。
   */
  const actualSankeyChart = useMemo(() => {
    if (!budgetData) return null;
    const revenues: FiscalFlowItem[] = [
      ...TAX_REVENUE_META.map((m) => ({
        label: t(m.labelKey),
        value: budgetData.total_revenue[m.key] ?? 0,
        color: m.color,
      })),
      ...buildBusinessRevenueItems(
        budgetData.total_revenue,
        masterData?.budget_categories,
        t,
      ),
    ];
    const budgetCategories = masterData?.budget_categories ?? {};
    const spends: FiscalFlowItem[] = [
      ...Object.entries(budgetCategories)
        .map(([id, entry]) => ({ id: Number(id), entry }))
        .sort((a, b) => a.id - b.id)
        .map(({ id, entry }) => ({
          label: t(`budget.category.${id}`, {
            defaultValue: (entry as BudgetCategoryEntry).display_name,
          }),
          value: getActualSpendingValue(String(id), budgetData.total_expense),
          color: spendCategoryColor(id),
        })),
      // 収用補償(市が民間から土地を収用した土地代)。予算カテゴリ外の税外支出だが、
      // 前エポックの実支出として右側に加える。
      buildLandAcquisitionSpendItem(budgetData.total_expense, t),
    ];
    return buildFiscalSankeyData(
      revenues,
      { label: t("fiscalSankey.pool", { defaultValue: "資金" }) },
      spends,
      {
        surplus: t("fiscalSankey.surplus", { defaultValue: "繰越" }),
        deficit: t("fiscalSankey.deficit", { defaultValue: "貯蓄取り崩し(赤字)" }),
      },
    );
  }, [budgetData, masterData, t]);

  const locale = i18n.language === "ja" ? "ja-JP" : "en-US";
  const nextEpochStart = currentDate;
  const nextEpochEnd = currentDate + simulationDays;
  const nextPeriod = formatEpochDateRange(nextEpochStart, nextEpochEnd, locale, { hideDay: true, simulationDays });

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

      {/* メインの3カラム分割: 左=Tier別カテゴリカード / 中央=Sankey+比較棒グラフ / 右=施設明細 */}
      <div className="budget-window__split-container">

        {/* 左カラム: 維持優先度(Tier)ごとにグループ化したカテゴリカード。
            Tier 1 が最優先維持、Tier 4 が資金不足時に最初に削減される。
            カードの内容はカテゴリ名と予算比率スライダーのみ（金額は中央の棒グラフで見る）。 */}
        <div className="budget-window__left-pane">
          {TIER_ORDER.map((tier) => {
            const tierCategories = sortedCategories.filter(
              (cat) =>
                String(cat.id) !== LEDGER_RESERVE_KEY &&
                getCategoryTier(String(cat.id), tiers) === tier,
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
                    const isSelected = activeCategoryId === catId;
                    const iconUrl = resolveLedgerIcon(catId);
                    return (
                      <div
                        key={catId}
                        className={`budget-category-card ${isSelected ? "budget-category-card--active" : ""}`}
                        onClick={() => setActiveCategoryId(catId)}
                      >
                        {/* 左: カテゴリアイコン(正方形)。画像が無いカテゴリは空枠。 */}
                        <div className="budget-category-card__icon">
                          {iconUrl && <img src={iconUrl} alt="" draggable={false} />}
                        </div>
                        {/* 右: タイトル + その下に予算スライダー */}
                        <div className="budget-category-card__body">
                          <span className="budget-category-card__name">
                            {t(`budget.category.${catId}`, { defaultValue: cat.display_name })}
                          </span>
                          {/* 予算スライダー: フル稼働必要額に対する比率(0〜150%) */}
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
                            />
                            <span className="budget-category-card__slider-value">{Math.round(ratioPct)}%</span>
                          </div>
                        </div>
                      </div>
                    );
                  })}
                </div>
              </div>
            );
          })}
        </div>

        {/* 右エリア: 上=必要金額/確定予算の内訳バー、下=施設明細+財政Sankeyの左右分割 */}
        <div className="budget-window__right-area">
          {/* 上段: カテゴリ別の内訳積み上げバー2本(上=必要金額、下=確定予算)。
              同一X軸で上下に並べ、資金不足の自動調整による削減幅を見比べられる。
              セグメント色は下段Sankeyの支出側と同じ spendCategoryColor で統一する。 */}
          <div className="budget-window__breakdown-section">
            <h3 className="budget-window__section-title">
              {t("budget.breakdownCompareTitle", { defaultValue: "必要金額と確定予算の内訳" })}
            </h3>
            <div className="budget-window__breakdown-chart">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart
                  data={breakdownChartData}
                  layout="vertical"
                  margin={{ top: 0, right: 16, left: 4, bottom: 0 }}
                  barCategoryGap="24%"
                >
                  <XAxis
                    type="number"
                    tick={{ fontSize: 10, fill: "#94a3b8" }}
                    tickFormatter={(v) => formatCompactAmount(Number(v), locale, false)}
                    stroke="rgba(255,255,255,0.15)"
                  />
                  <YAxis
                    type="category"
                    dataKey="name"
                    width={76}
                    tick={{ fontSize: 10, fill: "#cbd5e1" }}
                    stroke="rgba(255,255,255,0.15)"
                  />
                  <RechartsTooltip
                    formatter={(value) => formatCompactAmount(Number(value), locale, true)}
                    contentStyle={{
                      background: "rgba(15, 23, 42, 0.95)",
                      border: "1px solid rgba(255, 255, 255, 0.1)",
                      borderRadius: 8,
                      fontSize: 11,
                    }}
                    cursor={{ fill: "rgba(255, 255, 255, 0.04)" }}
                  />
                  <Legend wrapperStyle={{ fontSize: 10 }} />
                  {breakdownCategories.map((c) => (
                    <Bar
                      key={c.id}
                      dataKey={String(c.id)}
                      stackId="breakdown"
                      name={t(`budget.category.${c.id}`, { defaultValue: c.display_name })}
                      fill={spendCategoryColor(c.id)}
                    />
                  ))}
                </BarChart>
              </ResponsiveContainer>
            </div>
          </div>

          {/* 下段: 左=選択カテゴリの施設明細、右=財政Sankey */}
          <div className="budget-window__bottom-row">
            {/* 下段左: 選択カテゴリのエージェント明細 */}
            <div className="budget-window__agents-pane">
              <div className="budget-agent-table-wrapper">
                <div className="budget-agent-title-row">
                  <h3 className="budget-agent-title">
                    {t(`budget.category.${activeCategoryId}`, {
                      defaultValue: categories.find((c) => c.id === activeCategoryId)?.display_name
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

            {/* 下段右: 財政Sankey。「来期見込み」(前税収→資金→次支出予定、破線)と
                「前期実績」(前税収→資金→前支出実績、税率ウィンドウと同一内容)をタブで切り替える。 */}
            <div className="budget-window__sankey-section">
              <div className="budget-window__sankey-header">
                <h3 className="budget-window__section-title budget-window__sankey-title">
                  {t("fiscalSankey.title", { defaultValue: "収支フロー" })}
                </h3>
                <div className="budget-window__sankey-tabs" role="tablist">
                  <button
                    type="button"
                    role="tab"
                    aria-selected={sankeyTab === "projected"}
                    className={[
                      "budget-window__sankey-tab",
                      sankeyTab === "projected" && "budget-window__sankey-tab--active",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    onClick={() => setSankeyTab("projected")}
                  >
                    {t("fiscalSankey.projectedTab", { defaultValue: "来期見込み" })}
                  </button>
                  <button
                    type="button"
                    role="tab"
                    aria-selected={sankeyTab === "actual"}
                    className={[
                      "budget-window__sankey-tab",
                      sankeyTab === "actual" && "budget-window__sankey-tab--active",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    onClick={() => setSankeyTab("actual")}
                  >
                    {t("fiscalSankey.actualTab", { defaultValue: "前期実績" })}
                  </button>
                </div>
              </div>
              <FiscalSankey
                chart={sankeyTab === "projected" ? projectedSankeyChart : actualSankeyChart}
                linkStyle={sankeyTab === "projected" ? "planned" : "solid"}
                noDataLabel={t("fiscalSankey.noData", { defaultValue: "表示できる収支データがありません" })}
              />
            </div>
          </div>
        </div>

      </div>

      {/* フッター */}
      <div className="budget-window__footer">
        <button
          className="budget-window__confirm-btn"
          onClick={handleConfirmBudget}
          disabled={isSaving}
          type="button"
        >
          {isSaving ? t("budget.confirming") : t("budget.confirmBudget")}
        </button>
      </div>
    </div>
  );
}
