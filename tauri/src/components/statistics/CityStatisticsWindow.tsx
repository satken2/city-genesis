/**
 * Statistics window component.
 *
 * A two-pane layout with vertical tabs (icons and titles) on the left and tab content on the right.
 * Tab contents are separate components; this component only manages tab switching and layout.
 *
 * - Left pane: vertical tab list, with an icon and label for each tab.
 * - Right pane: the content component for the selected tab.
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { WellbeingTab } from "./tabs/WellbeingTab";
import { PopulationTab } from "./tabs/PopulationTab";
import { IncomeTab } from "./tabs/IncomeTab";
import { EconomyTab } from "./tabs/EconomyTab";
import { FinanceTab } from "./tabs/FinanceTab";
import { HealthTab } from "./tabs/HealthTab";
import { EducationTab } from "./tabs/EducationTab";
import { EmploymentTab } from "./tabs/EmploymentTab";
import { TradeTab } from "./tabs/TradeTab";
import { AccidentsTab } from "./tabs/AccidentsTab";
import { BusinessTab } from "./tabs/BusinessTab";
import { CommuteTab } from "./tabs/CommuteTab";
import { RealEstateTab } from "./tabs/RealEstateTab";
import { TourismTab } from "./tabs/TourismTab";
import { ZoneListTab } from "./tabs/ZoneListTab";
import { DebugAgentSpawnTab } from "./tabs/DebugAgentSpawnTab";
import { DebugBuildingSpawnTab } from "./tabs/DebugBuildingSpawnTab";
import "./CityStatisticsWindow.css";

type TabDef = {
  id: string;
  icon: string;
  labelKey: string;
};

const TABS: TabDef[] = [
  { id: "population", icon: "population", labelKey: "statistics.tabPopulation" },
  { id: "satisfaction", icon: "satisfaction", labelKey: "wellbeing.satisfactionTitle" },
  { id: "leisure", icon: "leisure", labelKey: "wellbeing.leisureTitle" },
  { id: "comfort", icon: "comfort", labelKey: "wellbeing.comfortTitle" },
  { id: "health", icon: "health", labelKey: "statistics.tabHealth" },
  { id: "income", icon: "income", labelKey: "statistics.tabIncome" },
  { id: "economy", icon: "economy", labelKey: "statistics.tabEconomy" },
  { id: "finance", icon: "income", labelKey: "statistics.tabFinance" },
  { id: "education", icon: "education", labelKey: "statistics.tabEducation" },
  { id: "employment", icon: "employment", labelKey: "statistics.tabEmployment" },
  { id: "trade", icon: "trade", labelKey: "statistics.tabTrade" },
  { id: "accidents", icon: "accidents", labelKey: "statistics.tabAccidents" },
  { id: "business", icon: "business", labelKey: "statistics.tabBusiness" },
  { id: "commute", icon: "commute", labelKey: "statistics.tabCommute" },
  { id: "realestate", icon: "realestate", labelKey: "statistics.tabRealEstate" },
  { id: "tourism", icon: "tourism", labelKey: "statistics.tabTourism" },
  { id: "zones", icon: "zones", labelKey: "statistics.tabZones" },
];

/**
 * Developer-only debug tab definitions (Agent spawning / building spawning).
 * Added to the end of the tab list only when debug_execution returned by the backend's
 * GET /api/v1/sessions is true (development runs only).
 */
const DEBUG_TABS: TabDef[] = [
  {
    id: "debugAgentSpawn",
    icon: "debugAgentSpawn",
    labelKey: "statistics.tabDebugAgentSpawn",
  },
  {
    id: "debugBuildingSpawn",
    icon: "debugBuildingSpawn",
    labelKey: "statistics.tabDebugBuildingSpawn",
  },
];

/** Fields of the GET /api/v1/sessions response used by this component. */
type SessionsResponse = {
  debug_execution?: boolean;
};

/**
 * Resolves statistics tab icons.
 *
 * Images are included at build time through Vite's import.meta.glob.
 * Specifying `src/assets/...` directly as a string literal excludes the asset from
 * production bundles and results in a 404, so always resolve it through the glob.
 */
const statisticsTabIconModules = import.meta.glob<{ default: string }>(
  "../../assets/images/statistics/*.png",
  { eager: true }
);

function tabIconUrl(icon: string): string {
  return (
    statisticsTabIconModules[`../../assets/images/statistics/tab_${icon}.png`]
      ?.default ?? ""
  );
}

function TabIcon({ icon, className }: { icon: string; className?: string }) {
  const url = tabIconUrl(icon);
  if (url) {
    return <img src={url} alt="" className={className} draggable={false} />;
  }

  if (icon === "satisfaction") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="9" />
        <path d="M8 9h.01M16 9h.01M7.5 14a5 5 0 0 0 9 0" />
      </svg>
    );
  }
  if (icon === "leisure") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M3 11a9 8 0 0 1 18 0H3ZM8 11c0-4 2-8 4-8s4 4 4 8M12 11v10M5 21h14" />
      </svg>
    );
  }
  if (icon === "comfort") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M3 11l9-8 9 8M5 10v11h14V10M9 21v-7h6v7" />
      </svg>
    );
  }
  if (icon === "population") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
        <circle cx="9" cy="7" r="4" />
        <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
        <path d="M16 3.13a4 4 0 0 1 0 7.75" />
      </svg>
    );
  }
  if (icon === "income") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <line x1="12" y1="1" x2="12" y2="23" />
        <path d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6" />
      </svg>
    );
  }
  if (icon === "economy") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <line x1="18" y1="20" x2="18" y2="10" />
        <line x1="12" y1="20" x2="12" y2="4" />
        <line x1="6" y1="20" x2="6" y2="14" />
      </svg>
    );
  }
  if (icon === "health") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M9 3h6v6h6v6h-6v6H9v-6H3V9h6V3Z" />
      </svg>
    );
  }
  if (icon === "education") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20" />
        <path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z" />
      </svg>
    );
  }
  if (icon === "trade") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M21 9H8" />
        <path d="M21 9L17 5" />
        <path d="M21 9L17 13" />
        <path d="M3 15H16" />
        <path d="M3 15L7 11" />
        <path d="M3 15L7 19" />
      </svg>
    );
  }
  if (icon === "employment") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <rect x="2" y="7" width="20" height="14" rx="2" ry="2" />
        <path d="M16 7V5a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v2" />
        <line x1="12" y1="12" x2="12" y2="16" />
        <line x1="10" y1="14" x2="14" y2="14" />
      </svg>
    );
  }
  if (icon === "accidents") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
        <line x1="12" y1="9" x2="12" y2="13" />
        <line x1="12" y1="17" x2="12.01" y2="17" />
      </svg>
    );
  }
  if (icon === "business") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M3 9l1.5-5h15L21 9" />
        <path d="M4 9v11a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1V9" />
        <path d="M3 9a3 3 0 0 0 6 0 3 3 0 0 0 6 0 3 3 0 0 0 6 0" />
        <path d="M9 21v-6h6v6" />
      </svg>
    );
  }
  if (icon === "commute") {
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M4 10l2-6h12l2 6M4 17v3M20 17v3" />
        <rect x="3" y="10" width="18" height="7" rx="2" />
        <path d="M7 13.5h1M16 13.5h1" />
      </svg>
    );
  }
  if (icon === "realestate") {
    // Building icon representing buildings and real estate.
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M3 21h18" />
        <path d="M19 21V5a2 2 0 0 0-2-2H7a2 2 0 0 0-2 2v16" />
        <path d="M9 9h2" />
        <path d="M9 13h2" />
        <path d="M13 9h2" />
        <path d="M13 13h2" />
      </svg>
    );
  }
  if (icon === "tourism") {
    // Tourism tab: suitcase icon.
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="7" width="18" height="13" rx="2" />
        <path d="M8 7V4h8v3" />
        <path d="M8 7v13" />
        <path d="M16 7v13" />
      </svg>
    );
  }
  if (icon === "zones") {
    // District list tab: stacked wide cards.
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="3" width="18" height="5" rx="1" />
        <rect x="3" y="10" width="18" height="5" rx="1" />
        <rect x="3" y="17" width="18" height="4" rx="1" />
      </svg>
    );
  }
  if (icon === "debugAgentSpawn") {
    // Developer-only tab (Agent spawning): bug icon representing debugging.
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <rect x="8" y="6" width="8" height="14" rx="4" />
        <path d="M19 7l-3 2" />
        <path d="M5 7l3 2" />
        <path d="M19 19l-3-2" />
        <path d="M5 19l3-2" />
        <path d="M12 20v-8" />
        <path d="M10 4l2 2 2-2" />
      </svg>
    );
  }
  if (icon === "debugBuildingSpawn") {
    // Developer-only tab (building spawning): crane icon representing construction.
    return (
      <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
        <path d="M4 21h16" />
        <path d="M7 21V5l12 4" />
        <path d="M7 8l12 1" />
        <path d="M19 9v4" />
        <path d="M17 13h4" />
        <path d="M13 21v-4h4v4" />
      </svg>
    );
  }
  return null;
}

export function CityStatisticsWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const [activeTab, setActiveTab] = useState(TABS[0].id);

  // Whether developer-only debug tabs are visible.
  // Check the backend's debug_execution (true only in development runs) once on mount.
  // If fetching fails, leave the tabs hidden (the normal display for players).
  const [debugEnabled, setDebugEnabled] = useState(false);
  useEffect(() => {
    let cancelled = false;
    fetchApi<SessionsResponse>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"))
      .then((res) => {
        if (!cancelled && res.debug_execution === true) {
          setDebugEnabled(true);
        }
      })
      .catch(() => {
        // Do nothing on failure (hiding debug tabs does not affect normal functionality).
      });
    return () => {
      cancelled = true;
    };
  }, [apiBaseUrl]);

  const tabs = debugEnabled ? [...TABS, ...DEBUG_TABS] : TABS;

  return (
    <div className="stats-window">
      <nav className="stats-window__tabs">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            className={[
              "stats-tab",
              tab.id === activeTab && "stats-tab--active",
            ]
              .filter(Boolean)
              .join(" ")}
            type="button"
            onClick={() => setActiveTab(tab.id)}
          >
            <TabIcon icon={tab.icon} className="stats-tab__icon" />
            <span className="stats-tab__label">{t(tab.labelKey)}</span>
          </button>
        ))}
      </nav>

      <div className="stats-window__content">
        {["satisfaction", "leisure", "comfort"].includes(activeTab) && <WellbeingTab apiBaseUrl={apiBaseUrl} view={activeTab as "satisfaction" | "leisure" | "comfort"} />}
        {activeTab === "population" && <PopulationTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "income" && <IncomeTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "economy" && <EconomyTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "finance" && <FinanceTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "health" && <HealthTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "education" && <EducationTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "employment" && <EmploymentTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "trade" && <TradeTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "accidents" && <AccidentsTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "business" && <BusinessTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "commute" && <CommuteTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "realestate" && <RealEstateTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "tourism" && <TourismTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "zones" && <ZoneListTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "debugAgentSpawn" && debugEnabled && <DebugAgentSpawnTab apiBaseUrl={apiBaseUrl} />}
        {activeTab === "debugBuildingSpawn" && debugEnabled && <DebugBuildingSpawnTab apiBaseUrl={apiBaseUrl} />}
      </div>
    </div>
  );
}
