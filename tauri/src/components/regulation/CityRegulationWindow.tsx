/**
 * Regulations window component
 *
 * Manages citywide regulations and policies across six tabs: working hours, speed limits, building regulations,
 * repair settings, immigration regulations, and social security.
 * This component handles data retrieval, draft values, saving, and tab switching; components under `tabs/`
 * render each tab.
 *
 * A single shared Save button submits changes from all tabs together. Mark tabs with unsaved edits so changes in
 * hidden tabs remain noticeable.
 *
 * Two save destinations:
 * - PATCH /api/v1/city: speed limits (max_speed) and repair thresholds (repair_threshold*).
 * - PATCH /api/v1/city/regulations: working hours, building regulations, immigration regulations, and social
 *   security policies.
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import type { PathMasterEntry } from "../../types";
import type {
  BuildingRegKey,
  CityData,
  ImmigrationRegulations,
  PathVariantInfo,
  RegulationsData,
  SpeedMap,
  WelfareRegulations,
} from "./regulationTypes";
import { FALLBACK_SPEED_LIMIT, SpeedLimitTab } from "./tabs/SpeedLimitTab";
import { BUILDING_REG_KEYS, BuildingRegulationTab } from "./tabs/BuildingRegulationTab";
import { RepairTab, type RepairTarget } from "./tabs/RepairTab";
import { IMMIGRATION_SCALAR_KEYS, ImmigrationTab } from "./tabs/ImmigrationTab";
import { WELFARE_KEYS, WelfareTab, type WelfareKey } from "./tabs/WelfareTab";
import { WorkHoursTab } from "./tabs/WorkHoursTab";
import "./CityRegulationWindow.css";

/** Tab order. Use section headings directly as labels. */
const TABS = [
  { id: "labor", labelKey: "wellbeing.workTitle" },
  { id: "speed", labelKey: "regulation.speedLimitTitle" },
  { id: "building", labelKey: "regulation.buildingRegulationTitle" },
  { id: "repair", labelKey: "regulation.repairTitle" },
  { id: "immigration", labelKey: "regulation.immigrationTitle" },
  { id: "welfare", labelKey: "regulation.welfareTitle" },
] as const;

type TabId = (typeof TABS)[number]["id"];

export function CityRegulationWindow({
  apiBaseUrl,
  onClose,
}: {
  apiBaseUrl: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [activeTab, setActiveTab] = useState<TabId>("speed");

  /** Saved and draft speed limits (keys are path_variant ID strings, values are km/h). */
  const [cityMaxSpeed, setCityMaxSpeed] = useState<SpeedMap>({});
  const [pendingSpeeds, setPendingSpeeds] = useState<SpeedMap>({});

  /**
   * Saved and draft repair thresholds (null for unedited targets).
   * building = public buildings; path = roads.
   * The API uses 0–1 ratios, while the UI stores 0–100 percentages.
   */
  const [cityRepair, setCityRepair] = useState<Record<RepairTarget, number>>({
    building: 30,
    path: 30,
  });
  const [pendingRepair, setPendingRepair] = useState<Record<RepairTarget, number | null>>({
    building: null,
    path: null,
  });

  /** Saved and draft building regulations (drafts use API units; absent keys use saved values). */
  const [cityRegulations, setCityRegulations] = useState<RegulationsData | null>(null);
  const [pendingRegulations, setPendingRegulations] = useState<Partial<RegulationsData>>({});

  /**
   * Saved and draft immigration regulations.
   * Manage separately from building regulations because fields include bool/dict types.
   */
  const [cityImmigration, setCityImmigration] = useState<ImmigrationRegulations | null>(null);
  const [pendingImmigration, setPendingImmigration] = useState<Partial<ImmigrationRegulations>>({});

  /** Saved and draft social security policies (API units; benefit rates are 0–1 ratios). */
  const [cityWelfare, setCityWelfare] = useState<WelfareRegulations | null>(null);
  const [pendingWelfare, setPendingWelfare] = useState<Partial<WelfareRegulations>>({});

  const [pendingWorkHours, setPendingWorkHours] = useState<number | null>(null);

  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  /**
   * All path variants from master data, in ID order.
   * Do not filter here because the save payload includes every variant, including excluded ones.
   */
  const pathVariants = useMemo<PathVariantInfo[]>(() => {
    if (!masterData?.paths) return [];
    return Object.entries(masterData.paths)
      .map(([id, entry]) => ({
        id: Number(id),
        display_name: (entry as PathMasterEntry).display_name,
        type: (entry as PathMasterEntry).type,
        default_speed_limit: (entry as PathMasterEntry).default_speed_limit ?? FALLBACK_SPEED_LIMIT,
      }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.paths]);

  /* ==== Initial data loading ==== */

  useEffect(() => {
    const controller = new AbortController();

    async function loadCityData() {
      try {
        const [cityRes, regulationsRes] = await Promise.all([
          fetchApi<CityData>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
            signal: controller.signal,
          }),
          fetchApi<RegulationsData>(buildApiUrl(apiBaseUrl, "/api/v1/city/regulations"), {
            signal: controller.signal,
          }),
        ]);

        const maxSpeed: SpeedMap = {};
        for (const [k, v] of Object.entries(cityRes.city.max_speed)) {
          maxSpeed[k] = Number(v);
        }
        setCityMaxSpeed(maxSpeed);
        setPendingSpeeds(maxSpeed);

        // Convert repair thresholds from API ratios (0–1) to UI percentages (0–100).
        setCityRepair((prev) => ({
          building: cityRes.city.repair_threshold != null ? cityRes.city.repair_threshold * 100 : prev.building,
          path: cityRes.city.repair_threshold_path != null ? cityRes.city.repair_threshold_path * 100 : prev.path,
        }));
        setPendingRepair({ building: null, path: null });

        setCityRegulations(regulationsRes);
        setPendingRegulations({});

        // Keep immigration regulation fields in separate state.
        // The response is one RegulationsData object, but drafts are separate because they include bool/dict fields.
        setCityImmigration({
          immigration_age_min: regulationsRes.immigration_age_min,
          immigration_age_max: regulationsRes.immigration_age_max,
          immigration_enabled: regulationsRes.immigration_enabled,
          immigration_allow_single: regulationsRes.immigration_allow_single,
          immigration_allow_married_no_children: regulationsRes.immigration_allow_married_no_children,
          immigration_allow_with_children: regulationsRes.immigration_allow_with_children,
          immigration_gender_policy: regulationsRes.immigration_gender_policy,
          immigration_skill_thresholds: { ...regulationsRes.immigration_skill_thresholds },
          immigration_quota: regulationsRes.immigration_quota,
        });
        setPendingImmigration({});

        setCityWelfare({
          retirement_age: regulationsRes.retirement_age,
          unemployment_benefit_ratio: regulationsRes.unemployment_benefit_ratio,
          pension_benefit_ratio: regulationsRes.pension_benefit_ratio,
          livelihood_assistance_ratio: regulationsRes.livelihood_assistance_ratio,
        });
        setPendingWelfare({});
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      }
    }

    loadCityData();
    return () => controller.abort();
  }, [apiBaseUrl]);

  /* ==== Change handlers ==== */

  const handleSpeedChange = useCallback((variantId: number, rawValue: string) => {
    const parsed = parseFloat(rawValue);
    if (isNaN(parsed) || parsed < 0) return;
    setPendingSpeeds((prev) => ({ ...prev, [String(variantId)]: parsed }));
  }, []);

  const handleRegulationChange = useCallback((key: BuildingRegKey, apiValue: number) => {
    setPendingRegulations((prev) => ({ ...prev, [key]: apiValue }));
  }, []);

  const handleRepairChange = useCallback((target: RepairTarget, value: number) => {
    setPendingRepair((prev) => ({ ...prev, [target]: value }));
  }, []);

  const handleImmNumberChange = useCallback(
    (
      key: "immigration_age_min" | "immigration_age_max" | "immigration_gender_policy" | "immigration_quota",
      value: number,
    ) => {
      setPendingImmigration((prev) => ({ ...prev, [key]: value }));
    },
    [],
  );

  const handleImmBoolChange = useCallback(
    (
      key: "immigration_enabled" | "immigration_allow_single" | "immigration_allow_married_no_children" | "immigration_allow_with_children",
      value: boolean,
    ) => {
      setPendingImmigration((prev) => ({ ...prev, [key]: value }));
    },
    [],
  );

  /**
   * Handle skill requirement slider changes.
   * Copy and replace the existing dictionary because skill_thresholds is updated partially.
   */
  const handleSkillThresholdChange = useCallback(
    (skillId: number, value: number) => {
      setPendingImmigration((prev) => {
        const current =
          prev.immigration_skill_thresholds ?? cityImmigration?.immigration_skill_thresholds ?? {};
        return {
          ...prev,
          immigration_skill_thresholds: { ...current, [String(skillId)]: value },
        };
      });
    },
    [cityImmigration],
  );

  const handleWelfareChange = useCallback((key: WelfareKey, apiValue: number) => {
    setPendingWelfare((prev) => ({ ...prev, [key]: apiValue }));
  }, []);

  /* ==== Unsaved change detection ==== */

  /**
   * Whether each tab has unsaved changes.
   * With a single Save button, tab markers make edits in hidden tabs noticeable.
   */
  const dirtyTabs = useMemo<Record<TabId, boolean>>(() => {
    const speed = Object.entries(pendingSpeeds).some(([key, value]) => {
      if (key in cityMaxSpeed) return value !== cityMaxSpeed[key];
      // For variants without saved values, use master data's default speed as the current value.
      const variant = pathVariants.find((v) => String(v.id) === key);
      return value !== (variant?.default_speed_limit ?? FALLBACK_SPEED_LIMIT);
    });

    const building =
      cityRegulations != null &&
      BUILDING_REG_KEYS.some((key) => (pendingRegulations[key] ?? cityRegulations[key]) !== cityRegulations[key]);

    const repair = (["building", "path"] as RepairTarget[]).some(
      (target) => pendingRepair[target] != null && pendingRepair[target] !== cityRepair[target],
    );

    let immigration = false;
    if (cityImmigration) {
      immigration = IMMIGRATION_SCALAR_KEYS.some(
        (key) => (pendingImmigration[key] ?? cityImmigration[key]) !== cityImmigration[key],
      );
      // Skill requirements are dictionaries; compare values over the union of keys.
      const pendingSkills = pendingImmigration.immigration_skill_thresholds;
      if (!immigration && pendingSkills) {
        const citySkills = cityImmigration.immigration_skill_thresholds;
        const allKeys = new Set([...Object.keys(pendingSkills), ...Object.keys(citySkills)]);
        immigration = Array.from(allKeys).some((k) => (pendingSkills[k] ?? 0) !== (citySkills[k] ?? 0));
      }
    }

    const welfare =
      cityWelfare != null &&
      WELFARE_KEYS.some((key) => (pendingWelfare[key] ?? cityWelfare[key]) !== cityWelfare[key]);

    const labor = pendingWorkHours != null && pendingWorkHours !== cityRegulations?.work_hours;
    return { labor, speed, building, repair, immigration, welfare };
  }, [
    pendingWorkHours,
    pendingSpeeds,
    cityMaxSpeed,
    pathVariants,
    pendingRegulations,
    cityRegulations,
    pendingRepair,
    cityRepair,
    pendingImmigration,
    cityImmigration,
    pendingWelfare,
    cityWelfare,
  ]);

  const hasUnsavedChanges = TABS.some((tab) => dirtyTabs[tab.id]);

  /* ==== Save ==== */

  const handleSave = useCallback(async () => {
    setIsSaving(true);
    setError(null);

    try {
      // Send speed limits for every variant, rather than a diff.
      const speedPayload: Record<string, number> = {};
      for (const variant of pathVariants) {
        const key = String(variant.id);
        speedPayload[key] = pendingSpeeds[key] ?? variant.default_speed_limit;
      }

      // Payload for PATCH /api/v1/city (speed limits and repair thresholds)
      // Convert UI repair percentages (0–100) back to API ratios (0–1).
      const cityPatchPayload: Record<string, unknown> = { max_speed: speedPayload };
      if (pendingRepair.building != null) {
        cityPatchPayload.repair_threshold = pendingRepair.building / 100;
      }
      if (pendingRepair.path != null) {
        cityPatchPayload.repair_threshold_path = pendingRepair.path / 100;
      }

      // Payload for building and immigration regulations.
      // Immigration regulations include bool/dict fields, so store the JSON payload as
      // Record<string, unknown> rather than Partial<RegulationsData>.
      const regulationPayload: Record<string, unknown> = {};
      if (pendingWorkHours != null) regulationPayload.work_hours = pendingWorkHours;
      if (cityRegulations) {
        for (const key of BUILDING_REG_KEYS) {
          regulationPayload[key] = pendingRegulations[key] ?? cityRegulations[key];
        }
      }
      if (cityImmigration) {
        for (const key of IMMIGRATION_SCALAR_KEYS) {
          regulationPayload[key] = pendingImmigration[key] ?? cityImmigration[key];
        }
        regulationPayload.immigration_skill_thresholds =
          pendingImmigration.immigration_skill_thresholds ?? cityImmigration.immigration_skill_thresholds;
      }
      if (cityWelfare) {
        for (const key of WELFARE_KEYS) {
          regulationPayload[key] = pendingWelfare[key] ?? cityWelfare[key];
        }
      }

      await Promise.all([
        fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(cityPatchPayload),
        }),
        fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city/regulations"), {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(regulationPayload),
        }),
      ]);

      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save");
    } finally {
      setIsSaving(false);
    }
  }, [
    apiBaseUrl,
    pendingWorkHours,
    pathVariants,
    pendingSpeeds,
    pendingRepair,
    cityRegulations,
    pendingRegulations,
    cityImmigration,
    pendingImmigration,
    cityWelfare,
    pendingWelfare,
    onClose,
  ]);

  /* ==== Rendering ==== */

  if (error && Object.keys(cityMaxSpeed).length === 0 && !cityRegulations) {
    return <div className="regulation-window__error">{error}</div>;
  }

  if (pathVariants.length === 0 || !cityRegulations || !cityImmigration || !cityWelfare) {
    return <div className="regulation-window__loading">{t("common.loading")}</div>;
  }

  return (
    <div className="regulation-window">
      <nav className="regulation-window__tabs">
        {TABS.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={[
              "regulation-tab",
              tab.id === activeTab && "regulation-tab--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setActiveTab(tab.id)}
          >
            <span className="regulation-tab__label">{t(tab.labelKey)}</span>
            {dirtyTabs[tab.id] && (
              <span className="regulation-tab__dot" title={t("regulation.unsavedChanges")} />
            )}
          </button>
        ))}
      </nav>

      <div className="regulation-window__content">
        {activeTab === "labor" && <WorkHoursTab value={pendingWorkHours ?? cityRegulations.work_hours} onChange={setPendingWorkHours} />}
        {activeTab === "speed" && (
          <SpeedLimitTab
            pathVariants={pathVariants}
            cityMaxSpeed={cityMaxSpeed}
            pendingSpeeds={pendingSpeeds}
            onChange={handleSpeedChange}
          />
        )}
        {activeTab === "building" && (
          <BuildingRegulationTab
            cityRegulations={cityRegulations}
            pendingRegulations={pendingRegulations}
            onChange={handleRegulationChange}
          />
        )}
        {activeTab === "repair" && (
          <RepairTab
            cityThresholds={cityRepair}
            pendingThresholds={pendingRepair}
            onChange={handleRepairChange}
          />
        )}
        {activeTab === "immigration" && (
          <ImmigrationTab
            cityImmigration={cityImmigration}
            pendingImmigration={pendingImmigration}
            onNumberChange={handleImmNumberChange}
            onBoolChange={handleImmBoolChange}
            onSkillThresholdChange={handleSkillThresholdChange}
          />
        )}
        {activeTab === "welfare" && (
          <WelfareTab
            cityWelfare={cityWelfare}
            pendingWelfare={pendingWelfare}
            onChange={handleWelfareChange}
          />
        )}
      </div>

      {error && <div className="regulation-window__error-inline">{error}</div>}

      <div className="regulation-window__footer">
        {hasUnsavedChanges && (
          <span className="regulation-window__unsaved">{t("regulation.unsavedChanges")}</span>
        )}
        <button
          className="regulation-window__save-btn"
          onClick={handleSave}
          disabled={isSaving}
          type="button"
        >
          {isSaving ? t("common.saving") : t("common.save")}
        </button>
      </div>
    </div>
  );
}
