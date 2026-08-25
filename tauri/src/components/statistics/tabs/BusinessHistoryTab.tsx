/**
 * 開店・閉店履歴タブコンポーネント
 *
 * 統計ウィンドウの「開店・閉店履歴」タブの中身。
 * GET /api/v1/stats/business_history から一般施設の開店・閉店の記録を取得し、
 * 内側のサブタブ（「開店」「閉店」）で切り替えて一覧表示する。
 *
 * 記録はバックエンド側で新しい順に並べられている。行はクリックできない
 * （閉店した施設は既に削除されている場合があり、詳細を開く先が無いため）。
 */

import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useEpochCachedStats } from "../useEpochCache";
import { useMasterData } from "../../../contexts/MasterDataContext";
import { formatEpochDate } from "../../../utils/date";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatCompactAmount } from "../../../utils/money";
import "./BusinessHistoryTab.css";

// --- バックエンド GET /api/v1/stats/business_history のレスポンス型 ---

type OpenItem = {
  date: number;
  agent_id: number;
  variant: number;
  name: string;
  /** 開店時に確保した床面積(m²) */
  floor_area: number;
};

type CloseItem = {
  date: number;
  agent_id: number;
  variant: number;
  name: string;
  /** 稼働開始からの持続日数 */
  duration_days: number;
  /** 閉鎖時点の現預金 */
  reserves: number;
  /** 閉鎖理由コード（deficit_paused / deficit_closed） */
  reason: string;
};

type BusinessHistory = {
  opens: OpenItem[];
  closes: CloseItem[];
};

const COLOR_OPEN = "#10b981"; // 開店: グリーン
const COLOR_CLOSE = "#ef4444"; // 閉店: レッド

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

  // 閉鎖理由コードを表示文言へ変換する。未知のコードはコードのまま出す。
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
