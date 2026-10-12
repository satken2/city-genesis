/**
 * 公共サービスウィンドウコンポーネント
 *
 * 公共サービス（products_master の public_service が真の製品）ごとのタブ構成。
 *
 * 各タブの中身は左右2分割。
 * - 左: 公共サービスの価格統制カード。価格方式（固定価格 / 原価比率）の切替・設定値
 *       （固定価格または原価への乗率）の入力・製造原価と、実績・需要見込み・現在の施設容量のグラフを表示する。
 *       市場変動は無く、必ず固定価格か原価比率のいずれかで統制される。
 * - 右: その公共サービスと関連する高圧財を供給する施設の一覧テーブル。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { StatusBadge } from "../common/StatusBadge";
import { SortableAgentTable, type SortableAgentColumn } from "../common/SortableAgentTable";
import type {
  PublicFacilityAgent,
  PublicServicePrice,
  PublicServicePriceMode,
  PublicServiceSupply,
  PublicServiceSupplyData,
} from "../../types";
import { useEpochCachedStats } from "../statistics/useEpochCache";
import { PublicServiceTrendChart } from "./PublicServiceTrendChart";
import type { PublicServiceHistoryData, PublicServiceProductHistory } from "./publicServiceTrend";
import { getPublicServiceFacilities, getPublicServiceInfrastructure } from "./publicServiceInfrastructure";
import "./PublicFacilitiesWindow.css";

type PublicServicePricesData = {
  prices: PublicServicePrice[];
};

/** 公共サービスのタブ。タブキーは製品IDの文字列。 */
type PublicServiceProduct = {
  id: number;
  display_name: string;
  unit: string;
};

type TFn2 = ReturnType<typeof useTranslation>["t"];

/** 数値を桁区切りで表示する */
function num(v?: number | null, digits = 0): string {
  return v == null
    ? "—"
    : v.toLocaleString(undefined, {
        minimumFractionDigits: digits,
        maximumFractionDigits: digits,
      });
}

/** 料金・原価を小数第2位まで残して丸める。電力(1kWh)・水道(1L)は1円未満の単価になるため */
function roundPrice(v: number): number {
  return Math.round(v * 100) / 100;
}

/** 料金・原価を桁区切りで表示する(1円未満の端数は小数第2位まで) */
function price(v: number): string {
  return roundPrice(v).toLocaleString(undefined, { maximumFractionDigits: 2 });
}

/** 割合をパーセント表記にする */
function pct(v?: number): string {
  return v == null ? "—" : `${Math.round(v * 100)}%`;
}

/**
 * 施設一覧テーブルの列定義。
 * 価格・生産量・売却量は左の価格統制カードが都市全体で示すため、
 * ここでは施設ごとの供給余力（生産キャパシティ）と経営指標だけを並べる。
 */
function buildColumns(t: TFn2, unit: string): SortableAgentColumn<PublicFacilityAgent>[] {
  return [
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
    {
      key: "production_capacity",
      label: `${t("publicFacilities.productionCapacity")} (${unit})`,
      align: "right",
      render: (a) => num(a.production_capacity, 0), // null = スペース制約なし（生産上限が定まらない）施設
      sortValue: (a) => a.production_capacity ?? 0,
    },
    {
      key: "balance",
      label: t("publicFacilities.balance"),
      align: "right",
      render: (a) => num(a.balance, 0),
      sortValue: (a) => a.balance,
    },
    {
      key: "utilization_rate",
      label: t("publicFacilities.utilizationRate"),
      align: "right",
      render: (a) => pct(a.utilization_rate),
      sortValue: (a) => a.utilization_rate,
    },
  ];
}

type ServicePriceCardProps = {
  product: PublicServiceProduct;
  row?: PublicServicePrice;
  supply?: PublicServiceSupply;
  infrastructureCapacity?: { label: string; value: number };
  capacityLabel?: string;
  epochs: number[];
  history?: PublicServiceProductHistory;
  currentEpoch: number;
  simulationDays: number;
  /** 価格/比率入力の編集途中の文字列。未編集なら undefined */
  draft?: string;
  onDraftChange: (value: string) => void;
  onModeChange: (mode: PublicServicePriceMode) => void;
  onCommit: () => void;
};

/**
 * 製品ひとつぶんの価格統制カード。
 * 価格方式・設定値・製造原価と、公共サービスの需給推移を表示する。
 */
function ServicePriceCard({
  product,
  row,
  supply,
  infrastructureCapacity,
  capacityLabel,
  epochs,
  history,
  currentEpoch,
  simulationDays,
  draft,
  onDraftChange,
  onModeChange,
  onCommit,
}: ServicePriceCardProps) {
  const { t } = useTranslation();

  const mode: PublicServicePriceMode = row?.mode ?? "cost_ratio";
  const isRatio = mode === "cost_ratio";
  // 入力欄の表示値。編集中はドラフト、確定済みは fixed=価格(小数第2位まで) / cost_ratio=比率。
  const valueText =
    draft !== undefined
      ? draft
      : row
        ? isRatio
          ? String(row.value)
          : String(roundPrice(row.value))
        : "";

  return (
    <div className="psp-card">
      {row && (
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

          <div className="psp-field__control">
            {isRatio && <span>{t("publicServicePrices.costRatioPrefix")}</span>}
            <input
              type="number"
              className="psp-row__input"
              aria-label={t(isRatio ? "publicServicePrices.modeCostRatio" : "publicServicePrices.modeFixed")}
              value={valueText}
              min={0}
              step={isRatio ? 0.05 : "any"}
              onChange={(e) => onDraftChange(e.target.value)}
              onBlur={onCommit}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  onCommit();
                  (e.target as HTMLInputElement).blur();
                }
              }}
            />
            <span>{t(isRatio ? "publicServicePrices.costRatioSuffix" : "publicServicePrices.currencyUnit")}</span>
          </div>

          <div className="psp-card__cost">
            <span className="psp-field__label">
              {t("publicServicePrices.unitCost")}
            </span>
            <span className="psp-field__num">
              {price(row.unit_cost)}
              <span className="psp-row__unit">{t("publicServicePrices.currencyUnit")}/{product.unit}</span>
            </span>
          </div>
        </div>
      )}

      <PublicServiceTrendChart
        epochs={epochs}
        history={history}
        currentEpoch={currentEpoch}
        supply={supply}
        infrastructureCapacity={infrastructureCapacity}
        capacityLabel={capacityLabel}
        simulationDays={simulationDays}
        unit={product.unit}
      />
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
  const marketHistory = useEpochCachedStats<PublicServiceHistoryData>(apiBaseUrl, "/api/v1/stats/market_price");
  const [simulationDays, setSimulationDays] = useState(30);

  const [activeTab, setActiveTab] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // 公共サービス価格。product_id をキーに各行の状態を保持する。
  const [servicePrices, setServicePrices] = useState<
    Record<number, PublicServicePrice>
  >({});
  // 公共サービスの最新需給と、グラフの水平線に使う現在の施設容量。
  const [serviceSupplies, setServiceSupplies] = useState<
    Record<number, PublicServiceSupply>
  >({});
  // 価格/比率テキストボックスの編集中の文字列（product_id -> 入力値）。
  // fixed モードでは固定価格、cost_ratio モードでは原価への乗率の編集途中の文字列を保持する。
  const [valueDrafts, setValueDrafts] = useState<Record<number, string>>({});

  useEffect(() => {
    const controller = new AbortController();

    async function loadData() {
      try {
        const [pricesRes, supplyRes, cityRes] = await Promise.all([
          fetchApi<PublicServicePricesData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-prices"),
            { signal: controller.signal },
          ),
          fetchApi<PublicServiceSupplyData>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-supply?include_facilities=true"),
            { signal: controller.signal },
          ),
          fetchApi<{ city: { simulation_days: number } }>(
            buildApiUrl(apiBaseUrl, "/api/v1/city"),
            { signal: controller.signal },
          ),
        ]);
        if (controller.signal.aborted) return;
        setSimulationDays(cityRes.city.simulation_days);
        const priceMap: Record<number, PublicServicePrice> = {};
        for (const row of pricesRes.prices) {
          priceMap[row.product_id] = row;
        }
        setServicePrices(priceMap);
        const supplyMap: Record<number, PublicServiceSupply> = {};
        for (const row of supplyRes.services) {
          supplyMap[row.product_id] = row;
        }
        setServiceSupplies(supplyMap);
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
        nextValue = Math.max(0, roundPrice(effective));
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

  // 公共サービスの製品一覧。マスタの public_service から抽出し、そのままタブになる。
  const publicServiceProducts = useMemo<PublicServiceProduct[]>(() => {
    if (!masterData?.products) return [];
    return Object.entries(masterData.products)
      .filter(([, entry]) => entry.public_service)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: entry.display_name,
        unit: entry.unit,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.products]);

  // データ読み込み後、未選択なら先頭タブを自動選択する。
  useEffect(() => {
    if (activeTab === null && publicServiceProducts.length > 0) {
      setActiveTab(String(publicServiceProducts[0].id));
    }
  }, [activeTab, publicServiceProducts]);

  const activeProduct = useMemo(
    () => publicServiceProducts.find((p) => String(p.id) === activeTab) ?? null,
    [publicServiceProducts, activeTab],
  );

  const activeFacilities = useMemo(() => activeProduct ? getPublicServiceFacilities(activeProduct.id, serviceSupplies) : [],
    [activeProduct, serviceSupplies]);
  const infrastructure = activeProduct ? getPublicServiceInfrastructure(activeProduct.id) : undefined;
  const infrastructureSupply = infrastructure ? serviceSupplies[infrastructure.productId] : undefined;
  const infrastructureCapacity = infrastructure && infrastructureSupply ? {
    label: t(`publicServiceTrend.${infrastructure.infrastructureCapacityLabel}`),
    value: infrastructureSupply.supply_capacity * infrastructure.quantityScale,
  } : undefined;
  const columns = useMemo(() => buildColumns(t, activeProduct?.unit ?? ""), [t, activeProduct?.unit]);

  if (loading || marketHistory.loading) {
    return <div className="pf-window__loading">{t("common.loading")}</div>;
  }

  const loadError = error ?? marketHistory.error;
  if (loadError) {
    return <div className="pf-window__error">{loadError}</div>;
  }

  return (
    <div className="pf-window">
      <nav className="pf-tabs">
        {publicServiceProducts.map((product) => (
          <button
            key={product.id}
            type="button"
            className={[
              "pf-tabs__tab",
              activeTab === String(product.id) && "pf-tabs__tab--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setActiveTab(String(product.id))}
          >
            {product.display_name}
          </button>
        ))}
      </nav>

      <div className="pf-window__content">
        {activeProduct && (
          <div className="pf-window__service">
            <ServicePriceCard
              product={activeProduct}
              row={servicePrices[activeProduct.id]}
              supply={serviceSupplies[activeProduct.id]}
              infrastructureCapacity={infrastructureCapacity}
              capacityLabel={infrastructure ? t(`publicServiceTrend.${infrastructure.capacityLabel}`) : undefined}
              epochs={marketHistory.data?.epochs ?? []}
              history={marketHistory.data?.products.find((row) => row.product_id === activeProduct.id)}
              currentEpoch={marketHistory.data?.epoch ?? 0}
              simulationDays={simulationDays}
              draft={valueDrafts[activeProduct.id]}
              onDraftChange={(value) =>
                setValueDrafts((prev) => ({ ...prev, [activeProduct.id]: value }))
              }
              onModeChange={(mode) => handleModeChange(activeProduct.id, mode)}
              onCommit={() => handleValueCommit(activeProduct.id)}
            />
          </div>
        )}

        <div className="pf-window__facilities">
          <SortableAgentTable
            columns={columns}
            rows={activeFacilities}
            emptyText={t("budget.noFacilities")}
          />
        </div>
      </div>
    </div>
  );
}
