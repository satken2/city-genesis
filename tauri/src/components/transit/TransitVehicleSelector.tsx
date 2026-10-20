/**
 * Public transport vehicle appearance selector
 *
 * Tiles with preview images select each line's player-chosen appearance (API vehicle_model).
 * Only Unity rendering/running sounds use it; capacity, cost, and speed remain unchanged.
 *
 * Choices are authoritative in data/transitVehicleModels.json (line type, i.e. section path
 * variant, mapped to appearance options). variant is the appearance number in Unity
 * model_weights.json; prop is the free-placement alias prop type ID depicting its leading car,
 * whose thumbnail is used as the preview. Match Unity's vehicle types in
 * Resources/Config/transit_vehicle_models.json and numbers in model_weights.json.
 * For out-of-catalog numbers, Unity falls back to selection based on line ID and train number.
 *
 * Used in both left-docked creation (bc-*) and right-side details (agent-detail-*),
 * so tiles use translucent neutral colors without a background.
 */

import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import catalog from "../../data/transitVehicleModels.json";
import { resolvePropThumbnail } from "../../utils/propThumbnail";
import "./TransitVehicleSelector.css";

/** One vehicle appearance option. */
export type TransitVehicleOption = {
  /** Vehicle appearance number from Unity model_weights.json (= API vehicle_model). */
  variant: number;
  /** Free-placement alias prop type ID used for the preview image (assets/images/props/{prop}.png). */
  prop: number;
  /** Display-name i18n key (transit.vehicleModels.<key>). */
  key: string;
};

const OPTIONS_BY_PATH_VARIANT: Record<string, TransitVehicleOption[]> = catalog;

/** Appearance options for a line type (section path variant). Empty for types outside the catalog. */
export function transitVehicleOptions(pathVariant: number): TransitVehicleOption[] {
  return OPTIONS_BY_PATH_VARIANT[String(pathVariant)] ?? [];
}

/** Initially selected appearance for creation: first option, or 0 for types without options. */
export function defaultTransitVehicleModel(pathVariant: number): number {
  return transitVehicleOptions(pathVariant)[0]?.variant ?? 0;
}

/** Preview image. Show fallback if ungenerated or loading fails. */
function VehicleThumbnail({ prop, className, fallback }: { prop: number; className?: string; fallback: ReactNode }) {
  const [failed, setFailed] = useState(false);
  const url = resolvePropThumbnail(prop);
  return url && !failed ? (
    <img className={className} src={url} alt="" loading="lazy" draggable={false} onError={() => setFailed(true)} />
  ) : (
    <>{fallback}</>
  );
}

export function TransitVehicleSelector({
  pathVariant,
  value,
  onChange,
  disabled = false,
}: {
  pathVariant: number;
  value: number;
  onChange: (vehicleModel: number) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  const options = transitVehicleOptions(pathVariant);
  if (options.length === 0) return null;

  return (
    <div className="tvs-grid" role="group" aria-label={t("transit.vehicleModel")}>
      {options.map((option) => {
        const name = t(`transit.vehicleModels.${option.key}`);
        return (
          <button
            key={option.variant}
            type="button"
            className="tvs-tile"
            disabled={disabled}
            aria-pressed={value === option.variant}
            title={name}
            onClick={() => onChange(option.variant)}
          >
            <span className="tvs-thumbnail">
              <VehicleThumbnail
                key={option.prop}
                prop={option.prop}
                fallback={<span className="tvs-thumbnail-missing">{t("prop_placement.noThumbnail")}</span>}
              />
            </span>
            <span className="tvs-name">{name}</span>
          </button>
        );
      })}
    </div>
  );
}

/** Read-only selected appearance with a small preview and name. Out-of-catalog values display "—". */
export function TransitVehicleLabel({ pathVariant, value }: { pathVariant: number; value: number }) {
  const { t } = useTranslation();
  const option = transitVehicleOptions(pathVariant).find((o) => o.variant === value);
  if (!option) return <>—</>;
  return (
    <span className="tvs-label">
      <VehicleThumbnail key={option.prop} prop={option.prop} className="tvs-label-thumbnail" fallback={null} />
      <span>{t(`transit.vehicleModels.${option.key}`)}</span>
    </span>
  );
}
