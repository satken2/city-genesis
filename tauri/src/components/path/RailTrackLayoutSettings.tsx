/**
 * Parallel track settings (track count, per-track directions, spacing).
 *
 * Default spacing is master width (adjacent corridors without gaps), adjustable within limits with widen/narrow buttons
 * and reset to default after each confirmed path. Hide spacing for a single track, where it is meaningless.
 *
 * Show directions as one image button per track (up = forward, down = backward, double arrow = both).
 * Click cycles forward → backward → both. Even counts default to equal forward/backward halves based on city traffic direction;
 * odd counts give the center track both directions (railBuild.defaultTravelDirections).
 *
 * People movers (e.g. ropeways) render/place as one bidirectional path like roads, so hide the entire section,
 * including separators, as for nontracks such as station buildings.
 *
 * Combined double-track variants (rail/monorail with lanes >= 2, including stations) also use one path.
 * Replace settings with a note that one path represents both directions. Retain the section to avoid large
 * vertical shifts below it when switching standards.
 */

import { useTranslation } from "react-i18next";
import { PATH_CONVEYOR } from "../../constants";
import type { PathMasterEntry } from "../../types";
import { NumberStepper } from "../common/NumberStepper";
import { MIN_TRACK_COUNT, MAX_TRACK_COUNT, isMultiTrackRailVariant, isRailType } from "./railBuild";
import { PathDirectionButton } from "./PathDirectionButton";

type RailTrackLayoutSettingsProps = {
  /** Selected variant. For combined double track, show a note instead of settings. */
  variant: PathMasterEntry | null;
  trackCount: number;
  /** Effective spacing (m), read-only here. Change through onAdjustSpacing. */
  trackSpacing: number;
  canWidenSpacing: boolean;
  canShrinkSpacing: boolean;
  trackDirections: number[];
  onChangeTrackCount: (n: number) => void;
  /** Widen (+1) or narrow (-1) track spacing by one step. */
  onAdjustSpacing: (direction: 1 | -1) => void;
  onCycleDirection: (index: number) => void;
  disabled?: boolean;
};

export function RailTrackLayoutSettings({
  variant,
  trackCount,
  trackSpacing,
  canWidenSpacing,
  canShrinkSpacing,
  trackDirections,
  onChangeTrackCount,
  onAdjustSpacing,
  onCycleDirection,
  disabled = false,
}: RailTrackLayoutSettingsProps) {
  const { t } = useTranslation();

  if (!variant || !isRailType(variant.type) || variant.type === PATH_CONVEYOR) return null;

  if (isMultiTrackRailVariant(variant)) {
    return (
      <div className="pc-section">
        <div className="pc-section-title">{t("path_construction.rail.parallel", "並走")}</div>
        <div className="pc-description-text">{t("path_construction.rail.builtInDoubleTrack")}</div>
      </div>
    );
  }

  return (
    <div className="pc-section">
      <div className="pc-section-title">{t("path_construction.rail.parallel", "並走")}</div>

      {/* Track count. */}
      <div className="pc-rail-field">
        <span className="pc-settings-label">
          {t("path_construction.rail.trackCount", "線路数")}
        </span>
        <div className="pc-stepper-row">
          <NumberStepper
            value={trackCount}
            onChange={onChangeTrackCount}
            min={MIN_TRACK_COUNT}
            max={MAX_TRACK_COUNT}
            step={1}
            integerOnly
            disabled={disabled}
          />
        </div>
      </div>

      {/*
 * Directions (one image button per track).
 * No caption, but keep an empty label slot matching other setting rows so buttons stay aligned.
 */}
      <div className="pc-rail-field">
        <span className="pc-settings-label" aria-hidden="true" />
        <div className="pc-track-direction-row">
          {trackDirections.map((dir, i) => (
            <PathDirectionButton
              key={i}
              direction={dir}
              disabled={disabled}
              onClick={() => onCycleDirection(i)}
            />
          ))}
        </div>
      </div>

      {/* Track spacing (hidden for a single track, where it has no meaning). */}
      {trackCount >= 2 && (
        <div className="pc-rail-field">
          <span className="pc-settings-label">
            {t("path_construction.rail.trackSpacing", "線路間隔")}
          </span>
          <button
            type="button"
            className="pc-spacing-btn"
            disabled={disabled || !canShrinkSpacing}
            onClick={() => onAdjustSpacing(-1)}
          >
            {t("path_construction.rail.spacingShrink", "縮める")}
          </button>
          <span className="pc-spacing-value">{trackSpacing.toFixed(1)} m</span>
          <button
            type="button"
            className="pc-spacing-btn"
            disabled={disabled || !canWidenSpacing}
            onClick={() => onAdjustSpacing(1)}
          >
            {t("path_construction.rail.spacingWiden", "広げる")}
          </button>
        </div>
      )}
    </div>
  );
}
