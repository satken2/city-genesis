/**
 * 駅情報セクション(パス詳細パネルの駅舎バリアント専用)
 *
 * 駅は独立したエンティティではなく駅舎パスそのもの。駅名はパスの name であり、
 * 通常のパス更新(PATCH /api/v1/paths/{id} {name})で変更する。所属ホーム・発着路線・
 * 乗降統計は保存されない集計値で、GET /api/v1/paths/{id}/station-info から取得する。
 * PathDetailPanel が path_master の type=PATH_STATION_HOUSE のバリアントのときだけ描画する。
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
import { StationHouseEndsSelect } from "./StationHouseEndsSelect";
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

type StationInfo = {
  house_path_id: number;
  footprints: { center: [number, number]; rotation: number; width: number; depth: number }[];
  paths: StationPlatform[];
  lines: StationLine[];
  /** 駅単位の乗降統計(路線の乗車回数を停留所数で按分した近似値) */
  ridership: number;
};

type StationInfoSectionProps = {
  houseEnds: string;
  apiBaseUrl: string;
  pathId: number;
  /** 駅名(=パスの name)。表示とインライン変更の初期値に使う。 */
  pathName: string;
  /** 駅名の PATCH 成功後にパス詳細を再取得させる。 */
  onRenamed: () => void;
};

/** 路線名をクリックで路線詳細ウィンドウ(transit_detail)を開くリンク。 */
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
  houseEnds,
  onRenamed,
}: StationInfoSectionProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [info, setInfo] = useState<StationInfo | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [revision, setRevision] = useState(0);

  async function saveHouseEnds(value: string) {
    setSaving(true);
    setError("");
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(pathId)), {
        method: "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ house_ends: value }),
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

  /** 駅名(=パスの name)を PATCH で保存し、成功したら親にパス詳細を再取得させる。 */
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
      <StationHouseEndsSelect value={houseEnds} onChange={saveHouseEnds} disabled={saving} />
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

      {/* ホーム一覧 */}
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

      {/* 発着路線 */}
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
    </section>
  );
}
