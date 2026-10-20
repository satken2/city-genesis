/**
 * Economy tab component.
 *
 * Content of the statistics window's "Economy" tab.
 * Fetches current city data from GET /api/v1/city and displays average prices, production,
 * consumption, exports, and imports for each unregulated good defined in the product master.
 * Splits the table into retail products and industrial materials; omits goods in neither category
 * (services, infrastructure, transport, etc.). Bars in quantity cells scale with monetary value
 * (quantity × average price), relative to the active tab's maximum amount and colored by column.
 * Displayed values remain quantities. Clicking a row opens that good's economic trends over the tab.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../../utils/api";
import { useMasterData } from "../../../contexts/MasterDataContext";
import { GOODS_MATERIAL, GOODS_RETAIL } from "../../../constants";
import { FlatTabs } from "../../common/FlatTabs";
import { EconomyTrendOverlay } from "./EconomyTrendOverlay";
import "./EconomyTab.css";

type CityResponse = {
  city: {
    average_prices?: Record<string, number>;
    total_production?: Record<string, number>;
    total_consumption?: Record<string, number>;
    total_exports?: Record<string, any>;
    total_imports?: Record<string, any>;
  };
};

type GoodsTab = "retail" | "material";

const GOODS_TABS: { id: GoodsTab; goodsType: number; labelKey: string }[] = [
  { id: "retail", goodsType: GOODS_RETAIL, labelKey: "statistics.economyRetail" },
  { id: "material", goodsType: GOODS_MATERIAL, labelKey: "statistics.economyMaterial" },
];

export function EconomyTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [cityData, setCityData] = useState<CityResponse["city"] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<GoodsTab>("retail");
  const [trendProduct, setTrendProduct] = useState<{ id: number; name: string } | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      try {
        const res = await fetchApi<CityResponse>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/"),
          { signal: controller.signal }
        );
        setCityData(res.city);
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      } finally {
        setLoading(false);
      }
    }

    load();
    return () => controller.abort();
  }, [apiBaseUrl]);

  if (loading) {
    return <div className="econ-tab__loading">{t("common.loading")}</div>;
  }

  if (error || !cityData || !masterData) {
    return <div className="econ-tab__error">{t("common.fetchError")}: {error || "No data"}</div>;
  }

  // Generic value accessor.
  const getAmount = (obj: any, id: number): number => {
    if (!obj) return 0;
    const val = obj[id] !== undefined ? obj[id] : obj[String(id)];
    if (val === undefined || val === null) return 0;
    if (typeof val === "object" && "amount" in val) {
      return Number(val.amount);
    }
    return Number(val);
  };

  const getPrice = (obj: any, id: number): number => {
    if (!obj) return 0;
    const val = obj[id] !== undefined ? obj[id] : obj[String(id)];
    return val !== undefined && val !== null ? Number(val) : 0;
  };

  // Exclude regulated products with controlled prices from economic scale comparisons.
  const goodsType = GOODS_TABS.find((tab) => tab.id === activeTab)!.goodsType;
  const products = Object.entries(masterData.products)
    .filter(([, product]) => product.type === goodsType && !product.regulated)
    .map(([id, product]) => ({
      id: Number(id),
      displayName: product.display_name,
      unit: product.unit,
      type: product.type,
    }))
    .sort((a, b) => a.id - b.id);

  // Build cell data and calculate the maximum within the tab (exclude rows with zero production, consumption, exports, and imports).
  let maxValue = 0;
  const tableRows = products
    .map((p) => {
      const price = getPrice(cityData.average_prices, p.id);
      const production = getAmount(cityData.total_production, p.id);
      const consumption = getAmount(cityData.total_consumption, p.id);
      const exports = getAmount(cityData.total_exports, p.id);
      const imports = getAmount(cityData.total_imports, p.id);

      return {
        ...p,
        price,
        production,
        consumption,
        exports,
        imports,
      };
    })
    .filter(
      (row) =>
        !(
          row.production === 0 &&
          row.consumption === 0 &&
          row.exports === 0 &&
          row.imports === 0
        )
    );

  tableRows.forEach((row) => {
    maxValue = Math.max(
      maxValue,
      row.production * row.price,
      row.consumption * row.price,
      row.exports * row.price,
      row.imports * row.price,
    );
  });

  return (
    <div className="econ-tab">
      <h2 className="econ-tab__title">{t("statistics.economyTitle")}</h2>
      <div className="econ-tab__body">
        <FlatTabs<GoodsTab>
          tabs={GOODS_TABS.map((tab) => ({ id: tab.id, label: t(tab.labelKey) }))}
          active={activeTab}
          onChange={setActiveTab}
        />
        <div className="econ-tab__table-container">
          <table className="econ-table">
            <thead>
              <tr>
                <th className="econ-table__header">{t("statistics.colProduct")}</th>
                <th className="econ-table__header econ-table__header--right">{t("statistics.colPrice")}</th>
                <th className="econ-table__header">{t("statistics.colProduction")}</th>
                <th className="econ-table__header">{t("statistics.colConsumption")}</th>
                <th className="econ-table__header">{t("statistics.colExport")}</th>
                <th className="econ-table__header">{t("statistics.colImport")}</th>
              </tr>
            </thead>
            <tbody>
              {tableRows.map((row) => {
                const prodWidth = maxValue > 0 ? (row.production * row.price / maxValue) * 100 : 0;
                const consWidth = maxValue > 0 ? (row.consumption * row.price / maxValue) * 100 : 0;
                const expWidth = maxValue > 0 ? (row.exports * row.price / maxValue) * 100 : 0;
                const impWidth = maxValue > 0 ? (row.imports * row.price / maxValue) * 100 : 0;
  
                return (
                  <tr
                    key={row.id}
                    className="econ-table__row"
                    onClick={() => setTrendProduct({ id: row.id, name: row.displayName })}
                  >
                    <td className="econ-table__cell econ-table__cell--product">
                      {row.displayName} <span className="econ-table__id">({row.id})</span>
                    </td>
                    <td className="econ-table__cell econ-table__cell--price econ-table__cell--right">
                      {row.price > 0
                        ? t("common.currency", { value: row.price.toFixed(1) })
                        : "-"}
                    </td>
                    <td className="econ-table__cell econ-table__cell--bar econ-table__cell--production">
                      <div
                        className="econ-table__bar econ-table__bar--production"
                        style={{ width: `${prodWidth}%` }}
                      />
                      <span className="econ-table__value">
                        {row.production > 0
                          ? `${row.production.toLocaleString(undefined, {
                              minimumFractionDigits: 1,
                              maximumFractionDigits: 1,
                            })} ${row.unit}`
                          : "-"}
                      </span>
                    </td>
                    <td className="econ-table__cell econ-table__cell--bar econ-table__cell--consumption">
                      <div
                        className="econ-table__bar econ-table__bar--consumption"
                        style={{ width: `${consWidth}%` }}
                      />
                      <span className="econ-table__value">
                        {row.consumption > 0
                          ? `${row.consumption.toLocaleString(undefined, {
                              minimumFractionDigits: 1,
                              maximumFractionDigits: 1,
                            })} ${row.unit}`
                          : "-"}
                      </span>
                    </td>
                    <td className="econ-table__cell econ-table__cell--bar econ-table__cell--export">
                      <div
                        className="econ-table__bar econ-table__bar--export"
                        style={{ width: `${expWidth}%` }}
                      />
                      <span className="econ-table__value">
                        {row.exports > 0
                          ? `${row.exports.toLocaleString(undefined, {
                              minimumFractionDigits: 1,
                              maximumFractionDigits: 1,
                            })} ${row.unit}`
                          : "-"}
                      </span>
                    </td>
                    <td className="econ-table__cell econ-table__cell--bar econ-table__cell--import">
                      <div
                        className="econ-table__bar econ-table__bar--import"
                        style={{ width: `${impWidth}%` }}
                      />
                      <span className="econ-table__value">
                        {row.imports > 0
                          ? `${row.imports.toLocaleString(undefined, {
                              minimumFractionDigits: 1,
                              maximumFractionDigits: 1,
                            })} ${row.unit}`
                          : "-"}
                      </span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </div>
      {trendProduct && (
        <EconomyTrendOverlay
          apiBaseUrl={apiBaseUrl}
          productId={trendProduct.id}
          productName={trendProduct.name}
          onClose={() => setTrendProduct(null)}
        />
      )}
    </div>
  );
}
