/**
 * 公共施設ウィンドウコンポーネント
 *
 * 縦2分割構成。
 * - 上: 公共サービス表。regulated（価格統制対象）な製品について、価格方式（固定価格 / 原価比率）の
 *       切替・設定値（固定価格または原価への乗率）の入力・製造原価・供給キャパシティ・想定最大需要・
 *       需要量・生産量を表示する。市場変動は無く、必ず固定価格か原価比率のいずれかで統制される。
 * - 下: 公共施設一覧テーブル。製品別タブ（公共サービスと同じ regulated 製品ごと）に加えて、
 *       不動産・公営施設・教育機関・港湾施設・市政府の各タブで施設をテーブル形式で一覧表示する。
 *       列ヘッダのクリックでフロントエンド側ソートを行う。
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

// カードの表示バリアント。アクティブタブ（グループキー）によって出し分ける。
//   - service:     regulated 製品別タブ（数値ID）と公営施設タブ
//   - real_estate: 不動産タブ
//   - port:        港湾施設タブ
//   - basic:       教育機関・市政府タブ（メトリクス無し、基本情報のみ）
type CardVariant = "service" | "real_estate" | "port" | "basic";

// メトリクスを持たない（基本情報のみの）タブキー。
const BASIC_TAB_KEYS = new Set(["education", "city"]);

function resolveCardVariant(tabKey: string): CardVariant {
  if (tabKey === "real_estate") return "real_estate";
  if (tabKey === "port") return "port";
  if (BASIC_TAB_KEYS.has(tabKey)) return "basic";
  return "service";
}

// --- 施設一覧テーブルの列定義 ---

type TFn2 = ReturnType<typeof useTranslation>["t"];

type CellAlign = "left" | "right" | "center";

type ColumnDef = {
  key: string;
  label: string;
  align?: CellAlign;
  render: (item: PublicFacilityAgent, masterLabel?: string) => React.ReactNode;
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
 * 全タブ共通で ID・ステータス・名前・バリアントを表示し、
 * バリアントに応じて追加のメトリクス列を並べる。
 */
function buildColumns(variant: CardVariant, t: TFn2): ColumnDef[] {
  const base: ColumnDef[] = [
    {
      key: "agent_id",
      label: "ID",
      align: "right",
      render: (a) => <span className="pf-col-id">{a.agent_id}</span>,
      sortValue: (a) => a.agent_id,
    },
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
      key: "variant",
      label: t("agentList.filterVariant"),
      render: (a, masterLabel) => masterLabel ?? String(a.variant),
      sortValue: (a) => a.variant,
    },
  ];

  switch (variant) {
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
      // basic: 基本情報のみ（ID・ステータス・名前・バリアント）
      return base;
  }
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
  const regulatedProducts = useMemo(() => {
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

  // 下半分のタブ一覧。先頭に製品別タブ（regulated 製品）、続けて type 軸の特別タブを並べる。
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
      {/* 上半分: 公共サービス表 */}
      <div className="pf-window__top">
        <h2 className="pf-window__title">{t("publicServicePrices.title")}</h2>

        <div className="psp-list">
          {regulatedProducts.length === 0 ? (
            <div className="pf-list__empty">{t("publicServicePrices.empty")}</div>
          ) : (
            <table className="psp-table">
              <thead>
                <tr>
                  <th className="psp-table__th-name">
                    {t("publicServicePrices.product")}
                  </th>
                  <th>{t("publicServicePrices.priceMode")}</th>
                  <th className="psp-table__th-num">
                    {t("publicServicePrices.value")}
                  </th>
                  <th className="psp-table__th-num">
                    {t("publicServicePrices.unitCost")}
                  </th>
                  <th className="psp-table__th-num">
                    {t("publicServicePrices.maxDemand")}
                  </th>
                  <th className="psp-table__th-graph">
                    {t("publicServicePrices.supplyStatus")}
                  </th>
                </tr>
              </thead>
              <tbody>
                {regulatedProducts.map((product) => {
                  const row = servicePrices[product.id];
                  const mode: PublicServicePriceMode = row?.mode ?? "cost_ratio";
                  const isRatio = mode === "cost_ratio";
                  const draft = valueDrafts[product.id];
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
                  const effectivePrice = row
                    ? isRatio
                      ? row.unit_cost * row.value
                      : row.value
                    : 0;
                  return (
                    <tr key={product.id}>
                      <td className="psp-table__name">
                        {product.display_name}
                        <span className="psp-row__unit">/{product.unit}</span>
                      </td>
                      <td className="psp-table__center">
                        <select
                          className="psp-row__mode"
                          value={mode}
                          onChange={(e) =>
                            handleModeChange(
                              product.id,
                              e.target.value as PublicServicePriceMode,
                            )
                          }
                        >
                          <option value="fixed">
                            {t("publicServicePrices.modeFixed")}
                          </option>
                          <option value="cost_ratio">
                            {t("publicServicePrices.modeCostRatio")}
                          </option>
                        </select>
                      </td>
                      <td className="psp-table__num">
                        <div className="psp-row__value">
                          <div className="psp-row__value-input">
                            {isRatio && <span className="psp-row__times">×</span>}
                            <input
                              type="number"
                              className="psp-row__input"
                              value={valueText}
                              min={0}
                              step={isRatio ? 0.05 : 1}
                              onChange={(e) =>
                                setValueDrafts((prev) => ({
                                  ...prev,
                                  [product.id]: e.target.value,
                                }))
                              }
                              onBlur={() => handleValueCommit(product.id)}
                              onKeyDown={(e) => {
                                if (e.key === "Enter") {
                                  handleValueCommit(product.id);
                                  (e.target as HTMLInputElement).blur();
                                }
                              }}
                            />
                          </div>
                          {isRatio && (
                            <span
                              className="psp-row__effective"
                              title={t("publicServicePrices.effectivePrice")}
                            >
                              = {Math.round(effectivePrice).toLocaleString()}
                            </span>
                          )}
                        </div>
                      </td>
                      <td className="psp-table__num">
                        {Math.round(row?.unit_cost ?? 0).toLocaleString()}
                      </td>
                      <td className="psp-table__num">
                        {Math.round(row?.max_demand ?? 0).toLocaleString()}
                      </td>
                      <td className="psp-table__graph-cell">
                        {(() => {
                          const capacity = row?.supply_capacity ?? 0;
                          const production = row?.production ?? 0;
                          const demand = row?.demand ?? 0;

                          const prodPercent = capacity > 0 ? (production / capacity) * 100 : 0;
                          const demandPercent = capacity > 0 ? (demand / capacity) * 100 : 0;
                          const isOverCapacity = capacity > 0 && demand > capacity;

                          return (
                            <div className="psp-status-gauge">
                              <div className="psp-status-gauge__labels">
                                <span className="psp-status-gauge__label psp-status-gauge__label--production" title={t("publicServicePrices.production")}>
                                  P: {Math.round(production).toLocaleString()}
                                </span>
                                <span className="psp-status-gauge__label psp-status-gauge__label--demand" title={t("publicServicePrices.demand")}>
                                  D: {Math.round(demand).toLocaleString()}
                                </span>
                                <span className="psp-status-gauge__label psp-status-gauge__label--capacity" title={t("publicServicePrices.supplyCapacity")}>
                                  C: {Math.round(capacity).toLocaleString()}
                                </span>
                              </div>
                              <div className="psp-status-gauge__track">
                                <div
                                  className="psp-status-gauge__bar psp-status-gauge__bar--production"
                                  style={{ width: `${Math.min(100, prodPercent)}%` }}
                                />
                                <div
                                  className={`psp-status-gauge__bar psp-status-gauge__bar--demand ${
                                    isOverCapacity ? "psp-status-gauge__bar--over" : ""
                                  }`}
                                  style={{ width: `${Math.min(100, demandPercent)}%` }}
                                />
                              </div>
                            </div>
                          );
                        })()}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {/* 下半分: 公共施設一覧テーブル */}
      <div className="pf-window__bottom">
        <h2 className="pf-window__title">{t("publicFacilities.title")}</h2>

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
                {rows.map((agent) => {
                  const variantName =
                    masterData?.agents[agent.variant]?.display_name ?? "";
                  return (
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
                          {col.key === "variant"
                            ? col.render(agent, variantName)
                            : col.render(agent)}
                        </td>
                      ))}
                    </tr>
                  );
                })}
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
