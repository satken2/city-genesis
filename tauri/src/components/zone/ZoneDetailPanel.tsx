/**
 * 地区(Zone)詳細パネル
 *
 * window_type="zone_detail" のポップアップウィンドウで表示する専用パネル。
 * 2つのモードをサポートする:
 *
 * - view: 既存地区の表示（名前と規制は読み取り専用）
 * - edit: 既存地区の編集（名前と建築規制をPATCH）
 *
 * 編集モードでは名前と建築規制のみ編集可能である。
 * 所属道路の割り当て変更はタスクバーの地区編集モード(3DビューのCtrlラジアルメニュー)で行うため、
 * このパネルでは扱わない。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { ZoneData, ZoneResponse } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useTemplateMode } from "../../hooks/useTemplateMode";
import { InfoHeader } from "../common/InfoHeader";
import { DataList } from "../common/DataList";
import { SpaceAmountChart } from "../common/SpaceAmountChart";
import { RestrictionFields, type RestrictionValues } from "../parcel/RestrictionFields";
import { TUTORIAL_FLAG_ZONE_PANEL_OPEN } from "../tutorial/tutorialSetup";
import zoneIcon from "../../assets/images/zone.png";
import "./ZoneDetailPanel.css";

type ZoneDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate?: string;
  initialEntityId?: string;
};

export function ZoneDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: ZoneDetailPanelProps) {
  const { t } = useTranslation();
  const templateMode = useTemplateMode(apiBaseUrl);

  const [zoneData, setZoneData] = useState<ZoneData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [restrictionValues, setRestrictionValues] = useState<RestrictionValues>({
    allowed_bd_mask: 0,
    limit_coverage_ratio: null,
    limit_volume_ratio: null,
    limit_height: null,
  });

  // 管理状態(自動管理/確定)のトグル送信中フラグと失敗表示
  const [managementBusy, setManagementBusy] = useState(false);
  const [managementError, setManagementError] = useState(false);

  // 地区のピン(確定)/ピン解除(自動管理へ戻す)。
  // auto_managed を明示指定した PATCH は、バックエンド側で名前・規制編集による
  // 昇格処理を経由せず送信値がそのまま反映される(ピン操作の専用経路)。
  const toggleAutoManaged = async () => {
    if (!zoneData || managementBusy) return;
    setManagementBusy(true);
    setManagementError(false);
    try {
      const data = await fetchApi<ZoneResponse>(
        buildApiUrl(apiBaseUrl, "/api/v1/zones/{id}", String(zoneData.zone_id)),
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ auto_managed: zoneData.auto_managed ? 0 : 1 }),
        }
      );
      setZoneData(data.zone);
    } catch {
      setManagementError(true);
    } finally {
      setManagementBusy(false);
    }
  };

  // 地区詳細パネルの開閉状態をチュートリアル用の localStorage フラグに反映する。
  // チュートリアル(シナリオ2)のステップ判定はバックエンドから観測できない
  // パネルの開閉状態を localStorage 経由で受け取るため、ここで橋渡しする。
  useEffect(() => {
    localStorage.setItem(TUTORIAL_FLAG_ZONE_PANEL_OPEN, "1");
    return () => {
      localStorage.setItem(TUTORIAL_FLAG_ZONE_PANEL_OPEN, "0");
    };
  }, []);

  // --- Data fetch ---
  useEffect(() => {
    if (!pathTemplate || !initialEntityId) return;

    const controller = new AbortController();
    setLoading(true);
    setError(null);

    const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<ZoneResponse>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          const zone = data.zone;
          setZoneData(zone);
          setRestrictionValues({
            allowed_bd_mask: zone.allowed_bd_mask ?? 0,
            limit_coverage_ratio: zone.limit_coverage_ratio ?? null,
            limit_volume_ratio: zone.limit_volume_ratio ?? null,
            limit_height: zone.limit_height ?? null,
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
  }, [apiBaseUrl, pathTemplate, initialEntityId]);

  // --- Render ---

  if (loading) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !zoneData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  const displayName = zoneData.name
    ? zoneData.name
    : t("zone.titleWithId", { id: zoneData.zone_id });

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={zoneIcon}
        variantName={t("zone.label", "地区")}
        name={displayName}
        showStatus={false}
      />

      {/* Management state section (2状態モデル: 自動管理/確定) */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">
          {t("zone.managementState", "管理状態")}
        </h3>
        <div className="zone-mgmt-row">
          <span
            className={
              zoneData.auto_managed
                ? "zone-mgmt-badge zone-mgmt-badge-auto"
                : "zone-mgmt-badge zone-mgmt-badge-confirmed"
            }
          >
            {zoneData.auto_managed
              ? t("zone.autoManaged", "自動管理")
              : t("zone.confirmed", "確定済み")}
          </span>
          <button
            className="zone-mgmt-toggle-btn"
            type="button"
            disabled={managementBusy}
            onClick={() => void toggleAutoManaged()}
          >
            {zoneData.auto_managed
              ? t("zone.pinAction", "この地区を確定する")
              : t("zone.unpinAction", "自動管理に戻す")}
          </button>
        </div>
        <p className="zone-mgmt-desc">
          {zoneData.auto_managed
            ? t(
                "zone.autoManagedDesc",
                "道路網の変化に合わせて、システムがこの地区の統合を自動で行うことがあります"
              )
            : t(
                "zone.confirmedDesc",
                "この地区の形状がシステムによって組み替えられることはありません（道路の追加による成長は行われます）"
              )}
        </p>
        {managementError && (
          <p className="zone-mgmt-error">
            {t("zone.managementUpdateFailed", "管理状態の更新に失敗しました")}
          </p>
        )}
      </section>

      {/* Basic info section */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("zone.basicInfo", "基本情報")}</h3>
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("zone.area", "面積")}</span>
          <span className="agent-detail-value">
            <div className="zone-area-meter">
              <div
                className="zone-area-meter-fill"
                style={{ width: `${Math.min(100, (zoneData.area / 10000) * 100)}%` }}
              />
              <span className="zone-area-meter-label">
                {t("common.sqm", { value: Math.round(zoneData.area) })}
              </span>
            </div>
          </span>

          <span className="agent-detail-label">{t("zone.zonePopulation", "居住人口")}</span>
          <span className="agent-detail-value">
            <DataList
              values={zoneData.zone_population || []}
              formatter={(val) => Math.round(val).toString()}
            />
          </span>

          <span className="agent-detail-label">{t("zone.daytimePopulation", "昼間人口")}</span>
          <span className="agent-detail-value">
            <DataList
              values={zoneData.daytime_population || []}
              formatter={(val) => Math.round(val).toString()}
            />
          </span>
        </div>
      </section>

      {/* Space chart */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("zone.spaceAmount", "フロア占有量")}</h3>
        <SpaceAmountChart
          supplies={zoneData.space_supplies || {}}
          demands={zoneData.space_demands || {}}
          supplyLabel={t("common.supply", "供給")}
          demandLabel={t("common.demand", "需要")}
        />
      </section>

      {/* Restriction fields — always disabled as edit mode is removed */}
      <RestrictionFields
        title={t("zone.restrictions", "地区規制")}
        values={restrictionValues}
        onChange={setRestrictionValues}
        disabled={true}
      />

      {templateMode && (
        <button
          className="god-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "god_edit",
              params: { extra: { entityType: "zone", entityId: String(zoneData.zone_id) } },
            });
          }}
        >
          {t("god_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
