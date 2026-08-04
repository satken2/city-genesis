/**
 * 駅詳細パネル
 *
 * window_type="station_detail" の画面右側ポップアップで表示する読み取り専用パネル。
 * 駅(Station)は近接する駅パス(プラットフォーム)群をバックエンドが自動で束ねた概念で、
 * 3Dビューの駅舎クリック(world:select_object)から開かれる。
 *
 * データは GET /api/v1/stations/{id} から取得し、駅名・所属ホーム・発着路線・駅舎の
 * 設置状態・乗降統計を表示する。駅名変更や駅舎の設置UIは別途(現段階では表示のみ)。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { InfoHeader } from "../common/InfoHeader";
import { PathLink } from "../common/PathLink";
import { resolvePathIcon } from "../../utils/pathIcon";
import { PATH_TRACK, STATUS_ACTIVE } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
// 右パネルの体裁(セクション・ラベル/値グリッド)は経済主体詳細パネルと共有する。
import "../agent/AgentDetailPanel.css";
import "./StationDetailPanel.css";

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

type StationHouse = {
  /** 駅舎の設置位置 [x, z] */
  position: [number, number];
  /** Y軸回転(度) */
  rotation: number;
};

type StationDetail = {
  station_id: number;
  name: string;
  version: number;
  center_point: [number, number] | null;
  /** 駅舎の設置情報。未設置(野ざらしの停留場)は null(用地確保中も null) */
  house: StationHouse | null;
  /** 駅舎の設置予約中(用地確保のため整地待ち)。true の間は house は null。 */
  house_pending: boolean;
  /** 設置する駅舎モデルのバリアント(station_houses のキー)。未選択なら null */
  house_variant: number | null;
  paths: StationPlatform[];
  lines: StationLine[];
  /** 駅単位の乗降統計(路線の乗車回数を停留所数で按分した近似値) */
  ridership: number;
};

type StationDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
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

export function StationDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: StationDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [station, setStation] = useState<StationDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  /* ---- 駅名変更 ---- */
  const [editingName, setEditingName] = useState(false);
  const [nameInput, setNameInput] = useState("");
  const [nameSaving, setNameSaving] = useState(false);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchApi<StationDetail>(url, { signal: controller.signal })
      .then((data) => {
        if (controller.signal.aborted) return;
        setStation(data);
        setLoading(false);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
          setLoading(false);
        }
      });
    return () => controller.abort();
  }, [url, refreshKey]);

  /**
   * 駅名を PATCH /api/v1/stations/{id} {name} で保存し、成功したら再取得する
   * (パス詳細パネルの地区変更と同じ PATCH→再取得パターン)。
   */
  const handleRenameSave = useCallback(() => {
    if (!station || nameSaving) return;
    const next = nameInput.trim();
    if (next === "" || next === station.name) {
      setEditingName(false);
      return;
    }
    setNameSaving(true);
    fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/stations/{id}", String(station.station_id)), {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: next }),
    })
      .then(() => {
        setEditingName(false);
        setRefreshKey((k) => k + 1);
      })
      .catch((err) => {
        console.error("failed to rename station", err);
      })
      .finally(() => setNameSaving(false));
  }, [station, nameInput, nameSaving, apiBaseUrl]);

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }
  if (error || !station) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const displayName =
    station.name && station.name !== "" ? station.name : `#${station.station_id}`;
  // プレイヤーが選択した駅舎モデルの表示名を種別として表示する(未設置は空)。
  const variantName =
    station.house_variant != null
      ? masterData?.station_houses[station.house_variant]?.display_name ?? ""
      : "";

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={resolvePathIcon(PATH_TRACK)}
        variantName={variantName}
        name={displayName}
        showStatus={false}
      />

      {/* 基本情報 */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("station.basicInfo")}</h3>
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("station.name")}</span>
          <span className="agent-detail-value">
            {editingName ? (
              <span className="station-detail__rename">
                <input
                  className="station-detail__rename-input"
                  type="text"
                  value={nameInput}
                  autoFocus
                  disabled={nameSaving}
                  onChange={(e) => setNameInput(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") handleRenameSave();
                    else if (e.key === "Escape") setEditingName(false);
                  }}
                />
                <button
                  className="station-detail__rename-btn"
                  type="button"
                  disabled={nameSaving}
                  onClick={handleRenameSave}
                >
                  {t("station.renameSave")}
                </button>
              </span>
            ) : (
              <span className="station-detail__name-display">
                {displayName}
                <button
                  className="station-detail__rename-btn"
                  type="button"
                  title={t("station.rename")}
                  onClick={() => {
                    setNameInput(station.name);
                    setEditingName(true);
                  }}
                >
                  {t("station.rename")}
                </button>
              </span>
            )}
          </span>
          <span className="agent-detail-label">{t("station.platformCount")}</span>
          <span className="agent-detail-value">{station.paths.length}</span>
          <span className="agent-detail-label">{t("station.ridership")}</span>
          <span className="agent-detail-value">
            {Math.round(station.ridership).toLocaleString()}
          </span>
          <span className="agent-detail-label">{t("station.house")}</span>
          <span className="agent-detail-value">
            {/* 設置状態: 用地確保中 → 設置済み → 未設置。設置済み/予約中はモデル名も併記する。 */}
            {station.house_pending
              ? t("station.housePending")
              : station.house
                ? t("station.housePlaced")
                : t("station.houseNone")}
            {(station.house || station.house_pending) && station.house_variant != null &&
              masterData?.station_houses[station.house_variant]?.display_name && (
                <span className="station-detail__house-model">
                  {" "}
                  ({masterData.station_houses[station.house_variant]!.display_name})
                </span>
              )}
          </span>
        </div>
      </section>

      {/* ホーム一覧 */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">
          {t("station.platforms")}
          <span style={{ marginLeft: 6, opacity: 0.7 }}>({station.paths.length})</span>
        </h3>
        <div className="agent-detail-grid">
          {station.paths.map((p) => (
            <div className="station-detail__platform-row" key={p.path_id}>
              <PathLink
                pathId={p.path_id}
                pathName={`${masterData?.paths[p.path_variant]?.display_name ?? ""} #${p.path_id}`}
              />
              <span className="station-detail__platform-meta">
                {Math.round(p.length)}m
                {p.status !== STATUS_ACTIVE && ` (${t(`status.${p.status}`, { defaultValue: "" })})`}
              </span>
            </div>
          ))}
        </div>
      </section>

      {/* 発着路線 */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">
          {t("station.lines")}
          <span style={{ marginLeft: 6, opacity: 0.7 }}>({station.lines.length})</span>
        </h3>
        {station.lines.length > 0 ? (
          <div className="agent-detail-grid">
            {station.lines.map((line) => (
              <div className="station-detail__line-row" key={line.line_agent_id}>
                <TransitLineLink
                  lineAgentId={line.line_agent_id}
                  label={line.name && line.name !== "" ? line.name : `#${line.line_agent_id}`}
                />
                <span className="station-detail__platform-meta">
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
    </div>
  );
}
