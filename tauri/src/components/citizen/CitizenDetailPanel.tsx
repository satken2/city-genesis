/**
 * Citizen detail panel
 *
 * Dedicated panel for the window_type="citizen_detail" popup.
 * Opens when clicking a household member (CitizenLink) in the household details (HouseholdInfoPanel in
 * AgentDetailPanel), clicking a car or pedestrian in the paused 3D view (Unity selects a citizen and opens via
 * world:select_object), or opening from the tracking list.
 *
 * Action buttons below the heading:
 * - Camera: follow this citizen's car or pedestrian in Unity; press again to stop. State follows
 *   `citizen:follow_state`.
 * - Track: add to or remove from the tracking list, which persists in save data.
 *
 * The Basic tab uses fields from /api/v1/citizens/{id} (the single_citizen schema in endpoints.yaml).
 * - Basic information: age, gender, marital status, household.
 * - Employment and education: workplace, school, major skill, higher education completion.
 * - Skills: radar chart of the six skill values.
 * - Public safety: criminal propensity.
 * The Activity tab contains a map of home, destinations, and outbound routes, with the destination list
 * (`CitizenActivityPanel`) below.
 *
 * Household, workplace, and school links open a separate agent detail window instead of navigating within this
 * panel (using open_registered_window, as in SearchWindow).
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import {
  Radar,
  RadarChart,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  ResponsiveContainer,
} from "recharts";
import type { CitizenData, CitizenResponse } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { MEMBER_MARRIED } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { InfoHeader } from "../common/InfoHeader";
import { sendCitizenFollow, sendCitizenUnfollow, sendFocusGraphic } from "../../utils/ipc";
import {
  TRACKED_CITIZENS_CHANGED_EVENT,
  fetchItinerary,
  fetchTrackedCitizens,
  trackCitizen,
  untrackCitizen,
  useFollowedCitizen,
  type CitizenItinerary,
} from "../../utils/citizenTracking";
import { CitizenActivityPanel } from "./CitizenActivityPanel";
import { CameraIcon, TrackIcon } from "./CitizenTrackingIcons";
import citizenIcon from "../../assets/images/citizen.png";
import "../agent/AgentDetailPanel.css";
import "../agent/AgentOperationPanel.css";
import "./CitizenDetailPanel.css";

/** Window type, also used as the sender label for IPC to Unity. */
const WINDOW_TYPE = "citizen_detail";

/**
 * High-skill threshold, matching the backend's HIGH_SKILL_BOUNDARY.
 * A citizen with any skill at or above this value is considered to have completed higher education (highly
 * skilled).
 */
const HIGH_SKILL_BOUNDARY = 1.5;

/**
 * Display maximum for skill values.
 * Backend skills grow up to MAX_BASE_SKILL (1.0) through basic education and HIGH_SKILL_BOUNDARY (1.5) through
 * higher education.
 * Fix the radar chart's radial maximum at 1.5 to keep comparisons on the same scale.
 */
const SKILL_DISPLAY_MAX = HIGH_SKILL_BOUNDARY;

type CitizenDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

/**
 * Clickable agent name link.
 * Opens a new detail window for the agent.
 * Show a placeholder if there is no name or referenced agent (e.g. unemployed or not enrolled).
 */
function AgentWindowLink({
  agentName,
  agentId,
  placeholder,
}: {
  agentName: string | null | undefined;
  agentId: number;
  placeholder: string;
}) {
  if (!agentName) {
    return <span className="agent-detail-muted">{placeholder}</span>;
  }

  function handleClick() {
    void invoke("open_registered_window", {
      windowType: "agent_detail",
      entityId: String(agentId),
    });
  }

  return (
    <button className="entity-link" onClick={handleClick} type="button">
      {agentName}
    </button>
  );
}

export function CitizenDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: CitizenDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [citizenData, setCitizenData] = useState<CitizenData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<"basic" | "activity">("basic");
  const [itinerary, setItinerary] = useState<CitizenItinerary | null>(null);
  const [itineraryFailed, setItineraryFailed] = useState(false);
  const [tracked, setTracked] = useState(false);
  const followed = useFollowedCitizen();
  const citizenId = Number(initialEntityId);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  const loadItinerary = useCallback(async (signal?: AbortSignal) => {
    try {
      const result = await fetchItinerary(apiBaseUrl, citizenId, signal);
      if (signal?.aborted) return;
      setItinerary(result);
      setItineraryFailed(false);
    } catch (err) {
      if (!signal?.aborted) {
        console.error("failed to load citizen itinerary", err);
        setItineraryFailed(true);
      }
    }
  }, [apiBaseUrl, citizenId]);

  const loadTracked = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = await fetchTrackedCitizens(apiBaseUrl, signal);
      if (!signal?.aborted) setTracked(list.some((row) => row.id === citizenId));
    } catch (err) {
      if (!signal?.aborted) console.error("failed to load tracked citizens", err);
    }
  }, [apiBaseUrl, citizenId]);

  useEffect(() => {
    if (!apiBaseUrl || !Number.isFinite(citizenId)) return;
    const controller = new AbortController();
    setItinerary(null);
    void loadItinerary(controller.signal);
    void loadTracked(controller.signal);
    return () => controller.abort();
  }, [apiBaseUrl, citizenId, loadItinerary, loadTracked]);

  // Follow tracking changes in other windows and epoch execution, which changes routes and shopping destinations.
  useEffect(() => {
    const unlistenTracked = listen(TRACKED_CITIZENS_CHANGED_EVENT, () => void loadTracked());
    const unlistenEpoch = listen<{ executing: boolean }>("epoch:executing", (event) => {
      if (!event.payload.executing) void loadItinerary();
    });
    return () => {
      void unlistenTracked.then((fn) => fn());
      void unlistenEpoch.then((fn) => fn());
    };
  }, [loadItinerary, loadTracked]);

  async function toggleFollow() {
    try {
      if (followed === citizenId) {
        await sendCitizenUnfollow(WINDOW_TYPE, citizenId);
      } else {
        await sendCitizenFollow(WINDOW_TYPE, citizenId);
        // Move keyboard focus to the 3D view so Esc can stop following and the wheel can zoom.
        await sendFocusGraphic();
      }
    } catch (err) {
      console.error("failed to toggle citizen follow", err);
    }
  }

  async function toggleTracked() {
    try {
      if (tracked) await untrackCitizen(apiBaseUrl, citizenId);
      else await trackCitizen(apiBaseUrl, citizenId);
      setTracked(!tracked);
      await emit(TRACKED_CITIZENS_CHANGED_EVENT, { citizen_id: citizenId });
    } catch (err) {
      console.error("failed to toggle tracked citizen", err);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchApi<CitizenResponse>(url, { signal: controller.signal })
      .then((response) => {
        if (!controller.signal.aborted) {
          setCitizenData(response.citizen);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          const message = err instanceof Error ? err.message : "Unknown error";
          setError(message);
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [url]);

  if (loading || !masterData) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !citizenData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  // Display strings for gender and marital status. Backend gender is 0 = male / 1 = female;
  // citizen_type is MEMBER_SINGLE (single) / MEMBER_MARRIED (married).
  const genderLabel =
    citizenData.gender === 0 ? t("citizen.male") : t("citizen.female");
  const maritalLabel =
    citizenData.citizen_type === MEMBER_MARRIED
      ? t("citizen.married")
      : t("citizen.single");

  // Show the higher education major only while enrolled and holding a valid skill ID.
  // (When not enrolled or in basic education, education_skill is VALUE_NONE and has no master entry.)
  const majorSkill = masterData.skills[citizenData.education_skill];

  // Determine higher education completion by skill (any skill at or above the high-skill threshold).
  const isHigherEducated = Object.values(citizenData.skills ?? {}).some(
    (value) => value >= HIGH_SKILL_BOUNDARY,
  );

  // Radar chart data in ascending skill ID order;
  // resolve display names from master data (display the ID itself for undefined IDs).
  const skillChartData = Object.entries(citizenData.skills ?? {})
    .map(([skillId, value]) => ({
      id: Number(skillId),
      subject: masterData.skills[Number(skillId)]?.display_name ?? skillId,
      value,
    }))
    .sort((a, b) => a.id - b.id);

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={citizenIcon}
        showStatus={false}
        variantName={maritalLabel}
        name={citizenData.name}
      />

      <div className="operation-panel">
        <button
          className={`operation-btn${followed === citizenId ? " operation-btn--active" : ""}`}
          type="button"
          title={t(followed === citizenId ? "citizenTracking.unfollow" : "citizenTracking.follow")}
          aria-pressed={followed === citizenId}
          onClick={() => void toggleFollow()}
        >
          <CameraIcon />
        </button>
        <button
          className={`operation-btn${tracked ? " operation-btn--active" : ""}`}
          type="button"
          title={t(tracked ? "citizenTracking.untrack" : "citizenTracking.track")}
          aria-pressed={tracked}
          onClick={() => void toggleTracked()}
        >
          <TrackIcon />
        </button>
      </div>

      <div className="agent-detail-tabs">
        {(["basic", "activity"] as const).map((tab) => (
          <button
            key={tab}
            className={`agent-detail-tab${tab === activeTab ? " agent-detail-tab--active" : ""}`}
            type="button"
            onClick={() => setActiveTab(tab)}
          >
            {t(tab === "basic" ? "citizenTracking.tabBasic" : "citizenTracking.tabActivity")}
          </button>
        ))}
      </div>

      <div className="agent-detail-tab-content">
        {activeTab === "activity" ? (
          <CitizenActivityPanel apiBaseUrl={apiBaseUrl} itinerary={itinerary} failed={itineraryFailed} />
        ) : (
          <>
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">
                {t("citizen.basicInfo")}
              </h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("citizen.age")}</span>
                <span className="agent-detail-value">
                  {t("citizen.ageValue", { age: citizenData.age })}
                </span>

                <span className="agent-detail-label">{t("citizen.gender")}</span>
                <span className="agent-detail-value">{genderLabel}</span>

                <span className="agent-detail-label">
                  {t("citizen.maritalStatus")}
                </span>
                <span className="agent-detail-value">{maritalLabel}</span>

                <span className="agent-detail-label">{t("citizen.household")}</span>
                <span className="agent-detail-value">
                  <AgentWindowLink
                    agentName={citizenData.household_agent_name}
                    agentId={citizenData.household_agent_id}
                    placeholder="-"
                  />
                </span>
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">
                {t("citizen.workEducation")}
              </h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("citizen.employer")}</span>
                <span className="agent-detail-value">
                  <AgentWindowLink
                    agentName={citizenData.employer_agent_name}
                    agentId={citizenData.employer_agent_id}
                    placeholder={t("citizen.unemployed")}
                  />
                </span>

                {citizenData.employer_agent_id > 0 && (
                  <>
                    <span className="agent-detail-label">{t("citizen.commuteTime")}</span>
                    <span className="agent-detail-value citizen-commute" style={{ display: "flex", alignItems: "center", gap: "8px" }}>
                      <div 
                        className="citizen-commute-meter-container" 
                        style={{
                          width: "120px",
                          height: "12px",
                          backgroundColor: "rgba(255, 255, 255, 0.1)",
                          borderRadius: "6px",
                          overflow: "hidden",
                          border: "1px solid rgba(255, 255, 255, 0.2)",
                          position: "relative"
                        }}
                        title={`${citizenData.commute_time.toFixed(1)} mins`}
                      >
                        <div 
                          className="citizen-commute-meter-fill"
                          style={{
                            width: `${Math.min(100, (citizenData.commute_time / 120) * 100)}%`,
                            height: "100%",
                            backgroundColor: `hsl(${Math.max(0, 120 - (citizenData.commute_time / 120) * 120)}, 80%, 50%)`,
                            borderRadius: "inherit",
                            transition: "width 0.3s ease, background-color 0.3s ease"
                          }}
                        />
                      </div>
                      <span style={{ fontSize: "1rem", color: "#c5d4e3" }}>
                        {`${citizenData.commute_time.toFixed(1)} ${t("common.minutes")}`}
                      </span>
                    </span>
                  </>
                )}

                <span className="agent-detail-label">{t("citizen.school")}</span>
                <span className="agent-detail-value">
                  <AgentWindowLink
                    agentName={citizenData.school_agent_name}
                    agentId={citizenData.school_agent_id}
                    placeholder={t("citizen.notEnrolled")}
                  />
                </span>

                {majorSkill && (
                  <>
                    <span className="agent-detail-label">{t("citizen.major")}</span>
                    <span className="agent-detail-value">
                      {majorSkill.display_name}
                    </span>
                  </>
                )}

                <span className="agent-detail-label">
                  {t("citizen.higherEducation")}
                </span>
                <span className="agent-detail-value">
                  {isHigherEducated
                    ? t("citizen.graduated")
                    : t("citizen.notGraduated")}
                </span>
              </div>
            </section>

            {skillChartData.length > 0 && (
              <section className="agent-detail-section">
                <h3 className="agent-detail-section-title">{t("citizen.skills")}</h3>
                <div className="citizen-skill-chart">
                  <ResponsiveContainer width="100%" height={260}>
                    <RadarChart
                      cx="50%"
                      cy="50%"
                      outerRadius="75%"
                      data={skillChartData}
                    >
                      <PolarGrid />
                      <PolarAngleAxis
                        dataKey="subject"
                        tick={{ fontSize: "1rem", fill: "#c5d4e3" }}
                      />
                      <PolarRadiusAxis
                        angle={30}
                        domain={[0, SKILL_DISPLAY_MAX]}
                        tick={false}
                        axisLine={false}
                      />
                      <Radar
                        name={t("citizen.skills")}
                        dataKey="value"
                        stroke="#4ea1ff"
                        fill="#4ea1ff"
                        fillOpacity={0.6}
                      />
                    </RadarChart>
                  </ResponsiveContainer>
                </div>
              </section>
            )}

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">
                {t("citizen.publicSafety")}
              </h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("citizen.criminality")}</span>
                <span className="agent-detail-value citizen-criminality">
                  <meter
                    className="citizen-criminality-meter"
                    value={citizenData.potential_criminality}
                    min={0}
                    max={1}
                  />
                  {`${(citizenData.potential_criminality * 100).toFixed(1)}%`}
                </span>
              </div>
            </section>
          </>
        )}
      </div>
    </div>
  );
}
