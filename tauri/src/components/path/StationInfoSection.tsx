/**
 * Station information (station building variants only, in path details).
 *
 * A station is its station building path rather than a separate entity. Its name is the path's name, changed through
 * normal path updates (PATCH /api/v1/paths/{id} {name}). Fetch platforms, serving lines, and stop-level boardings from
 * GET /api/v1/paths/{id}/station-info. PathDetailPanel renders this only for variants whose path_master type is PATH_STATION_HOUSE.
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { EditableLabel } from "../common/EditableLabel";
import { PathLink } from "../common/PathLink";
import { STATUS_ACTIVE } from "../../constants";
import "./StationInfoSection.css";
import { StationPassageModeSelect } from "./StationPassageModeSelect";
import { sendSessionReload } from "../../utils/ipc";

type StationPlatform = {
  path_id: number;
  path_variant: number;
  status: number;
  length: number;
  platform_side: number;
};

type StationLine = {
  line_agent_id: number;
  name: string | null;
  status: number;
};

/** Stop on a station building's platform. ridership is daily boardings there in the previous epoch; null if unrecorded. */
type StationStop = {
  node_id: number;
  line_agent_id: number;
  platform_path_id: number;
  ridership: number | null;
};

type StationInfo = {
  house_path_id: number;
  footprints: { center: [number, number]; rotation: number; width: number; depth: number }[];
  paths: StationPlatform[];
  lines: StationLine[];
  stops: StationStop[];
  /** Total boardings at stops belonging to the station. */
  ridership: number;
};

type StationInfoSectionProps = {
  passageMode: string;
  apiBaseUrl: string;
  pathId: number;
  /** Station name (= path name), used for display and initial inline editing value. */
  pathName: string;
  /** Refetch path details after a successful station-name PATCH. */
  onRenamed: () => void;
};

/** Clickable line-name link opening the line detail window (transit_detail). */
function TransitLineLink({ lineAgentId, label }: { lineAgentId: number; label: string }) {
  async function handleClick() {
    try {
      await invoke("open_registered_window", {
        windowType: "transit_detail",
        entityId: String(lineAgentId),
      });
    } catch (error) {
      console.error("failed to open transit detail", error);
    }
  }
  return (
    <button className="entity-link" onClick={handleClick} type="button">
      {label}
    </button>
  );
}

export function StationInfoSection({
  apiBaseUrl,
  pathId,
  pathName,
  passageMode,
  onRenamed,
}: StationInfoSectionProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [info, setInfo] = useState<StationInfo | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [revision, setRevision] = useState(0);

  async function savePassageMode(value: string) {
    setSaving(true);
    setError("");
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(pathId)), {
        method: "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ passage_mode: value }),
      });
      setRevision((r) => r + 1);
      onRenamed();
      await sendSessionReload();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<StationInfo>(
      buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}/station-info", String(pathId)),
      { signal: controller.signal },
    )
      .then((data) => {
        if (!controller.signal.aborted) setInfo(data);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("failed to fetch station info", err);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl, pathId, revision]);

  const lineLabel = (lineAgentId: number) => {
    const name = info?.lines.find((line) => line.line_agent_id === lineAgentId)?.name;
    return name && name !== "" ? name : `#${lineAgentId}`;
  };
  const platformLabel = (pathId: number) => {
    const variant = info?.paths.find((p) => p.path_id === pathId)?.path_variant;
    return `${variant != null ? masterData?.paths[variant]?.display_name ?? "" : ""} #${pathId}`;
  };

  /** Save station name (= path name) with PATCH and ask the parent to refetch path details on success. */
  const handleRenameSave = useCallback(async (next: string) => {
    await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(pathId)), {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: next }),
    });
    onRenamed();
  }, [apiBaseUrl, pathId, onRenamed]);

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("station.sectionTitle")}</h3>
      <StationPassageModeSelect value={passageMode} onChange={savePassageMode} disabled={saving} />
      {error && <p role="alert">{error}</p>}
      <div className="agent-detail-grid">
        <span className="agent-detail-label">{t("station.name")}</span>
        <span className="agent-detail-value">
          <EditableLabel value={pathName} onSave={handleRenameSave} />
        </span>
        <span className="agent-detail-label">{t("station.ridership")}</span>
        <span className="agent-detail-value">
          {info ? Math.round(info.ridership).toLocaleString() : "-"}
        </span>
      </div>

      {/* Platform list. */}
      <h4 className="station-info__subtitle">
        {t("station.platforms")}
        {info && <span className="station-info__count">({info.paths.length})</span>}
      </h4>
      {info && info.paths.length > 0 ? (
        <div className="agent-detail-grid">
          {info.paths.map((p) => (
            <div className="station-info__row" key={p.path_id}>
              <PathLink
                pathId={p.path_id}
                pathName={`${masterData?.paths[p.path_variant]?.display_name ?? ""} #${p.path_id}`}
              />
              <span className="station-info__meta">
                {Math.round(p.length)}m
                {p.status !== STATUS_ACTIVE && ` (${t(`status.${p.status}`, { defaultValue: "" })})`}
              </span>
            </div>
          ))}
        </div>
      ) : (
        <p className="agent-detail-muted">{t("station.noPlatforms")}</p>
      )}

      {/* Serving lines. */}
      <h4 className="station-info__subtitle">
        {t("station.lines")}
        {info && <span className="station-info__count">({info.lines.length})</span>}
      </h4>
      {info && info.lines.length > 0 ? (
        <div className="agent-detail-grid">
          {info.lines.map((line) => (
            <div className="station-info__row" key={line.line_agent_id}>
              <TransitLineLink
                lineAgentId={line.line_agent_id}
                label={line.name && line.name !== "" ? line.name : `#${line.line_agent_id}`}
              />
              <span className="station-info__meta">
                {line.status === STATUS_ACTIVE
                  ? t("station.lineRunning")
                  : t("station.lineSuspended")}
              </span>
            </div>
          ))}
        </div>
      ) : (
        <p className="agent-detail-muted">{t("station.noLines")}</p>
      )}

      {/* Boardings by stop. */}
      <h4 className="station-info__subtitle">
        {t("station.stops")}
        {info && <span className="station-info__count">({info.stops.length})</span>}
      </h4>
      {info && info.stops.length > 0 ? (
        <div className="agent-detail-grid">
          {info.stops.map((stop) => (
            <div className="station-info__row" key={stop.node_id}>
              <span className="station-info__stop">
                <TransitLineLink lineAgentId={stop.line_agent_id} label={lineLabel(stop.line_agent_id)} />
                <PathLink pathId={stop.platform_path_id} pathName={platformLabel(stop.platform_path_id)} />
              </span>
              <span className="station-info__meta">
                {stop.ridership == null ? "-" : Math.round(stop.ridership).toLocaleString()}
              </span>
            </div>
          ))}
        </div>
      ) : (
        <p className="agent-detail-muted">{t("station.noStops")}</p>
      )}
    </section>
  );
}
