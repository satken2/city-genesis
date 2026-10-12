/**
 * 財別の経済推移オーバーレイ
 *
 * 統計ウィンドウ「経済」タブの表で行をクリックすると、タブの上に重ねて開く。
 * GET /api/v1/stats/market_price から財別の時系列データ(平均価格・生産量・消費量・
 * 輸出量・輸入量・注文量)を取得し、クリックした財について4分割グリッドで推移グラフ等を描画する。
 *
 * レイアウト(2列x2行):
 *   - 上段左: 左右2分割(左=スコープ選択プルダウン, 右=直近エポックの数値表示)
 *   - 上段右: 平均価格推移
 *   - 下段左: Sankeyダイアグラム(最新エポックの物資フロー)
 *   - 下段右: 需給推移(生産量・消費量・輸出量・輸入量 + 注文量(点線) を1グラフに統合)
 *
 * 現状は「都市全体」のみ対応。地区別時系列は将来拡張枠としてプルダウンのみ用意し、
 * 地区を選んだ場合は都市全体と同じデータを表示する(未実装である旨を表示)。
 * 幕(ボックスの外側)を押すか、閉じるボタンで閉じる。
 */

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
  Sankey,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./EconomyTrendOverlay.css";
import { useRemPx } from "../../../hooks/useRemPx";

/** /api/v1/stats/market_price の商品別推移データ */
type MarketPriceProduct = {
  product_id: number;
  product_name: string;
  unit: string;
  type: number;
  average_prices: number[];
  total_production: number[];
  total_order: number[];
  total_consumption: number[];
  total_exports: number[];
  total_imports: number[];
  total_self_supply: number[];
};

/** /api/v1/stats/market_price のレスポンス全体 */
type MarketPriceStats = {
  epoch: number;
  epochs: number[];
  products: MarketPriceProduct[];
};

// チャート系列の色
const COLOR_PRICE = "#fbbf24";       // 平均価格: Amber
const COLOR_PRODUCTION = "#4ea1ff";  // 生産量: Blue
const COLOR_CONSUMPTION = "#c084fc"; // 消費量(実消費): Purple
const COLOR_ORDER = "#94a3b8";       // 注文量(計画需要): Slate Gray(点線)
const COLOR_EXPORT = "#10b981";      // 輸出量: Emerald
const COLOR_IMPORT = "#ef4444";      // 輸入量: Red
const COLOR_SELFSUPPLY = "#14b8a6";  // 自給(自家供給): Teal
const COLOR_POOL = "#64748b";        // 供給プール: Slate Gray
const COLOR_UNSOLD = "#f59e0b";      // 売れ残り: Amber
const COLOR_SHORTAGE = "#f97316";    // 不足: Orange

// recharts の共通スタイル定数(他タブと同じテーマ)
const CHART_MARGIN = { top: 12, right: 16, left: -10, bottom: 0 };
const Y_AXIS_WIDTH_REM = 4.44; // Y軸の幅(rem)。描画時に useRemPx で px に直す
const AXIS_TICK = { fontSize: "0.93rem", fill: "#8ba3bb" };
const TOOLTIP_STYLE = {
  background: "#1a2230",
  border: "1px solid rgba(255,255,255,0.1)",
  borderRadius: "4px",
  fontSize: "1rem",
};

/**
 * エポック番号の配列と値の配列を recharts 用のデータポイント配列に変換する。
 * 各配列は同長で、古いエポック順に並んでいる前提。
 */
function zipToChartData(epochs: number[], values: number[]): Array<{ epoch: number; value: number }> {
  const len = Math.min(epochs.length, values.length);
  const out: Array<{ epoch: number; value: number }> = [];
  for (let i = 0; i < len; i++) {
    out.push({ epoch: epochs[i], value: values[i] });
  }
  return out;
}

/**
 * 時系列グラフを描画するカード。
 * タイトル・色・データ・単位・値フォーマッタを指定する。
 *
 * 副系列（secondaryData）を指定すると、主系列を実線、副系列を点線として重ねて描画し、
 * 凡例を表示する。消費量推移カードで「実消費量(実線)」と「注文量(点線)」を併記するために使う。
 * 主系列のみの場合は従来どおり1本の実線として描画する。
 */
function TrendChartCard({
  title,
  data,
  color,
  formatter,
  noDataLabel,
  primaryLabel,
  secondaryData,
  secondaryColor,
  secondaryLabel,
}: {
  title: string;
  data: Array<{ epoch: number; value: number }>;
  color: string;
  formatter: (value: number) => string;
  noDataLabel: string;
  primaryLabel?: string;
  secondaryData?: Array<{ epoch: number; value: number }>;
  secondaryColor?: string;
  secondaryLabel?: string;
}) {
  const remPx = useRemPx();
  const hasSecondary = !!secondaryData;

  // 主系列・副系列を {epoch, value, secondary} の結合データに揃える。
  // 副系列が無いカードでは secondary を含めず、従来の単一系列描画と同じ挙動になる。
  const chartData = data.map((d, i) => ({
    epoch: d.epoch,
    value: d.value,
    secondary: hasSecondary ? secondaryData![i]?.value ?? 0 : 0,
  }));

  // 主系列・副系列のいずれかに非ゼロ値があれば「データあり」とみなす。
  const hasData =
    chartData.length > 0 &&
    chartData.some((d) => d.value !== 0 || (hasSecondary && d.secondary !== 0));

  return (
    <div className="economy-trend-card">
      <h3 className="economy-trend-card__title">{title}</h3>
      <div className="economy-trend-card__chart-container">
        {hasData ? (
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={chartData} margin={CHART_MARGIN}>
              <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
              <XAxis
                dataKey="epoch"
                tick={AXIS_TICK}
                tickLine={false}
                axisLine={{ stroke: "#253246" }}
              />
              <YAxis
                width={remPx(Y_AXIS_WIDTH_REM)}
                tick={AXIS_TICK}
                tickLine={false}
                axisLine={{ stroke: "#253246" }}
                domain={["auto", "auto"]}
                tickFormatter={(v: number) => formatter(v)}
              />
              <Tooltip
                contentStyle={TOOLTIP_STYLE}
                labelFormatter={(label) => `Epoch ${label}`}
                formatter={(value: any) => formatter(Number(value))}
              />
              {hasSecondary && (
                <Legend wrapperStyle={{ fontSize: "1rem" }} iconType="plainline" />
              )}
              <Line
                type="monotone"
                dataKey="value"
                name={primaryLabel}
                stroke={color}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              {hasSecondary && (
                <Line
                  type="monotone"
                  dataKey="secondary"
                  name={secondaryLabel}
                  stroke={secondaryColor ?? "#94a3b8"}
                  strokeWidth={2}
                  strokeDasharray="5 4"
                  dot={false}
                  activeDot={{ r: 4 }}
                  isAnimationActive={false}
                />
              )}
            </LineChart>
          </ResponsiveContainer>
        ) : (
          <div className="economy-trend-card__no-data">{noDataLabel}</div>
        )}
      </div>
    </div>
  );
}

/** Sankeyダイアグラムのノード・リンクデータ */
type SankeyData = {
  nodes: Array<{ name: string }>;
  links: Array<{ source: number; target: number; value: number }>;
};

/**
 * 需給推移カード。
 *
 * 生産量・消費量・輸出量・輸入量の4系列を実線で、注文量を点線で1つのグラフに統合描画する。
 * 従来は4つのカードに分かれていた推移グラフを1つにマージすることで、
 * 各系列のバランスを1画面で比較できるようにする。
 * 消費量(実線)と注文量(点線)の対比も残すため、注文量のみ点線で重ねて描画する。
 */
function SupplyDemandTrendCard({
  title,
  epochs,
  production,
  consumption,
  exports,
  imports,
  order,
  formatter,
  noDataLabel,
  labels,
}: {
  title: string;
  epochs: number[];
  production: number[];
  consumption: number[];
  exports: number[];
  imports: number[];
  order: number[];
  formatter: (value: number) => string;
  noDataLabel: string;
  labels: {
    production: string;
    consumption: string;
    export: string;
    import: string;
    order: string;
  };
}) {
  const remPx = useRemPx();
  // 各系列を同長に揃えて recharts 用データポイントに変換する。
  // 古いエポック順に並んでいる前提で、epochs を基準にインデックスを進める。
  const len = Math.min(
    epochs.length,
    production.length,
    consumption.length,
    exports.length,
    imports.length,
    order.length
  );
  const chartData: Array<{
    epoch: number;
    production: number;
    consumption: number;
    exports: number;
    imports: number;
    order: number;
  }> = [];
  for (let i = 0; i < len; i++) {
    chartData.push({
      epoch: epochs[i],
      production: production[i] ?? 0,
      consumption: consumption[i] ?? 0,
      exports: exports[i] ?? 0,
      imports: imports[i] ?? 0,
      order: order[i] ?? 0,
    });
  }

  // いずれかの系列に非ゼロ値があれば「データあり」とみなす。
  const hasData =
    chartData.length > 0 &&
    chartData.some(
      (d) =>
        d.production !== 0 ||
        d.consumption !== 0 ||
        d.exports !== 0 ||
        d.imports !== 0 ||
        d.order !== 0
    );

  return (
    <div className="economy-trend-card">
      <h3 className="economy-trend-card__title">{title}</h3>
      <div className="economy-trend-card__chart-container">
        {hasData ? (
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={chartData} margin={CHART_MARGIN}>
              <CartesianGrid strokeDasharray="3 3" stroke="#253246" />
              <XAxis
                dataKey="epoch"
                tick={AXIS_TICK}
                tickLine={false}
                axisLine={{ stroke: "#253246" }}
              />
              <YAxis
                width={remPx(Y_AXIS_WIDTH_REM)}
                tick={AXIS_TICK}
                tickLine={false}
                axisLine={{ stroke: "#253246" }}
                domain={["auto", "auto"]}
                tickFormatter={(v: number) => formatter(v)}
              />
              <Tooltip
                contentStyle={TOOLTIP_STYLE}
                labelFormatter={(label) => `Epoch ${label}`}
                formatter={(value: any) => formatter(Number(value))}
              />
              <Legend wrapperStyle={{ fontSize: "1rem" }} iconType="plainline" />
              <Line
                type="monotone"
                dataKey="production"
                name={labels.production}
                stroke={COLOR_PRODUCTION}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              <Line
                type="monotone"
                dataKey="consumption"
                name={labels.consumption}
                stroke={COLOR_CONSUMPTION}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              <Line
                type="monotone"
                dataKey="exports"
                name={labels.export}
                stroke={COLOR_EXPORT}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              <Line
                type="monotone"
                dataKey="imports"
                name={labels.import}
                stroke={COLOR_IMPORT}
                strokeWidth={2}
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
              {/* 注文量は計画需要の参考値のため、実線系列と区別するため点線で描画する */}
              <Line
                type="monotone"
                dataKey="order"
                name={labels.order}
                stroke={COLOR_ORDER}
                strokeWidth={2}
                strokeDasharray="5 4"
                dot={false}
                activeDot={{ r: 4 }}
                isAnimationActive={false}
              />
            </LineChart>
          </ResponsiveContainer>
        ) : (
          <div className="economy-trend-card__no-data">{noDataLabel}</div>
        )}
      </div>
    </div>
  );
}

/**
 * 選択中の財の最新エポック値からSankeyダイアグラム用データを構築する。
 *
 * トポロジー(2段階):
 *   [生産][輸入][自給] → [供給プール] → [消費][輸出][売れ残り/不足]
 *
 * 「売れ残り」は入力合計が出力合計を上回った場合(供給過剰)に出力ノードとして現れ、
 * 「不足」は出力合計が入力合計を上回った場合(需要超過)に入力ノードとして現れる。
 * これによりSankeyのフローが常にバランスする。
 *
 * なお total_production は当エポックの生産量であり、
 * total_imports / total_exports は取引実績であるため、厳密な意味での
 * 「当エポック純生産・純取引」とは一致しない。特に工業原料(GOODS_MATERIAL)では
 * 繰越在庫からの販売分が「不足」ノードに現れる点に注意。
 */
function buildSankeyData(
  production: number,
  imports: number,
  selfSupply: number,
  consumption: number,
  exports: number,
  labels: {
    production: string;
    imports: string;
    selfSupply: string;
    pool: string;
    consumption: string;
    exports: string;
    unsold: string;
    shortage: string;
  }
): SankeyData | null {
  const totalInput = production + imports + selfSupply;
  const totalOutput = consumption + exports;
  const residual = totalInput - totalOutput;

  // 全てのフローがゼロならデータ無し
  if (totalInput === 0 && totalOutput === 0) {
    return null;
  }

  const isShortage = residual < 0;
  const residualAbs = Math.abs(residual);

  // ゼロ値のリンクは除外する(recharts Sankeyが0幅リンクを正常に描画できないため)
  if (isShortage) {
    // 不足(需要 > 供給): 不足ノードが入力側に現れる
    // [生産][輸入][自給][不足] → [供給プール] → [消費][輸出]
    const nodes = [
      { name: labels.production },
      { name: labels.imports },
      { name: labels.selfSupply },
      { name: labels.shortage },
      { name: labels.pool },
      { name: labels.consumption },
      { name: labels.exports },
    ];
    const links: Array<{ source: number; target: number; value: number }> = [];
    if (production > 0) links.push({ source: 0, target: 4, value: production });
    if (imports > 0) links.push({ source: 1, target: 4, value: imports });
    if (selfSupply > 0) links.push({ source: 2, target: 4, value: selfSupply });
    if (residualAbs > 0) links.push({ source: 3, target: 4, value: residualAbs });
    if (consumption > 0) links.push({ source: 4, target: 5, value: consumption });
    if (exports > 0) links.push({ source: 4, target: 6, value: exports });
    return { nodes, links };
  } else {
    // 売れ残り(供給 ≥ 需要): 売れ残りノードが出力側に現れる
    // [生産][輸入][自給] → [供給プール] → [消費][輸出][売れ残り]
    const nodes = [
      { name: labels.production },
      { name: labels.imports },
      { name: labels.selfSupply },
      { name: labels.pool },
      { name: labels.consumption },
      { name: labels.exports },
      { name: labels.unsold },
    ];
    const links: Array<{ source: number; target: number; value: number }> = [];
    if (production > 0) links.push({ source: 0, target: 3, value: production });
    if (imports > 0) links.push({ source: 1, target: 3, value: imports });
    if (selfSupply > 0) links.push({ source: 2, target: 3, value: selfSupply });
    if (consumption > 0) links.push({ source: 3, target: 4, value: consumption });
    if (exports > 0) links.push({ source: 3, target: 5, value: exports });
    if (residualAbs > 0) links.push({ source: 3, target: 6, value: residualAbs });
    return { nodes, links };
  }
}

/**
 * Sankeyダイアグラムカード。
 *
 * 選択中の財の最新エポックにおける、生産・輸入・自給から消費・輸出・売れ残り/不足への
 * 物資の流れを2段階Sankeyで可視化する。中央に「供給プール」ノードを挟むことで、
 * 入力と出力のペアワイズフロー量が不明でも合計値だけで描画できる。
 *
 * ノード色は入力(生産=青/輸入=赤/自給=Teal)、供給プール(Slate Gray)、
 * 出力(消費=紫/輸出=緑/売れ残り=Amber/不足=Orange)で色分けする。
 * リンク色は送信元ノードの色を半透明で引く。
 */
function SankeyFlowCard({
  title,
  sankeyData,
  isShortage,
  noDataLabel,
}: {
  title: string;
  sankeyData: SankeyData | null;
  isShortage: boolean;
  noDataLabel: string;
}) {
  // ノード色のマップ。インデックスは buildSankeyData のノード順序に対応する。
  // 売れ残り/不足でノード順序が異なるため、isShortage で切り替える。
  const nodeColors = isShortage
    ? [
        COLOR_PRODUCTION,  // 0: 生産
        COLOR_IMPORT,      // 1: 輸入
        COLOR_SELFSUPPLY,  // 2: 自給
        COLOR_SHORTAGE,    // 3: 不足
        COLOR_POOL,        // 4: 供給プール
        COLOR_CONSUMPTION, // 5: 消費
        COLOR_EXPORT,      // 6: 輸出
      ]
    : [
        COLOR_PRODUCTION,  // 0: 生産
        COLOR_IMPORT,      // 1: 輸入
        COLOR_SELFSUPPLY,  // 2: 自給
        COLOR_POOL,        // 3: 供給プール
        COLOR_CONSUMPTION, // 4: 消費
        COLOR_EXPORT,      // 5: 輸出
        COLOR_UNSOLD,      // 6: 売れ残り
      ];

  // 入力ノードのインデックス上限(この値未満のインデックスが入力側)。
  // 売れ残り時は 0-2 が入力、不足時は 0-3 が入力。
  const inputNodeCount = isShortage ? 4 : 3;

  // 供給プールノードのインデックス。
  // 中央に挟むプールノードは中継点であり、実体を持たないためラベル(タイトル)を表示しない。
  const poolNodeIndex = isShortage ? 4 : 3;

  const hasData = sankeyData !== null && sankeyData.links.length > 0;

  return (
    <div className="economy-trend-card">
      <h3 className="economy-trend-card__title">{title}</h3>
      <div className="economy-trend-card__chart-container">
        {hasData && sankeyData ? (
          <ResponsiveContainer width="100%" height="100%">
            <Sankey
              data={sankeyData}
              nodeWidth={12}
              nodePadding={20}
              linkCurvature={0.5}
              margin={{ top: 8, right: 90, bottom: 8, left: 90 }}
              node={(props: any) => {
                const color = nodeColors[props.index] ?? COLOR_POOL;
                // 入力側ノード(左端)はラベルを左に、それ以外は右に配置する。
                const isInput = props.index < inputNodeCount;
                // 供給プールノードはラベルを描画しない(中継点のため)。
                const isPool = props.index === poolNodeIndex;
                const labelX = isInput
                  ? props.x - 6
                  : props.x + props.width + 6;
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
                        fontSize="1rem"
                        textAnchor={textAnchor}
                      >
                        {props.payload?.name}
                      </text>
                    )}
                  </g>
                );
              }}
              link={(linkProps: any) => {
                // リンクの送信元ノード色を半透明で引く
                const sourceIdx = linkProps.payload?.source?.index ?? 0;
                const color = nodeColors[sourceIdx] ?? COLOR_POOL;
                return (
                  <path
                    d={`M${linkProps.sourceX},${linkProps.sourceY}C${linkProps.sourceControlX},${linkProps.sourceY} ${linkProps.targetControlX},${linkProps.targetY} ${linkProps.targetX},${linkProps.targetY}`}
                    fill="none"
                    stroke={color}
                    strokeWidth={linkProps.linkWidth}
                    strokeOpacity={0.3}
                  />
                );
              }}
            />
          </ResponsiveContainer>
        ) : (
          <div className="economy-trend-card__no-data">{noDataLabel}</div>
        )}
      </div>
    </div>
  );
}

export function EconomyTrendOverlay({
  apiBaseUrl,
  productId,
  productName,
  onClose,
}: {
  apiBaseUrl: string;
  productId: number;
  productName: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { data, loading, error } = useEpochCachedStats<MarketPriceStats>(
    apiBaseUrl,
    "/api/v1/stats/market_price"
  );

  return (
    <div className="economy-trend-overlay" onMouseDown={onClose}>
      <div
        className="economy-trend-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="economy-trend-title"
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="economy-trend-dialog__header">
          <h2 id="economy-trend-title" className="economy-trend-dialog__title">
            {productName}
          </h2>
          <button
            type="button"
            className="economy-trend-dialog__close"
            aria-label={t("common.close")}
            title={t("common.close")}
            onClick={onClose}
          >
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>
        {loading ? (
          <div className="economy-trend-dialog__status">{t("common.loading")}</div>
        ) : error || !data ? (
          <div className="economy-trend-dialog__status economy-trend-dialog__status--error">
            {t("common.fetchError")}{error ? `: ${error}` : ""}
          </div>
        ) : (
          <EconomyTrendGrid
            epochs={data.epochs}
            product={data.products.find((p) => p.product_id === productId) ?? null}
          />
        )}
      </div>
    </div>
  );
}

/** 選択中の財の推移を4分割グリッドで描く。統計に財が無いときは各カードを「データなし」にする。 */
function EconomyTrendGrid({
  epochs,
  product: selectedProduct,
}: {
  epochs: number[];
  product: MarketPriceProduct | null;
}) {
  const { t } = useTranslation();
  // スコープ選択(都市全体=city / 地区=zone)。現状は都市全体のみ実装。
  const [scopeMode, setScopeMode] = useState<"city" | "zone">("city");

  // Sankeyダイアグラム用: 選択中の財の最新エポックの値を取得する。
  // 時系列配列の最後の要素が最新エポック(直近の完了エポック)に対応する。
  const sankeyData = useMemo(() => {
    if (!selectedProduct) return { data: null, isShortage: false };
    const len = selectedProduct.total_production.length;
    if (len === 0) return { data: null, isShortage: false };
    const idx = len - 1;
    const production = selectedProduct.total_production[idx] ?? 0;
    const imports = selectedProduct.total_imports[idx] ?? 0;
    const selfSupply = selectedProduct.total_self_supply[idx] ?? 0;
    const consumption = selectedProduct.total_consumption[idx] ?? 0;
    const exports = selectedProduct.total_exports[idx] ?? 0;
    const totalInput = production + imports + selfSupply;
    const totalOutput = consumption + exports;
    const isShortage = totalInput - totalOutput < 0;
    const built = buildSankeyData(production, imports, selfSupply, consumption, exports, {
      production: t("statistics.economyTrendSankeyProduction"),
      imports: t("statistics.economyTrendSankeyImport"),
      selfSupply: t("statistics.economyTrendSankeySelfSupply"),
      pool: t("statistics.economyTrendSankeyPool"),
      consumption: t("statistics.economyTrendSankeyConsumption"),
      exports: t("statistics.economyTrendSankeyExport"),
      unsold: t("statistics.economyTrendSankeyUnsold"),
      shortage: t("statistics.economyTrendSankeyShortage"),
    });
    return { data: built, isShortage };
  }, [selectedProduct, t]);

  const unit = selectedProduct?.unit ?? "";

  // 通貨フォーマット(価格用)
  const priceFormatter = (v: number) =>
    t("common.currency", { value: v.toFixed(0) });
  // 数量フォーマット(生産・消費・輸出入用)。単位があれば付ける。
  const amountFormatter = (v: number) => {
    if (Math.abs(v) >= 1000) {
      return `${(v / 1000).toFixed(1)}k${unit ? ` ${unit}` : ""}`;
    }
    return `${v.toFixed(0)}${unit ? ` ${unit}` : ""}`;
  };

  // 直近エポック(時系列配列の末尾)の値。上段左の数値表示とSankeyで共有する。
  // 配列が空のときは 0 とし、存在しないことを示すために表示側で "-" を当てる。
  const lastIdx = selectedProduct
    ? selectedProduct.total_production.length - 1
    : -1;
  const latestPrice = selectedProduct && lastIdx >= 0
    ? selectedProduct.average_prices[lastIdx] ?? 0
    : 0;
  const latestProduction = selectedProduct && lastIdx >= 0
    ? selectedProduct.total_production[lastIdx] ?? 0
    : 0;
  const latestConsumption = selectedProduct && lastIdx >= 0
    ? selectedProduct.total_consumption[lastIdx] ?? 0
    : 0;
  const latestImport = selectedProduct && lastIdx >= 0
    ? selectedProduct.total_imports[lastIdx] ?? 0
    : 0;
  const latestExport = selectedProduct && lastIdx >= 0
    ? selectedProduct.total_exports[lastIdx] ?? 0
    : 0;
  const hasLatest = !!selectedProduct && lastIdx >= 0;

  return (
    <div className="economy-trend-grid">
      {/*
       * 上段左: 左右2分割。
       * 左=スコープ選択プルダウン、右=直近エポックの数値(価格・生産・消費・輸入・輸出)。
       */}
      <div className="economy-trend-card economy-trend-card--top-left">
        <div className="economy-trend-topleft">
          <div className="economy-trend-topleft__controls">
            <div className="economy-trend-control">
              <label className="economy-trend-control__label">
                {t("statistics.economyTrendScope")}
              </label>
              <select
                className="economy-trend-control__select"
                value={scopeMode}
                onChange={(e) => setScopeMode(e.target.value as "city" | "zone")}
              >
                <option value="city">{t("statistics.economyTrendWholeCity")}</option>
                <option value="zone">{t("statistics.economyTrendByZone")}</option>
              </select>
            </div>
            {scopeMode === "zone" && (
              <div className="economy-trend-control__note">
                {t("statistics.economyTrendZoneNotImplemented")}
              </div>
            )}
          </div>

          <div className="economy-trend-topleft__stats">
            <h3 className="economy-trend-card__title">
              {t("statistics.economyTrendLatest")}
            </h3>
            <dl className="economy-trend-stats">
              <div className="economy-trend-stat">
                <dt>{t("statistics.colPrice")}</dt>
                <dd>{hasLatest ? priceFormatter(latestPrice) : "-"}</dd>
              </div>
              <div className="economy-trend-stat">
                <dt>{t("statistics.colProduction")}</dt>
                <dd>{hasLatest ? amountFormatter(latestProduction) : "-"}</dd>
              </div>
              <div className="economy-trend-stat">
                <dt>{t("statistics.colConsumption")}</dt>
                <dd>{hasLatest ? amountFormatter(latestConsumption) : "-"}</dd>
              </div>
              <div className="economy-trend-stat">
                <dt>{t("statistics.colImport")}</dt>
                <dd>{hasLatest ? amountFormatter(latestImport) : "-"}</dd>
              </div>
              <div className="economy-trend-stat">
                <dt>{t("statistics.colExport")}</dt>
                <dd>{hasLatest ? amountFormatter(latestExport) : "-"}</dd>
              </div>
            </dl>
          </div>
        </div>
      </div>

      {/* 上段右: 平均価格推移 */}
      <TrendChartCard
        title={t("statistics.economyTrendPrice")}
        data={selectedProduct ? zipToChartData(epochs, selectedProduct.average_prices) : []}
        color={COLOR_PRICE}
        formatter={priceFormatter}
        noDataLabel={t("statistics.economyTrendNoData")}
      />

      {/* 下段左: 物資フローSankey(最新エポック) */}
      <SankeyFlowCard
        title={t("statistics.economyTrendSankey")}
        sankeyData={sankeyData.data}
        isShortage={sankeyData.isShortage}
        noDataLabel={t("statistics.economyTrendNoData")}
      />

      {/*
       * 下段右: 需給推移。
       * 生産量・消費量・輸出量・輸入量を実線で、注文量を点線で1グラフに統合描画する。
       */}
      <SupplyDemandTrendCard
        title={t("statistics.economyTrendSupplyDemand")}
        epochs={epochs}
        production={selectedProduct?.total_production ?? []}
        consumption={selectedProduct?.total_consumption ?? []}
        exports={selectedProduct?.total_exports ?? []}
        imports={selectedProduct?.total_imports ?? []}
        order={selectedProduct?.total_order ?? []}
        formatter={amountFormatter}
        noDataLabel={t("statistics.economyTrendNoData")}
        labels={{
          production: t("statistics.economyTrendProductionLegend"),
          consumption: t("statistics.economyTrendConsumptionLegend"),
          export: t("statistics.economyTrendExportLegend"),
          import: t("statistics.economyTrendImportLegend"),
          order: t("statistics.economyTrendOrderLegend"),
        }}
      />
    </div>
  );
}
