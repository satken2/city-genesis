/**
 * 路線詳細パネル
 *
 * window_type="transit_detail" の画面右側ポップアップで表示する専用パネル。
 * 経済主体詳細(AgentDetailPanel)と同じ右パネルの体裁・タブ構成で、1つの公共交通路線を扱う。
 *
 * タブ:
 *   - 基本情報 ... 種別・車両の外観・状態・車両・編成・区間数と、運行実績(乗車回数・収支・混雑率・調達率)
 *   - 設定     ... 運賃・運行間隔・1編成の車両数・路線カラー・車両の外観(PATCH で保存)、経路の編集、路線の廃止。
 *                運賃・運行間隔・車両数の直下に、編集中の値での収支見込み(TransitEstimateCard)を出す。
 *                経路(停留所・経由点の巡回順)の編集は新設パネル(transit_creation)を経路編集モードで開いて行う
 *   - 路線地図 ... 実地図上の路線(TransitLineMap。共通地図の公共交通路線レイヤーを当該路線に注目させたもの)
 *   - 路線図   ... 縦型ストリップ路線図(TransitLineDiagram)
 *
 * 供給図は基本情報タブの先頭、運休・車両不足の警告はヘッダ直下に置く。
 * データは GET /api/v1/transit/lines/{id} から取得し、設定の保存は PATCH、廃止は DELETE。
 * 保存・削除後は "transit:lines_changed" で公共交通ウィンドウに再取得を促し(経路編集パネルが経路を
 * 更新したときも同じ通知を受けて取り直す)、
 * Unityの共有路線データは定期取得で更新し、ユーザーの表示設定は維持する。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { buildApiUrl } from "../../utils/api";
import { InfoHeader } from "../common/InfoHeader";
import { NumberStepper } from "../common/NumberStepper";
import { ColorSwatchPicker } from "../common/ColorSwatchPicker";
import { Meter } from "../common/Meter";
import { resolveTransitIcon } from "../../utils/pathIcon";
import { STATUS_ACTIVE } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { TRANSIT_LINE_COLOR_PRESETS, resolveTransitColor } from "../../local-constants";
import { TransitLineMap } from "./TransitLineMap";
import { TransitLineDiagram } from "./TransitLineDiagram";
import { TransitFleetSection } from "./TransitFleetSection";
import { TransitVehicleLabel, TransitVehicleSelector, transitVehicleOptions } from "./TransitVehicleSelector";
import { pauseReasonKey } from "./transitPauseReason";
import { TransitEstimateCard } from "./TransitEstimateCard";
import { useTransitEstimate } from "../../hooks/useTransitEstimate";
import { SupplyChainSection } from "../supply/SupplyChainSection";
import { useSupplyGraph } from "../../hooks/useSupplyGraph";
// 右パネルの体裁(セクション・ラベル/値グリッド・タブ)は経済主体詳細パネルと共有する。
import "../agent/AgentDetailPanel.css";
// 運行状態バッジ(.transit-badge)は公共交通ウィンドウの一覧と共通。
import "./TransitManagementWindow.css";
import "./TransitDetailPanel.css";

type TransitSegment = {
  path_id: number;
  name: string | null;
  status: number;
};

/**
 * 停留所ノード。路線図上の「駅」として描画する単位。巡回順(1周で2回訪れる停留所は最初の訪問)に
 * 1回ずつ並び、order はその順番。
 * name は停留所が乗る下地パスから解決した表示名(駅舎所属なら駅名、街路なら道路名)で、
 * 既定名のままの無名停留所は null(UI が連番で補う)。house_path_id は駅名の出所の駅舎パス。
 */
export type TransitStop = {
  node_id: number;
  order: number;
  /** [x, y, z] のワールド座標。UI 側では X-Z 平面として扱う */
  position: [number, number, number];
  name: string | null;
  station_path_id: number | null;
  house_path_id: number | null;
};

/**
 * 路線の1区間(巡回順で隣り合う停留所を結ぶ PATH_TRANSPORT)。路線図上の「線」として描画する単位。
 * 区間パスは路線の進行方向の一方通行で、from_node_id → to_node_id が進む向き(travel_direction は
 * DIR_FORWARD)。下地の線路の通行方向とは別物。路線図は status(運休区間の破線)にだけ使う。
 */
/**
 * 路線の巡回点(停留所・経由点を巡回順に並べたもの。最後の点の次は先頭の点)。
 * turnback は車両がその停留所で折り返すか(往復運行の端。運休中は false)。経由点の node_id は null。
 */
export type TransitRoutePointSummary = {
  kind: "stop" | "waypoint";
  position: [number, number, number];
  node_id: number | null;
  turnback: boolean;
};

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
  // 路線カラー("#rrggbb")。空文字は未設定で、種別ごとの既定色にフォールバックする。
  line_color: string;
  vehicle_model: number; // 車両の外観番号(描画専用。候補は TransitVehicleSelector のカタログ)
  vehicle_count: number;
  required_vehicle_count: number;
  revenues: number;
  expenses: number;
  ridership: number;
  avg_utilization: number;
  path_count: number;
  // 運休理由コード(null / "route_disconnected" / "no_underlay" / "train_too_long")。
  pause_reason: string | null;
  segments: TransitSegment[];
  // 路線図描画用の停留所(巡回順に1回ずつ)・巡回点・区間エッジ。GET /api/v1/transit/lines/{id} が返す。
  stops: TransitStop[];
  route_points: TransitRoutePointSummary[];
  edges: TransitEdge[];
};

type TabId = "basic" | "settings" | "map" | "diagram";

type TransitDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

/** 路線カラーを解決する。未設定(空)の路線は種別ごとの既定色を使う */
function effectiveLineColor(line: TransitLineDetail): string {
  return line.line_color && line.line_color !== "" ? line.line_color : resolveTransitColor(line.path_variant);
}

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
  const [activeTab, setActiveTab] = useState<TabId>("basic");
  const supplyGraph = useSupplyGraph(apiBaseUrl, initialEntityId, true);

  // 編集中の運賃・運行間隔・1編成の車両数・路線カラー・車両の外観
  const [fare, setFare] = useState<number>(0);
  const [headway, setHeadway] = useState<number>(1);
  const [carsPerTrain, setCarsPerTrain] = useState<number>(1);
  const [lineColor, setLineColor] = useState<string>("");
  const [vehicleModel, setVehicleModel] = useState<number>(0);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);
  // 廃止は2段階確認(1回目のクリックで確認行を出し、2回目で実行する)。
  const [confirmDelete, setConfirmDelete] = useState(false);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

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
        setLineColor(effectiveLineColor(data));
        setVehicleModel(data.vehicle_model ?? 0);
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

  // 経路編集パネルなど、他のウィンドウで路線が変わったら取り直す。
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen("transit:lines_changed", () => setRefreshKey((k) => k + 1))
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);
    return () => {
      unlisten?.();
    };
  }, []);

  /** 新設パネルを経路編集モードで開き、この路線の停留所・経由点を3Dマップ上で編集させる */
  const openRouteEditor = useCallback(() => {
    if (!line) return;
    void invoke("open_registered_window", {
      windowType: "transit_creation",
      params: { extra: { editLineId: line.line_agent_id, titleKey: "windowType.transit_route_edit" } },
    });
  }, [line]);

  const isDirty =
    line != null &&
    (fare !== Math.round(line.fare_price) ||
      headway !== Math.round(line.target_headway) ||
      carsPerTrain !== Math.max(1, Math.round(line.cars_per_train ?? 1)) ||
      lineColor !== effectiveLineColor(line) ||
      vehicleModel !== (line.vehicle_model ?? 0));

  const resetEdits = useCallback(() => {
    if (!line) return;
    setFare(Math.round(line.fare_price));
    setHeadway(Math.round(line.target_headway));
    setCarsPerTrain(Math.max(1, Math.round(line.cars_per_train ?? 1)));
    setLineColor(effectiveLineColor(line));
    setVehicleModel(line.vehicle_model ?? 0);
    setSaveError(null);
  }, [line]);

  const handleSave = useCallback(async () => {
    if (!line) return;
    setSaving(true);
    setSaveError(null);
    try {
      const res = await fetch(
        buildApiUrl(apiBaseUrl, `/api/v1/transit/lines/${line.line_agent_id}`),
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            fare_price: fare,
            target_headway: headway,
            cars_per_train: carsPerTrain,
            line_color: lineColor,
            vehicle_model: vehicleModel,
          }),
        },
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      await emit("transit:lines_changed", {});
      setRefreshKey((k) => k + 1);
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Failed to save");
    } finally {
      setSaving(false);
    }
  }, [apiBaseUrl, line, fare, headway, carsPerTrain, lineColor, vehicleModel]);

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
      setSaveError(err instanceof Error ? err.message : "Failed to delete");
      setDeleting(false);
      setConfirmDelete(false);
    }
  }, [apiBaseUrl, line]);

  // 収支見込みは設定タブを開いている間だけ、保存前の編集中の値で取り直す(保存してから採算に気づく手戻りを防ぐ)。
  const { estimate, loading: estimateLoading, error: estimateError } = useTransitEstimate(
    apiBaseUrl,
    line != null && activeTab === "settings"
      ? {
          kind: "line",
          lineAgentId: line.line_agent_id,
          farePrice: fare,
          targetHeadway: headway,
          carsPerTrain,
        }
      : null,
    refreshKey,
  );

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
  const pauseReasonText = running ? null : t(pauseReasonKey(line.pause_reason));
  const procurementRatio =
    line.required_vehicle_count > 0
      ? line.vehicle_count / line.required_vehicle_count
      : null;
  const vehicleShortage = running && procurementRatio !== null && procurementRatio < 1;
  const savedColor = effectiveLineColor(line);
  const balance = line.revenues - line.expenses;
  // 収支見込みが保存済みの設定ではなく編集中の値によるものかを見出しに示す(路線カラー・外観は採算に関係しない)。
  const estimateEdited =
    fare !== Math.round(line.fare_price) ||
    headway !== Math.round(line.target_headway) ||
    carsPerTrain !== Math.max(1, Math.round(line.cars_per_train ?? 1));
  // 外観の候補を持たない種別(カタログ未登録)は、外観の行ごと出さない。
  const hasVehicleOptions = transitVehicleOptions(line.path_variant).length > 0;
  const freight = Object.values(masterData?.transit ?? {}).some(
    (entry) => entry.transit_path_id === line.path_variant && entry.freight,
  );

  const tabs: { id: TabId; label: string }[] = [
    { id: "basic", label: t("transit.tabBasicInfo") },
    { id: "settings", label: t("transit.tabSettings") },
    { id: "map", label: t("transit.tabLineMap") },
    { id: "diagram", label: t("transit.tabLineDiagram") },
  ];

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={resolveTransitIcon(line.path_variant)}
        status={line.status}
        variantName={variantName}
        name={displayName}
      />
      {/* 路線カラーの帯。ヘッダ直下に敷いて、どのタブでも路線の色が分かるようにする。
          運休中は帯を薄くして状態を補強する。 */}
      <div
        className={`transit-detail__stripe${running ? "" : " transit-detail__stripe--suspended"}`}
        style={{ background: savedColor }}
        aria-hidden="true"
      />

      {(pauseReasonText || vehicleShortage) && <div className="transit-detail__notice" role="status">
        <span className="transit-detail__notice-text">{pauseReasonText ?? t("transit.vehicleShortage")}</span>
      </div>}

      <div className="agent-detail-tabs">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            className={[
              "agent-detail-tab",
              tab.id === activeTab && "agent-detail-tab--active",
            ]
              .filter(Boolean)
              .join(" ")}
            onClick={() => setActiveTab(tab.id)}
          >
            {tab.label}
            {/* 設定タブに未保存の変更があることを、タブを離れていても点で示す */}
            {tab.id === "settings" && isDirty && <span className="transit-detail__tab-dot" />}
          </button>
        ))}
      </div>

      <div className="agent-detail-tab-content">
        {activeTab === "basic" && (
          <>
            <SupplyChainSection {...supplyGraph} />
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("transit.basicInfo")}</h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("transit.colName")}</span>
                <span className="agent-detail-value">{variantName}</span>
                {hasVehicleOptions && (
                  <>
                    <span className="agent-detail-label">{t("transit.vehicleModel")}</span>
                    <span className="agent-detail-value">
                      <TransitVehicleLabel pathVariant={line.path_variant} value={line.vehicle_model ?? 0} />
                    </span>
                  </>
                )}
                <span className="agent-detail-label">{t("transit.status")}</span>
                <span className="agent-detail-value">
                  <span className={`transit-badge ${running ? "transit-badge--running" : "transit-badge--suspended"}`}>
                    {running ? t("transit.running") : t("transit.suspended")}
                  </span>
                </span>
                <span className="agent-detail-label">{t("transit.colVehicles")}</span>
                <span className="agent-detail-value">
                  {procurementRatio === null
                    ? Math.round(line.vehicle_count)
                    : `${Math.round(line.vehicle_count)} / ${Math.round(line.required_vehicle_count)}`}
                  <span className="transit-detail__unit">{t("transit.carsUnit")}</span>
                </span>
                {/* 編成数はバックエンドが運行間隔と路線長から算出する派生値(読み取り専用)。 */}
                <span className="agent-detail-label">{t("transit.trainCount")}</span>
                <span className="agent-detail-value">
                  {Math.round(line.train_count ?? 0).toLocaleString()}
                  <span className="transit-detail__unit">{t("transit.trainsUnit")}</span>
                  <span className="transit-detail__hint">
                    {t("transit.carsPerTrainShort", { n: Math.max(1, Math.round(line.cars_per_train ?? 1)) })}
                  </span>
                </span>
                <span className="agent-detail-label">{t("transit.stopsCount")}</span>
                <span className="agent-detail-value">{line.stops.length}</span>
                <span className="agent-detail-label">{t("transit.stopCount")}</span>
                <span className="agent-detail-value">{line.path_count}</span>
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("transit.performance")}</h3>
              {/* 前エポックの実績をタイルで並べる。収支だけは符号で色を変える */}
              <div className="transit-detail__tiles">
                <StatTile label={t("transit.colRidership")} value={Math.round(line.ridership).toLocaleString()} />
                <StatTile
                  label={t("transit.colRevenues")}
                  value={Math.round(line.revenues).toLocaleString()}
                  unit={t("transit.currencyUnit")}
                />
                <StatTile
                  label={t("transit.vehicleCost")}
                  value={Math.round(line.expenses).toLocaleString()}
                  unit={t("transit.currencyUnit")}
                />
                <StatTile
                  label={t("transit.balance")}
                  value={`${balance > 0 ? "+" : ""}${Math.round(balance).toLocaleString()}`}
                  unit={t("transit.currencyUnit")}
                  tone={balance < 0 ? "negative" : balance > 0 ? "positive" : undefined}
                />
              </div>
              <div className="agent-detail-grid" style={{ alignItems: "center" }}>
                {/* 混雑率は高いほど悪い指標、車両調達率は高いほど良い指標。
                    共通メーターの polarity で色の対応を切り替える。 */}
                <span className="agent-detail-label">{t("transit.colUtilization")}</span>
                <span className="agent-detail-value transit-detail__meter">
                  <Meter value={line.avg_utilization} polarity="negative" />
                  <span className="transit-detail__meter-value">{formatPercent(line.avg_utilization)}</span>
                </span>
                <span className="agent-detail-label">{t("transit.vehicleProcurement")}</span>
                <span className="agent-detail-value transit-detail__meter">
                  {procurementRatio === null ? (
                    "—"
                  ) : (
                    <>
                      <Meter value={procurementRatio} />
                      <span className="transit-detail__meter-value">{formatPercent(procurementRatio)}</span>
                    </>
                  )}
                </span>
              </div>
            </section>

            <TransitFleetSection
              carsPerTrain={line.cars_per_train}
              vehicleCount={line.vehicle_count}
              requiredTrains={line.train_count}
              color={savedColor}
            />
          </>
        )}

        {activeTab === "settings" && (
          <>
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("transit.tabSettings")}</h3>
              <div className="agent-detail-grid transit-detail__settings-grid">
                <span className="agent-detail-label">{t("transit.fare")}</span>
                <span className="agent-detail-value">
                  <NumberStepper value={fare} onChange={setFare} min={0} max={100000} step={10} integerOnly={true} />
                  <span className="transit-detail__unit">{t("transit.currencyUnit")}</span>
                </span>
                <span className="agent-detail-label">{t("transit.headway")}</span>
                <span className="agent-detail-value">
                  <NumberStepper value={headway} onChange={setHeadway} min={1} max={120} step={1} integerOnly={true} />
                  <span className="transit-detail__unit">{t("transit.minutesUnit")}</span>
                </span>
                <span className="agent-detail-label">{t("transit.carsPerTrain")}</span>
                <span className="agent-detail-value">
                  <NumberStepper value={carsPerTrain} onChange={setCarsPerTrain} min={freight ? 2 : 1} max={20} step={1} integerOnly={true} />
                  <span className="transit-detail__unit">{t("transit.carsUnit")}</span>
                </span>
                {freight && <p className="transit-detail__note">{t("transit.freightConsistNote")}</p>}
                {/* 採算に効くのは上の3項目だけなので、その直下で変更のたびに試算し直す */}
                <div className="transit-detail__estimate">
                  <div className="transit-detail__estimate-title">
                    {t("transit.estimate.title")}
                    {estimateEdited && (
                      <span className="transit-detail__estimate-tag">{t("transit.estimate.editedTag")}</span>
                    )}
                  </div>
                  <TransitEstimateCard estimate={estimate} loading={estimateLoading} error={estimateError} />
                </div>
                <span className="agent-detail-label">{t("transit.lineColor")}</span>
                <span className="agent-detail-value transit-detail__color-value">
                  <ColorSwatchPicker value={lineColor} onChange={setLineColor} presets={TRANSIT_LINE_COLOR_PRESETS} />
                </span>
                {hasVehicleOptions && (
                  <>
                    <span className="agent-detail-label">{t("transit.vehicleModel")}</span>
                    <span className="agent-detail-value">
                      <TransitVehicleLabel pathVariant={line.path_variant} value={vehicleModel} />
                    </span>
                    <div className="transit-detail__vehicle-models">
                      <TransitVehicleSelector
                        pathVariant={line.path_variant}
                        value={vehicleModel}
                        onChange={setVehicleModel}
                        disabled={saving}
                      />
                      <p className="transit-detail__note">{t("transit.vehicleModelNote")}</p>
                    </div>
                  </>
                )}
              </div>

              {/* 保存ツールバー: 左に未保存の注記、右に「元に戻す」「保存」 */}
              <div className="transit-detail__toolbar">
                <span className={`transit-detail__dirty${isDirty ? " transit-detail__dirty--on" : ""}`}>
                  {isDirty ? t("transit.unsavedChanges") : t("transit.allSaved")}
                </span>
                <div className="transit-detail__actions">
                  <button
                    type="button"
                    className="transit-detail__btn transit-detail__btn--ghost"
                    disabled={!isDirty || saving}
                    onClick={resetEdits}
                  >
                    {t("transit.discardChanges")}
                  </button>
                  <button
                    type="button"
                    className="transit-detail__btn transit-detail__btn--primary"
                    disabled={!isDirty || saving}
                    onClick={() => void handleSave()}
                  >
                    {saving ? t("common.processing") : t("transit.save")}
                  </button>
                </div>
              </div>
              {saveError && <p className="transit-detail__error">{saveError}</p>}
            </section>

            {/* 経路(停留所・経由点の巡回順)の編集。3Dマップ上で行うので、編集パネルを開くボタンだけを置く */}
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">{t("transit.routeSection")}</h3>
              <div className="transit-detail__route-row">
                <span className="transit-detail__route-desc">
                  {t("transit.routeSummary", {
                    stops: line.route_points.filter((p) => p.kind === "stop").length,
                    waypoints: line.route_points.filter((p) => p.kind === "waypoint").length,
                  })}
                </span>
                <button type="button" className="transit-detail__btn transit-detail__btn--ghost" onClick={openRouteEditor}>
                  {t("transit.editRoute")}
                </button>
              </div>
            </section>

            {/* 路線の廃止。誤操作を避けるため設定タブの末尾に控えめに置き、2段階で確認する */}
            <section className="agent-detail-section transit-detail__danger">
              <div className="transit-detail__danger-row">
                <div className="transit-detail__danger-text">
                  <span className="transit-detail__danger-title">{t("transit.deleteTitle")}</span>
                  <span className="transit-detail__danger-desc">{t("transit.deleteDescription")}</span>
                </div>
                {confirmDelete ? (
                  <div className="transit-detail__actions">
                    <button
                      type="button"
                      className="transit-detail__btn transit-detail__btn--ghost"
                      disabled={deleting}
                      onClick={() => setConfirmDelete(false)}
                    >
                      {t("transit.cancel")}
                    </button>
                    <button
                      type="button"
                      className="transit-detail__btn transit-detail__btn--danger-solid"
                      disabled={deleting}
                      onClick={() => void handleDelete()}
                    >
                      {deleting ? t("common.processing") : t("transit.deleteConfirm")}
                    </button>
                  </div>
                ) : (
                  <button
                    type="button"
                    className="transit-detail__btn transit-detail__btn--danger"
                    onClick={() => setConfirmDelete(true)}
                  >
                    {t("transit.delete")}
                  </button>
                )}
              </div>
            </section>
          </>
        )}

        {activeTab === "map" && (
          <section className="agent-detail-section">
            {line.stops.length >= 2 ? (
              <TransitLineMap
                apiBaseUrl={apiBaseUrl}
                lineAgentId={line.line_agent_id}
                stops={line.stops}
              />
            ) : (
              <p className="agent-detail-muted">{t("transit.noStops")}</p>
            )}
          </section>
        )}

        {activeTab === "diagram" && (
          <section className="agent-detail-section">
            {line.stops.length >= 2 ? (
              <TransitLineDiagram
                stops={line.stops}
                routePoints={line.route_points}
                edges={line.edges}
                color={savedColor}
                running={running}
              />
            ) : (
              <p className="agent-detail-muted">{t("transit.noStops")}</p>
            )}
          </section>
        )}
      </div>
    </div>
  );
}

/** メーターに添える百分率表記。共通メーター(Meter)自体は数値を持たない */
function formatPercent(ratio: number): string {
  return `${Math.round(Math.max(0, Math.min(1, ratio)) * 100)}%`;
}

type StatTileProps = {
  label: string;
  value: string;
  unit?: string;
  /** 収支など符号で良し悪しが決まる値の色分け */
  tone?: "positive" | "negative";
};

/** 運行実績のタイル(ラベル上・値下)。トップメニュー系のフラット意匠に合わせる */
function StatTile({ label, value, unit, tone }: StatTileProps) {
  return (
    <div className={`transit-detail__tile${tone ? ` transit-detail__tile--${tone}` : ""}`}>
      <span className="transit-detail__tile-label">{label}</span>
      <span className="transit-detail__tile-value">
        {value}
        {unit && unit !== "" && <span className="transit-detail__unit">{unit}</span>}
      </span>
    </div>
  );
}
