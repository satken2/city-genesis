/**
 * 土地区画詳細パネル
 *
 * window_type="parcel_detail" のポップアップウィンドウで表示する専用パネル。
 * 土地区画のヘッダー情報(アイコン・ステータス・名前)と各種属性を表示する。
 *
 * 編集可能フィールド:
 * - restriction_override: 地区規制を個別オーバーライドするかどうかのフラグ
 * - allowed_bd_mask: 建設可能建物種別のビットマスク
 * - limit_coverage_ratio: 建ぺい率上限(API では 0〜1、入力欄では %)
 * - limit_volume_ratio: 容積率上限(API では 0〜1、入力欄では %)
 * - limit_height: 高さ規制
 *
 * 規制関連フィールドは restriction_override=true の場合のみ有効になる。
 * 変更内容は「保存」ボタンで PATCH リクエストとして送信する。
 */

import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import type { ParcelData } from "../../types";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import { showNotification } from "../../utils/notification";
import { resolveParcelIcon } from "../../utils/parcelIcon";
import { useTemplateMode } from "../../hooks/useTemplateMode";
import { useMasterData } from "../../contexts/MasterDataContext";
import { InfoHeader } from "../common/InfoHeader";
import { ZoneLink } from "../common/ZoneLink";
import { PathLink } from "../common/PathLink";
import { AgentLink } from "../common/AgentLink";
import { ResourceCard } from "../common/ResourceCard";
import { RestrictionFields, type RestrictionValues } from "./RestrictionFields";
import { ParcelOperationPanel } from "./ParcelOperationPanel";
import { ParcelEnvironmentSection } from "./ParcelEnvironmentSection";
import {
  sendEditParcelCancelShapeEdit,
  sendEditParcelFinishShapeEdit,
  sendShowParcelFrontPath,
  sendHideParcelFrontPath,
} from "../../utils/ipc";
import { IPC_EVENT_EDIT_PARCEL_UPDATE_SHAPE } from "../../utils/ipc";
import { STATUS_ACTIVE } from "../../constants";
import "./ParcelDetailPanel.css";

type ResourceListItem = {
  resource_id: number;
  product_id: number;
  max_amount: number;
  resource_amount: number;
  recovery_rate: number;
};

/**
 * 接道（front_path_id）の変更候補となる街路。
 * バックエンドの GET /api/v1/parcels/{id}/facing-paths が返す各要素に対応する。
 */
type FrontPathCandidate = {
  path_id: number;
  distance: number;
  name: string | null;
  zone_id: number | null;
  zone_name: string | null;
  is_current: boolean;
};

type ParcelDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

export function ParcelDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: ParcelDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const templateMode = useTemplateMode(apiBaseUrl);
  const [parcelData, setParcelData] = useState<ParcelData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [cityAgentId, setCityAgentId] = useState<number | null>(null);

  /** 形状編集モード中かどうか */
  const [isEditingShape, setIsEditingShape] = useState(false);
  /** アンマウント時の判定用に、最新の編集状態を保持する */
  const isEditingShapeRef = useRef(isEditingShape);
  /** Unity側から通知された編集中のポリゴン */
  const [editingPolygon, setEditingPolygon] = useState<number[][] | null>(null);

  /** 編集中の restriction_override フラグ */
  const [restrictionOverride, setRestrictionOverride] = useState(false);

  /** 編集中の規制フィールド値 */
  const [restrictionValues, setRestrictionValues] = useState<RestrictionValues>({
    allowed_bd_mask: 0,
    limit_coverage_ratio: null,
    limit_volume_ratio: null,
    limit_height: null,
    min_structure: 0,
  });

  // パネル表示中、選択中区画の接道(front_path)を Unity の3Dビューに青い矢印で可視化する。
  // マウント時に表示要求、アンマウント時にクリア要求を送る。Unity 側が parcel_id から
  // 接道辺を算出して描画する(チュートリアルフラグや形状編集キャンセルと同じ lifecycle パターン)。
  useEffect(() => {
    const parcelId = Number.parseInt(initialEntityId, 10);
    if (!Number.isFinite(parcelId) || parcelId <= 0) return;
    void sendShowParcelFrontPath(parcelId);
    return () => {
      void sendHideParcelFrontPath();
    };
  }, [initialEntityId]);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  const [allResources, setAllResources] = useState<ResourceListItem[] | null>(null);

  /** 接道の変更候補となる街路の一覧 */
  const [frontPathCandidates, setFrontPathCandidates] = useState<FrontPathCandidate[]>([]);
  /** プルダウンで選択中の接道の path_id（null は現在の接道と同じ = 未変更） */
  const [selectedFrontPathId, setSelectedFrontPathId] = useState<number | null>(null);
  /** 接道変更の保存中フラグ */
  const [frontPathSaving, setFrontPathSaving] = useState(false);
  /** 接道変更の保存エラー */
  const [frontPathSaveError, setFrontPathSaveError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ resources: ResourceListItem[] }>(
      buildApiUrl(apiBaseUrl, "/api/v1/resources"),
      { signal: controller.signal }
    )
      .then((res) => {
        if (controller.signal.aborted) return;
        setAllResources(res.resources);
      })
      .catch((err: unknown) => {
        console.error("Failed to fetch resources in ParcelDetailPanel:", err);
      });
    return () => controller.abort();
  }, [apiBaseUrl, refreshKey]);

  // 接道の変更候補（接道閾値内の街路）を取得する。
  // 区画IDや保存後の refreshKey 変化で再取得し、選択状態は現在の接道に初期化する。
  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ candidates: FrontPathCandidate[] }>(
      buildApiUrl(apiBaseUrl, `/api/v1/parcels/${initialEntityId}/facing-paths`),
      { signal: controller.signal }
    )
      .then((res) => {
        if (controller.signal.aborted) return;
        setFrontPathCandidates(res.candidates);
        setSelectedFrontPathId(null);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        console.error("Failed to fetch front path candidates:", err);
        setFrontPathCandidates([]);
      });
    return () => controller.abort();
  }, [apiBaseUrl, initialEntityId, refreshKey]);

  function handleResourceClick(resourceId: number) {
    void invoke("open_registered_window", {
      windowType: "resource_detail",
      entityId: String(resourceId),
    });
  }

  /** 編集状態の最新値を Ref に反映する */
  useEffect(() => {
    isEditingShapeRef.current = isEditingShape;
  }, [isEditingShape]);

  /**
   * コンポーネントがアンマウントされる際、形状編集モード中であれば
   * Unity 側に編集キャンセルを通知する。
   * これにより、パネルを閉じた後も頂点などの編集用描画が残る事象を防ぐ。
   */
  useEffect(() => {
    return () => {
      if (isEditingShapeRef.current) {
        void sendEditParcelCancelShapeEdit();
      }
    };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<ParcelData>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          setParcelData(data);
          setRestrictionOverride(data.restriction_override);
          setRestrictionValues({
            allowed_bd_mask: data.allowed_bd_mask ?? 0,
            limit_coverage_ratio: data.limit_coverage_ratio ?? null,
            limit_volume_ratio: data.limit_volume_ratio ?? null,
            limit_height: data.limit_height ?? null,
            min_structure: data.min_structure ?? 0,
          });
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

  useEffect(() => {
    const controller = new AbortController();
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/city/`, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) return;
        const data = await res.json() as { city?: { city_agent_id?: number } };
        if (data.city?.city_agent_id !== undefined) {
          setCityAgentId(data.city.city_agent_id);
        }
      })
      .catch(() => {
        // 市政府エージェントIDの取得に失敗しても、表示は続行する
      });

    return () => {
      controller.abort();
    };
  }, [apiBaseUrl]);

  useEffect(() => {
    const unlistenPromise = listen(IPC_EVENT_EDIT_PARCEL_UPDATE_SHAPE, (event) => {
      const payload = event.payload as {
        parcel_id?: number;
        polygon?: number[][];
      };
      if (payload.parcel_id === parcelData?.parcel_id && payload.polygon) {
        setEditingPolygon(payload.polygon);
      }
    });

    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [parcelData?.parcel_id]);

  /**
   * 編集内容を PATCH リクエストで保存する
   */
  async function handleSave() {
    if (!parcelData) return;
    setSaving(true);
    setSaveError(null);

    const patchUrl = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/parcels/${parcelData.parcel_id}`;
    const body = {
      restriction_override: restrictionOverride,
      ...(restrictionOverride
        ? {
            allowed_bd_mask: restrictionValues.allowed_bd_mask,
            limit_coverage_ratio: restrictionValues.limit_coverage_ratio,
            limit_volume_ratio: restrictionValues.limit_volume_ratio,
            limit_height: restrictionValues.limit_height,
            min_structure: restrictionValues.min_structure,
          }
        : {}),
    };

    try {
      const res = await fetch(patchUrl, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const updated = await res.json() as ParcelData;
      setParcelData(updated);
      setRestrictionOverride(updated.restriction_override);
      setRestrictionValues({
        allowed_bd_mask: updated.allowed_bd_mask ?? 0,
        limit_coverage_ratio: updated.limit_coverage_ratio ?? null,
        limit_volume_ratio: updated.limit_volume_ratio ?? null,
        limit_height: updated.limit_height ?? null,
        min_structure: updated.min_structure ?? 0,
      });
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      setSaving(false);
    }
  }

  /**
   * 接道の変更を PATCH リクエストで保存する。
   * front_path_id のみを送信し、規制フィールドの保存（handleSave）とは独立して動作する。
   * 接道が変わると所属地区も変わりうるため、成功後は refreshKey を進めて
   * 所属地区表示や候補一覧を再取得する。
   */
  async function handleSaveFrontPath() {
    if (!parcelData || selectedFrontPathId === null) return;
    setFrontPathSaving(true);
    setFrontPathSaveError(null);

    const patchUrl = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/parcels/${parcelData.parcel_id}`;
    try {
      const res = await fetch(patchUrl, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ front_path_id: selectedFrontPathId }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const updated = await res.json() as ParcelData;
      setParcelData(updated);
      setSelectedFrontPathId(null);
      setRefreshKey((k) => k + 1);
    } catch (err) {
      setFrontPathSaveError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      setFrontPathSaving(false);
    }
  }

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !parcelData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const iconSrc = resolveParcelIcon(parcelData.parcel_type);

  /** マスターデータから土地区画種別の表示名を取得する */
  const variantName = masterData?.parcel_types[parcelData.parcel_type]?.display_name;

  /** 土地区画の表示名はIDベースで生成する */
  const displayName = t("parcel.titleWithId", { id: parcelData.parcel_id });

  // 接道の有無は ID で判定する(パス名は既定で空文字なので、名前では接道が無いように見える)
  const hasFrontPath = parcelData.front_path_id != null && parcelData.front_path_id > 0;
  /** 接道の表示名。名前の無いパスは「パス #id」 */
  const frontPathLabel = (id: number, name?: string | null) => name || t("path.titleWithId", { id });

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={iconSrc}
        status={parcelData.status ?? STATUS_ACTIVE}
        showStatus={false}
        variantName={variantName}
        name={displayName}
      />

      <ParcelOperationPanel
        parcel={parcelData}
        apiBaseUrl={apiBaseUrl}
        onActionPerformed={() => setRefreshKey((k) => k + 1)}
        cityAgentId={cityAgentId ?? -1}
        isEditingShape={isEditingShape}
        canConfirmEdit={editingPolygon != null && editingPolygon.length >= 3}
        onStartEditShape={() => setIsEditingShape(true)}
        onConfirmEditShape={async () => {
          if (!editingPolygon || editingPolygon.length < 3) return;
          // 形状の保存はUIが直接バックエンドにPATCHする(他の建設系パネルと同じ責務分担)。
          // 成功したら finish_shape_edit を送り、Unity側の編集状態をリセットして再描画させる。
          try {
            await fetchApi(
              buildApiUrl(apiBaseUrl, `/api/v1/parcels/${parcelData.parcel_id}`),
              {
                method: "PATCH",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ polygon: editingPolygon }),
              }
            );
          } catch (err) {
            console.error("[ParcelDetail] shape edit confirm failed:", err);
            let message = t("parcel.shapeEditFailed", "形状の保存に失敗しました");
            let code: string | undefined;
            if (err instanceof ApiError) {
              message = err.message;
              code = err.code;
            } else if (err instanceof Error) {
              message = err.message;
            }
            const i18nKey = code ? `errors.${code}` : null;
            const displayMessage =
              i18nKey && i18nKey !== "errors.UNKNOWN_ERROR"
                ? t(i18nKey, { defaultValue: message })
                : message;
            void showNotification({ message: displayMessage, durationMs: 5000 });
            return;
          }
          await sendEditParcelFinishShapeEdit();
          setIsEditingShape(false);
          setEditingPolygon(null);
          setRefreshKey((k) => k + 1);
        }}
        onCancelEditShape={async () => {
          await sendEditParcelCancelShapeEdit();
          setIsEditingShape(false);
          setEditingPolygon(null);
        }}
      />

      {/* 横断パス（地下埋設・上空高架）の注記。
          has_crossing_path=1 の区画には民間建物がスポーンしないため、
          「空き地なのに建物が建たない」と混乱しないようにここで案内する。
          公共施設はプレイヤーの操作で建設できる。 */}
      {parcelData.has_crossing_path === 1 && (
        <div className="parcel-crossing-note">
          <span className="parcel-crossing-note-title">
            {t("parcel.crossingPathTitle", "地下/上空にパスあり")}
          </span>
          <span className="parcel-crossing-note-body">
            {t(
              "parcel.crossingPathBody",
              "この区画の地下または上空をパス（埋設インフラ・トンネル・高架など）が横断しています。民間の建物は建ちません（公共施設は建設できます）。"
            )}
          </span>
        </div>
      )}

      {/* 基本情報セクション */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("parcel.basicInfo", "基本情報")}</h3>
        <div className="agent-detail-grid">
          {parcelData.zone_name && (
            <>
              <span className="agent-detail-label">{t("parcel.zoneName")}</span>
              <span className="agent-detail-value">
                <ZoneLink
                  zoneId={parcelData.zone_id}
                  zoneName={parcelData.zone_name}
                />
              </span>
            </>
          )}

          <span className="agent-detail-label">{t("parcel.landPrice")}</span>
          <span className="agent-detail-value">
            {t("common.currency", { value: parcelData.land_price })}
          </span>

          <span className="agent-detail-label">{t("parcel.area")}</span>
          <span className="agent-detail-value">
            {t("common.sqm", { value: Math.round(parcelData.area) })}
          </span>

          <span className="agent-detail-label">{t("parcel.altitude")}</span>
          <span className="agent-detail-value">
            {t("parcel.altitudeValue", { value: Math.round(parcelData.altitude * 10) / 10 })}
          </span>

          {(hasFrontPath || frontPathCandidates.length > 0) && (
            <>
              <span className="agent-detail-label">{t("parcel.frontPath")}</span>
              <span className="agent-detail-value">
                {(() => {
                  // 候補が無い場合は従来通り読み取り専用のリンク表示にフォールバックする。
                  if (frontPathCandidates.length === 0) {
                    return hasFrontPath ? (
                      <PathLink
                        pathId={parcelData.front_path_id}
                        pathName={frontPathLabel(parcelData.front_path_id, parcelData.front_path_name)}
                      />
                    ) : (
                      <span className="agent-detail-muted">
                        {t("parcel.frontPathNoCandidates")}
                      </span>
                    );
                  }

                  // 現在の接道が候補一覧に含まれない場合でも選択状態を表示できるよう、
                  // 先頭に現在の接道の選択肢を補う。
                  const currentId = parcelData.front_path_id;
                  const hasCurrent = frontPathCandidates.some((c) => c.path_id === currentId);
                  const options: FrontPathCandidate[] = [...frontPathCandidates];
                  if (hasFrontPath && !hasCurrent) {
                    options.unshift({
                      path_id: currentId,
                      distance: 0,
                      name: parcelData.front_path_name,
                      zone_id: parcelData.zone_id,
                      zone_name: parcelData.zone_name,
                      is_current: true,
                    });
                  }

                  const effectiveSelected = selectedFrontPathId ?? currentId ?? -1;

                  return (
                    <div className="parcel-front-path-edit">
                      <select
                        className="parcel-front-path-select"
                        value={effectiveSelected}
                        disabled={frontPathSaving}
                        onChange={(e) => {
                          const next = Number(e.target.value);
                          // 現在の接道と同じ選択に戻したら未変更（null）に戻す。
                          setSelectedFrontPathId(next === currentId ? null : next);
                          setFrontPathSaveError(null);
                        }}
                      >
                        {options.map((c) => {
                          const label = t("parcel.frontPathOption", {
                            name: frontPathLabel(c.path_id, c.name),
                            distance: Math.round(c.distance),
                          });
                          return (
                            <option key={c.path_id} value={c.path_id}>
                              {c.zone_name ? `${label} / ${c.zone_name}` : label}
                            </option>
                          );
                        })}
                      </select>
                      <button
                        type="button"
                        className="parcel-save-btn"
                        disabled={selectedFrontPathId === null || frontPathSaving}
                        onClick={() => void handleSaveFrontPath()}
                      >
                        {frontPathSaving
                          ? t("parcel.frontPathSaving")
                          : t("parcel.frontPathSave")}
                      </button>
                      {frontPathSaveError && (
                        <span className="parcel-save-error">
                          {t("parcel.frontPathSaveError")}
                        </span>
                      )}
                    </div>
                  );
                })()}
              </span>
            </>
          )}

          {parcelData.owner_agent_name && (
            <>
              <span className="agent-detail-label">{t("parcel.owner")}</span>
              <span className="agent-detail-value">
                <AgentLink
                  agentId={parcelData.owner_agent_id}
                  agentName={parcelData.owner_agent_name}
                  onNavigate={() => {
                    // 土地区画パネル内でのエージェントナビゲーションは現時点では未対応
                  }}
                />
              </span>
            </>
          )}
        </div>
      </section>

      {/* 環境セクション(区画固有の日照・大気汚染・騒音・犯罪) */}
      <ParcelEnvironmentSection
        apiBaseUrl={apiBaseUrl}
        parcelId={parcelData.parcel_id}
      />

      {/* 割り当てられている天然資源セクション */}
      {(() => {
        const assignedResourceIds = parcelData?.resource_id
          ? Object.values(parcelData.resource_id).filter((id) => id > 0)
          : [];
        const assignedResources = allResources
          ? allResources.filter((r) => assignedResourceIds.includes(r.resource_id))
          : [];

        if (assignedResources.length === 0) return null;

        return (
          <section className="agent-detail-section">
            <h3 className="agent-detail-section-title">
              {t("parcel.assignedResources", "割り当てられている天然資源")}
            </h3>
            <div className="parcel-resources-list">
              {assignedResources.map((r) => (
                <ResourceCard
                  key={r.resource_id}
                  resourceId={r.resource_id}
                  productId={r.product_id}
                  maxAmount={r.max_amount}
                  resourceAmount={r.resource_amount}
                  onClick={handleResourceClick}
                />
              ))}
            </div>
          </section>
        );
      })()}

      {/* 規制セクション */}
      <RestrictionFields
        title={t("parcel.restrictions", "地区規制")}
        values={restrictionValues}
        onChange={setRestrictionValues}
        disabled={!restrictionOverride}
        onSave={() => void handleSave()}
        saving={saving}
        saveError={saveError}
      >
        <div className="parcel-restriction-header">
          <label className="parcel-restriction-override-label">
            <input
              type="checkbox"
              className="parcel-restriction-override-check"
              checked={restrictionOverride}
              onChange={(e) => setRestrictionOverride(e.target.checked)}
            />
            <span className="agent-detail-label">{t("parcel.restrictionOverride")}</span>
          </label>
        </div>
      </RestrictionFields>

      {templateMode && (
        <button
          className="god-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "god_edit",
              params: { extra: { entityType: "parcel", entityId: String(parcelData.parcel_id) } },
            });
          }}
        >
          {t("god_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
