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

import { useEffect, useState } from "react";
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
  /** 駅舎の設置情報。未設置(野ざらしの停留場)は null */
  house: StationHouse | null;
  /** 駅舎モデルの代表バリアント(所属ホームから導出)。所属ゼロなら null */
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
  }, [url]);

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
  // 駅舎モデルの代表バリアント名(=最上位グレードのホームの表示名)を種別として表示する。
  const variantName =
    station.house_variant != null
      ? masterData?.paths[station.house_variant]?.display_name ?? ""
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
          <span className="agent-detail-value">{displayName}</span>
          <span className="agent-detail-label">{t("station.platformCount")}</span>
          <span className="agent-detail-value">{station.paths.length}</span>
          <span className="agent-detail-label">{t("station.ridership")}</span>
          <span className="agent-detail-value">
            {Math.round(station.ridership).toLocaleString()}
          </span>
          <span className="agent-detail-label">{t("station.house")}</span>
          <span className="agent-detail-value">
            {station.house ? t("station.housePlaced") : t("station.houseNone")}
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
