/**
 * Citizen detail panel
 *
 * Dedicated panel for the window_type="citizen_detail" popup.
 * Opens when clicking a household member (CitizenLink) in the household details (HouseholdInfoPanel in
 * AgentDetailPanel), clicking a car or pedestrian in the paused 3D view (Unity selects a citizen and opens via
 * world:select_object), opening from the tracking list, or clicking a citizen voice card.
 *
 * The heading shows age, marital status, and gender above the name. Below it, a full-width speech bubble pointing at
 * the portrait holds the citizen's voice (`citizenVoiceRemark.ts`). Opened from a voice card, the bubble repeats that
 * card's remark (`params.extra.voice`), so both say the same words; otherwise a remark is composed from
 * `/api/v1/citizens/{id}/voice`. Citizens who cannot speak in the citizen voice (such as children) have no bubble.
 *
 * Action buttons below the bubble:
 * - Camera: follow this citizen's car or pedestrian in Unity; press again to stop. State follows
 *   `citizen:follow_state`.
 * - Track: add to or remove from the tracking list, which persists in save data.
 * - The mayor's actions (`citizenActions.ts`). Expelling closes the panel, since the citizen no longer exists.
 *
 * The Basic tab uses fields from /api/v1/citizens/{id} (the single_citizen schema in endpoints.yaml) and the
 * household's satisfaction from /api/v1/agents/{household id}.
 * - One list: household, workplace, commute time, school, major, higher education completion, and a criminal
 *   propensity meter.
 * - Household status: the household's car ownership, standard of living (demand_tier, worded as in the household
 *   section of the agent detail), and meters for its nutrition, healthcare, and leisure satisfaction.
 * - Skills: radar chart of the six skill values.
 * The Activity tab contains a map of home, destinations, and outbound routes, with the destination list
 * (`CitizenActivityPanel`) below.
 *
 * Household, workplace, and school links open a separate agent detail window instead of navigating within this
 * panel (using open_registered_window, as in SearchWindow). The household and workplace also have a button moving
 * the 3D camera to them.
 */

import { Fragment, useCallback, useEffect, useState } from "react";
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
import type { AgentData, AgentResponse, CitizenData, CitizenResponse } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { MEMBER_MARRIED } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { InfoHeader } from "../common/InfoHeader";
import { CameraFocusButton } from "../common/CameraFocusButton";
import { Meter, type MeterPolarity } from "../common/Meter";
import { sendCameraFocus, sendCitizenFollow, sendCitizenUnfollow, sendFocusGraphic } from "../../utils/ipc";
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
import { CITIZEN_ACTIONS, runCitizenAction, type CitizenActionId } from "./citizenActions";
import { composeRemark, remarkText, type Voice, type VoiceRemark } from "./citizenVoiceRemark";
import { CameraIcon, TrackIcon } from "./CitizenTrackingIcons";
import { citizenFaceUrl, citySkinTone } from "../../utils/citizenFace";
import trafficPedestrian from "../../assets/images/traffics/traffic_pedestrian.png";
import trafficRoad from "../../assets/images/traffics/traffic_road.png";
import { useCityClimate } from "../../hooks/useCityClimate";
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
  /** Remark of the voice card the panel was opened from. */
  initialVoice?: VoiceRemark;
};

/**
 * Clickable agent name link.
 * Opens a new detail window for the agent. Supplying onFocusCamera adds the camera button at the right end.
 * Show a placeholder if there is no name or referenced agent (e.g. unemployed or not enrolled).
 */
function AgentWindowLink({
  agentName,
  agentId,
  placeholder,
  onFocusCamera,
}: {
  agentName: string | null | undefined;
  agentId: number;
  placeholder: string;
  onFocusCamera?: () => void;
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
    <>
      <button className="entity-link" onClick={handleClick} type="button">
        {agentName}
      </button>
      {onFocusCamera && <CameraFocusButton onClick={onFocusCamera} />}
    </>
  );
}

export function CitizenDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
  initialVoice,
}: CitizenDetailPanelProps) {
  const { t, i18n } = useTranslation();
  const masterData = useMasterData();
  const [citizenData, setCitizenData] = useState<CitizenData | null>(null);
  const [household, setHousehold] = useState<AgentData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  /** undefined while loading, null when the citizen has no voice. */
  const [remark, setRemark] = useState<VoiceRemark | null | undefined>(undefined);
  const [activeTab, setActiveTab] = useState<"basic" | "activity">("basic");
  const [itinerary, setItinerary] = useState<CitizenItinerary | null>(null);
  const [itineraryFailed, setItineraryFailed] = useState(false);
  const [tracked, setTracked] = useState(false);
  const followed = useFollowedCitizen();
  const climate = useCityClimate(apiBaseUrl);
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

  useEffect(() => {
    if (initialVoice) {
      setRemark(initialVoice);
      return;
    }
    if (!apiBaseUrl || !Number.isFinite(citizenId)) return;
    const controller = new AbortController();
    setRemark(undefined);
    fetchApi<{ voice: Voice | null }>(buildApiUrl(apiBaseUrl, `/api/v1/citizens/${citizenId}/voice`), { signal: controller.signal })
      .then(({ voice }) => {
        if (!controller.signal.aborted) setRemark(voice && composeRemark(i18n, voice));
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        console.error("failed to load citizen voice", err);
        setRemark(null);
      });
    return () => controller.abort();
  }, [apiBaseUrl, citizenId, initialVoice, i18n]);

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

  async function handleAction(citizen: CitizenData, action: CitizenActionId) {
    const target = { citizenId: citizen.citizen_id, name: citizen.name, householdAgentId: citizen.household_agent_id };
    const done = await runCitizenAction(apiBaseUrl, action, target, t, i18n.language);
    if (done && action === "expel") await invoke("close_current_window").catch(console.error);
  }

  /** Agents have no geometry, so focus on their parcel (the containing building's parcel for tenants). */
  async function focusAgent(agentId: number) {
    const { economic_agent: agent } = await fetchApi<AgentResponse>(buildApiUrl(apiBaseUrl, `/api/v1/agents/${agentId}`));
    const parcelId = agent.parcel_id ?? 0;
    if (parcelId <= 0) return;
    const parcel = await fetchApi<{ polygon: number[][] }>(buildApiUrl(apiBaseUrl, `/api/v1/parcels/${parcelId}`));
    await sendCameraFocus(WINDOW_TYPE, parcel.polygon);
  }

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchApi<CitizenResponse>(url, { signal: controller.signal })
      .then(async ({ citizen }) => {
        const householdData = citizen.household_agent_id > 0
          ? (await fetchApi<AgentResponse>(
              buildApiUrl(apiBaseUrl, `/api/v1/agents/${citizen.household_agent_id}`),
              { signal: controller.signal },
            )).economic_agent
          : null;
        if (!controller.signal.aborted) {
          setCitizenData(citizen);
          setHousehold(householdData);
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
  }, [apiBaseUrl, url]);

  // Wait for the climate and the voice as well, so the face keeps its skin tone and the layout holds still after opening.
  if (loading || !masterData || climate === undefined || remark === undefined) {
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

  // Backend gender is 0 = male / 1 = female; citizen_type is MEMBER_SINGLE (single) / MEMBER_MARRIED (married).
  const profile = [
    t("citizen.ageValue", { age: citizenData.age }),
    citizenData.citizen_type === MEMBER_MARRIED ? t("citizen.married") : t("citizen.single"),
    citizenData.gender === 0 ? t("citizen.male") : t("citizen.female"),
  ].join(t("citizenVoice.separator"));

  // Show the higher education major only while enrolled and holding a valid skill ID.
  // (When not enrolled or in basic education, education_skill is VALUE_NONE and has no master entry.)
  const majorSkill = masterData.skills[citizenData.education_skill];

  // Determine higher education completion by skill (any skill at or above the high-skill threshold).
  const isHigherEducated = Object.values(citizenData.skills ?? {}).some(
    (value) => value >= HIGH_SKILL_BOUNDARY,
  );

  const meterRows = (meters: { label: string; value: number | undefined; polarity?: MeterPolarity }[]) =>
    meters.map(({ label, value, polarity }) => value != null && (
      <Fragment key={label}>
        <span className="agent-detail-label">{label}</span>
        <span className="agent-detail-value">
          <Meter value={value} polarity={polarity} />
        </span>
      </Fragment>
    ));

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
    <div className="agent-detail citizen-detail">
      <InfoHeader
        iconSrc={citizenFaceUrl({
          citizenId: citizenData.citizen_id,
          gender: citizenData.gender,
          age: citizenData.age,
          ...citySkinTone(climate),
        })}
        showStatus={false}
        variantName={profile}
        name={citizenData.name}
      />

      {remark && <p className="citizen-detail-voice">{remarkText(remark, t, i18n.language)}</p>}

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
        {CITIZEN_ACTIONS.map(({ id, icon }) => (
          <button
            key={id}
            className="operation-btn"
            type="button"
            title={t(`citizenAction.${id}.label`)}
            data-guide="citizen.action"
            data-guide-value={id}
            onClick={() => void handleAction(citizenData, id)}
          >
            <img src={icon} alt={t(`citizenAction.${id}.label`)} />
          </button>
        ))}
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
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("citizen.household")}</span>
                <span className="agent-detail-value">
                  <AgentWindowLink
                    agentName={citizenData.household_agent_name}
                    agentId={citizenData.household_agent_id}
                    placeholder="-"
                    onFocusCamera={() => void focusAgent(citizenData.household_agent_id).catch(console.error)}
                  />
                </span>

                <span className="agent-detail-label">{t("citizen.employer")}</span>
                <span className="agent-detail-value">
                  <AgentWindowLink
                    agentName={citizenData.employer_agent_name}
                    agentId={citizenData.employer_agent_id}
                    placeholder={t("citizen.unemployed")}
                    onFocusCamera={() => void focusAgent(citizenData.employer_agent_id).catch(console.error)}
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

                {meterRows([
                  { label: t("citizen.criminality"), value: citizenData.potential_criminality, polarity: "negative" },
                ])}
              </div>
            </section>

            {household && (
              <section className="agent-detail-section">
                <h3 className="agent-detail-section-title">{t("citizen.householdStatus")}</h3>
                <div className="agent-detail-grid">
                  <span className="agent-detail-label">{t("citizen.automobile")}</span>
                  <span className="agent-detail-value citizen-automobile">
                    <img
                      className="citizen-automobile-icon"
                      src={household.has_automobile ? trafficRoad : trafficPedestrian}
                      alt=""
                    />
                    {household.has_automobile ? t("citizen.automobileOwned") : t("citizen.automobileNone")}
                  </span>

                  {household.demand_tier != null && (
                    <>
                      <span className="agent-detail-label">{t("household.livingStandard")}</span>
                      <span className="agent-detail-value">
                        {t(`household.livingStandardLevel.${household.demand_tier}`)}
                      </span>
                    </>
                  )}

                  {meterRows([
                    { label: t("household.satisNutrition"), value: household.satis_nutrition },
                    { label: t("household.satisHealthcare"), value: household.satis_healthcare },
                    { label: t("household.satisLeisure"), value: household.satis_leisure },
                  ])}
                </div>
              </section>
            )}

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
          </>
        )}
      </div>
    </div>
  );
}
