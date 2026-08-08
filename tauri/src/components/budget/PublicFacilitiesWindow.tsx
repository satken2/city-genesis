/**
 * 公共施設ウィンドウコンポーネント
 *
 * ページ全体がカテゴリタブ構成。タブは regulated（価格統制対象）な製品ごとのタブに続けて、
 * 不動産・公営施設・教育機関・港湾施設・市政府の各タブを並べる。
 *
 * 各タブの中身は縦2分割。
 * - 上: 公共サービスの価格統制カード。価格方式（固定価格 / 原価比率）の切替・設定値
 *       （固定価格または原価への乗率）の入力・製造原価・想定最大需要・需給ドーナツを表示する。
 *       市場変動は無く、必ず固定価格か原価比率のいずれかで統制される。
 *       公共サービスを持たないタブ（製品別タブ以外）ではこのカードは出さず、下の一覧だけになる。
 * - 下: そのカテゴリに属する施設の一覧テーブル。列ヘッダのクリックでフロントエンド側ソートを行い、
 *       行クリックで経済主体詳細ウィンドウを開く。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { StatusBadge } from "../common/StatusBadge";
import type {
  PublicFacilityAgent,
  PublicServicePrice,
  PublicServicePriceMode,
} from "../../types";
import "./PublicFacilitiesWindow.css";

type PublicFacilitiesData = {
  facilities: Record<string, PublicFacilityAgent[]>;
};

type PublicServicePricesData = {
  prices: PublicServicePrice[];
};

// 製品別タブの後ろに並ぶ、type 軸の特別タブ。
// key はバックエンドの get_public_facilities が返すグループキーと一致させる。
const SPECIAL_TABS: { key: string; labelKey: string }[] = [
  { key: "real_estate", labelKey: "publicFacilities.tabRealEstate" },
  { key: "public_standard", labelKey: "publicFacilities.tabPublicStandard" },
  { key: "education", labelKey: "publicFacilities.tabEducation" },
  { key: "port", labelKey: "publicFacilities.tabPort" },
  { key: "city", labelKey: "publicFacilities.tabCity" },
];

// 施設一覧テーブルの表示バリアント。アクティブタブ（グループキー）によって出し分ける。
//   - public_service: regulated 製品別タブ（タブキーが製品IDの数値）
//   - service:        公営施設タブ
//   - real_estate:    不動産タブ
//   - port:           港湾施設タブ
//   - basic:          教育機関・市政府タブ（メトリクス無し、基本情報のみ）
type CardVariant = "public_service" | "service" | "real_estate" | "port" | "basic";

// メトリクスを持たない（基本情報のみの）タブキー。
const BASIC_TAB_KEYS = new Set(["education", "city"]);

function resolveCardVariant(tabKey: string): CardVariant {
  if (tabKey === "real_estate") return "real_estate";
  if (tabKey === "port") return "port";
  if (BASIC_TAB_KEYS.has(tabKey)) return "basic";
  if (tabKey === "public_standard") return "service";
  return "public_service";
}

/** regulated 製品のタブ。タブキーが製品IDの数値文字列かどうかで判別する。 */
type RegulatedProduct = {
  id: number;
  display_name: string;
  unit: string;
};

// --- 施設一覧テーブルの列定義 ---

type TFn2 = ReturnType<typeof useTranslation>["t"];

type CellAlign = "left" | "right" | "center";

type ColumnDef = {
  key: string;
  label: string;
  align?: CellAlign;
  render: (item: PublicFacilityAgent) => React.ReactNode;
  sortValue?: (item: PublicFacilityAgent) => number | string;
};

/** 数値を桁区切りで表示する */
function num(v?: number | null, digits = 0): string {
  return v == null
    ? "—"
    : v.toLocaleString(undefined, {
        minimumFractionDigits: digits,
        maximumFractionDigits: digits,
      });
}

/** 割合をパーセント表記にする */
function pct(v?: number): string {
  return v == null ? "—" : `${Math.round(v * 100)}%`;
}

/**
 * タブの種類に応じて表示する列定義を返す。
 * 全タブ共通でステータス・名前を表示し、バリアントに応じて追加のメトリクス列を並べる。
 */
function buildColumns(variant: CardVariant, t: TFn2): ColumnDef[] {
  const base: ColumnDef[] = [
    {
      key: "status",
      label: t("agentList.colStatus"),
      align: "center",
      render: (a) => <StatusBadge status={a.status} />,
      sortValue: (a) => a.status,
    },
    {
      key: "name",
      label: t("agentList.colName"),
      render: (a) => a.name,
      sortValue: (a) => a.name,
    },
  ];

  switch (variant) {
    case "public_service":
      // 公共サービスの供給施設。価格・生産量・売却量は上の価格統制カードが都市全体で示すため、
      // ここでは施設ごとの供給余力（生産キャパシティ）と経営指標だけを並べる。
      return [
        ...base,
        {
          key: "production_capacity",
          label: t("publicFacilities.productionCapacity"),
          align: "right" as const,
          // null = スペース制約なし（生産上限が定まらない）施設。
          render: (a: PublicFacilityAgent) => num(a.production_capacity, 0),
          sortValue: (a: PublicFacilityAgent) => a.production_capacity ?? 0,
        },
        {
          key: "balance",
          label: t("publicFacilities.balance"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.balance, 0),
          sortValue: (a: PublicFacilityAgent) => a.balance ?? 0,
        },
        {
          key: "utilization_rate",
          label: t("publicFacilities.utilizationRate"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => pct(a.utilization_rate),
          sortValue: (a: PublicFacilityAgent) => a.utilization_rate ?? 0,
        },
      ];
    case "service":
      return [
        ...base,
        {
          key: "production",
          label: t("publicFacilities.production"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.production, 1),
          sortValue: (a: PublicFacilityAgent) => a.production ?? 0,
        },
        {
          key: "sold_amount",
          label: t("publicFacilities.soldAmount"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.sold_amount, 1),
          sortValue: (a: PublicFacilityAgent) => a.sold_amount ?? 0,
        },
        {
          key: "price",
          label: t("publicFacilities.price"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.price, 1),
          sortValue: (a: PublicFacilityAgent) => a.price ?? 0,
        },
        {
          key: "balance",
          label: t("publicFacilities.balance"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.balance, 0),
          sortValue: (a: PublicFacilityAgent) => a.balance ?? 0,
        },
        {
          key: "utilization_rate",
          label: t("publicFacilities.utilizationRate"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => pct(a.utilization_rate),
          sortValue: (a: PublicFacilityAgent) => a.utilization_rate ?? 0,
        },
      ];
    case "real_estate":
      return [
        ...base,
        {
          key: "supply_floor",
          label: t("publicFacilities.supplyFloor"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.supply_floor, 0),
          sortValue: (a: PublicFacilityAgent) => a.supply_floor ?? 0,
        },
        {
          key: "occupancy_rate",
          label: t("publicFacilities.occupancyRate"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => pct(a.occupancy_rate),
          sortValue: (a: PublicFacilityAgent) => a.occupancy_rate ?? 0,
        },
        {
          key: "rent",
          label: t("publicFacilities.rent"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.rent, 0),
          sortValue: (a: PublicFacilityAgent) => a.rent ?? 0,
        },
        {
          key: "balance",
          label: t("publicFacilities.balance"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.balance, 0),
          sortValue: (a: PublicFacilityAgent) => a.balance ?? 0,
        },
        {
          key: "utilization_rate",
          label: t("publicFacilities.utilizationRate"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => pct(a.utilization_rate),
          sortValue: (a: PublicFacilityAgent) => a.utilization_rate ?? 0,
        },
      ];
    case "port":
      return [
        ...base,
        {
          key: "capacity",
          label: t("publicFacilities.capacity"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.capacity, 0),
          sortValue: (a: PublicFacilityAgent) => a.capacity ?? 0,
        },
        {
          key: "import_handling",
          label: t("publicFacilities.importHandling"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.import_handling, 0),
          sortValue: (a: PublicFacilityAgent) => a.import_handling ?? 0,
        },
        {
          key: "export_handling",
          label: t("publicFacilities.exportHandling"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => num(a.export_handling, 0),
          sortValue: (a: PublicFacilityAgent) => a.export_handling ?? 0,
        },
        {
          key: "utilization_rate",
          label: t("publicFacilities.utilizationRate"),
          align: "right" as const,
          render: (a: PublicFacilityAgent) => pct(a.utilization_rate),
          sortValue: (a: PublicFacilityAgent) => a.utilization_rate ?? 0,
        },
      ];
    default:
      // basic: 基本情報のみ（ステータス・名前）
      return base;
  }
}

// 需給ドーナツの寸法（viewBox 0 0 100 100 のユーザー単位）。
// 外周リング=生産量、内周リング=需要量で、リング1周が供給キャパシティに相当する。
const DONUT_OUTER_R = 40;
const DONUT_INNER_R = 27;
const DONUT_OUTER_W = 11;
const DONUT_INNER_W = 9;

/** キャパシティに対する比率を 0〜1 に丸める。キャパシティ 0 なら 0 扱い。 */
function capacityRatio(value: number, capacity: number): number {
  if (capacity <= 0) return 0;
  return Math.min(1, value / capacity);
}

/** ドーナツ中央の数値。桁が大きくても収まるよう短縮表記にする。 */
const compactFormat = new Intl.NumberFormat(undefined, {
  notation: "compact",
  maximumFractionDigits: 1,
});

type SupplyDonutProps = {
  production: number;
  demand: number;
  capacity: number;
  /** 製品の単位。中央の数値の添え字に使う */
  unit: string;
};

/**
 * 需給とキャパシティのドーナツ表示。
 * 二重リングの1周が供給キャパシティで、外周が生産量・内周が需要量の占有率を示す。
 * 需要がキャパシティを超えた場合はリングを1周で止め、色を赤に変えて溢れを示す
 * （正確な超過量は凡例のパーセントで読む）。
 * 合成比率ではなく上限つきゲージのため recharts の Pie ではなく素の SVG で描く。
 */
function SupplyDonut({ production, demand, capacity, unit }: SupplyDonutProps) {
  const { t } = useTranslation();

  const outerCirc = 2 * Math.PI * DONUT_OUTER_R;
  const innerCirc = 2 * Math.PI * DONUT_INNER_R;
  const prodRatio = capacityRatio(production, capacity);
  const demandRatio = capacityRatio(demand, capacity);
  const isOverCapacity = capacity > 0 && demand > capacity;

  const legend: { key: string; label: string; value: number; ratio: number | null }[] = [
    {
      key: "production",
      label: t("publicServicePrices.production"),
      value: production,
      ratio: capacity > 0 ? production / capacity : null,
    },
    {
      key: "demand",
      label: t("publicServicePrices.demand"),
      value: demand,
      ratio: capacity > 0 ? demand / capacity : null,
    },
    {
      key: "capacity",
      label: t("publicServicePrices.supplyCapacity"),
      value: capacity,
      ratio: null,
    },
  ];

  return (
    <div className="psp-donut">
      <svg className="psp-donut__chart" viewBox="0 0 100 100" role="img">
        {/* 12時始まりにするためリングだけ反時計回りに90度戻す */}
        <g transform="rotate(-90 50 50)" fill="none">
          <circle
            className="psp-donut__track"
            cx="50"
            cy="50"
            r={DONUT_OUTER_R}
            strokeWidth={DONUT_OUTER_W}
          />
          <circle
            className="psp-donut__arc psp-donut__arc--production"
            cx="50"
            cy="50"
            r={DONUT_OUTER_R}
            strokeWidth={DONUT_OUTER_W}
            strokeDasharray={`${outerCirc * prodRatio} ${outerCirc}`}
          />
          <circle
            className="psp-donut__track"
            cx="50"
            cy="50"
            r={DONUT_INNER_R}
            strokeWidth={DONUT_INNER_W}
          />
          <circle
            className={`psp-donut__arc psp-donut__arc--demand ${
              isOverCapacity ? "psp-donut__arc--over" : ""
            }`}
            cx="50"
            cy="50"
            r={DONUT_INNER_R}
            strokeWidth={DONUT_INNER_W}
            strokeDasharray={`${innerCirc * demandRatio} ${innerCirc}`}
          />
        </g>
        <text className="psp-donut__center-value" x="50" y="50">
          {compactFormat.format(Math.round(capacity))}
        </text>
        <text className="psp-donut__center-unit" x="50" y="61">
          {unit}
        </text>
      </svg>

      <ul className="psp-donut__legend">
        {legend.map((item) => (
          <li
            key={item.key}
            className={`psp-donut__legend-item psp-donut__legend-item--${item.key}`}
          >
            <span className="psp-donut__legend-swatch" />
            <span className="psp-donut__legend-label">{item.label}</span>
            <span className="psp-donut__legend-value">
              {Math.round(item.value).toLocaleString()}
              {item.ratio != null && (
                <span className="psp-donut__legend-ratio">
                  {Math.round(item.ratio * 100)}%
                </span>
              )}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

type ServicePriceCardProps = {
  product: RegulatedProduct;
  row?: PublicServicePrice;
  /** 価格/比率入力の編集途中の文字列。未編集なら undefined */
  draft?: string;
  onDraftChange: (value: string) => void;
  onModeChange: (mode: PublicServicePriceMode) => void;
  onCommit: () => void;
};

/**
 * 製品ひとつぶんの価格統制カード。
 * 価格方式・設定値・製造原価・想定最大需要と、需給ドーナツ（生産量 / 需要量 / 供給キャパシティ）を並べる。
 */
function ServicePriceCard({
  product,
  row,
  draft,
  onDraftChange,
  onModeChange,
  onCommit,
}: ServicePriceCardProps) {
  const { t } = useTranslation();

  const mode: PublicServicePriceMode = row?.mode ?? "cost_ratio";
  const isRatio = mode === "cost_ratio";
  // 入力欄の表示値。編集中はドラフト、確定済みは fixed=整数価格 / cost_ratio=比率。
  const valueText =
    draft !== undefined
      ? draft
      : row
        ? isRatio
          ? String(row.value)
          : String(Math.round(row.value))
        : "";
  // 原価比率モードでの実効価格 = 製造原価 × 比率。固定価格モードでは value がそのまま価格。
  const effectivePrice = row ? (isRatio ? row.unit_cost * row.value : row.value) : 0;

  return (
    <div className="psp-card">
      <div className="psp-card__fields">
        <div className="psp-field">
          <span className="psp-field__label">
            {t("publicServicePrices.priceMode")}
          </span>
          <select
            className="psp-row__mode"
            value={mode}
            onChange={(e) =>
              onModeChange(e.target.value as PublicServicePriceMode)
            }
          >
            <option value="fixed">{t("publicServicePrices.modeFixed")}</option>
            <option value="cost_ratio">
              {t("publicServicePrices.modeCostRatio")}
            </option>
          </select>
        </div>

        <div className="psp-field">
          <span className="psp-field__label">
            {t("publicServicePrices.value")}
          </span>
          <div className="psp-field__control">
            {isRatio && <span className="psp-row__times">×</span>}
            <input
              type="number"
              className="psp-row__input"
              value={valueText}
              min={0}
              step={isRatio ? 0.05 : 1}
              onChange={(e) => onDraftChange(e.target.value)}
              onBlur={onCommit}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  onCommit();
                  (e.target as HTMLInputElement).blur();
                }
              }}
            />
            {isRatio && (
              <span
                className="psp-row__effective"
                title={t("publicServicePrices.effectivePrice")}
              >
                = {Math.round(effectivePrice).toLocaleString()}
              </span>
            )}
          </div>
        </div>

        <div className="psp-field">
          <span className="psp-field__label">
            {t("publicServicePrices.unitCost")}
          </span>
          <span className="psp-field__num">
            {Math.round(row?.unit_cost ?? 0).toLocaleString()}
            <span className="psp-row__unit">/{product.unit}</span>
          </span>
        </div>

        <div className="psp-field">
          <span className="psp-field__label">
            {t("publicServicePrices.maxDemand")}
          </span>
          <span className="psp-field__num">
            {Math.round(row?.max_demand ?? 0).toLocaleString()}
            <span className="psp-row__unit">{product.unit}</span>
          </span>
        </div>
      </div>

      <div className="psp-card__gauge">
        <span className="psp-field__label">
          {t("publicServicePrices.supplyStatus")}
        </span>
        <SupplyDonut
          production={row?.production ?? 0}
          demand={row?.demand ?? 0}
          capacity={row?.supply_capacity ?? 0}
          unit={product.unit}
        />
      </div>
    </div>
  );
}

export function PublicFacilitiesWindow({
  apiBaseUrl,
}: {
  apiBaseUrl: string;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [facilities, setFacilities] = useState<PublicFacilitiesData | null>(null);
  const [activeTab, setActiveTab] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // 公共サービス価格。product_id をキーに各行の状態を保持する。
  const [servicePrices, setServicePrices] = useState<
    Record<number, PublicServicePrice>
  >({});
  // 価格/比率テキストボックスの編集中の文字列（product_id -> 入力値）。
  // fixed モードでは固定価格、cost_ratio モードでは原価への乗率の編集途中の文字列を保持する。
  const [valueDrafts, setValueDrafts] = useState<Record<number, string>>({});

  // 施設一覧テーブルのソート状態。
  const [sortKey, setSortKey] = useState<string | null>(null);
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");

  useEffect(() => {
    const controller = new AbortController();

    async function loadData() {
      try {
        const [facilitiesRes, pricesRes] = await Promise.all([
          fetchApi<PublicFacilitiesData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/budgets/public-facilities"),
            { signal: controller.signal },
          ),
          fetchApi<PublicServicePricesData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-prices"),
            { signal: controller.signal },
          ),
        ]);
        setFacilities(facilitiesRes);
        const priceMap: Record<number, PublicServicePrice> = {};
        for (const row of pricesRes.prices) {
          priceMap[row.product_id] = row;
        }
        setServicePrices(priceMap);
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      } finally {
        setLoading(false);
      }
    }

    loadData();
    return () => controller.abort();
  }, [apiBaseUrl]);

  // 公共サービス価格ポリシー（モード + 値）の更新をバックエンドへ送信し、レスポンスで全行を更新する。
  const patchServicePrice = useCallback(
    async (productId: number, mode: PublicServicePriceMode, value: number) => {
      try {
        const res = await fetchApi<PublicServicePricesData>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-prices"),
          {
            method: "PATCH",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              prices: { [String(productId)]: { mode, value } },
            }),
          },
        );
        const priceMap: Record<number, PublicServicePrice> = {};
        for (const row of res.prices) {
          priceMap[row.product_id] = row;
        }
        setServicePrices(priceMap);
        setValueDrafts((prev) => {
          const next = { ...prev };
          delete next[productId];
          return next;
        });
      } catch (err) {
        setError(err instanceof Error ? err.message : "Unknown error");
      }
    },
    [apiBaseUrl],
  );

  // 価格方式（固定価格 / 原価比率）の切替。
  // 切替時は価格が不連続に飛ばないよう、現在の実効価格を保ったまま新モードの設定値へ換算する。
  //   - 固定価格へ切替: 新しい値 = 現在の実効価格
  //   - 原価比率へ切替: 新しい値 = 現在の実効価格 / 製造原価（原価が 0 なら既定比率 1.0）
  const handleModeChange = useCallback(
    (productId: number, nextMode: PublicServicePriceMode) => {
      const row = servicePrices[productId];
      if (!row) return;
      if (row.mode === nextMode) return;
      const effective =
        row.mode === "cost_ratio" ? row.unit_cost * row.value : row.value;
      let nextValue: number;
      if (nextMode === "fixed") {
        nextValue = Math.max(0, Math.round(effective));
      } else {
        nextValue =
          row.unit_cost > 0
            ? Math.max(0, Math.round((effective / row.unit_cost) * 100) / 100)
            : 1.0;
      }
      patchServicePrice(productId, nextMode, nextValue);
    },
    [servicePrices, patchServicePrice],
  );

  // 価格/比率テキストボックスの確定（onBlur / Enter）。現在のモードを維持したまま値だけ更新する。
  const handleValueCommit = useCallback(
    (productId: number) => {
      const draft = valueDrafts[productId];
      if (draft === undefined) return;
      const parsed = Number(draft);
      const row = servicePrices[productId];
      if (!row || Number.isNaN(parsed)) {
        setValueDrafts((prev) => {
          const next = { ...prev };
          delete next[productId];
          return next;
        });
        return;
      }
      patchServicePrice(productId, row.mode, Math.max(0, parsed));
    },
    [valueDrafts, servicePrices, patchServicePrice],
  );

  // regulated（価格統制対象）な製品の一覧。マスタから抽出する。
  const regulatedProducts = useMemo<RegulatedProduct[]>(() => {
    if (!masterData?.products) return [];
    return Object.entries(masterData.products)
      .filter(([, entry]) => entry.regulated)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: entry.display_name,
        unit: entry.unit,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.products]);

  // ページ全体のタブ一覧。先頭に製品別タブ（regulated 製品）、続けて type 軸の特別タブを並べる。
  const facilityTabs = useMemo(() => {
    const productTabs = regulatedProducts.map((p) => ({
      key: String(p.id),
      label: p.display_name,
    }));
    const specialTabs = SPECIAL_TABS.map((tab) => ({
      key: tab.key,
      label: t(tab.labelKey),
    }));
    return [...productTabs, ...specialTabs];
  }, [regulatedProducts, t]);

  // データ読み込み後、未選択なら先頭タブを自動選択する。
  useEffect(() => {
    if (activeTab === null && facilityTabs.length > 0) {
      setActiveTab(facilityTabs[0].key);
    }
  }, [activeTab, facilityTabs]);

  // 行クリックで経済主体詳細ウィンドウを新規に開く。
  const openAgentDetail = useCallback((agentId: number) => {
    void invoke("open_registered_window", {
      windowType: "agent_detail",
      entityId: String(agentId),
    });
  }, []);

  const activeFacilities =
    activeTab !== null && facilities
      ? facilities.facilities[activeTab] ?? []
      : [];

  const cardVariant = activeTab !== null ? resolveCardVariant(activeTab) : "basic";

  // アクティブタブが製品別タブなら、その製品。特別タブでは null（価格カードを出さない）。
  const activeProduct = useMemo(
    () => regulatedProducts.find((p) => String(p.id) === activeTab) ?? null,
    [regulatedProducts, activeTab],
  );

  // タブ種類に応じた列定義。
  const columns = useMemo(
    () => buildColumns(cardVariant, t),
    [cardVariant, t],
  );

  // ソート適用後の表示行。
  const rows = useMemo(() => {
    if (sortKey == null) return activeFacilities;
    const col = columns.find((c) => c.key === sortKey);
    if (!col?.sortValue) return activeFacilities;
    const accessor = col.sortValue;
    const sorted = [...activeFacilities].sort((a, b) => {
      const va = accessor(a);
      const vb = accessor(b);
      if (typeof va === "string" || typeof vb === "string") {
        return String(va).localeCompare(String(vb));
      }
      return va - vb;
    });
    if (sortDir === "desc") sorted.reverse();
    return sorted;
  }, [activeFacilities, columns, sortKey, sortDir]);

  function toggleSort(col: ColumnDef) {
    if (!col.sortValue) return;
    if (sortKey === col.key) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(col.key);
      setSortDir("asc");
    }
  }

  if (loading) {
    return <div className="pf-window__loading">{t("common.loading")}</div>;
  }

  if (error) {
    return <div className="pf-window__error">{error}</div>;
  }

  return (
    <div className="pf-window">
      <nav className="pf-tabs">
        {facilityTabs.map((tab) => (
          <button
            key={tab.key}
            type="button"
            className={[
              "pf-tabs__tab",
              activeTab === tab.key && "pf-tabs__tab--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setActiveTab(tab.key)}
          >
            {tab.label}
          </button>
        ))}
      </nav>

      {/* 上半分: 公共サービスの価格統制（製品別タブのみ） */}
      {activeProduct && (
        <div className="pf-window__top">
          <ServicePriceCard
            product={activeProduct}
            row={servicePrices[activeProduct.id]}
            draft={valueDrafts[activeProduct.id]}
            onDraftChange={(value) =>
              setValueDrafts((prev) => ({ ...prev, [activeProduct.id]: value }))
            }
            onModeChange={(mode) => handleModeChange(activeProduct.id, mode)}
            onCommit={() => handleValueCommit(activeProduct.id)}
          />
        </div>
      )}

      {/* 下半分: 公共施設一覧テーブル */}
      <div className="pf-window__bottom">
        <div className="pf-list">
          {activeFacilities.length > 0 ? (
            <table className="pf-table">
              <thead>
                <tr>
                  {columns.map((col) => {
                    const sortable = !!col.sortValue;
                    const active = sortKey === col.key;
                    return (
                      <th
                        key={col.key}
                        className={[
                          col.align ? `pf-th--${col.align}` : "",
                          sortable ? "pf-th--sortable" : "",
                          active ? "pf-th--active" : "",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        onClick={() => toggleSort(col)}
                      >
                        {col.label}
                        {sortable && (
                          <span className="pf-th__arrow">
                            {active ? (sortDir === "asc" ? "▲" : "▼") : "⇅"}
                          </span>
                        )}
                      </th>
                    );
                  })}
                </tr>
              </thead>
              <tbody>
                {rows.map((agent) => (
                  <tr
                    key={agent.agent_id}
                    className="pf-row"
                    onClick={() => openAgentDetail(agent.agent_id)}
                  >
                    {columns.map((col) => (
                      <td
                        key={col.key}
                        className={col.align ? `pf-td--${col.align}` : ""}
                      >
                        {col.render(agent)}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <div className="pf-list__empty">
              {t("budget.noFacilities")}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
