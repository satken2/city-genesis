/**
 * 路線詳細パネル
 *
 * window_type="transit_detail" の画面右側ポップアップで表示する専用パネル。
 * 経済主体詳細(AgentDetailPanel)・パス詳細(PathDetailPanel)と同じ右パネルの体裁で、
 * 1つの公共交通路線の詳細(種別・車両数・運賃・運行間隔・運行実績・区間パス)を表示する。
 *
 * データは GET /api/v1/transit/lines/{id} から取得する。運賃・運行間隔は編集して
 * PATCH /api/v1/transit/lines/{id} で保存でき、路線の削除も行える。
 * 削除・保存後は "transit:lines_changed" イベントで公共交通ウィンドウに再取得を促す。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit } from "@tauri-apps/api/event";
import { buildApiUrl } from "../../utils/api";
import { InfoHeader } from "../common/InfoHeader";
import { NumberStepper } from "../common/NumberStepper";
import { UsageMeter } from "../statistics/tabs/AccidentsTab";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { STATUS_ACTIVE } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { sendChangeDisplay } from "../../utils/ipc";
import type { TransitMasterEntry } from "../../types";
import { TransitLineMap } from "./TransitLineMap";
// 右パネルの体裁(セクション・ラベル/値グリッド)は経済主体詳細パネルと共有する。
import "../agent/AgentDetailPanel.css";
import "./TransitDetailPanel.css";

type TransitSegment = {
  path_id: number;
  name: string | null;
  status: number;
};

/**
 * 停留所ノード。路線図上の「駅」として描画する単位。順序はバックエンド側で
 * 区間パスのトポロジーから復元したもの(order=0 が始点、order=last が終点)。
 * name は将来の停留所名表示用で、現状はノードに名称を持たないため null。
 */
export type TransitStop = {
  node_id: number;
  order: number;
  /** [x, y, z] のワールド座標。UI 側では X-Z 平面として扱う */
  position: [number, number, number];
  name: string | null;
};

/**
 * 路線の1区間(停留所間を結ぶ PATH_TRANSPORT)。路線図上の「線」として描画する単位。
 * travel_direction はバックエンド consts.py の DIR_* と同じ値:
 *   0 (DIR_BOTH)     = 双方向(バス等。1本の道路を往復運行)
 *   1 (DIR_FORWARD)  = start→end 片方向(複線の往路側)
 *   2 (DIR_BACKWARD) = end→start 片方向(複線の復路側)
 * UI はこれで進行方向の矢印を描き分ける。
 */
export type TransitEdge = {
  path_id: number;
  from_node_id: number;
  to_node_id: number;
  travel_direction: number;
  status: number;
};

type TransitLineDetail = {
  line_agent_id: number;
  name: string | null;
  path_variant: number;
  status: number;
  fare_price: number;
  target_headway: number;
  // 1編成の車両数(プレイヤー設定)と編成数(=ceil(1周時間/headway)。バックエンドが算出する派生値。
  // 初回シミュレーション前は 0)。
  cars_per_train: number;
  train_count: number;
  vehicle_count: number;
  required_vehicle_count: number;
  revenues: number;
  expenses: number;
  ridership: number;
  avg_utilization: number;
  path_count: number;
  // 運休理由コード(null / "route_disconnected" / "return_leg_blocked" / "no_underlay" / "train_too_long")。
  pause_reason: string | null;
  segments: TransitSegment[];
  // 路線図描画用の停留所(順序付き)と区間エッジ。GET /api/v1/transit/lines/{id} が返す。
  stops: TransitStop[];
  edges: TransitEdge[];
};

// 運休理由コード → i18n キー。未知コードは汎用文言にフォールバックする。
const PAUSE_REASON_KEYS: Record<string, string> = {
  return_leg_blocked: "transit.pauseReason.returnLegBlocked",
  route_disconnected: "transit.pauseReason.routeDisconnected",
  no_underlay: "transit.pauseReason.noUnderlay",
  train_too_long: "transit.pauseReason.trainTooLong",
};

type TransitDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

export function TransitDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: TransitDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [line, setLine] = useState<TransitLineDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  // 編集中の運賃・運行間隔・1編成の車両数
  const [fare, setFare] = useState<number>(0);
  const [headway, setHeadway] = useState<number>(1);
  const [carsPerTrain, setCarsPerTrain] = useState<number>(1);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  // 詳細パネルを開いている間は公共交通の表示をONにする(全種別を表示)。
  // 表示中の路線の区間パスがグラフィック上で見えるようにするため。
  useEffect(() => {
    if (!masterData) return;
    const allIds = Object.values(masterData.transit)
      .map((e) => (e as TransitMasterEntry).transit_path_id)
      .join(",");
    void sendChangeDisplay("transit", "default", allIds);
  }, [masterData]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<TransitLineDetail>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        setLine(data);
        setFare(Math.round(data.fare_price));
        setHeadway(Math.round(data.target_headway));
        setCarsPerTrain(Math.max(1, Math.round(data.cars_per_train ?? 1)));
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

  const isDirty =
    line != null &&
    (fare !== Math.round(line.fare_price) ||
      headway !== Math.round(line.target_headway) ||
      carsPerTrain !== Math.max(1, Math.round(line.cars_per_train ?? 1)));

  const handleSave = useCallback(async () => {
    if (!line) return;
    setSaving(true);
    try {
      const res = await fetch(
        buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${line.line_agent_id}`),
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ fare_price: fare, target_headway: headway, cars_per_train: carsPerTrain }),
        },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await emit("transit:lines_changed", {});
      setRefreshKey((k) => k + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save");
    } finally {
      setSaving(false);
    }
  }, [apiBaseUrl, line, fare, headway]);

  const handleDelete = useCallback(async () => {
    if (!line) return;
    setDeleting(true);
    try {
      const res = await fetch(
        buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${line.line_agent_id}`),
        { method: "DELETE" },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await emit("transit:lines_changed", {});
      await invoke("close_current_window");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete");
      setDeleting(false);
    }
  }, [apiBaseUrl, line]);

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }
  if (error || !line) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  // 種別名は path_master の当該バリアントの表示名を使う。
  const variantName = masterData?.paths[line.path_variant]?.display_name ?? "";
  const displayName = line.name && line.name !== "" ? line.name : `#${line.line_agent_id}`;
  // 運行状態は路線エージェントの status で判定する(ヘッダの status 表示と一致させる)。
  // 車両不足は基本情報の「調達/必要」表記と運行実績の調達率メーターで示す。
  const running = line.status === STATUS_ACTIVE;
  // 運休時は理由を表示する。理由コードが無い/未知の運休(車両不足など)は汎用文言にする。
  const pauseReasonText = running
    ? null
    : t(
        (line.pause_reason && PAUSE_REASON_KEYS[line.pause_reason]) ||
          "transit.pauseReason.generic",
      );
  const procurementRatio =
    line.required_vehicle_count > 0
      ? line.vehicle_count / line.required_vehicle_count
      : null;

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={resolveTransitIcon(line.path_variant)}
        status={line.status}
        variantName={variantName}
        name={displayName}
      />

      {/* 基本情報 */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("transit.basicInfo")}</h3>
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("transit.colName")}</span>
          <span className="agent-detail-value">{variantName}</span>
          <span className="agent-detail-label">{t("transit.status")}</span>
          <span className="agent-detail-value">{running ? t("transit.running") : t("transit.suspended")}</span>
          <span className="agent-detail-label">{t("transit.colVehicles")}</span>
          <span className="agent-detail-value">
            {procurementRatio === null
              ? Math.round(line.vehicle_count)
              : `${Math.round(line.vehicle_count)} / ${Math.round(line.required_vehicle_count)}`}
          </span>
          <span className="agent-detail-label">{t("transit.stopCount")}</span>
          <span className="agent-detail-value">{line.path_count}</span>
        </div>
        {pauseReasonText && (
          <p className="transit-detail__pause-reason" role="status">
            {pauseReasonText}
          </p>
        )}
        {/* 運休中または車両不足のときだけ、原因を掘り下げる診断ウィンドウへの導線を出す。
            路線詳細のIDは路線エージェントの agent_id と同じなので、そのまま診断対象にできる。 */}
        {(!running || (procurementRatio !== null && procurementRatio < 1)) && (
          <button
            type="button"
            className="transit-detail__diagnose-btn"
            onClick={() => {
              void invoke("open_registered_window", {
                windowType: "diagnosis",
                entityId: initialEntityId,
              });
            }}
          >
            {t("transit.diagnose")}
          </button>
        )}
      </section>

      {/* 運賃・運行間隔の設定 */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("transit.settings")}</h3>
        <div className="agent-detail-grid" style={{ alignItems: "center", rowGap: 8 }}>
          <span className="agent-detail-label">{t("transit.fare")}</span>
          <span className="agent-detail-value">
            <NumberStepper value={fare} onChange={setFare} min={0} max={100000} step={10} integerOnly={true} />
          </span>
          <span className="agent-detail-label">{t("transit.headway")}</span>
          <span className="agent-detail-value">
            <NumberStepper value={headway} onChange={setHeadway} min={1} max={120} step={1} integerOnly={true} />
          </span>
          <span className="agent-detail-label">{t("transit.carsPerTrain")}</span>
          <span className="agent-detail-value">
            <NumberStepper value={carsPerTrain} onChange={setCarsPerTrain} min={1} max={20} step={1} integerOnly={true} />
          </span>
          {/* 編成数はバックエンドが運行間隔と路線長から算出する派生値(読み取り専用表示)。 */}
          <span className="agent-detail-label">{t("transit.trainCount")}</span>
          <span className="agent-detail-value">
            {Math.round(line.train_count ?? 0).toLocaleString()} {t("transit.trainsUnit")}
          </span>
        </div>
        <button
          type="button"
          className="transit-detail__save-btn"
          disabled={!isDirty || saving}
          onClick={() => void handleSave()}
        >
          {saving ? t("common.processing") : t("transit.save")}
        </button>
      </section>

      {/* 運行実績 */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("transit.performance")}</h3>
        <div className="agent-detail-grid" style={{ alignItems: "center" }}>
          <span className="agent-detail-label">{t("transit.colRidership")}</span>
          <span className="agent-detail-value">{Math.round(line.ridership).toLocaleString()}</span>
          <span className="agent-detail-label">{t("transit.colRevenues")}</span>
          <span className="agent-detail-value">{Math.round(line.revenues).toLocaleString()}</span>
          <span className="agent-detail-label">{t("transit.vehicleCost")}</span>
          <span className="agent-detail-value">{Math.round(line.expenses).toLocaleString()}</span>
          <span className="agent-detail-label">{t("transit.colUtilization")}</span>
          <span className="agent-detail-value">
            <UsageMeter usage_ratio={line.avg_utilization} />
          </span>
          <span className="agent-detail-label">{t("transit.vehicleProcurement")}</span>
          <span className="agent-detail-value">
            {procurementRatio === null ? (
              "—"
            ) : (
              <UsageMeter usage_ratio={procurementRatio} goodHigh />
            )}
          </span>
        </div>
      </section>

      {/* 路線図 */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">
          {t("transit.lineMap")}
          <span style={{ marginLeft: 6, opacity: 0.7 }}>({line.stops.length})</span>
        </h3>
        {line.stops.length >= 2 ? (
          <TransitLineMap
            apiBaseUrl={apiBaseUrl}
            stops={line.stops}
            edges={line.edges}
            pathVariant={line.path_variant}
            running={running}
          />
        ) : (
          <p className="agent-detail-muted">{t("transit.noStops")}</p>
        )}
      </section>

      {/* 削除 */}
      <section className="agent-detail-section">
        <button
          type="button"
          className="transit-detail__delete-btn"
          disabled={deleting}
          onClick={() => void handleDelete()}
        >
          {deleting ? t("common.processing") : t("transit.delete")}
        </button>
      </section>
    </div>
  );
}
