/**
 * 経済主体詳細パネル
 *
 * window_type="agent_detail" のポップアップウィンドウで表示する専用パネル。
 * 経済主体のヘッダー情報(アイコン・ステータス・名前)と基本情報を表示する。
 *
 * ナビゲーション機能:
 * 同じパネル内で別の経済主体に遷移できる(経済主体リンク経由)。
 * 戻るボタンで前の経済主体に戻ることができる。
 * 履歴は entityHistory 配列で管理し、末尾要素が現在表示中の経済主体となる。
 *
 * 将来的な拡張:
 * agent.type や agent.is_bd に応じて、基本情報より下の領域に
 * タイプ固有の詳細情報を追加することを想定している。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { AgentData, AgentResponse } from "../../types";
import {
  sendChangeState,
  sendEditConnectorShow,
  sendEditConnectorHide,
} from "../../utils/ipc";
import { 
  TYPE_BUILDING,
  TYPE_HOUSEHOLD,
  TYPE_TRADE,
  TYPE_STANDARD,
  TYPE_EDUCATION,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
  STATUS_PENDING,
  STATUS_DEMOLISHING,
  MODEL_MASHUP
} from "../../constants";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useTemplateMode } from "../../hooks/useTemplateMode";
import { useConstructionBudgetRow } from "../../hooks/useConstructionBudgetRow";
import { resolveAgentIcon } from "../../utils/agentIcon";
import { useMasterData } from "../../contexts/MasterDataContext";
import { InfoHeader } from "../common/InfoHeader";
import { TagRow } from "../common/TagRow";
import { isTaggable } from "../../utils/constructionTags";
import { AgentAlertsPanel } from "./AgentAlertsPanel";
import { AgentBasicInfoPanel } from "./AgentBasicInfoPanel";
import { BuildingInfo } from "./BuildingInfo";
import { BuildingInfoPanel } from "./BuildingInfoPanel";
import { HouseholdInfoPanel } from "./HouseholdInfoPanel";
import { DemandInfoPanel } from "./DemandInfoPanel";
import { SupplyInfoPanel } from "./SupplyInfoPanel";
import { CargoInfoPanel } from "./CargoInfoPanel";
import { InfraConnectionPanel } from "./InfraConnectionPanel";
import { EducationTabPanel } from "./EducationTabPanel";
import { EmploymentInfoPanel } from "./EmploymentInfoPanel";
import { AgentOperationPanel } from "./AgentOperationPanel";
import { PublicFacilityPanel } from "./PublicFacilityPanel";
import { BuildingConstructionInfoPanel } from "./BuildingConstructionInfoPanel";
import "./AgentDetailPanel.css";
// アイコンボタン(.operation-panel / .operation-btn)を借りるため明示的に読み込む
import "./AgentOperationPanel.css";
import rocketIcon from "../../assets/images/ui/rocket.png";
import subBuildingIcon from "../../assets/images/buildings/bd_16.png";

/** 買収交渉中のアイコン縁取り色。公共施設の黄色と区別するためのオレンジ */
const ACQUISITION_FRAME_COLOR = "#f97316";

type AgentDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

export function AgentDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: AgentDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const templateMode = useTemplateMode(apiBaseUrl);
  const [entityHistory, setEntityHistory] = useState<string[]>([
    initialEntityId,
  ]);
  const [agentData, setAgentData] = useState<AgentData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const [activeTab, setActiveTab] = useState<string>("basic");
  const [utilizationTarget, setUtilizationTarget] = useState<number>(100);
  const [statusSaving, setStatusSaving] = useState(false);

  const currentEntityId = entityHistory[entityHistory.length - 1];
  const canGoBack = entityHistory.length > 1;

  // 建設中の建物の「1エポック建設コスト」「残り建設期間（エポック）」。
  // （建設コスト・工期の算出ロジックは get_budget_agents に一元化されており、共通フックで取得する）
  const { costPerEpoch: constructionCostPerEpoch, remainingEpochs } = useConstructionBudgetRow(
    apiBaseUrl,
    "agent",
    currentEntityId,
    agentData?.status === STATUS_CONSTRUCTING,
    refreshKey,
  );

  useEffect(() => {
    if (agentData && agentData.utilization_target !== undefined) {
      setUtilizationTarget(Math.round(agentData.utilization_target * 100));
    } else {
      setUtilizationTarget(100);
    }
  }, [agentData]);

  // 表示中の施設が接続端子ノード(infra_node_id / depot_node_id)を持つ間、
  // Unity 上にハイライトを表示し、パネルを開いた瞬間からノードをドラッグ移動可能に
  // する（接続点の編集モード）。agent_id / ノードID が変わったとき（別施設へ遷移など）
  // に張り替え、別施設への遷移／パネルを閉じる直前に通常モードへ戻す。
  const highlightInfraNodeId = agentData?.infra_node_id ?? 0;
  const highlightDepotNodeId = agentData?.depot_node_id ?? 0;
  const highlightAgentId = agentData?.agent_id ?? 0;
  useEffect(() => {
    if (highlightInfraNodeId <= 0 && highlightDepotNodeId <= 0) return;
    void sendEditConnectorShow(highlightAgentId, highlightInfraNodeId, highlightDepotNodeId);
    void sendChangeState(
      "STATE_EDIT_CONNECTOR",
      {
        agent_id: highlightAgentId,
        infra_node_id: highlightInfraNodeId,
        depot_node_id: highlightDepotNodeId,
      },
      "agent_detail",
    );
    return () => {
      void sendChangeState("STATE_NORMAL", undefined, "agent_detail");
      void sendEditConnectorHide();
    };
  }, [highlightAgentId, highlightInfraNodeId, highlightDepotNodeId]);

  const handleUtilizationTargetChange = async () => {
    if (!agentData) return;
    const url = buildApiUrl(apiBaseUrl, pathTemplate, currentEntityId);
    try {
      const updated = await fetchApi<AgentResponse>(url, {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          utilization_target: utilizationTarget / 100,
        }),
      });
      setAgentData(updated.economic_agent);
    } catch (err) {
      console.error("Failed to update utilization target:", err);
    }
  };

  /**
   * 名称を変更する。市が管理する公共施設だけがプレイヤーによる改名の対象で、
   * 民間の経済主体(店舗・世帯など)の名称は変更できない。
   */
  const handleNameSave = async (next: string) => {
    const url = buildApiUrl(apiBaseUrl, pathTemplate, currentEntityId);
    const updated = await fetchApi<AgentResponse>(url, {
      method: "PATCH",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ name: next }),
    });
    setAgentData(updated.economic_agent);
  };

  /** 着工と保留を切り替えるトグルハンドラ */
  const handleStatusToggle = async (checked: boolean) => {
    if (!agentData || statusSaving) return;
    setStatusSaving(true);
    const newStatus = checked ? STATUS_SCHEDULED_CONSTRUCTION : STATUS_PENDING;
    const url = buildApiUrl(apiBaseUrl, pathTemplate, currentEntityId);
    try {
      const updated = await fetchApi<AgentResponse>(url, {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          status: newStatus,
        }),
      });
      setAgentData(updated.economic_agent);
    } catch (err) {
      console.error("Failed to update agent status:", err);
    } finally {
      setStatusSaving(false);
    }
  };

  // 現在の経済主体IDが変更されたときにデータを再取得する
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    const url = buildApiUrl(apiBaseUrl, pathTemplate, currentEntityId);

    fetchApi<AgentResponse>(url, { signal: controller.signal })
      .then((response) => {
        if (!controller.signal.aborted) {
          setAgentData(response.economic_agent);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          const message =
            err instanceof Error ? err.message : "Unknown error";
          setError(message);
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [apiBaseUrl, pathTemplate, currentEntityId, refreshKey]);

  // 現在の経済主体の種類に合わせてactiveTabを適切に補正・初期化する
  useEffect(() => {
    if (!agentData) return;

    const isImportOrExport = agentData.type === TYPE_TRADE;
    const showBasicInfo = !isImportOrExport;
    const showHouseholdInfo = agentData.type === TYPE_HOUSEHOLD;
    const hasBasicInfoTab = showBasicInfo;
    const hasEducationInfoTab = agentData.type === TYPE_EDUCATION;
    const hasHouseholdInfoTab = showHouseholdInfo;
    const hasCargoInfoTab = isImportOrExport;

    const showBuildingPanel = agentData.is_bd;
    const showBuildingDetails = agentData.type === TYPE_BUILDING;
    const hasBuildingInfoTab = showBuildingPanel || showBuildingDetails;

    const demandAmount: Record<string, number> = agentData.demand_amount ?? {};
    const receivedAmount: Record<string, number> = agentData.received_amount ?? {};
    const acquiredAmount: Record<string, number> = agentData.remain_amount ?? {};
    const productIds = new Set([
      ...Object.keys(demandAmount),
      ...Object.keys(receivedAmount),
      ...Object.keys(acquiredAmount),
    ]);
    const hasDemand = Array.from(productIds).some(
      (id) =>
        (demandAmount[id] ?? 0) !== 0 ||
        (receivedAmount[id] ?? 0) !== 0 ||
        (acquiredAmount[id] ?? 0) !== 0
    );

    const showDemandInfo = !isImportOrExport && hasDemand;
    const showSupplyInfo = agentData.type === TYPE_STANDARD;
    const hasTradeInfoTab = showDemandInfo || showSupplyInfo;

    const showEmploymentInfo = agentData.type === TYPE_STANDARD;
    const hasEmploymentInfoTab = showEmploymentInfo;

    const showConstructionTab = [
      STATUS_PENDING,
      STATUS_SCHEDULED_CONSTRUCTION,
      STATUS_CONSTRUCTING,
      STATUS_DEMOLISHING
    ].includes(agentData.status);

    const isPreConstruction =
      agentData.status === STATUS_SCHEDULED_CONSTRUCTION ||
      agentData.status === STATUS_PENDING;

    const availableIds: string[] = [];
    if (isPreConstruction) {
      if (showConstructionTab) availableIds.push("construction");
    } else {
      if (hasBasicInfoTab) availableIds.push("basic");
      if (hasEducationInfoTab) availableIds.push("education");
      if (hasCargoInfoTab) availableIds.push("cargo");
      if (hasBuildingInfoTab) availableIds.push("building");
      if (hasHouseholdInfoTab) availableIds.push("household");
      if (hasTradeInfoTab) availableIds.push("trade");
      if (hasEmploymentInfoTab) availableIds.push("employment");
      if (showConstructionTab) availableIds.push("construction");
    }

    if (availableIds.length > 0 && !availableIds.includes(activeTab)) {
      if (availableIds.includes("construction") && (agentData.status === STATUS_CONSTRUCTING || isPreConstruction)) {
        setActiveTab("construction");
      } else {
        setActiveTab(availableIds[0]);
      }
    }
  }, [agentData, activeTab]);

  /**
   * 別の経済主体にナビゲーションする
   * 履歴スタックに新しいエンティティIDを追加する
   */
  function navigateToAgent(entityId: string) {
    setEntityHistory((prev) => [...prev, entityId]);
  }

  /**
   * 一つ前の経済主体に戻る
   */
  function navigateBack() {
    if (canGoBack) {
      setEntityHistory((prev) => prev.slice(0, -1));
    }
  }

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !agentData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const iconSrc = resolveAgentIcon(
    agentData.variant,
    agentData.type,
    agentData.is_bd,
    agentData.bd_type
  );

  const isImportOrExport = agentData.type === TYPE_TRADE;

  const isPreConstruction = agentData.status === STATUS_SCHEDULED_CONSTRUCTION || agentData.status === STATUS_PENDING;

  /** 立地する土地区画が道路建設などのために市政府と買収交渉中か */
  const underAcquisition = agentData.under_acquisition === true;

  /**
   * マスターデータからバリアントの表示名を取得する。
   * 観光名所(landmark>0)はバリアント名が「観光名所」で全個体共通になってしまうため、
   * 名所そのものの名前(大展望塔など)を出す。
   */
  const variantName =
    (agentData.landmark != null && agentData.landmark > 0
      ? masterData?.landmarks?.[agentData.landmark]?.display_name
      : undefined) ?? masterData?.agents[agentData.variant]?.display_name;

  /**
   * マッシュアップ建物(MODEL_MASHUP)かどうか。該当する建物には、サブ建物(校舎・体育館などの棟)の
   * 配置を編集する専用ウィンドウ(mashup_edit)を開くボタンを表示する。
   */
  const isMashupBuilding =
    agentData.is_bd &&
    masterData?.agents[agentData.variant]?.model_type === MODEL_MASHUP;

  // 各詳細セクションの表示条件
  const showBasicInfo = !isImportOrExport;
  const showHouseholdInfo = agentData.type === TYPE_HOUSEHOLD;
  const hasBasicInfoTab = showBasicInfo;
  const hasEducationInfoTab = agentData.type === TYPE_EDUCATION;
  const hasHouseholdInfoTab = showHouseholdInfo;

  const hasCargoInfoTab = isImportOrExport;

  const showBuildingPanel = agentData.is_bd;
  const showBuildingDetails = agentData.type === TYPE_BUILDING;
  const hasBuildingInfoTab = showBuildingPanel || showBuildingDetails;

  const demandAmount: Record<string, number> = agentData.demand_amount ?? {};
  const receivedAmount: Record<string, number> = agentData.received_amount ?? {};
  const acquiredAmount: Record<string, number> = agentData.remain_amount ?? {};
  const productIds = new Set([
    ...Object.keys(demandAmount),
    ...Object.keys(receivedAmount),
    ...Object.keys(acquiredAmount),
  ]);
  const hasDemand = Array.from(productIds).some(
    (id) =>
      (demandAmount[id] ?? 0) !== 0 ||
      (receivedAmount[id] ?? 0) !== 0 ||
      (acquiredAmount[id] ?? 0) !== 0
  );

  const showDemandInfo = !isImportOrExport && hasDemand;
  const showSupplyInfo = agentData.type === TYPE_STANDARD;
  const hasTradeInfoTab = showDemandInfo || showSupplyInfo;

  const showEmploymentInfo = agentData.type === TYPE_STANDARD;
  const hasEmploymentInfoTab = showEmploymentInfo;

  const showConstructionTab = [
    STATUS_PENDING,
    STATUS_SCHEDULED_CONSTRUCTION,
    STATUS_CONSTRUCTING,
    STATUS_DEMOLISHING
  ].includes(agentData.status);

  const tabs: { id: string; label: string }[] = [];
  if (isPreConstruction) {
    if (showConstructionTab) {
      tabs.push({ id: "construction", label: t("agent.tabConstructionInfo", "建設工事") });
    }
  } else {
    if (hasBasicInfoTab) {
      tabs.push({ id: "basic", label: t("agent.tabBasicInfo", "基本情報") });
    }
    if (hasEducationInfoTab) {
      tabs.push({ id: "education", label: t("agent.tabEducationInfo", "教育") });
    }
    if (hasCargoInfoTab) {
      tabs.push({ id: "cargo", label: t("agent.tabCargoInfo", "荷役情報") });
    }
    if (hasBuildingInfoTab) {
      tabs.push({ id: "building", label: t("agent.tabBuildingInfo", "建物情報") });
    }
    if (hasHouseholdInfoTab) {
      tabs.push({ id: "household", label: t("agent.tabHouseholdInfo", "世帯情報") });
    }
    if (hasTradeInfoTab) {
      tabs.push({ id: "trade", label: t("agent.tabTradeInfo", "需給・取引") });
    }
    if (hasEmploymentInfoTab) {
      tabs.push({ id: "employment", label: t("agent.tabEmploymentInfo", "雇用情報") });
    }
    if (showConstructionTab) {
      tabs.push({ id: "construction", label: t("agent.tabConstructionInfo", "建設工事") });
    }
  }

  return (
    <div className="agent-detail">
      {canGoBack && (
        <button
          className="agent-detail-back"
          onClick={navigateBack}
          type="button"
        >
          ← {t("agent.back")}
        </button>
      )}

      <InfoHeader
        iconSrc={iconSrc}
        status={agentData.status}
        statusReason={agentData.status_reason}
        variantName={variantName}
        name={agentData.name}
        constructionProgress={agentData.construction_progress}
        iconLabel={
          underAcquisition
            ? t("agent.underAcquisition")
            : agentData.public_facility
              ? t("agent.publicFacility")
              : undefined
        }
        iconFrameColor={underAcquisition ? ACQUISITION_FRAME_COLOR : undefined}
        onNameSave={agentData.public_facility ? handleNameSave : undefined}
      />

      {/* 建設プロジェクトのタグと、この建物だけを3Dビューで隠す切り替え。
          タグを付け替えられるのは着工前・建設中の公共施設だけなので、その間だけ行を出す
          (竣工後もタグ自体は残り、建設プロジェクトウィンドウで竣工済みとして数えられる)。 */}
      {agentData.is_bd && isTaggable("agent", agentData.status, agentData.public_facility) && (
        <TagRow apiBaseUrl={apiBaseUrl} kind="agent" entityId={agentData.agent_id} />
      )}

      {/* 未着工（保留）状態の警告バナー */}
      {agentData.is_bd && agentData.status === STATUS_PENDING && (
        <div className="pending-banner pending-banner--active">
          <span className="pending-banner__text">
            {t("agent.pendingWarning", "この建物は計画中です。着工を指示してください。")}
          </span>
          <button
            className="pending-banner__btn"
            type="button"
            disabled={statusSaving}
            onClick={() => void handleStatusToggle(true)}
          >
            <img src={rocketIcon} alt="rocket" />
            {t("agent.action.startConstruction", "着工")}
          </button>
        </div>
      )}

      {/* 公共施設は操作ボタン・稼働率目標・放棄をまとめた
          コントロールパネルカードを表示する。非公共の経済主体は従来どおり
          操作ボタン列と稼働率目標カードを個別に表示する。 */}
      {agentData.public_facility ? (
        <PublicFacilityPanel
          agent={agentData}
          apiBaseUrl={apiBaseUrl}
          onActionPerformed={() => setRefreshKey((k) => k + 1)}
          utilizationTarget={utilizationTarget}
          setUtilizationTarget={setUtilizationTarget}
          onUtilizationCommit={handleUtilizationTargetChange}
          constructionCostPerEpoch={constructionCostPerEpoch}
          remainingEpochs={remainingEpochs}
        />
      ) : (
      <>
      <AgentOperationPanel
        agent={agentData}
        apiBaseUrl={apiBaseUrl}
        onActionPerformed={() => setRefreshKey((k) => k + 1)}
      />

      {(() => {
        const agentMaster = masterData?.agents[agentData.variant];
        // 余力方式(>0)・定量方式(<0)とも稼働率目標がプレイヤーのレバーになる
        const isHeadroomFacility = (agentMaster?.production_headroom ?? 0) !== 0;
        // 建設中の建物は utilization_target を「建設スピード」として調整できる（最大500%）。
        const isConstructing = agentData.status === STATUS_CONSTRUCTING;
        if (!isConstructing && !isHeadroomFacility) return null;

        const sliderMax = isConstructing ? 500 : 100;

        return (
          <div className="agent-detail-utilization-target">
            <div className="agent-detail-utilization-target-header">
              <span className="agent-detail-utilization-target-title">
                {isConstructing
                  ? t("agent.constructionSpeed", "建設スピード")
                  : t("agent.utilizationTarget", "稼働率目標")}
              </span>
              <span className="agent-detail-utilization-target-value">
                {utilizationTarget}%
              </span>
            </div>
            <div className="agent-detail-utilization-target-slider-container">
              <input
                type="range"
                min="0"
                max={sliderMax}
                value={utilizationTarget}
                onChange={(e) => setUtilizationTarget(Number(e.target.value))}
                onMouseUp={handleUtilizationTargetChange}
                onTouchEnd={handleUtilizationTargetChange}
                className="agent-detail-utilization-target-slider"
              />
            </div>
          </div>
        );
      })()}
      </>
      )}

      {/* アラートカードのクリックで、原因を因果順に掘り下げる診断ウィンドウを開く */}
      <AgentAlertsPanel
        alert={agentData.alert}
        onDiagnose={() => {
          void invoke("open_registered_window", {
            windowType: "diagnosis",
            entityId: currentEntityId,
          });
        }}
      />

      {/* マッシュアップ建物のサブ建物配置エディタを開くボタン。
          操作系は他のパネルと同じアイコンボタンに揃え、名称はツールチップに持たせる。 */}
      {isMashupBuilding && (
        <div className="operation-panel">
          <button
            className="operation-btn"
            type="button"
            title={t("agent.editSubBuildings", "サブ建物の配置を編集")}
            onClick={() => {
              void invoke("open_registered_window", {
                windowType: "mashup_edit",
                entityId: currentEntityId,
              });
            }}
          >
            <img
              src={subBuildingIcon}
              alt={t("agent.editSubBuildings", "サブ建物の配置を編集")}
            />
          </button>
        </div>
      )}

      {/* 計画中のエージェントは既存データを表示する意味がないため、
          削除・編集ボタンだけを表示して詳細セクションは省略する（建設工事タブがある場合を除く）。 */}
      {tabs.length > 0 && (
        <div className="agent-detail-tabs">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              className={[
                "agent-detail-tab",
                tab.id === activeTab && "agent-detail-tab--active",
              ]
                .filter(Boolean)
                .join(" ")}
              type="button"
              onClick={() => setActiveTab(tab.id)}
            >
              {tab.label}
            </button>
          ))}
        </div>
      )}

      {tabs.length > 0 && (
        <div className="agent-detail-tab-content">
          {activeTab === "basic" && hasBasicInfoTab && !isPreConstruction && (
            <AgentBasicInfoPanel
              agent={agentData}
              onNavigate={navigateToAgent}
            />
          )}

          {activeTab === "education" && hasEducationInfoTab && !isPreConstruction && (
            <EducationTabPanel
              agent={agentData}
              apiBaseUrl={apiBaseUrl}
              pathTemplate={pathTemplate}
              onSaved={() => setRefreshKey((k) => k + 1)}
            />
          )}

          {activeTab === "cargo" && hasCargoInfoTab && !isPreConstruction && (
            <CargoInfoPanel agent={agentData} apiBaseUrl={apiBaseUrl} />
          )}

          {activeTab === "building" && hasBuildingInfoTab && !isPreConstruction && (
            <>
              {showBuildingPanel && <BuildingInfoPanel agent={agentData} />}
              {showBuildingDetails && (
                <BuildingInfo agent={agentData} onNavigate={navigateToAgent} />
              )}
            </>
          )}

          {activeTab === "household" && hasHouseholdInfoTab && !isPreConstruction && (
            <>
              {showHouseholdInfo && (
                <HouseholdInfoPanel agent={agentData} />
              )}
            </>
          )}

          {activeTab === "trade" && hasTradeInfoTab && !isPreConstruction && (
            <>
              {/* インフラ財を授受する施設のみ表示される(コンポーネント内で判定) */}
              <InfraConnectionPanel agent={agentData} />
              {showDemandInfo && <DemandInfoPanel agent={agentData} />}
              {showSupplyInfo && <SupplyInfoPanel agent={agentData} />}
            </>
          )}

          {activeTab === "employment" && hasEmploymentInfoTab && !isPreConstruction && (
            <>
              {showEmploymentInfo && <EmploymentInfoPanel agent={agentData} />}
            </>
          )}

          {activeTab === "construction" && showConstructionTab && (
            <BuildingConstructionInfoPanel
              agent={agentData}
              apiBaseUrl={apiBaseUrl}
              refreshKey={refreshKey}
            />
          )}
        </div>
      )}

      {templateMode && (
        <button
          className="god-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "god_edit",
              params: { extra: { entityType: "agent", entityId: currentEntityId } },
            });
          }}
        >
          {t("god_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
