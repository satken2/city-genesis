/**
 * Business opening and closure history tab component.
 *
 * Content of the statistics window's "Opening and closure history" tab.
 * Fetches ordinary facility opening/closure records from GET /api/v1/stats/business_history,
 * and lists them in the inner "Openings" and "Closures" subtabs.
 *
 * The backend sorts records from newest to oldest. Rows are not clickable because closed
 * facilities may already have been deleted, leaving no destination for a details view.
 */

import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useEpochCachedStats } from "../useEpochCache";
import { useMasterData } from "../../../contexts/MasterDataContext";
import { formatEpochDate } from "../../../utils/date";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatCompactAmount } from "../../../utils/money";
import "./BusinessHistoryTab.css";

// --- Backend GET /api/v1/stats/business_history response types ---

type OpenItem = {
  date: number;
  agent_id: number;
  variant: number;
  name: string;
  /** Floor area secured when opening (m²). */
  floor_area: number;
};

type CloseItem = {
  date: number;
  agent_id: number;
  variant: number;
  name: string;
  /** Number of days in operation. */
  duration_days: number;
  /** Cash at closure. */
  reserves: number;
  /** Closure reason code (deficit_paused / deficit_closed / demolition_closed). */
  reason: string;
};

type BusinessHistory = {
  opens: OpenItem[];
  closes: CloseItem[];
};

const COLOR_OPEN = "#10b981"; // Openings: green.
const COLOR_CLOSE = "#ef4444"; // Closures: red.

export function BusinessHistoryTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const { data, loading, error } = useEpochCachedStats<BusinessHistory>(
    apiBaseUrl,
    "/api/v1/stats/business_history"
  );

  const [activeSubTab, setActiveSubTab] = useState<"open" | "close">("open");

  if (loading) {
    return <div className="business-history-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data) {
    return (
      <div className="business-history-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  const locale = resolveIntlLocale(i18n.language);
  const variantName = (variant: number): string =>
    masterData?.agents[variant]?.display_name ?? `#${variant}`;
  const formatDate = (date: number): string => formatEpochDate(date, locale);

  // Convert closure reason codes to display text. Display unknown codes as they are.
  const reasonLabel = (reason: string): string => {
    const key = `statistics.businessHistory.reason.${reason}`;
    const label = t(key);
    return label === key ? reason : label;
  };

  const { opens, closes } = data;

  return (
    <div className="business-history-tab-container">
      <nav className="business-history-sub-tabs">
        <button
          type="button"
          className={`business-history-sub-tab-btn ${
            activeSubTab === "open" ? "business-history-sub-tab-btn--active" : ""
          }`}
          style={{ borderBottomColor: activeSubTab === "open" ? COLOR_OPEN : "transparent" }}
          onClick={() => setActiveSubTab("open")}
        >
          {t("statistics.businessHistory.subTabOpen")}
          <span className="business-history-sub-tab-btn__count">({opens.length})</span>
        </button>
        <button
          type="button"
          className={`business-history-sub-tab-btn ${
            activeSubTab === "close" ? "business-history-sub-tab-btn--active" : ""
          }`}
          style={{ borderBottomColor: activeSubTab === "close" ? COLOR_CLOSE : "transparent" }}
          onClick={() => setActiveSubTab("close")}
        >
          {t("statistics.businessHistory.subTabClose")}
          <span className="business-history-sub-tab-btn__count">({closes.length})</span>
        </button>
      </nav>

      <div className="business-history-list">
        {activeSubTab === "open" &&
          (opens.length > 0 ? (
            <table className="business-history-table">
              <thead>
                <tr>
                  <th>{t("statistics.businessHistory.columnDate")}</th>
                  <th>{t("statistics.businessHistory.columnVariant")}</th>
                  <th>{t("statistics.businessHistory.columnName")}</th>
                  <th className="business-history-table__th--num">
                    {t("statistics.businessHistory.columnFloorArea")}
                  </th>
                </tr>
              </thead>
              <tbody>
                {opens.map((item, index) => (
                  <tr key={`open-${item.agent_id}-${item.date}-${index}`}>
                    <td className="business-history-table__cell--muted">
                      {formatDate(item.date)}
                    </td>
                    <td className="business-history-table__cell--muted">
                      {variantName(item.variant)}
                    </td>
                    <td className="business-history-table__cell--name">
                      {item.name || `#${item.agent_id}`}
                      <span className="business-history-table__id">#{item.agent_id}</span>
                    </td>
                    <td className="business-history-table__cell--num">
                      {item.floor_area.toFixed(1)}
                      {t("statistics.businessHistory.unitArea")}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <div className="business-history-list__empty">
              {t("statistics.businessHistory.noOpens")}
            </div>
          ))}

        {activeSubTab === "close" &&
          (closes.length > 0 ? (
            <table className="business-history-table">
              <thead>
                <tr>
                  <th>{t("statistics.businessHistory.columnDate")}</th>
                  <th>{t("statistics.businessHistory.columnVariant")}</th>
                  <th>{t("statistics.businessHistory.columnName")}</th>
                  <th className="business-history-table__th--num">
                    {t("statistics.businessHistory.columnDuration")}
                  </th>
                  <th className="business-history-table__th--num">
                    {t("statistics.businessHistory.columnReserves")}
                  </th>
                  <th>{t("statistics.businessHistory.columnReason")}</th>
                </tr>
              </thead>
              <tbody>
                {closes.map((item, index) => (
                  <tr key={`close-${item.agent_id}-${item.date}-${index}`}>
                    <td className="business-history-table__cell--muted">
                      {formatDate(item.date)}
                    </td>
                    <td className="business-history-table__cell--muted">
                      {variantName(item.variant)}
                    </td>
                    <td className="business-history-table__cell--name">
                      {item.name || `#${item.agent_id}`}
                      <span className="business-history-table__id">#{item.agent_id}</span>
                    </td>
                    <td className="business-history-table__cell--num">
                      {t("statistics.businessHistory.unitDays", { days: item.duration_days })}
                    </td>
                    <td
                      className="business-history-table__cell--num"
                      style={{ color: item.reserves < 0 ? COLOR_CLOSE : undefined }}
                    >
                      {formatCompactAmount(item.reserves, locale, true)}
                    </td>
                    <td className="business-history-table__cell--muted">
                      {reasonLabel(item.reason)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <div className="business-history-list__empty">
              {t("statistics.businessHistory.noCloses")}
            </div>
          ))}
      </div>
    </div>
  );
}
