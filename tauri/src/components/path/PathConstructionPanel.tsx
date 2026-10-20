/**
 * Path construction panel.
 *
 * Popup window_type="path_build" handles both new path placement (roads, rail, infrastructure, etc., laid by drawing lines)
 * and existing path shape editing (opened from path detail's Edit with params.extra.editPathId).
 *
 * Selection has the same three levels as building construction; the upper-left back button returns one level.
 *   1. Category tiles: categories are path_type values, grouped into roads, tracks, and infrastructure (pathBuildCategories).
 *   2. Variant cards: use icon tabs for category classifications when present, with classification names in the list.
 *   3. Construction screen: show the selected variant card first as a summary, followed by placement settings and confirm/cancel.
 * Existing path editing preserves type/variant, so starts on the construction screen with no back navigation.
 *
 * Construction flow:
 *   1. Select a card → notify the graphics program of placement mode (STATE_EDIT_PATH) through IPC.
 *   2. Place nodes in the graphics program → receive node information through IPC.
 *   3. Confirm → send to the backend (new placement continues with the same variant for the next path).
 *
 * Settings depend on the selected variant's type. Single-track rail/monorail variants have track count, directions, spacing,
 * and station platform sides. Combined double-track variants have neither (station platform positions are variant-defined
 * side/island). Choose station building ends, passage mode, and construction project tags only during new placement
 * (they are either immutable after construction or edited in path details). Set road membership afterward in path/road details.
 * Unity's controls (Ctrl radial menu and cursor HUD) handle curve/straight and snapping settings, which are absent here.
 * During station building placement, Unity disables snapping to existing roads/tracks (grid snapping remains enabled).
 */

import { useCallback, useEffect, useId, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import { emit } from "@tauri-apps/api/event";
import { useMasterData } from "../../contexts/MasterDataContext";

import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError, fetchApi } from "../../utils/api";
import { resolvePathCategoryIcon } from "../../utils/pathCategoryIcon";
import { usePathConstruction } from "./usePathConstruction";
import { useVariantAffordability } from "../../hooks/useVariantAffordability";
import { sendEditPathFinish, sendSetGrid, sendChangeDisplay, sendChangeState, sendFocusGraphic, IPC_EVENT_VIEW_GRID_TOGGLED } from "../../utils/ipc";
import type { GridDisplayMode } from "./GridSettingsPanel";
import { isInfraPathType } from "../../local-constants";
import { PATH_BUILD_METHOD_GRID, PATH_STATION_HOUSE, PLAY_MODE_SCENARIO } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";
import { tutorialVariantAllowed } from "../../tutorial/TutorialLayer";
import { buildCategoryGroups, buildCategoryVariants, buildCategoryVariantTabs, buildablePathMaster } from "./pathBuildCategories";
import { FlatTabs } from "../common/FlatTabs";
import { PathVariantTabIcon } from "./PathVariantTabIcon";
import { isRailType } from "./railBuild";
import { PathVariantCard } from "./PathVariantCard";
import { UnaffordableOverlay } from "../common/UnaffordableOverlay";
import { RailTrackLayoutSettings } from "./RailTrackLayoutSettings";
import { StationHouseEndsSelect } from "./StationHouseEndsSelect";
import { StationPassageModeSelect } from "./StationPassageModeSelect";
import { PathBuildSections } from "./PathBuildSections";
import { PathBuildMethodSection } from "./PathBuildMethodSection";
import { PathTagSection } from "./PathTagSection";
import { TAGS_CHANGED_EVENT } from "../../utils/constructionTags";
import turnBackIcon from "../../assets/images/ui/turn-back.png";
import "./PathConstructionPanel.css";
import "../building/BuildingConstructionPanel.css";

type PathConstructionPanelProps = {
  apiBaseUrl?: string;
  onClose: () => void;
  editPathId?: number | null;
};

export function PathConstructionPanel({ apiBaseUrl, onClose, editPathId }: PathConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const isNewBuild = editPathId == null;
  const pathConstruction = usePathConstruction(masterData, apiBaseUrl, editPathId ?? null);
  // Per-variant affordability guide (prevent selecting standards whose reference flat-ground length is already unaffordable).
  const { affordability: variantAffordability, refresh: refreshAffordability } =
    useVariantAffordability("paths", apiBaseUrl);
  const isVariantUnaffordable = (variantId: number) =>
    variantAffordability != null
    && !variantAffordability.unlimited
    && variantAffordability.variants[variantId]?.affordable === false;

  // Category (path_type) whose cards are displayed. null shows category tiles.
  const [selectedCategory, setSelectedCategory] = useState<number | null>(null);
  const [selectedVariantTab, setSelectedVariantTab] = useState<string | null>(null);
  const variantTabPrefix = useId();

  const selectedVariantData = pathConstruction.selectedVariantData;
  const selectedPathType = selectedVariantData?.type ?? null;

  /* ---- New placement: return Unity to normal editing state when opened. ---- */
  // Do not enter placement mode until a variant is selected, and do not leave the previous mode (e.g. bulldoze) active.
  useEffect(() => {
    if (isNewBuild) pathConstruction.deselectVariant();
    // Run once on mount (deselectVariant is stable).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isNewBuild]);

  /* ---- Existing path editing: fetch data ---- */
  // Wait for master data before selection because the variant type is looked up there and passed to placement mode.
  const masterDataLoaded = masterData != null;
  useEffect(() => {
    if (editPathId == null || !apiBaseUrl || !masterDataLoaded) return;
    const controller = new AbortController();

    fetchApi<{ path_variant: number }>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${editPathId}`, {
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        pathConstruction.selectVariant(data.path_variant);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("[PathConstructionPanel] Failed to fetch existing path:", err);
        }
      });

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editPathId, apiBaseUrl, masterDataLoaded]);

  /* ---- Grid settings state (hidden by default) ---- */
  const [gridMode, setGridMode] = useState<GridDisplayMode>("off");
  const [gridSize, setGridSize] = useState(20);
  const [gridAngle, setGridAngle] = useState(0);
  const [gridOrigin, setGridOrigin] = useState<{ x: number; z: number } | null>(null);

  /* ---- Confirmation in progress ---- */
  const [isConfirming, setIsConfirming] = useState(false);

  /* ---- Construction project tag for placed paths (empty = none). Retained during continuous placement. ---- */
  const [projectTag, setProjectTag] = useState("");

  /* ---- Save/restore grid visibility during angle-setting mode ---- */
  // Unity (GridSetManager) saves/restores snapping flags; the panel manages only grid visibility.
  const prevGridModeRef = useRef<GridDisplayMode | null>(null);
  const prevGridOriginRef = useRef<{ x: number; z: number } | null>(null);

  const handleStartSetAngleMode = useCallback(() => {
    prevGridModeRef.current = gridMode;
    prevGridOriginRef.current = gridOrigin;

    if (gridMode === "off") {
      setGridMode("line");
    }
  }, [gridMode, gridOrigin]);

  const handleEndSetAngleMode = useCallback(() => {
    if (prevGridModeRef.current !== null) {
      setGridMode(prevGridModeRef.current);
      prevGridModeRef.current = null;
    }
    if (prevGridOriginRef.current !== null) {
      setGridOrigin(prevGridOriginRef.current);
      prevGridOriginRef.current = null;
    }
  }, []);

  /* ---- Category and variant candidates ---- */
  // Tile/card availability also depends on tutorial guidance. When guidance changes,
  // usePathConstruction (internally subscribing to useGuideSnapshot) rerenders the panel, so check on each render.
  const playMode = usePlayMode(apiBaseUrl ?? "");
  const choiceMaster = useMemo(() => buildablePathMaster(masterData, playMode === PLAY_MODE_SCENARIO), [masterData, playMode]);
  const categoryGroups = useMemo(() => buildCategoryGroups(choiceMaster), [choiceMaster]);
  const variantsForCategory = useMemo(
    () => (selectedCategory === null ? [] : buildCategoryVariants(choiceMaster, selectedCategory)),
    [choiceMaster, selectedCategory],
  );
  const variantTabs = useMemo(
    () => selectedCategory === null ? [] : buildCategoryVariantTabs(choiceMaster, selectedCategory),
    [choiceMaster, selectedCategory],
  );
  // Prefer tabs with selectable candidates so tutorial-specified cards are not hidden in another tab.
  const tabAllowed = (tab: (typeof variantTabs)[number]) => tab.variants.some(v => tutorialVariantAllowed(v.id));
  const activeVariantTab = variantTabs.find(tab => tab.id === selectedVariantTab && tabAllowed(tab))
    ?? variantTabs.find(tabAllowed) ?? variantTabs[0];
  const visibleVariants = activeVariantTab?.variants ?? variantsForCategory;

  /* ---- Handlers ---- */

  /**
 * Shared handler capturing clicks across the panel.
 * After clicks on explicitly interactive elements such as buttons/cards, focus Unity so users can seamlessly
 * continue in the 3D view. Restrict targets to avoid moving focus away from elements that need it,
 * such as numeric inputs (NumberStepper inputs).
 */
  const handlePanelClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    const button = target.closest("button, [role='button']");
    if (button && button.getAttribute("role") !== "tab") {
      sendFocusGraphic().catch(() => {});
    }
  }, []);

  /** Select a variant card. While the tutorial specifies a variant to place, prevent selecting any other. */
  const handleVariantSelect = useCallback((variantId: number) => {
    if (!tutorialVariantAllowed(variantId)) return;
    pathConstruction.selectVariant(variantId);
  }, [pathConstruction]);

  /**
 * Confirm button handler. Tauri sends path data directly to the backend.
 *
 * New placement (editPathId == null) uses continuous placement: keep the panel open and the selected variant,
 * then return to a state ready for placing the next path.
 * Existing path editing (editPathId != null) closes the panel immediately after confirmation.
 * On failure, report the error and allow retrying.
 */
  const handleConfirm = useCallback(async () => {
    setIsConfirming(true);
    try {
      const createdPathIds = await pathConstruction.confirmPath({
        ...(projectTag ? { tag: projectTag } : {}),
      });
      if (projectTag && createdPathIds.length > 0) {
        void emit(TAGS_CHANGED_EVENT, { tag: projectTag }).catch(console.error);
      }
      // On success, notify Unity that editing ended and request a data reload.
      await sendEditPathFinish();
      // Close the confirmed shape's 2D editor, whose work is complete (no effect if not open).
      void invoke("close_registered_window", { windowType: "path_shape_editor" }).catch(console.error);
      // Ordering increased committed funds, so refresh affordability guides for each standard.
      refreshAffordability();
      if (editPathId != null) {
        // Existing path editing: close the panel after confirmation.
        // Return to STATE_NORMAL before switching visual modes.
        // Outside STATE_NORMAL, view:change_view changes are not stored in _normalStateMemory,
        // so a later STATE_NORMAL from Rust would revert to default.
        await sendChangeState("STATE_NORMAL");
        await sendSetGrid("off");
        // A road was created; enable planned-object display so the user can inspect the result.
        await sendChangeDisplay("road", "default", null, ["plan"]);
        onClose();
      } else {
        // New placement: retain the selected variant and prepare the next path in continuous placement mode.
        pathConstruction.continuePlacement();
        setIsConfirming(false);
        void showNotification({
          message: t("path_construction.placedContinue"),
          durationMs: 3000,
        });
      }
    } catch (err) {
      setIsConfirming(false);
      let displayMessage: string;
      if (err instanceof ApiError) {
        const i18nKey = `errors.${err.code}`;
        displayMessage =
          i18nKey !== "errors.UNKNOWN_ERROR"
            ? t(i18nKey, { defaultValue: err.message })
            : err.message;
      } else {
        displayMessage = t("path_construction.confirmFailedUnknown");
      }
      void showNotification({ message: displayMessage, durationMs: 8000 });
    }
  }, [editPathId, onClose, pathConstruction, projectTag, refreshAffordability, t]);

  /**
 * Cancel button handler.
 * Keep the variant selected; clear only Unity's provisional shape (unconfirmed node sequence), returning to placement
 * with the same variant (also retain track spacing adjustments because users may be redrawing the same path).
 * continuePlacement resets placement state and resends STATE_EDIT_PATH, clearing Unity's provisional geometry
 * while preserving placement mode.
 */
  const handleCancel = useCallback(() => {
    pathConstruction.continuePlacement();
  }, [pathConstruction]);

  /* ---- Automatically disable the grid when selecting infrastructure paths ---- */
  useEffect(() => {
    if (isInfraPathType(selectedPathType)) { // Power, water, and pipelines (PATH_TYPES_INFRA).
      setGridMode("off");
    }
  }, [selectedPathType]);

  /* ---- Ensure the grid is off when the component is disposed ---- */
  useEffect(() => {
    return () => {
      sendSetGrid("off").catch(() => {});
    };
  }, []);

  /* ---- Synchronize visibility with Unity radial-menu grid toggles ---- */
  // Unity's GridOverlay links grid visibility and grid snapping.
  // The radial menu emits view:grid_toggled when toggled, so
  // update only the panel grid settings visibility (gridMode).
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;
    appWindow
      .listen<{ visible: boolean }>(IPC_EVENT_VIEW_GRID_TOGGLED, (event) => {
        setGridMode((prev) => {
          if (event.payload.visible) {
            return prev === "off" ? "line" : prev;
          }
          return "off";
        });
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);
    return () => { unlisten?.(); };
  }, []);

  /* ---- Notify Unity when grid settings change ---- */
  useEffect(() => {
    if (pathConstruction.selectedVariant !== null) {
      sendSetGrid(gridMode, gridSize, gridAngle, gridOrigin?.x, gridOrigin?.z).catch(() => {});
    } else {
      // Always hide the grid when no variant is selected (including deselection through the back button).
      // Centralize the off notification here so deselection paths bypassing handleCancel also clear the grid.
      sendSetGrid("off").catch(() => {});
    }
  }, [gridMode, gridSize, gridAngle, gridOrigin, pathConstruction.selectedVariant]);

  /* ---- Hint bar display ---- */

  /**
 * Show placement instructions in the hint bar when the panel opens or the selected variant changes.
 * Station building hints explain their relationship to lines and platforms; height_editable variants include
 * height shortcut instructions. Clear hints when the panel closes.
 */
  const hintKey = selectedPathType === PATH_STATION_HOUSE
    ? "path_construction.stationHouseHint"
    : pathConstruction.buildMethod === PATH_BUILD_METHOD_GRID
      ? "path_construction.gridHint"
    : pathConstruction.heightEditable
      ? "hint.pathConstructionElevated"
      : "hint.pathConstruction";
  useEffect(() => {
    let cancelled = false;
    setHintbarText(t(hintKey)).catch((err) => {
      if (!cancelled) {
        console.error("[PathConstruction] setHintbarText failed:", err);
      }
    });
    return () => {
      cancelled = true;
      void setHintbarText("");
    };
  }, [t, hintKey]);

  /* ---- Master data not loaded ---- */

  if (!masterData) {
    return <p className="pc-muted">{t("common.loading")}</p>;
  }

  /* ---- Rendering ---- */

  if (selectedVariantData === null) {
    // Existing path editing starts on the construction screen once the variant loads.
    if (!isNewBuild) {
      return <p className="pc-muted">{t("common.loading")}</p>;
    }
    return (
      <div className="pc-layout" onClick={handlePanelClick}>
        {selectedCategory === null ? (
          categoryGroups.map(({ group, categories }) => (
            <div key={group} className="pc-section">
              <div className="pc-section-title">{t(`path_construction.group.${group}`)}</div>
              <div className="bc-tile-grid">
                {categories.map((category) => (
                  <CategoryTile
                    key={category}
                    category={category}
                    icon={resolvePathCategoryIcon(category)}
                    name={t(`path_construction.category.${category}`)}
                    disabled={!buildCategoryVariants(choiceMaster, category).some((v) => tutorialVariantAllowed(v.id))}
                    onClick={() => { setSelectedCategory(category); setSelectedVariantTab(null); }}
                  />
                ))}
              </div>
            </div>
          ))
        ) : (
          <div className="pc-section">
            <div className="bc-selection-header">
              <BackButton onClick={() => setSelectedCategory(null)} />
            </div>
            {activeVariantTab && (
              <div className="pc-variant-tabs">
                <FlatTabs
                  tabs={variantTabs.map(tab => ({
                    id: tab.id,
                    label: t(tab.labelKey),
                    icon: <PathVariantTabIcon icon={tab.icon} />,
                    disabled: !tabAllowed(tab),
                    guide: { name: "path.variant", values: tab.variants.map(variant => variant.id) },
                  }))}
                  active={activeVariantTab.id}
                  onChange={setSelectedVariantTab}
                  label={t(`path_construction.category.${selectedCategory}`)}
                  iconOnly
                  idPrefix={variantTabPrefix}
                />
              </div>
            )}
            <div
              role={activeVariantTab ? "tabpanel" : undefined}
              id={activeVariantTab ? `${variantTabPrefix}-panel-${activeVariantTab.id}` : undefined}
              aria-labelledby={activeVariantTab ? `${variantTabPrefix}-tab-${activeVariantTab.id}` : undefined}
              className={activeVariantTab ? "pc-variant-tab-panel" : undefined}
            >
              {activeVariantTab && <h2 className="pc-variant-tab-title">{t(activeVariantTab.labelKey)}</h2>}
              <div className="bc-variant-card-list">
                {visibleVariants.map(({ id, data }) => {
                  const unaffordable = isVariantUnaffordable(id);
                  return (
                    <UnaffordableOverlay key={id} unaffordable={unaffordable}>
                      <PathVariantCard
                        variantId={id}
                        entry={data}
                        disabled={unaffordable || !tutorialVariantAllowed(id)}
                        onClick={() => handleVariantSelect(id)}
                      />
                    </UnaffordableOverlay>
                  );
                })}
              </div>
            </div>
          </div>
        )}
      </div>
    );
  }

  return (
    <div className="pc-layout" onClick={handlePanelClick}>
      {/* Selected variant summary. Back returns to the category's card list. */}
      <div className="pc-section">
        {isNewBuild && <BackButton onClick={pathConstruction.deselectVariant} />}
        <PathVariantCard entry={selectedVariantData} variantId={pathConstruction.selectedVariant ?? undefined} />
      </div>

      {/* Choose the placement method only for new paths (existing path editing changes the polyline itself). */}
      {isNewBuild && (
        <PathBuildMethodSection
          pathType={selectedPathType}
          buildMethod={pathConstruction.buildMethod}
          gridDivisions={pathConstruction.gridDivisions}
          onChangeBuildMethod={pathConstruction.changeBuildMethod}
          onChangeGridDivision={pathConstruction.changeGridDivision}
        />
      )}

      {/* Set station building ends and passage mode during construction (ends cannot change after construction starts). */}
      {isNewBuild && selectedPathType === PATH_STATION_HOUSE && (
        <div className="pc-section">
          <StationHouseEndsSelect value={pathConstruction.houseEnds} onChange={pathConstruction.setHouseEnds} />
          <StationPassageModeSelect value={pathConstruction.passageMode} onChange={pathConstruction.setPassageMode} />
        </div>
      )}

      {/* Track count, directions, and spacing apply only to parallel track configurations (render nothing for nontracks or people movers). */}
      {isNewBuild && (
        <RailTrackLayoutSettings
          variant={selectedVariantData}
          trackCount={pathConstruction.trackCount}
          trackSpacing={pathConstruction.trackSpacing}
          canWidenSpacing={pathConstruction.canWidenTrackSpacing}
          canShrinkSpacing={pathConstruction.canShrinkTrackSpacing}
          trackDirections={pathConstruction.trackDirections}
          onChangeTrackCount={pathConstruction.changeTrackCount}
          onAdjustSpacing={pathConstruction.adjustTrackSpacing}
          onCycleDirection={pathConstruction.cycleTrackDirection}
        />
      )}

      {/* Do not change tags during existing path shape editing (use path details to reassign tags). */}
      {isNewBuild && (
        <PathTagSection
          apiBaseUrl={apiBaseUrl ?? ""}
          value={projectTag}
          onChange={setProjectTag}
          disabled={isConfirming}
        />
      )}

      {/*
 * Shared body below the description card (shape, grid, cost, confirm/cancel).
 * Platform sides can only be specified during construction, so show them only for new track placement
 * (and only station variants with selectable sides).
 */}
      <PathBuildSections
        pc={pathConstruction}
        editPathId={editPathId ?? null}
        gridMode={gridMode}
        gridSize={gridSize}
        gridAngle={gridAngle}
        onGridModeChange={setGridMode}
        onGridSizeChange={setGridSize}
        onGridAngleChange={setGridAngle}
        onGridOriginChange={setGridOrigin}
        onStartSetAngleMode={handleStartSetAngleMode}
        onEndSetAngleMode={handleEndSetAngleMode}
        isConfirming={isConfirming}
        onConfirm={handleConfirm}
        onCancel={handleCancel}
        showPlatformSide={isNewBuild && isRailType(selectedPathType)}
      />
    </div>
  );
}

/** Category tile. Reserve the icon space even without an image (same styling as building construction category tiles). */
function CategoryTile({ category, icon, name, disabled, onClick }: {
  category: number;
  icon: string;
  name: string;
  disabled: boolean;
  onClick: () => void;
}) {
  return (
    <button className="bc-tile" type="button" data-guide="path.type" data-guide-value={category} disabled={disabled} onClick={onClick}>
      <span className="bc-tile-icon">{icon && <img src={icon} alt="" draggable={false} />}</span>
      <span className="bc-tile-name">{name}</span>
    </button>
  );
}

/** Button returning one selection level back (same styling as building construction). */
function BackButton({ onClick }: { onClick: () => void }) {
  const { t } = useTranslation();
  return (
    <button className="bc-header-btn bc-back-btn" type="button" data-guide="path.back" onClick={onClick}>
      <img src={turnBackIcon} alt="" className="bc-header-btn-icon" draggable={false} />
      <span>{t("path_construction.back")}</span>
    </button>
  );
}
