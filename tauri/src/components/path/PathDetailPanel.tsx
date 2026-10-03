/**
 * パス詳細パネル
 *
 * window_type="path_detail" のポップアップウィンドウで表示する専用パネル。
 * パスのヘッダー情報(アイコン・ステータス・名前)と各種属性を表示する。
 *
 * 表示項目:
 * - InfoHeader: アイコン・ステータス・名前(名前が空で道路に属していれば「道路名(区間 #id)」)
 * - 街路・高速道路には所属道路の行(road/RoadMembershipRow)
 * - 基本情報タブ:
 *   - 基本情報セクション: 所属地区・地形追従・最小曲線半径・最大勾配と、
 *     耐久度・騒音・大気汚染の状態タイル(PathConditionPanel)
 *   - 交通・速度設定セクション(PathTrafficPanel): 通行方向トグル・制限速度の標識と入力欄・通過速度・
 *     利用率と路上駐車量のドーナツ・車種別交通量
 * - 道路設備タブ: 街灯・柵・街路樹・配電方式
 * - 駅舎には駅情報、計画中・工事中には建設工事タブを表示する
 *
 * アイコンは path_type に応じて paths/path_N.png を使用する。
 * ステータス表示は経済主体・土地区画パネルと共通の InfoHeader を利用する。
 *
 * nearby_zones が API レスポンスに含まれる場合、所属地区をドロップダウンで変更できる。
 * 隣接パスの所属地区のみ選択可能であり、地区の連続性要件を満たす。
 */
import { PLAY_MODE_SCEARIO } from "../../constants";


import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import type { NearbyZoneEntry, PathData } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { sendHighlightObjects, sendClearHighlights } from "../../utils/ipc";
import type { PathInterferenceHighlight } from "../../utils/ipc";
import { usePlayMode } from "../../hooks/usePlayMode";
import { resolvePathIcon } from "../../utils/pathIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import { PATH_TYPES_WITH_ALIGNMENT } from "../../local-constants";
import {
  PATH_STREET,
  PATH_STATION_HOUSE,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
  STATUS_PENDING,
  STATUS_DEMOLISHING,
  ALERT_PATH_CONSTRUCTION_SHORTAGE,
  ALERT_PATH_ACCIDENT,
  ALERT_PATH_NO_STATION_HOUSE,
  VALUE_NONE
} from "../../constants";
import { InfoHeader } from "../common/InfoHeader";
import { TagRow } from "../common/TagRow";
import { isTaggable } from "../../utils/constructionTags";
import { RoadMembershipRow, useRoadList, type PathRoadFields } from "../road/RoadMembershipRow";
import { hasRoad, isNamedRoadPathType, notifyRoadsChanged, roadDisplayName, ROADS_CHANGED_EVENT } from "../road/roadApi";
import { ZoneLink } from "../common/ZoneLink";
import { StationInfoSection } from "./StationInfoSection";
import { PathConditionPanel } from "./PathConditionPanel";
import { PathEquipmentPanel, pathEquipmentFields } from "./PathEquipmentPanel";
import { PathTrafficPanel } from "./PathTrafficPanel";
import { PathOperationPanel } from "./PathOperationPanel";
import { PathTopologyEditPanel } from "./PathTopologyEditPanel";
import type { PathTopologyEditMode } from "./PathTopologyEditPanel";
import { PathConstructionInfoPanel } from "./PathConstructionInfoPanel";
import shortageIcon from "../../assets/images/ui/agent_panel/bribery.png";
import accidentIcon from "../../assets/images/ui/agent_panel/car-accident.png";
// アラートカード・建設スピードの体裁は経済主体パネルと共有する。各パネルは遅延読み込み
// (App.tsx の lazy)でCSSもチャンク単位に分割されるため、共有元を明示的に import しないと
// 当該チャンクが読み込まれていないウィンドウでスタイルが当たらない。
import "../agent/AgentDetailPanel.css";
import "./PathDetailPanel.css";
import rocketIcon from "../../assets/images/ui/rocket.png";

// バックエンド geo_path_alignment.NO_CURVATURE_RADIUS(直線扱いの半径上限)。これ以上は「直線」と表示する。
const STRAIGHT_RADIUS_THRESHOLD = 1.0e6;

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
  const godMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCEARIO;
  const [pathData, setPathData] = useState<(PathData & PathRoadFields) | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selectedTab, setSelectedTab] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  // 建設中の道路の「建設スピード」(utilization_target, ％整数) と建設コスト/期間。
  const [constructionSpeed, setConstructionSpeed] = useState<number>(100);
  const [statusSaving, setStatusSaving] = useState(false);
  // 進行中のパス結合/分割の操作。null 以外の間は3Dビューが対象選択モードになっている。
  const [topologyEditMode, setTopologyEditMode] = useState<PathTopologyEditMode | null>(null);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<PathData & PathRoadFields>;
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

  const isNamedRoad = isNamedRoadPathType(pathData?.path_type);
  const { roads } = useRoadList(apiBaseUrl, isNamedRoad);

  // 所属道路は道路詳細・他のパス詳細からも変わる。パネル全体を取り直すと設備の未保存の入力が
  // 消えるので、変更通知では road_id だけを差し替える。
  const refreshRoadMembership = useCallback(async () => {
    try {
      const data = await fetchApi<PathData & PathRoadFields>(url);
      setPathData((prev) => (prev && prev.path_id === data.path_id ? { ...prev, road_id: data.road_id } : prev));
    } catch (err) {
      console.error("[PathDetailPanel] road membership refresh failed:", err);
    }
  }, [url]);

  useEffect(() => {
    if (!isNamedRoad) return;
    const unlistenPromise = listen(ROADS_CHANGED_EVENT, () => void refreshRoadMembership());
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [isNamedRoad, refreshRoadMembership]);

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

  // 着工前パスを開いている間、干渉する区画・建物・既存パスを3Dビュー上で自動ハイライトする。
  // バックエンドが既に持つ権威データ(GET /paths/{id}/interference)を取得して Unity へ送る。
  // 建設済み/建設中パスは干渉が無意味なので何もしない。閉じる/状態遷移でクリアする。
  useEffect(() => {
    if (!apiBaseUrl || !pathData || !isPreConstruction) return;
    const pathId = pathData.path_id;
    let cancelled = false;
    const controller = new AbortController();
    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${pathId}/interference`, {
      signal: controller.signal,
    })
      .then((res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<PathInterferenceHighlight>;
      })
      .then((data) => {
        if (!cancelled) void sendHighlightObjects(data, "path_detail");
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("[PathDetailPanel] interference fetch failed:", err);
        }
      });
    return () => {
      cancelled = true;
      controller.abort();
      void sendClearHighlights("path_detail");
    };
  }, [apiBaseUrl, pathData, isPreConstruction]);

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

  /**
   * 名称(道路名・駅名)を変更する。パスはすべて市が敷設する公共物なので常に変更できる。
   * 成功後はパス詳細を再取得し、駅情報セクションなど名称を参照する箇所も更新する。
   */
  const handleNameSave = useCallback(async (next: string) => {
    if (!pathData) return;
    await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/paths/{id}", String(pathData.path_id)), {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: next }),
    });
    setRefreshKey((k) => k + 1);
  }, [pathData, apiBaseUrl]);

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
        // 道路詳細の「計画中・工事中の延長」や路線図の破線表示が変わる
        await notifyRoadsChanged(undefined, "path_detail");
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

  const iconSrc = resolvePathIcon(pathData.path_type, pathData.path_variant);

  /** マスターデータからパスバリアントの表示名を取得する */
  const variantName = masterData?.paths[pathData.path_variant]?.display_name;

  // 駅舎パス(=駅)なら駅情報セクション(駅名変更・所属ホーム・発着路線)を表示する。
  const isStationHouse =
    masterData?.paths[pathData.path_variant]?.type === PATH_STATION_HOUSE;

  const hasEquipment = pathEquipmentFields.some(field =>
    masterData?.paths[pathData.path_variant]?.[`${field}_allowed`] || pathData[field] !== 0
  );
  const tabs: { id: string; label: string }[] = [];
  if (!isPreConstruction) {
    tabs.push({ id: "basic", label: t("path.tabs.basic") });
    if (hasEquipment) tabs.push({ id: "equipment", label: t("pathEquipment.title") });
  }
  if (isStationHouse) tabs.push({ id: "station", label: t("station.sectionTitle") });
  if (showConstructionInfo) tabs.push({ id: "construction", label: t("path.tabs.construction") });
  const activeTab = tabs.some(tab => tab.id === selectedTab)
    ? selectedTab
    : (showConstructionInfo && (isPreConstruction || isConstructing) ? "construction" : tabs[0]?.id);

  /**
   * パスの表示名。name(区間名)があればそれ、無ければ所属道路の名前と区間ID、
   * 道路にも属さなければIDから作る。
   */
  const roadId = hasRoad(pathData.road_id) ? pathData.road_id : null;
  const displayName =
    pathData.name !== ""
      ? pathData.name
      : roadId !== null
        ? t("path.titleInRoad", {
          road: roadDisplayName(roads.find((road) => road.road_id === roadId)?.name, roadId, t),
          id: pathData.path_id,
        })
        : t("path.titleWithId", { id: pathData.path_id });

  // 基本情報セクションの表の行。線形指標は形状から算出する種別でしか意味を持たない
  // (それ以外の種別では既定値の「直線・0%」のまま)。
  const showZoneRow = pathData.zone_name != null || canChangeZone;
  const showFollowTerrainRow = pathData.follow_terrain === 0 || pathData.follow_terrain === 1;
  const hasAlignment = PATH_TYPES_WITH_ALIGNMENT.includes(pathData.path_type);
  const minCurveRadius = hasAlignment ? pathData.min_curve_radius ?? null : null;
  const maxGrade = hasAlignment ? pathData.max_grade ?? null : null;
  const hasBasicGrid = showZoneRow || showFollowTerrainRow || minCurveRadius != null || maxGrade != null;
  const hasCondition =
    !!pathData.under_maintenance ||
    pathData.durability != null ||
    pathData.noise != null ||
    pathData.air_pollution != null;

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={iconSrc}
        status={pathData.status}
        variantName={variantName}
        name={displayName}
        constructionProgress={pathData.construction_progress ?? undefined}
        onNameSave={handleNameSave}
        nameValue={pathData.name}
      />

      {/* 建設プロジェクトのタグと、この道路だけを3Dビューで隠す切り替え。
          タグを付け替えられるのは着工前・建設中の道路だけなので、その間だけ行を出す
          (竣工後もタグ自体は残り、建設プロジェクトウィンドウで竣工済みとして数えられる)。 */}
      {isTaggable("path", pathData.status) && (
        <TagRow apiBaseUrl={apiBaseUrl} kind="path" entityId={pathData.path_id} />
      )}

      {/* 区間をどの道路(名前付き道路)に属させるか。道路に属せるのは街路と高速道路だけ。 */}
      {isNamedRoad && (
        <RoadMembershipRow
          apiBaseUrl={apiBaseUrl}
          pathId={pathData.path_id}
          roadId={roadId}
          roads={roads}
          onChanged={(next) =>
            setPathData((prev) => (prev ? { ...prev, road_id: next ?? VALUE_NONE } : prev))
          }
        />
      )}

      <PathOperationPanel
        path={pathData}
        apiBaseUrl={apiBaseUrl}
        onActionPerformed={() => setRefreshKey((k) => k + 1)}
        topologyEditMode={topologyEditMode}
        onStartTopologyEdit={(mode) =>
          // 同じボタンをもう一度押したら操作をやめる(3Dビューも通常モードへ戻る)。
          setTopologyEditMode((current) => (current === mode ? null : mode))
        }
      />

      {/* 結合・分割の操作中: 3Dビューでの対象選択と長さの確認・確定を担うセクション。
          モードを切り替えたときに選択状態を持ち越さないよう key でリマウントする。 */}
      {topologyEditMode && (
        <PathTopologyEditPanel
          key={topologyEditMode}
          apiBaseUrl={apiBaseUrl}
          path={pathData}
          mode={topologyEditMode}
          onClose={() => setTopologyEditMode(null)}
          onCompleted={() => setRefreshKey((k) => k + 1)}
        />
      )}

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

      {tabs.length > 0 && (
        <div className="agent-detail-tabs path-detail-tabs">
          {tabs.map(tab => (
            <button key={tab.id} type="button"
              className={`agent-detail-tab${activeTab === tab.id ? " agent-detail-tab--active" : ""}`}
              aria-pressed={activeTab === tab.id}
              aria-controls={`path-detail-${tab.id}`}
              onClick={() => setSelectedTab(tab.id)}>
              {tab.label}
            </button>
          ))}
        </div>
      )}

      {/* hiddenで表示だけを切り替え、設備の未保存の入力と制限速度の反映待ちの入力を保持する。 */}
      {!isPreConstruction && (
        <>
          <div id="path-detail-basic" hidden={activeTab !== "basic"}>
            <div className="agent-detail-tab-content">
              {/* 基本情報セクション: 所属地区・線形指標の表と、耐久度・騒音・大気汚染の状態タイル */}
              {(hasBasicGrid || hasCondition) && (
                <section className="agent-detail-section">
                  <h3 className="agent-detail-section-title">{t("path.tabs.basic")}</h3>
                  {hasBasicGrid && (
                    <div className="agent-detail-grid">
                      {showZoneRow && (
                        <>
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
                        </>
                      )}
                      {/* 地形追従の個別設定。バリアント既定を上書きしている(0/1)ときだけ表示する。 */}
                      {showFollowTerrainRow && (
                        <>
                          <span className="agent-detail-label">{t("path.followTerrain", "地形追従")}</span>
                          <span className="agent-detail-value">
                            {pathData.follow_terrain === 1
                              ? t("path.followTerrainFollow", "する(個別設定)")
                              : t("path.followTerrainNone", "しない(個別設定)")}
                          </span>
                        </>
                      )}
                      {minCurveRadius != null && (
                        <>
                          <span className="agent-detail-label">{t("path.minCurveRadius")}</span>
                          <span className="agent-detail-value">
                            {minCurveRadius >= STRAIGHT_RADIUS_THRESHOLD
                              ? t("path.straightLine")
                              : `${Math.round(minCurveRadius)} m`}
                          </span>
                        </>
                      )}
                      {maxGrade != null && (
                        <>
                          <span className="agent-detail-label">{t("path.maxGrade")}</span>
                          <span className="agent-detail-value">{maxGrade.toFixed(1)} %</span>
                        </>
                      )}
                    </div>
                  )}
                  {zoneError && (
                    <div className="zone-change-error">{zoneError}</div>
                  )}
                  <PathConditionPanel path={pathData} apiBaseUrl={apiBaseUrl} />
                </section>
              )}

              <PathTrafficPanel
                key={pathData.path_id}
                path={pathData}
                apiBaseUrl={apiBaseUrl}
                onSaved={setPathData}
              />
            </div>
          </div>
          {hasEquipment && (
            <div id="path-detail-equipment" hidden={activeTab !== "equipment"}>
              <PathEquipmentPanel path={pathData} apiBaseUrl={apiBaseUrl} onSaved={setPathData} />
            </div>
          )}
        </>
      )}
      {isStationHouse && (
        <div id="path-detail-station" hidden={activeTab !== "station"}>
          <StationInfoSection apiBaseUrl={apiBaseUrl} pathId={pathData.path_id}
            passageMode={pathData.passage_mode}
            pathName={displayName} onRenamed={() => setRefreshKey(k => k + 1)} />
        </div>
      )}
      {showConstructionInfo && (
        <div id="path-detail-construction" hidden={activeTab !== "construction"}>
          <PathConstructionInfoPanel path={pathData} apiBaseUrl={apiBaseUrl} refreshKey={refreshKey} />
        </div>
      )}
      {godMode && (
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
