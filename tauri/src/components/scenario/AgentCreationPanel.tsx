/**
 * Agent creation panel (window_type="agent_creation"), scenario mode only.
 *
 * Create one nonbuilding agent (private facility = TYPE_STANDARD / trade hub = TYPE_TRADE) already occupying an
 * existing shared building and active.
 * Normal play uses spawn then relocation; scenario authors need to place facilities directly without waiting.
 * POST /api/v1/scenario-tools/tenant-agent handles creation: occupancy, district/parcel inheritance, rent/wage
 * seeding, and activation.
 *
 * Workflow: type → variant dropdown → Select Building → click a building in 3D → occupied floors → Create.
 *
 * Reuse Unity's building picker (MODE_PICK_BUILDING) from Copy Existing Building in construction
 * (`sendPickCopySource` → `building_construction:copy_source`). Unity performs a generic building selection;
 * only source_agent_id is used here.
 * The windows are mutually exclusive so they do not compete for notifications. If closed while awaiting
 * selection, Rust (EDIT_MODE_WINDOW_TYPES in `window.rs`) sends STATE_NORMAL to exit picking.
 *
 * Occupancy is actually area (m²), with no floor concept, but authors specify floors: occupied area = floors ×
 * building base_area.
 * The maximum is available area for the selected space_type divided by one-floor area. Nonshared buildings
 * require one tenant to occupy all floors, so floors are fixed and occupied buildings cannot be selected.
 * TYPE_DEFAULT buildings (border checkpoints) use registry-only occupancy like City Hall: no floor types or
 * vacancies; the maximum is the building's floor count.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";

import {
  IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE,
  sendChangeState,
  sendPickCopySource,
} from "../../utils/ipc";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { STATUS_ACTIVE, STATUS_PAUSED, TYPE_BUILDING, TYPE_DEFAULT, TYPE_STANDARD, TYPE_TRADE } from "../../constants";
import type { AgentData } from "../../types";
import "./AgentCreationPanel.css";

/** Window type, used as the IPC sender label */
const WINDOW_TYPE = "agent_creation";

/** Creatable agent types, matching backend _TENANT_AGENT_TYPES */
const AGENT_TYPE_OPTIONS = [
  { type: TYPE_STANDARD, labelKey: "scenarioTools.agentCreation.typeStandard" },
  { type: TYPE_TRADE, labelKey: "scenarioTools.agentCreation.typeTrade" },
] as const;

/** Tolerance for float32 rounding when flooring the number of available floors */
const FLOOR_EPSILON = 1e-6;

/** Return value of POST /api/v1/scenario-tools/tenant-agent */
type TenantAgentResult = {
  agent_id: number;
  name: string;
  variant: number;
  building_agent_id: number;
  space_type: number;
  realestate_amount: number;
  floors: number;
  vacancy_after: number | null;
};

/** Notification from Unity's building picker (only source_agent_id is used) */
type CopySourcePayload = { source_agent_id: number };

/** Display area as a truncated integer; fractions of m² do not affect the author's decision. */
function formatArea(value: number): string {
  return Math.floor(value).toLocaleString();
}

export function AgentCreationPanel({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [agentType, setAgentType] = useState<number>(TYPE_STANDARD);
  const [variant, setVariant] = useState<number | null>(null);
  const [building, setBuilding] = useState<AgentData | null>(null);
  const [buildingLoading, setBuildingLoading] = useState(false);
  const [picking, setPicking] = useState(false);
  const [floors, setFloors] = useState(1);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);

  // Since construction uses the same event name, receive it only while this panel has requested a selection.
  const pickingRef = useRef(false);
  pickingRef.current = picking;

  /** Nonbuilding variants belonging to the selected type */
  const variantOptions = useMemo(() => {
    if (!masterData) return [] as { id: number; name: string }[];
    return Object.entries(masterData.agents)
      .map(([id, entry]) => ({ id: Number(id), entry }))
      .filter(({ entry }) => entry.type === agentType && !entry.building)
      .sort((a, b) => a.id - b.id)
      .map(({ id, entry }) => ({ id, name: entry.display_name }));
  }, [masterData, agentType]);

  // On a type change, select its first variant, or none if the list is empty.
  useEffect(() => {
    if (variant !== null && variantOptions.some((v) => v.id === variant)) return;
    setVariant(variantOptions[0]?.id ?? null);
  }, [variantOptions, variant]);

  /* ---- Building loading ---- */
  const loadBuilding = useCallback(
    async (agentId: number) => {
      setBuildingLoading(true);
      setMessage(null);
      try {
        const result = await fetchApi<{ economic_agent: AgentData }>(
          buildApiUrl(apiBaseUrl, "/api/v1/agents/{id}", String(agentId)),
        );
        setBuilding(result.economic_agent);
      } catch (err) {
        setBuilding(null);
        setMessage({ text: describeError(err, t("scenarioTools.agentCreation.loadingBuilding")), error: true });
      } finally {
        setBuildingLoading(false);
      }
    },
    [apiBaseUrl, t],
  );

  /* ---- Unity building picker ---- */
  const stopPicking = useCallback(async () => {
    setPicking(false);
    // Picking overrides the input preset; restore STATE_NORMAL to exit it.
    await sendChangeState("STATE_NORMAL", undefined, WINDOW_TYPE);
  }, []);

  const startPicking = useCallback(async () => {
    setMessage(null);
    setPicking(true);
    try {
      await sendPickCopySource(WINDOW_TYPE);
    } catch (err) {
      console.error("[AgentCreation] sendPickCopySource failed:", err);
      setPicking(false);
    }
  }, []);

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen<CopySourcePayload>(IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE, (event) => {
      if (!pickingRef.current) return;
      const agentId = event.payload?.source_agent_id;
      if (agentId == null) return;
      void stopPicking().catch(console.error);
      void loadBuilding(agentId);
    })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, [loadBuilding, stopPicking]);

  // Exit picking if unmounted while waiting (Rust handles closing with ×).
  useEffect(() => {
    return () => {
      if (pickingRef.current) {
        void sendChangeState("STATE_NORMAL", undefined, WINDOW_TYPE).catch(console.error);
      }
    };
  }, []);

  /* ---- Available floor area evaluation ---- */
  const variantEntry = variant !== null ? masterData?.agents[variant] : undefined;
  const buildingEntry = building ? masterData?.agents[building.variant] : undefined;
  const availability = useMemo(() => {
    if (!building || !variantEntry) return null;
    // TYPE_DEFAULT buildings (border checkpoints) use registry-only occupancy like City Hall, without floor types/vacancies, matching the backend.
    const ledger = building.is_bd && building.type === TYPE_DEFAULT;
    if (!building.is_bd || (building.type !== TYPE_BUILDING && !ledger)) {
      return { ok: false as const, reasonKey: "scenarioTools.agentCreation.notSharedBuilding" };
    }
    if (building.status !== STATUS_ACTIVE && building.status !== STATUS_PAUSED) {
      return { ok: false as const, reasonKey: "scenarioTools.agentCreation.statusUnavailable" };
    }
    const baseArea = building.base_area ?? 0;
    if (ledger) {
      // Floors are capped at the building's floor count. With no vacancy accounting, any number of agents can enter the same building.
      const maxFloors = Math.max(1, building.floors ?? 1);
      return { ok: true as const, shared: true, ledger: true, baseArea, vacancy: 0, maxFloors, minFloors: 1 };
    }
    const spaceKey = String(variantEntry.space_type);
    const capacity = building.capacity?.[spaceKey] ?? 0;
    const occupied = building.occupied?.[spaceKey] ?? 0;
    if (capacity <= 0 || baseArea <= 0) {
      return { ok: false as const, reasonKey: "scenarioTools.agentCreation.noSpaceType" };
    }
    // Building variants always have shared_building. Treat an undefined value as nonshared, matching the backend.
    const shared = buildingEntry?.shared_building === true;
    if (!shared) {
      if (occupied > 0) {
        return { ok: false as const, reasonKey: "scenarioTools.agentCreation.alreadyOccupied" };
      }
      const fullFloors = Math.max(1, Math.round(capacity / baseArea));
      return { ok: true as const, shared: false, ledger: false, baseArea, vacancy: capacity, maxFloors: fullFloors, minFloors: fullFloors };
    }
    const vacancy = Math.max(capacity - occupied, 0);
    const maxFloors = Math.floor(vacancy / baseArea + FLOOR_EPSILON);
    if (maxFloors < 1) {
      return { ok: false as const, reasonKey: "scenarioTools.agentCreation.noSpaceType" };
    }
    return { ok: true as const, shared: true, ledger: false, baseArea, vacancy, maxFloors, minFloors: 1 };
  }, [building, variantEntry, buildingEntry]);

  // Clamp floors when a building/variant change alters the maximum.
  useEffect(() => {
    if (!availability?.ok) return;
    setFloors((prev) => Math.min(Math.max(prev, availability.minFloors), availability.maxFloors));
  }, [availability]);

  const occupiedArea = availability?.ok ? floors * availability.baseArea : 0;
  const spaceMin = variantEntry?.space_min ?? 0;
  const spaceMax = variantEntry?.space_max ?? 0;
  const belowMin = availability?.ok === true && spaceMin > 0 && occupiedArea < spaceMin - FLOOR_EPSILON;
  const aboveMax = availability?.ok === true && spaceMax > 0 && occupiedArea > spaceMax + FLOOR_EPSILON;
  const canCreate = availability?.ok === true && variant !== null && !busy && !belowMin && !aboveMax;

  /* ---- Creation ---- */
  async function createAgent() {
    if (!canCreate || !building || variant === null) return;
    setBusy(true);
    setMessage(null);
    try {
      const result = await fetchApi<TenantAgentResult>(buildApiUrl(apiBaseUrl, "/api/v1/scenario-tools/tenant-agent"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ variant, building_agent_id: building.agent_id, floors }),
      });
      setMessage({
        text: t("scenarioTools.agentCreation.created", {
          name: result.name,
          area: formatArea(result.realestate_amount),
          floors: result.floors,
        }),
        error: false,
      });
      // Refetch occupancy to refresh vacancies so more agents can enter the same building consecutively.
      await loadBuilding(building.agent_id);
    } catch (err) {
      setMessage({ text: describeError(err, t("scenarioTools.agentCreation.createFailed")), error: true });
    } finally {
      setBusy(false);
    }
  }

  /** For ApiError, use errors.<code> translation, falling back to the server message. */
  function describeError(err: unknown, fallback: string): string {
    if (err instanceof ApiError) {
      const i18nKey = `errors.${err.code}`;
      return i18nKey !== "errors.UNKNOWN_ERROR" ? t(i18nKey, { defaultValue: err.message }) : err.message;
    }
    return fallback;
  }

  const controlsDisabled = busy || picking;

  return (
    <div className="agent-creation">
      <p className="agent-creation__hint">{t("scenarioTools.agentCreation.hint")}</p>

      <section className="agent-creation__section">
        <div className="agent-creation__label">{t("scenarioTools.agentCreation.agentType")}</div>
        <div className="agent-creation__seg" role="radiogroup">
          {AGENT_TYPE_OPTIONS.map((opt) => (
            <button
              key={opt.type}
              type="button"
              role="radio"
              aria-checked={agentType === opt.type}
              className={`agent-creation__seg-btn${agentType === opt.type ? " agent-creation__seg-btn--active" : ""}`}
              disabled={controlsDisabled}
              onClick={() => setAgentType(opt.type)}
            >
              {t(opt.labelKey)}
            </button>
          ))}
        </div>
      </section>

      <section className="agent-creation__section">
        <label className="agent-creation__label" htmlFor="agent-creation-variant">
          {t("scenarioTools.agentCreation.variant")}
        </label>
        <select
          id="agent-creation-variant"
          className="agent-creation__select"
          value={variant ?? ""}
          disabled={controlsDisabled || variantOptions.length === 0}
          onChange={(e) => setVariant(Number(e.target.value))}
        >
          {variantOptions.length === 0 && <option value="">{t("scenarioTools.agentCreation.noVariants")}</option>}
          {variantOptions.map((v) => (
            <option key={v.id} value={v.id}>
              {v.name}
            </option>
          ))}
        </select>
        {variantEntry?.description && <p className="agent-creation__description">{variantEntry.description}</p>}
      </section>

      <section className="agent-creation__section">
        <div className="agent-creation__label">{t("scenarioTools.agentCreation.building")}</div>
        <div className="agent-creation__row">
          {picking ? (
            <button type="button" className="agent-creation__btn" onClick={() => void stopPicking().catch(console.error)}>
              {t("scenarioTools.agentCreation.cancelPick")}
            </button>
          ) : (
            <button type="button" className="agent-creation__btn" disabled={busy} onClick={() => void startPicking()}>
              {t("scenarioTools.agentCreation.pickBuilding")}
            </button>
          )}
          <span className="agent-creation__building">
            {picking
              ? t("scenarioTools.agentCreation.picking")
              : buildingLoading
                ? t("scenarioTools.agentCreation.loadingBuilding")
                : building
                  ? t("scenarioTools.agentCreation.buildingInfo", {
                      name: building.name,
                      floors: building.floors ?? 1,
                      area: formatArea(building.base_area ?? 0),
                    })
                  : t("scenarioTools.agentCreation.noBuilding")}
          </span>
        </div>
        {availability && !availability.ok && <p className="agent-creation__warn">{t(availability.reasonKey)}</p>}
      </section>

      {availability?.ok && (
        <section className="agent-creation__section">
          <div className="agent-creation__label">{t("scenarioTools.agentCreation.floors")}</div>
          {availability.shared ? (
            <>
              {!availability.ledger && (
                <p className="agent-creation__note">
                  {t("scenarioTools.agentCreation.vacancy", {
                    floors: availability.maxFloors,
                    area: formatArea(availability.vacancy),
                  })}
                </p>
              )}
              <div className="agent-creation__row">
                <input
                  type="range"
                  min={1}
                  max={availability.maxFloors}
                  step={1}
                  value={floors}
                  disabled={controlsDisabled}
                  onChange={(e) => setFloors(Number(e.target.value))}
                />
                <input
                  type="number"
                  className="agent-creation__number"
                  min={1}
                  max={availability.maxFloors}
                  step={1}
                  value={floors}
                  disabled={controlsDisabled}
                  onChange={(e) => {
                    const v = Number.parseInt(e.target.value, 10);
                    if (!Number.isNaN(v)) setFloors(Math.min(Math.max(v, 1), availability.maxFloors));
                  }}
                />
              </div>
            </>
          ) : (
            <p className="agent-creation__note">
              {t("scenarioTools.agentCreation.fullOccupancy", {
                floors: availability.maxFloors,
                area: formatArea(availability.vacancy),
              })}
            </p>
          )}
          <p className="agent-creation__note">
            {t("scenarioTools.agentCreation.occupiedArea", { area: formatArea(occupiedArea) })}
          </p>
          {belowMin && (
            <p className="agent-creation__warn">
              {t("scenarioTools.agentCreation.belowSpaceMin", { min: formatArea(spaceMin) })}
            </p>
          )}
          {aboveMax && (
            <p className="agent-creation__warn">
              {t("scenarioTools.agentCreation.aboveSpaceMax", { max: formatArea(spaceMax) })}
            </p>
          )}
        </section>
      )}

      <div className="agent-creation__actions">
        <button
          type="button"
          className="agent-creation__btn agent-creation__btn--primary"
          disabled={!canCreate}
          onClick={() => void createAgent()}
        >
          {busy ? t("scenarioTools.agentCreation.creating") : t("scenarioTools.agentCreation.create")}
        </button>
      </div>

      {message && (
        <div className={`agent-creation__message${message.error ? " agent-creation__message--error" : ""}`}>{message.text}</div>
      )}
    </div>
  );
}
