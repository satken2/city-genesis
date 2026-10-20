/**
 * Shared construction-screen body for PathConstructionPanel.
 *
 * Draw sections common to all variant types: shape (elevation/terrain following/embankment/platform side),
 * grid settings, construction cost card (path length/estimated cost/construction time), and confirm/cancel action bar.
 * The panel places type-specific settings (parallel track configuration, station building ends, road membership, etc.) above this.
 * Receives the usePathConstruction result, grid state, and confirm/cancel handlers.
 *
 * Rationale for settings here:
 *   - Curve/straight and snapping controls belong to Unity's Ctrl radial menu, so are omitted.
 *   - Retain elevation/embankment steppers for precise numeric adjustment of selected existing nodes
 *     (Unity is the source of truth; stepper changes are sent back with change_node/change_embankment for two-way sync).
 *   - Platform side can only be specified during construction, so belongs here (shown only for single-track station
 *     variants with selectable sides). Combined double-track stations have variant-defined platform positions
 *     (side/island), so do not show the setting.
 */

import { useCallback } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { NumberStepper } from "../common/NumberStepper";
import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { isConstructionAffordable } from "../../utils/constructionFunding";
import { GridSettingsPanel } from "./GridSettingsPanel";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { FOLLOW_TERRAIN_UNSET, usePathConstruction } from "./usePathConstruction";
import {
  DIR_BOTH,
  PLATFORM_SIDE_OUTSIDE,
  PLATFORM_SIDE_INSIDE,
  PLATFORM_SIDE_BOTH,
} from "../../constants";

/** usePathConstruction return type, received in full from the panel. */
export type PathBuildState = ReturnType<typeof usePathConstruction>;

type PathBuildSectionsProps = {
  pc: PathBuildState;
  /** ID of an existing path being edited. null for new placement. Used to restore the grid's original state. */
  editPathId: number | null;

  /* ---- Grid settings state (owned by parent, synchronized with the restoration listener) ---- */
  gridMode: GridDisplayMode;
  gridSize: number;
  gridAngle: number;
  onGridModeChange: (mode: GridDisplayMode) => void;
  onGridSizeChange: (size: number) => void;
  onGridAngleChange: (angle: number) => void;
  onGridOriginChange: (origin: { x: number; z: number } | null) => void;
  onStartSetAngleMode: () => void;
  onEndSetAngleMode: () => void;

  /* ---- Confirm/cancel ---- */
  isConfirming: boolean;
  onConfirm: () => void;
  onCancel: () => void;

  /**
 * Whether the platform-side toggle (outside/inside/both) may be shown.
 * True only for new track placement (platform side cannot change after construction).
 * Actual visibility also requires pc.hasPlatformSide (a station variant with selectable platform sides).
 */
  showPlatformSide: boolean;
};

export function PathBuildSections({
  pc,
  editPathId,
  gridMode,
  gridSize,
  gridAngle,
  onGridModeChange,
  onGridSizeChange,
  onGridAngleChange,
  onGridOriginChange,
  onStartSetAngleMode,
  onEndSetAngleMode,
  isConfirming,
  onConfirm,
  onCancel,
  showPlatformSide,
}: PathBuildSectionsProps) {
  const { t } = useTranslation();

  /**
 * Elevation stepper change handler. Do not force focus to Unity with sendFocusGraphic() here,
 * to prevent the input losing focus on every typed character.
 */
  const handleHeightChange = useCallback(
    (value: number) => {
      pc.updateHeight(value);
    },
    [pc],
  );

  /** Embankment stepper change handler. As with elevation, do not call sendFocusGraphic(). */
  const handleEmbankmentHeightChange = useCallback(
    (value: number) => {
      pc.updateEmbankmentHeight(value);
    },
    [pc],
  );

  /**
 * Open the 2D path shape editor (path_shape_editor). usePathConstruction opens it automatically when
 * confirming shapes in curve mode, but allow manual opening in straight mode or during placement too.
 * If already open, only bring it forward (single policy).
 */
  const handleOpenShapeEditor = useCallback(() => {
    void invoke("open_registered_window", { windowType: "path_shape_editor" }).catch(console.error);
  }, []);

  return (
    <>
      {/*
 * Shape section. Curve/straight and snapping controls belong to Unity's Ctrl radial menu, but retain elevation
 * and embankment steppers for precise numeric adjustment of selected existing nodes.
 * Platform side belongs here because it can be set only during construction.
 * Embankments are available even when elevation is not editable (streets) and the path is not infrastructure,
 * so show this if either setting applies. Hide for expanding placement methods (grids), which follow the surface.
 */}
      {pc.selectedVariant !== null && !pc.isLayoutBuild &&
        (pc.heightEditable || !pc.isInfrastructure || pc.hasPlatformSide) && (
        <div className="pc-section">
          <div className="pc-section-title">{t("path_construction.shape", "形状")}</div>


          {/*
 * Elevation (relative height). Show only for height_editable=true variants (elevated roads, subway tracks, etc.).
 * The minimum is master min_height. Only variants with negative minima (underground) can be built below height 0.
 */}
          {pc.heightEditable && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.elevation", "標高 (Y)")}</span>
              <div className="pc-stepper-row" data-guide="path.height">
                <NumberStepper
                  value={pc.height}
                  onChange={handleHeightChange}
                  min={pc.selectedVariantData?.min_height ?? 0}
                  max={500}
                  step={0.1}
                  integerOnly={false}
                />
                <span className="pc-stepper-unit">m</span>
              </div>
            </div>
          )}

          {/*
 * Per-path terrain-following override, shown only for height_editable variants.
 * Disabling following automatically creates cuttings/tunnels through hills and bridges over valleys,
 * avoiding manual elevation adjustments. When unset, use the variant default.
 */}
          {pc.heightEditable && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.followTerrain", "地形追従")}</span>
              <div className="pc-platform-side-group">
                {[
                  { value: FOLLOW_TERRAIN_UNSET, label: t("path_construction.followTerrainDefault", "既定") },
                  { value: 1, label: t("path_construction.followTerrainFollow", "追従") },
                  { value: 0, label: t("path_construction.followTerrainNone", "非追従") },
                ].map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    data-guide="path.followTerrain"
                    data-guide-value={option.value}
                    className={`pc-platform-side-btn ${pc.followTerrain === option.value ? "active" : ""}`}
                    onClick={() => pc.setFollowTerrain(option.value)}
                    title={t(
                      "path_construction.followTerrainHelp",
                      "非追従にすると、丘に差し掛かる区間は自動的に切り通し・トンネルに、谷を渡る区間は橋になります。",
                    )}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </div>
          )}

          {/*
 * Embankment/cutting (-5 to +5 m). Hide for infrastructure paths (power lines/water mains), which do not deform terrain.
 * Keep road Y at 0 and deform Dynamic-tier terrain by this height when construction starts.
 * Hide for non-terrain-following paths, which are conceptually incompatible with embankments.
 */}
          {!pc.isInfrastructure && pc.followTerrain !== 0 && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.embankmentHeight", "盛土高さ")}</span>
              <div className="pc-stepper-row" data-guide="path.embankment">
                <NumberStepper
                  value={pc.embankmentHeight}
                  onChange={handleEmbankmentHeightChange}
                  min={-5}
                  max={5}
                  step={0.1}
                  integerOnly={false}
                />
                <span className="pc-stepper-unit">m</span>
              </div>
            </div>
          )}

          {/*
 * Platform side for station paths (single-track stations with platform_width only).
 * "Inside/outside" is resolved using the city's traffic direction (inside means right of travel for left-hand traffic).
 * For double track made from two single tracks, inside on both directions gives an island platform; outside gives side platforms.
 * Combined double-track station variants define their platform positions (side/island), so hide this setting.
 * Can only be specified during construction; cannot change afterward.
 */}
          {showPlatformSide && pc.hasPlatformSide && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("path_construction.platformSide", "ホーム設置側")}</span>
              <div className="pc-platform-side-group">
                {[
                  { value: PLATFORM_SIDE_OUTSIDE, label: t("path_construction.platformSideOutside", "外側") },
                  { value: PLATFORM_SIDE_INSIDE, label: t("path_construction.platformSideInside", "内側") },
                  { value: PLATFORM_SIDE_BOTH, label: t("path_construction.platformSideBoth", "両側") },
                ].map((option) => (
                  <button
                    key={option.value}
                    type="button"
                    data-guide="path.platformSide"
                    data-guide-value={option.value}
                    className={`pc-platform-side-btn ${pc.platformSide === option.value ? "active" : ""}`}
                    onClick={() => pc.setPlatformSide(option.value)}
                  >
                    {option.label}
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      )}

      {/* Grid settings (shown only after variant selection). */}
      {pc.selectedVariant !== null && (
        <div className="pc-section">
          <GridSettingsPanel
            mode={gridMode}
            size={gridSize}
            angle={gridAngle}
            onModeChange={onGridModeChange}
            onSizeChange={onGridSizeChange}
            onAngleChange={onGridAngleChange}
            onOriginChange={onGridOriginChange}
            originalState="STATE_EDIT_PATH"
            originalExtra={{
              // When a composite path is selected, pass the representative variant's type/variant
              // (the first component), which Unity understands, rather than a UI-only pseudo-type.
              path_type: pc.unityPathType,
              path_variant: pc.unityPathVariant,
              path_id: editPathId ?? null,
              // When resending STATE_EDIT_PATH after angle-setting mode, also pass offsets and directions
              // so the parallel-track preview does not revert to a single track
              // (omission resets them to empty).
              lateral_offsets: pc.railComponents
                ? pc.railComponents.map((c) => c.lateral_offset ?? 0)
                : [],
              travel_directions: pc.railComponents
                ? pc.railComponents.map((c) => c.travel_direction ?? DIR_BOTH)
                : [],
              build_method: pc.buildMethod,
              grid_divisions: pc.gridDivisions,
            }}
            onStartSetAngleMode={onStartSetAngleMode}
            onEndSetAngleMode={onEndSetAngleMode}
          />
        </div>
      )}

      {/*
 * Construction cost section at the panel bottom. Costs cannot be calculated until two nodes exist,
 * so show a prompt to start placement.
 */}
      {pc.selectedVariant !== null && (
        <div className="pc-section">
          <div className="pc-section-title">
            {t("path_construction.costInfo", "建設費用")}
          </div>
          {pc.nodes.length < 2 ? (
            <div className="pc-no-nodes-message">
              {t("path_construction.noNodesYet")}
            </div>
          ) : (
            <ConstructionInfoCard
              pathLength={pc.pathLength}
              estimatedCost={pc.estimatedCost}
              standardCost={pc.estimatedStandardCost}
              costRatioHelp={t("path_construction.costRatioHelp")}
              estimatedDurationDays={pc.estimatedDurationDays}
              isCostLoading={pc.isCostLoading}
              maintenanceCost={pc.maintenanceCost}
              epochRevenue={pc.epochRevenue}
              availableFunds={pc.availableFunds}
              requiredTotal={pc.estimatedCost}
              showAvailableFunds={false}
              supplyRatio={pc.supplyRatio}
              expectedDurationDays={pc.expectedDurationDays}
            />
          )}
        </div>
      )}

      {/*
 * Action buttons (cancel/confirm).
 * Keep them visible at the panel bottom while content scrolls (position: sticky; bottom: 0).
 * Show only after selecting a variant.
 */}
      {pc.selectedVariant !== null && (
        <div className="pc-action-bar">
          {pc.nodes.length >= 2 && (!pc.canConfirm || !pc.alignmentOk) && (
            <div className="pc-validation-message" role="status">
              {(pc.canConfirm ? [t("path_construction.shapeError")] : pc.confirmMessages)
                .map((message, index) => <p key={index}>{message}</p>)}
              {pc.canRetryValidation && (
                <button className="pc-btn" type="button" onClick={pc.retryValidation}>
                  {t("path_construction.validationRetry")}
                </button>
              )}
            </div>
          )}
          <div className="pc-actions">
            {!pc.isLayoutBuild && (
              <button data-guide="path.shapeEditor" className="pc-btn" type="button" onClick={handleOpenShapeEditor}>
                {t("path_construction.openShapeEditor")}
              </button>
            )}
            <button data-guide="path.cancel" className="pc-btn" type="button" onClick={onCancel}>
              {t("path_construction.cancel")}
            </button>
            <button
              className="pc-btn pc-btn-confirm"
              data-guide="path.confirm"
              // Block confirmation for insufficient funds (estimated total > available funds) and alignment violations (tight curves/steep grades).
              // The backend rejects these using the same criteria (INSUFFICIENT_FUNDS / CREATEPATH_CURVE_TOO_TIGHT / CREATEPATH_GRADE_TOO_STEEP).
              disabled={
                !pc.canConfirm
                || isConfirming
                || !pc.alignmentOk
                || !isConstructionAffordable(pc.estimatedCost, pc.availableFunds)
              }
              type="button"
              onClick={onConfirm}
            >
              {isConfirming ? t("path_construction.confirming") : t("path_construction.confirm")}
            </button>
          </div>
        </div>
      )}
    </>
  );
}
