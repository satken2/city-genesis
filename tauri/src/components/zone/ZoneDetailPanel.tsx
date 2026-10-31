import { requireApiSuccess } from "../../utils/api";
import { describeError } from "../../utils/errors";
/**
 * District (Zone) detail panel
 *
 * Dedicated panel for the window_type="zone_detail" popup.
 * Show basic information and edit name, identifying color, and building regulations.
 *
 * Road assignments are edited on the district editor's city map, opened from the icon panel, so this panel does
 * not handle them.
 */
import { PLAY_MODE_SCENARIO, ROAD_COLOR_PALETTE } from "../../constants";


import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import type { ZoneData, ZoneResponse, ZoneSlotStarvation } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { usePlayMode } from "../../hooks/usePlayMode";
import { sendCameraFocus, sendZoneUpdated } from "../../utils/ipc";
import { InfoHeader } from "../common/InfoHeader";
import { ColorSwatchPicker } from "../common/ColorSwatchPicker";
import { DataList } from "../common/DataList";
import { SpaceAmountChart } from "../common/SpaceAmountChart";
import { RestrictionFields, type RestrictionValues } from "../parcel/RestrictionFields";
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
  const scenarioMode = usePlayMode(apiBaseUrl) === PLAY_MODE_SCENARIO;

  const [zoneData, setZoneData] = useState<ZoneData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Waiting demand blocked by insufficient parcel size, returned only as supplementary data by GET /zones/{id}.
  // PATCH responses omit it, so retain the initially fetched value.
  const [slotStarvation, setSlotStarvation] = useState<ZoneSlotStarvation[]>([]);

  const [restrictionValues, setRestrictionValues] = useState<RestrictionValues>({
    allowed_bd_mask: 0,
    limit_coverage_ratio: null,
    limit_volume_ratio: null,
    limit_height: null,
  });

  // Building regulation save state and failure display
  const [restrictionSaving, setRestrictionSaving] = useState(false);
  const [restrictionSaveError, setRestrictionSaveError] = useState<string | null>(null);

  const [colorSaving, setColorSaving] = useState(false);
  const [colorSaveError, setColorSaveError] = useState(false);

  // Compare against saved values so reverting an input also clears its changed state.
  const restrictionsChanged = zoneData !== null && (
    restrictionValues.allowed_bd_mask !== (zoneData.allowed_bd_mask ?? 0) ||
    restrictionValues.limit_coverage_ratio !== (zoneData.limit_coverage_ratio ?? null) ||
    restrictionValues.limit_volume_ratio !== (zoneData.limit_volume_ratio ?? null) ||
    restrictionValues.limit_height !== (zoneData.limit_height ?? null)
  );

  // Notify district refresh only after a successful save, synchronizing Unity's label, regulation icons, and outline.
  const patchZone = async (zoneId: number, changes: Partial<ZoneData>) => {
    const data = await fetchApi<ZoneResponse>(
      buildApiUrl(apiBaseUrl, "/api/v1/zones/{id}", String(zoneId)),
      {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(changes),
      }
    );
    setZoneData(data.zone);
    await sendZoneUpdated(data.zone.zone_id);
    return data.zone;
  };

  const saveColor = async (color: string) => {
    if (!zoneData || colorSaving || color === zoneData.color.toLowerCase()) return;
    setColorSaving(true);
    setColorSaveError(false);
    try {
      await patchZone(zoneData.zone_id, { color });
    } catch {
      setColorSaveError(true);
    } finally {
      setColorSaving(false);
    }
  };

  // District regulations cannot be unset because parcels inherit them, so
  // reject empty fields before sending a save request.
  const saveRestrictions = async () => {
    if (!zoneData || restrictionSaving || !restrictionsChanged) return;
    if (
      restrictionValues.limit_coverage_ratio == null ||
      restrictionValues.limit_volume_ratio == null ||
      restrictionValues.limit_height == null
    ) {
      setRestrictionSaveError(
        t("zone.restrictionRequired", "建ぺい率・容積率・高さは空にできません")
      );
      return;
    }
    setRestrictionSaving(true);
    setRestrictionSaveError(null);
    try {
      const zone = await patchZone(zoneData.zone_id, {
        allowed_bd_mask: restrictionValues.allowed_bd_mask,
        limit_coverage_ratio: restrictionValues.limit_coverage_ratio,
        limit_volume_ratio: restrictionValues.limit_volume_ratio,
        limit_height: restrictionValues.limit_height,
      });
      setRestrictionValues({
        allowed_bd_mask: zone.allowed_bd_mask ?? 0,
        limit_coverage_ratio: zone.limit_coverage_ratio ?? null,
        limit_volume_ratio: zone.limit_volume_ratio ?? null,
        limit_height: zone.limit_height ?? null,
      });
    } catch {
      setRestrictionSaveError(t("zone.restrictionUpdateFailed", "地区規制の更新に失敗しました"));
    } finally {
      setRestrictionSaving(false);
    }
  };

  // Rename the district. Districts are city administrative divisions and can always be renamed.
  const saveName = async (next: string) => {
    if (!zoneData) return;
    await patchZone(zoneData.zone_id, { name: next });
  };

  // --- Data fetch ---
  useEffect(() => {
    if (!pathTemplate || !initialEntityId) return;

    const controller = new AbortController();
    setLoading(true);
    setError(null);
    setColorSaveError(false);

    const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

    fetch(url, { signal: controller.signal })
      .then(async (res) => {
        await requireApiSuccess(res);
        return res.json() as Promise<ZoneResponse>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          const zone = data.zone;
          setZoneData(zone);
          setSlotStarvation(data.slot_starvation ?? []);
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
          setError(describeError(err));
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
        onNameSave={saveName}
        nameValue={zoneData.name ?? ""}
        onFocusCamera={
          zoneData.polygon.length > 0
            ? () => void sendCameraFocus("zone_detail", zoneData.polygon).catch(console.error)
            : undefined
        }
      />

      {/* Basic info section */}
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("zone.basicInfo", "基本情報")}</h3>
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("zone.color")}</span>
          <span className="agent-detail-value zone-color-value">
            <ColorSwatchPicker
              value={zoneData.color.toLowerCase()}
              presets={ROAD_COLOR_PALETTE}
              disabled={colorSaving}
              onChange={(color) => void saveColor(color)}
            />
            {colorSaveError && <p className="zone-mgmt-error" role="alert">{t("zone.colorUpdateFailed")}</p>}
          </span>

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
        {/*
 * Insufficient parcel size warning: waiting tenants exist, but no parcel in the district can provide the
 * required floor area in a single building, blocking construction (slot starvation).
 */}
        {slotStarvation.length > 0 && (
          <div className="zone-starvation-warning">
            <p className="zone-starvation-warning-title">
              {t("zone.slotStarvationTitle", "建設できない入居待ち需要があります")}
            </p>
            {slotStarvation.map((item) => (
              <p
                key={`${item.space_type}-${item.variant}`}
                className="zone-starvation-warning-row"
              >
                {t("zone.slotStarvationWarning", {
                  tenant: item.tenant_name,
                  required: Math.ceil(item.required),
                  max: Math.floor(item.max_capacity),
                })}
              </p>
            ))}
          </div>
        )}
      </section>

      {/* Restriction fields */}
      <RestrictionFields
        title={t("zone.restrictions", "建築規制")}
        values={restrictionValues}
        onChange={setRestrictionValues}
        disabled={restrictionSaving}
        onSave={() => void saveRestrictions()}
        saving={restrictionSaving}
        saveDisabled={!restrictionsChanged}
        saveError={restrictionSaveError}
      />

      {scenarioMode && (
        <button
          className="property-edit-open-btn"
          type="button"
          onClick={() => {
            void invoke("open_registered_window", {
              windowType: "property_edit",
              params: { extra: { entityType: "zone", entityId: String(zoneData.zone_id) } },
            });
          }}
        >
          {t("property_edit.openEditor", "Edit properties")}
        </button>
      )}
    </div>
  );
}
