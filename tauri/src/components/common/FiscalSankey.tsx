/**
 * 財政Sankeyダイアグラム(共通コンポーネント)
 *
 * 左=税収(歳入ソース) → 中央=資金プール → 右=支出(歳出カテゴリ) の資金フローを
 * 2段階Sankeyで可視化する。中央にプールノードを挟むことで、入力と出力のペアワイズ
 * フロー量が不明でも合計値だけで描画できる(EconomyTrendTab の SankeyFlowCard と同じ手法)。
 *
 * 総税収と総支出の差は residual ノードで埋めてフローをバランスさせる:
 *   - 黒字(税収 > 支出): 「繰越」ノードが出力側(プールの右)に現れる
 *   - 赤字(支出 > 税収): 「取り崩し」ノードが入力側(プールの左)に現れる
 *
 * linkStyle="planned" のとき、プールから出るリンク(=支出側)を淡色/破線で描き、
 * 「まだ実際には支出していない予定」であることを視覚的に明示する。
 */

import { ResponsiveContainer, Sankey } from "recharts";
import { LEDGER_LAND_ACQUISITION } from "../../constants";
import "./FiscalSankey.css";

/** residual(繰越/取り崩し)ノードの色 */
const COLOR_SURPLUS = "#22c55e"; // 繰越(黒字): Green(出力側)
const COLOR_DEFICIT = "#f97316"; // 取り崩し(赤字): Orange(入力側)
/** 資金プールノードの色 */
export const FISCAL_COLOR_POOL = "#64748b"; // Slate Gray

/** 貯蓄(LEDGER_RESERVE)の台帳キー。支出カテゴリではないため歳出の実績集計から除外する。 */
export const FISCAL_LEDGER_RESERVE_KEY = "10";

/** 収用補償(税外の歳出)ノードの色。土色にして「土地取引」であることを一目でわかるようにする。 */
const FISCAL_COLOR_LAND = "#b08968"; // Earth Brown

/**
 * 税収ソース(歳入)のSankeyメタデータ。CityBudgetWindow / CityTaxWindow で共通利用する。
 * キーは total_revenue の台帳(ledger)キー: 31=消費税,32=所得税,33=輸入関税,34=輸出関税,35=法人税。
 * ラベルは税率設定ウィンドウと共通の i18n キー(tax.*)を再利用する。
 * 税以外の歳入(公営事業収入)は buildBusinessRevenueItems が別途組み立てる。
 */
export const TAX_REVENUE_META: Array<{ key: string; labelKey: string; color: string }> = [
  { key: "32", labelKey: "tax.incomeTax", color: "#4ea1ff" },      // 所得税: Blue
  { key: "35", labelKey: "tax.businessTax", color: "#f59e0b" },    // 法人税: Amber
  { key: "31", labelKey: "tax.consumptionTax", color: "#c084fc" }, // 消費税: Purple
  { key: "33", labelKey: "tax.importTax", color: "#ef4444" },      // 輸入関税: Red
  { key: "34", labelKey: "tax.exportTax", color: "#10b981" },      // 輸出関税: Emerald
];

/** 支出カテゴリの色。既存の予算バー(BudgetActualBarGraph)と同じ生成式で色を合わせる。 */
export function spendCategoryColor(catId: number): string {
  return `hsl(${(catId * 50) % 360}, 50%, 55%)`;
}

/** buildBusinessRevenueItems が受け取る翻訳関数の型(useTranslation の t で満たせる)。 */
type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

/**
 * 税外収入(公営事業収入)のSankeyフロー項目を構築する。
 *
 * バックエンドの total_revenue には税(台帳キー31〜35)のほかに、公営施設の事業収益
 * (電気・水道料金などの公共サービス売上)と公営住宅・公共ビルの家賃収入が、その施設の
 * コストカテゴリキー(1〜7)で計上される(market_match / pay_rent が計上元)。
 * これを支出側と同じ予算カテゴリ粒度・同じ色(spendCategoryColor)で歳入側に描くことで、
 * 「どのカテゴリの公営事業がどれだけ稼いでいるか」を支出と対比できる。
 *
 * value<=0 の項目(収入のないカテゴリ)は buildFiscalSankeyData 側で除外されるため、
 * ここでは全カテゴリ分を素直に返してよい。貯蓄(LEDGER_RESERVE)はカテゴリではないので除外する。
 */
export function buildBusinessRevenueItems(
  totalRevenue: Record<string, number>,
  budgetCategories: Record<number, { display_name: string }> | undefined,
  t: TranslateFn,
): FiscalFlowItem[] {
  if (!budgetCategories) return [];
  return Object.entries(budgetCategories)
    .map(([id, entry]) => ({ id: Number(id), entry }))
    .filter(({ id }) => String(id) !== FISCAL_LEDGER_RESERVE_KEY)
    .sort((a, b) => a.id - b.id)
    .map(({ id, entry }) => ({
      label: t("fiscalSankey.categoryRevenue", {
        defaultValue: "{{category}}事業収入",
        category: t(`budget.category.${id}`, { defaultValue: entry.display_name }),
      }),
      value: totalRevenue[String(id)] ?? 0,
      color: spendCategoryColor(id),
    }));
}

/**
 * 収用補償(税外の歳出)のSankeyフロー項目を構築する。
 *
 * 市が民間所有の土地区画を収用したときに所有者へ支払う土地代(市→所有者の補償)が、
 * バックエンドの total_expense の LEDGER_LAND_ACQUISITION キーに計上される。予算
 * カテゴリ外の税外支出のため、土色(FISCAL_COLOR_LAND)のノードで歳出側に描く。
 * 予算枠ではなく実支出のため、前エポック実績を描く税率ウィンドウでのみ使用する
 * (次エポックの予算枠を描く予算ウィンドウでは使用しない)。
 * value<=0(収用のなかったエポック)のときは buildFiscalSankeyData 側で除外される。
 */
export function buildLandAcquisitionSpendItem(
  totalExpense: Record<string, number>,
  t: TranslateFn,
): FiscalFlowItem {
  return {
    label: t("fiscalSankey.landAcquisitionExpense", { defaultValue: "収用補償" }),
    value: totalExpense[String(LEDGER_LAND_ACQUISITION)] ?? 0,
    color: FISCAL_COLOR_LAND,
  };
}

/**
 * カテゴリの実支出額。各カテゴリは自身の予算枠内でのみ執行される(共有プールによる補填は
 * 廃止済み)ため total_expense がそのまま実支出。貯蓄(10)は支出カテゴリではないため 0 を
 * 返す(CityBudgetWindow の getActualSpending と同じ定義)。
 */
export function getActualSpendingValue(
  catKey: string,
  totalExpense: Record<string, number>,
): number {
  if (catKey === FISCAL_LEDGER_RESERVE_KEY) return 0;
  return totalExpense[catKey] ?? 0;
}

/** Sankeyの1フロー(歳入ソース or 歳出カテゴリ)。value<=0 のものは描画時に除外される。 */
export type FiscalFlowItem = {
  /** ノードに表示するラベル(必要なら金額を含めて呼び出し側で整形する) */
  label: string;
  /** フロー量(円) */
  value: number;
  /** ノード/リンクの色 */
  color: string;
};

/** buildFiscalSankeyData が返す、FiscalSankey がそのまま描画できる形 */
export type FiscalSankeyChart = {
  data: {
    nodes: Array<{ name: string }>;
    links: Array<{ source: number; target: number; value: number }>;
  };
  /** ノードindex順に対応する色。node/link の描画色に使う。 */
  nodeColors: string[];
  /** 入力側ノード数(この値未満のindexが入力側=ラベルを左に配置) */
  inputNodeCount: number;
  /** 資金プールノードのindex(ラベルを描画しない中継点) */
  poolNodeIndex: number;
};

/**
 * 歳入・プール・歳出から Sankey 描画用データを構築する。
 *
 * トポロジー(2段階):
 *   [税収ソース...][取り崩し?] → [資金プール] → [支出カテゴリ...][繰越?]
 *
 * value<=0 のフローはノードごと除外する(recharts Sankeyが0幅リンクを正常に描画できないため)。
 * 描画すべきフローが1本も無ければ null を返す。
 */
export function buildFiscalSankeyData(
  revenues: FiscalFlowItem[],
  pool: { label: string },
  spends: FiscalFlowItem[],
  residualLabels: { surplus: string; deficit: string },
): FiscalSankeyChart | null {
  const revNonzero = revenues.filter((r) => r.value > 0);
  const spendNonzero = spends.filter((s) => s.value > 0);

  const totalRevenue = revNonzero.reduce((sum, r) => sum + r.value, 0);
  const totalSpend = spendNonzero.reduce((sum, s) => sum + s.value, 0);

  if (totalRevenue === 0 && totalSpend === 0) return null;

  const residual = totalRevenue - totalSpend;
  const isDeficit = residual < 0;
  const residualAbs = Math.abs(residual);

  const nodes: Array<{ name: string }> = [];
  const nodeColors: string[] = [];
  const links: Array<{ source: number; target: number; value: number }> = [];

  // --- 入力側ノード: 税収ソース(+ 赤字なら取り崩し) ---
  revNonzero.forEach((r) => {
    nodes.push({ name: r.label });
    nodeColors.push(r.color);
  });
  if (isDeficit && residualAbs > 0) {
    nodes.push({ name: residualLabels.deficit });
    nodeColors.push(COLOR_DEFICIT);
  }
  const inputNodeCount = nodes.length;

  // --- 中央: 資金プール ---
  const poolNodeIndex = nodes.length;
  nodes.push({ name: pool.label });
  nodeColors.push(FISCAL_COLOR_POOL);

  // --- 出力側ノード: 支出カテゴリ(+ 黒字なら繰越) ---
  const spendStartIndex = nodes.length;
  spendNonzero.forEach((s) => {
    nodes.push({ name: s.label });
    nodeColors.push(s.color);
  });
  let surplusIndex = -1;
  if (!isDeficit && residualAbs > 0) {
    surplusIndex = nodes.length;
    nodes.push({ name: residualLabels.surplus });
    nodeColors.push(COLOR_SURPLUS);
  }

  // --- リンク: 入力→プール ---
  revNonzero.forEach((r, i) => {
    links.push({ source: i, target: poolNodeIndex, value: r.value });
  });
  if (isDeficit && residualAbs > 0) {
    // 取り崩しノードは入力側の末尾(= inputNodeCount - 1)
    links.push({ source: inputNodeCount - 1, target: poolNodeIndex, value: residualAbs });
  }

  // --- リンク: プール→出力 ---
  spendNonzero.forEach((s, i) => {
    links.push({ source: poolNodeIndex, target: spendStartIndex + i, value: s.value });
  });
  if (!isDeficit && residualAbs > 0) {
    links.push({ source: poolNodeIndex, target: surplusIndex, value: residualAbs });
  }

  if (links.length === 0) return null;

  return { data: { nodes, links }, nodeColors, inputNodeCount, poolNodeIndex };
}

/**
 * 財政Sankeyの描画コンポーネント。
 *
 * buildFiscalSankeyData の出力をそのまま渡す。カスタム node/link render props で
 * 色分けした矩形ノード・曲線リンクを描く(EconomyTrendTab の SankeyFlowCard を汎用化したもの)。
 */
export function FiscalSankey({
  chart,
  noDataLabel,
  linkStyle = "solid",
}: {
  chart: FiscalSankeyChart | null;
  noDataLabel: string;
  /** "planned" のときプール→支出リンクを淡色/破線で描く(予定であることを明示) */
  linkStyle?: "solid" | "planned";
}) {
  const hasData = chart !== null && chart.data.links.length > 0;

  if (!hasData || !chart) {
    return (
      <div className="fiscal-sankey">
        <div className="fiscal-sankey__no-data">{noDataLabel}</div>
      </div>
    );
  }

  const { data, nodeColors, inputNodeCount, poolNodeIndex } = chart;

  return (
    <div className="fiscal-sankey">
      <ResponsiveContainer width="100%" height="100%">
        <Sankey
          key={JSON.stringify(data)}
          data={data}
          nodeWidth={12}
          nodePadding={16}
          linkCurvature={0.5}
          margin={{ top: 8, right: 72, bottom: 8, left: 72 }}
          node={(props: any) => {
            const color = nodeColors[props.index] ?? FISCAL_COLOR_POOL;
            // 入力側ノード(左端)はラベルを左に、それ以外は右に配置する。
            const isInput = props.index < inputNodeCount;
            // 資金プールノードはラベルを描画しない(中継点のため)。
            const isPool = props.index === poolNodeIndex;
            const labelX = isInput ? props.x - 6 : props.x + props.width + 6;
            const textAnchor = isInput ? "end" : "start";
            return (
              <g>
                <rect
                  x={props.x}
                  y={props.y}
                  width={props.width}
                  height={props.height}
                  fill={color}
                  fillOpacity={0.85}
                  rx={2}
                />
                {!isPool && (
                  <text
                    x={labelX}
                    y={props.y + props.height / 2}
                    dy=".35em"
                    fill="#cbd5e1"
                    fontSize="1.02rem"
                    textAnchor={textAnchor}
                  >
                    {props.payload?.name}
                  </text>
                )}
              </g>
            );
          }}
          link={(linkProps: any) => {
            const sourceIdx = linkProps.payload?.source?.index ?? 0;
            const color = nodeColors[sourceIdx] ?? FISCAL_COLOR_POOL;
            // プールから出るリンク(=支出側)。planned のとき淡色/破線にする。
            const isOutputLink = sourceIdx === poolNodeIndex;
            const planned = linkStyle === "planned" && isOutputLink;
            return (
              <path
                d={`M${linkProps.sourceX},${linkProps.sourceY}C${linkProps.sourceControlX},${linkProps.sourceY} ${linkProps.targetControlX},${linkProps.targetY} ${linkProps.targetX},${linkProps.targetY}`}
                fill="none"
                stroke={color}
                strokeWidth={linkProps.linkWidth}
                strokeOpacity={planned ? 0.18 : 0.3}
                strokeDasharray={planned ? "5 4" : undefined}
              />
            );
          }}
        />
      </ResponsiveContainer>
    </div>
  );
}
