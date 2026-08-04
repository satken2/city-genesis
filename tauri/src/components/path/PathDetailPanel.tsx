/**
 * パス詳細パネル
 *
 * window_type="path_detail" のポップアップウィンドウで表示する専用パネル。
 * パスのヘッダー情報(アイコン・ステータス・名前)と各種属性を表示する。
 *
 * 表示項目:
 * - InfoHeader: アイコン・ステータス・名前
 * - 基本情報セクション: 所属地区(ZoneLink または地区変更ドロップダウン)
 * - PathConditionPanel: 耐久度・修繕状態・騒音・大気汚染
 * - PathTrafficPanel: 制限速度設定・利用率・交通量テーブル
 *
 * アイコンは path_type に応じて paths/path_N.png を使用する。
 * ステータス表示は経済主体・土地区画パネルと共通の InfoHeader を利用する。
 *
 * nearby_zones が API レスポンスに含まれる場合、所属地区をドロップダウンで変更できる。
 * 隣接パスの所属地区のみ選択可能であり、地区の連続性要件を満たす。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { NearbyZoneEntry, PathData } from "../../types";
import { buildApiUrl } from "../../utils/api";
import { useTemplateMode } from "../../hooks/useTemplateMode";
import { resolvePathIcon } from "../../utils/pathIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import {
  PATH_STREET,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
  STATUS_PENDING,
  STATUS_DEMOLISHING,
  ALERT_PATH_CONSTRUCTION_SHORTAGE,
  ALERT_PATH_ACCIDENT,
  ALERT_PATH_NO_STATION_HOUSE
} from "../../constants";
import { InfoHeader } from "../common/InfoHeader";
import { ZoneLink } from "../common/ZoneLink";
import { PathConditionPanel } from "./PathConditionPanel";
import { PathTrafficPanel } from "./PathTrafficPanel";
import { PathOperationPanel } from "./PathOperationPanel";
import { PathConstructionInfoPanel } from "./PathConstructionInfoPanel";
import shortageIcon from "../../assets/images/ui/agent_panel/bribery.png";
import accidentIcon from "../../assets/images/ui/agent_panel/car-accident.png";
import "./PathDetailPanel.css";
import rocketIcon from "../../assets/images/ui/rocket.png";

type PathDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

export function PathDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: PathDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const templateMode = useTemplateMode(apiBaseUrl);
  const [pathData, setPathData] = useState<PathData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  // 建設中の道路の「建設スピード」(utilization_target, ％整数) と建設コスト/期間。
  const [constructionSpeed, setConstructionSpeed] = useState<number>(100);
  const [statusSaving, setStatusSaving] = useState(false);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<PathData>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          setPathData(data);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [url, refreshKey]);

  const isPreConstruction = pathData?.status === STATUS_SCHEDULED_CONSTRUCTION || pathData?.status === STATUS_PENDING;
  const isConstructing = pathData?.status === STATUS_CONSTRUCTING;
  const showConstructionInfo = pathData && [
    STATUS_PENDING,
    STATUS_SCHEDULED_CONSTRUCTION,
    STATUS_CONSTRUCTING,
    STATUS_DEMOLISHING
  ].includes(pathData.status);

  // 建設中の道路の建設スピード（utilization_target）をローカル state に初期化する。
  useEffect(() => {
    if (pathData && pathData.utilization_target != null) {
      setConstructionSpeed(Math.round(pathData.utilization_target * 100));
    } else {
      setConstructionSpeed(100);
    }
  }, [pathData]);

  /** 建設スピード(utilization_target)を PATCH する（値は％整数）。 */
  const handleConstructionSpeedChange = useCallback(() => {
    if (!pathData) return;
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/paths/${pathData.path_id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ utilization_target: constructionSpeed / 100 }),
    })
      .then(() => setRefreshKey((k) => k + 1))
      .catch((err) => console.error("Failed to update construction speed:", err));
  }, [pathData, apiBaseUrl, constructionSpeed]);

  /** 着工と保留を切り替えるトグルハンドラ */
  const handleStatusToggle = useCallback((checked: boolean) => {
    if (!pathData || statusSaving) return;
    setStatusSaving(true);
    const newStatus = checked ? STATUS_SCHEDULED_CONSTRUCTION : STATUS_PENDING;
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/paths/${pathData.path_id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ status: newStatus }),
    })
      .then(async (res) => {
        if (!res.ok) {
          const body = await res.text().catch(() => "");
          throw new Error(`HTTP ${res.status}: ${body}`);
        }
        setRefreshKey((k) => k + 1);
      })
      .catch((err) => {
        console.error("Failed to update path status:", err);
      })
      .finally(() => setStatusSaving(false));
  }, [pathData, apiBaseUrl, statusSaving]);

  /**
   * nearby_zones に基づいて地区変更が可能かどうかを判定する。
   * PATH_STREET で、nearby_zones が1件以上存在する場合に変更可能。
   */
  const canChangeZone =
    pathData?.path_type === PATH_STREET &&
    pathData.nearby_zones != null &&
    pathData.nearby_zones.length > 0;

  const [zoneSaving, setZoneSaving] = useState(false);
  const [zoneError, setZoneError] = useState<string | null>(null);

  /**
   * 地区変更ドロップダウンの選択肢を構築する。
   * 現在の所属地区が nearby_zones に含まれていない場合は先頭に追加する。
   */
  const zoneOptions: NearbyZoneEntry[] = (() => {
    if (!pathData || !pathData.nearby_zones) return [];
    const options = [...pathData.nearby_zones];
    const hasCurrentZone =
      pathData.zone_id != null &&
      options.some((z) => z.zone_id === pathData.zone_id);
    if (!hasCurrentZone && pathData.zone_name != null && pathData.zone_id > 0) {
      options.unshift({ zone_id: pathData.zone_id, zone_name: pathData.zone_name });
    }
    return options;
  })();

  /**
   * 地区変更を PATCH リクエストで送信する。
   * 成功時にデータを再取得する。
   */
  const handleZoneChange = useCallback(
    (newZoneId: number) => {
      if (!pathData || zoneSaving) return;
      setZoneSaving(true);
      setZoneError(null);

      const base = apiBaseUrl.replace(/\/+$/, "");
      fetch(`${base}/api/v1/paths/${pathData.path_id}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ zone_id: newZoneId }),
      })
        .then(async (res) => {
          if (!res.ok) {
            const body = await res.text().catch(() => "");
            throw new Error(`HTTP ${res.status}: ${body}`);
          }
          setRefreshKey((k) => k + 1);
        })
        .catch((err) => {
          setZoneError(err instanceof Error ? err.message : "Unknown error");
        })
        .finally(() => setZoneSaving(false));
    },
    [pathData, apiBaseUrl, zoneSaving],
  );

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !pathData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const iconSrc = resolvePathIcon(pathData.path_type);

  /** マスターデータからパスバリアントの表示名を取得する */
  const variantName = masterData?.paths[pathData.path_variant]?.display_name;

  /**
   * パスの表示名はIDベースで生成する。
   * name が設定されている場合はそちらを優先する。
   */
  const displayName =
    pathData.name !== ""
      ? pathData.name
      : t("path.titleWithId", { id: pathData.path_id });

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={iconSrc}
        status={pathData.status}
        variantName={variantName}
        name={displayName}
        constructionProgress={pathData.construction_progress ?? undefined}
      />

      <PathOperationPanel
        path={pathData}
        apiBaseUrl={apiBaseUrl}
        onActionPerformed={() => setRefreshKey((k) => k + 1)}
      />

      {(() => {
        const hasShortage = pathData.alert ? (pathData.alert & ALERT_PATH_CONSTRUCTION_SHORTAGE) !== 0 : false;
        const hasAccident = pathData.alert ? (pathData.alert & ALERT_PATH_ACCIDENT) !== 0 : false;
        // 駅パス(プラットフォーム)に駅舎が未設置。station_id 相当のフィールドは PathData に無いため、
        // 判定はアラートビットのみで行う(軌道建設パネルの駅舎タブで設置を促す)。
        const hasNoStationHouse = pathData.alert ? (pathData.alert & ALERT_PATH_NO_STATION_HOUSE) !== 0 : false;
        const hasAlerts = hasShortage || hasAccident || hasNoStationHouse;
        if (!hasAlerts) return null;

        return (
          <div className="agent-detail-alerts">
            {hasShortage && (
              <div className="agent-detail-alert-card">
                <div className="agent-detail-alert-title">
                  <img src={shortageIcon} alt="shortage" className="agent-detail-alert-icon" />
                  {t("path.alerts.constructionShortage.title")}
                </div>
                <div className="agent-detail-alert-desc">
                  {t("path.alerts.constructionShortage.description")}
                </div>
              </div>
            )}
            {hasAccident && (
              <div className="agent-detail-alert-card">
                <div className="agent-detail-alert-title">
                  <img src={accidentIcon} alt="accident" className="agent-detail-alert-icon" />
                  {t("path.alerts.accident.title")}
                </div>
                <div className="agent-detail-alert-desc">
                  {t("path.alerts.accident.description")}
                </div>
              </div>
            )}
            {hasNoStationHouse && (
              <div className="agent-detail-alert-card">
                <div className="agent-detail-alert-title">
                  <img src={shortageIcon} alt="no station house" className="agent-detail-alert-icon" />
                  {t("path.alerts.noStationHouse.title")}
                </div>
                <div className="agent-detail-alert-desc">
                  {t("path.alerts.noStationHouse.description")}
                </div>
              </div>
            )}
          </div>
        );
      })()}

      {/* 未着工（保留）状態の警告バナー */}
      {pathData.status === STATUS_PENDING && (
        <div className="pending-banner pending-banner--active">
          <span className="pending-banner__text">
            {t("path.pendingWarning", "この道路は計画中です。着工を指示してください。")}
          </span>
          <button
            className="pending-banner__btn"
            type="button"
            disabled={statusSaving}
            onClick={() => handleStatusToggle(true)}
          >
            <img src={rocketIcon} alt="rocket" />
            {t("path.action.startConstruction", "着工")}
          </button>
        </div>
      )}

      {/* 建設中の道路: 建設スピード(utilization_target)の調整 */}
      {isConstructing && (
        <div className="agent-detail-utilization-target">
          <div className="agent-detail-utilization-target-header">
            <span className="agent-detail-utilization-target-title">
              {t("path_construction.constructionSpeed", "建設スピード")}
            </span>
            <span className="agent-detail-utilization-target-value">
              {constructionSpeed}%
            </span>
          </div>
          <div className="agent-detail-utilization-target-slider-container">
            <input
              type="range"
              min="0"
              max="500"
              value={constructionSpeed}
              onChange={(e) => setConstructionSpeed(Number(e.target.value))}
              onMouseUp={handleConstructionSpeedChange}
              onTouchEnd={handleConstructionSpeedChange}
              className="agent-detail-utilization-target-slider"
            />
          </div>
        </div>
      )}

      {/* 計画中・建設中・解体中の時は建設工事情報を表示する */}
      {showConstructionInfo && (
        <PathConstructionInfoPanel
          path={pathData}
          apiBaseUrl={apiBaseUrl}
          refreshKey={refreshKey}
        />
      )}

      {/* 計画中のパスは既存データを表示する意味がないため、
          削除・形状編集ボタンだけを表示して各種属性セクションは省略する。 */}
      {!isPreConstruction && (
        <>
          {/* 基本情報セクション: 所属地区を表示・編集する */}
          {(pathData.zone_name != null || canChangeZone) && (
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("path.basicInfo", "基本情報")}</h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("path.zoneName")}</span>
                <span className="agent-detail-value">
                  {canChangeZone ? (
                    <select
                      className="zone-change-select"
                      value={pathData.zone_id ?? ""}
                      disabled={zoneSaving}
                      onChange={(e) => handleZoneChange(Number(e.target.value))}
                    >
                      {zoneOptions.map((z) => (
                        <option key={z.zone_id} value={z.zone_id}>
                          {z.zone_name}
                        </option>
                      ))}
                    </select>
                  ) : (
                    <ZoneLink
                      zoneId={pathData.zone_id}
                      zoneName={pathData.zone_name ?? ""}
                    />
                  )}
                </span>
                {/* 地形追従の個別設定。バリアント既定を上書きしている(0/1)ときだけ表示する。 */}
                {(pathData.follow_terrain === 0 || pathData.follow_terrain === 1) && (
                  <>
                    <span className="agent-detail-label">{t("path.followTerrain", "地形追従")}</span>
                    <span className="agent-detail-value">
                      {pathData.follow_terrain === 1
                        ? t("path.followTerrainFollow", "する(個別設定)")
                        : t("path.followTerrainNone", "しない(個別設定)")}
                    </span>
                  </>
                )}
              </div>
              {zoneError && (
                <div className="zone-change-error">{zoneError}</div>
              )}
            </section>
          )}

          {/* 物理状態セクション */}
          <PathConditionPanel path={pathData} />

          {/* 交通・速度設定セクション */}
          <PathTrafficPanel
            path={pathData}
            apiBaseUrl={apiBaseUrl}
            onSaved={setPathData}
          />
        </>
      )}
      {templateMode && (
        <button
          className="god-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "god_edit",
              params: { extra: { entityType: "path", entityId: String(pathData.path_id) } },
            });
          }}
        >
          {t("god_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
