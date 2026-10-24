/**
 * Building construction panel
 *
 * Dedicated panel in the window_type="building_construction" popup. Select a category
 * (master bd_category) and building (variant), set floors, and preview its shape.
 *
 * Flow:
 * 1. Select a category tile to show its building cards.
 * 2. Select a building card to replace the list with a summary and construction settings.
 *    The upper-left Back button returns one level at a time: building cards, then category tiles.
 * 3. Send STATE_EDIT_BUILDING to the graphics module via IPC.
 * 4. Graphics selection of a parcel sends edit_building.select_parcel.
 *    Free/roadside placement selected through Ctrl sends the rectangle and position in placement_state.
 * 5. Display shape preview and information.
 * 6. Changing floors sends edit_building.update_floors.
 * 7. [Confirm construction] sends directly from UI to backend (same responsibilities as road construction):
 *    parcel mode = POST /buildings, direct placement = POST /buildings/direct,
 *    existing building editing = PATCH /agents/{id}. On success, session:reload asks Unity to redraw.
 *
 * Opening from City Hall's relocation button (relocateCityHall) fixes selection to City Hall in
 * relocation mode. Controls and requests match new placement; replacing existing City Hall
 * is handled by the backend as a free, immediate relocation.
 */
import { PLAY_MODE_SCENARIO, PLAY_MODE_NORMAL } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";


import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import type { AgentMasterEntry, LandmarkMasterEntry, ParcelData } from "../../types";
import { resolveBdCategoryIcon } from "../../utils/bdCategoryIcon";
import { resolveParcelIcon } from "../../utils/parcelIcon";

import { setHintbarText } from "../../utils/hintbar";
import {
  sendChangeState,
  sendChangeDisplay,
  sendEditBuildingUpdateFloors,
  sendEditBuildingUpdateLandmark,
  sendEditBuildingUpdateShape,
  sendEditBuildingUpdateEffectRange,
  sendBuildingPlacementCommand,
  IPC_EVENT_BUILDING_PLACEMENT_STATE,
  sendFocusGraphic,
  sendSessionReload,
  sendPickCopySource,
  sendHighlightObjects,
  sendClearHighlights,
  IPC_EVENT_EDIT_BUILDING_SELECT_PARCEL,
  IPC_EVENT_EDIT_BUILDING_INVALID_PARCEL,
  IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE,
} from "../../utils/ipc";
import type { BuildingPlacementState, EffectRangeRegion, PathInterferenceHighlight } from "../../utils/ipc";
import { fetchApi, ApiError } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import { TagPicker } from "../common/TagPicker";
import { TAGS_CHANGED_EVENT } from "../../utils/constructionTags";

import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { isConstructionAffordable } from "../../utils/constructionFunding";
import { useVariantAffordability } from "../../hooks/useVariantAffordability";
import { BuildingFacilityInfoCard } from "./BuildingFacilityInfoCard";
import { BuildingVariantCard } from "./BuildingVariantCard";
import { ParcelSpecs } from "./ParcelSpecs";
import { UnaffordableOverlay } from "../common/UnaffordableOverlay";
import { showNotification } from "../../utils/notification";
import { BD_CATEGORY_OTHER, PARCEL_LAND, VARIANT_CITY } from "../../constants";
import { isGradingVariant } from "../grading/grading";
import copyIcon from "../../assets/images/ui/copy.png";
import turnBackIcon from "../../assets/images/ui/turn-back.png";
import "./BuildingConstructionPanel.css";

// Fallback for direct placement variants without default_width (m).
// Match backend crud_buildings.DEFAULT_BUILDING_WIDTH.
const DEFAULT_BUILDING_WIDTH = 20.0;

// Interference highlight source (Unity HighlightManager source layer name).
// Must match this window's windowType, since window.rs sends cleanup when it closes.
const HIGHLIGHT_SOURCE = "building_construction";

// Cross-window event fired by OK in the land acquisition confirmation window
// (BuildingDirectAcquisitionConfirmWindow). The panel subscribes and commits with consent.
const EVENT_ACQUISITION_CONFIRMED = "building_direct:acquisition_confirmed";

// Normalize rotation degrees to [0, 360). The rotation stepper permits negative and over-360 values,
// so apply mod 360 here before committing (e.g. 450→90, -90→270).
function normalizeAngle(angle: number): number {
  return ((angle % 360) + 360) % 360;
}

type BuildingPreviewData = {
  polygon: number[][];
  height: number;
  center_point: number[];
  area?: number;
};

type BuildingConstructionPanelProps = {
  apiBaseUrl: string;
  onClose: () => void;
  editAgentId?: number | null;
  /** Whether opened from City Hall's relocation button. Placement controls match new construction; confirmation relocates it. */
  relocateCityHall?: boolean;
};

/* ---- Main component ---- */

export function BuildingConstructionPanel({ apiBaseUrl, onClose, editAgentId, relocateCityHall = false }: BuildingConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  // Funding estimate by variant (do not allow tiles for facilities unaffordable even at minimum size).
  const { affordability: variantAffordability } = useVariantAffordability("buildings", apiBaseUrl);
  const isVariantUnaffordable = (variantId: number) =>
    variantAffordability != null
    && !variantAffordability.unlimited
    && variantAffordability.variants[variantId]?.affordable === false;
  const appWindow = useMemo(() => getCurrentWindow(), []);

  // Category whose building tiles are displayed (BD_CATEGORY_*). Null displays category tiles.
  const [selectedCategory, setSelectedCategory] = useState<number | null>(null);
  // bd_type used to filter existing-building edit options (the edited building's value). Null for new construction.
  const [editBdType, setEditBdType] = useState<number | null>(null);
  const [selectedVariant, setSelectedVariant] = useState<number | null>(null);
  const [floors, setFloors] = useState(1);
  // Landmark (landmarks master ID). Selecting a variant with a landmark axis initializes the lowest unplaced ID;
  // variants without that axis keep null (omit it from the request, so the backend commits 0).
  const [landmark, setLandmark] = useState<number | null>(null);
  // IDs of landmarks already in the city. Each landmark is limited to one per city, so disable those options.
  // On fetch failure, leave empty without blocking selection; the backend performs the final rejection.
  const [placedLandmarkIds, setPlacedLandmarkIds] = useState<number[]>([]);
  // Construction project tag for the new building (empty means none). Save together in the creation request.
  const [projectTag, setProjectTag] = useState("");
  // Building coverage ratio (0.0-1.0). Maximum footprint area relative to site area, used for preview shapes and costs.
  const [coverageRatio, setCoverageRatio] = useState(0.6);
  // Appearance variants (visual_variant1/2/3). Values from 0.0 to 1.0 determine appearance.
  // New construction initializes to 0.5 on variant selection; sliders adjust it, then confirmation sends it to the backend.
  // Edit mode loads existing values. If GET /agents/{id} omits visual_variant, retain null;
  // null fields are not sent on confirmation, preserving appearance unless the player moves a slider.
  const [visualVariant, setVisualVariant] = useState<number | null>(null);
  const [visualVariant2, setVisualVariant2] = useState<number | null>(null);
  const [visualVariant3, setVisualVariant3] = useState<number | null>(null);
  const [parcelId, setParcelId] = useState<number | null>(null);
  const [parcelData, setParcelData] = useState<ParcelData | null>(null);
  const [isParcelLoading, setIsParcelLoading] = useState(false);
  // Fixed direct-placement position. placementMode determines method; null means unfixed cursor tracking.
  const [pickedPosition, setPickedPosition] = useState<number[] | null>(null);
  // Direct-placement footprint rectangle (width, depth, rotation). Initialize from default_width on variant selection.
  const [rectWidth, setRectWidth] = useState(DEFAULT_BUILDING_WIDTH);
  const [rectDepth, setRectDepth] = useState(DEFAULT_BUILDING_WIDTH);
  const [rectAngle, setRectAngle] = useState(0);
  const [placementMode, setPlacementMode] = useState<BuildingPlacementState["mode"]>("parcel");
  const [placementRevision, setPlacementRevision] = useState(-1);
  const [validatedDirectKey, setValidatedDirectKey] = useState<string | null>(null);
  const [placementCommandPending, setPlacementCommandPending] = useState(false);
  const latestPlacementRevision = useRef(-1);
  const pendingPlacementRequest = useRef<string | null>(null);
  const placementRequestSequence = useRef(0);
  // Direct-placement validity (valid / invalid_reason from backend preview-direct).
  const [directValid, setDirectValid] = useState(false);
  const [directInvalidReason, setDirectInvalidReason] = useState<string | null>(null);
  // Confirmation allowed if automatic land acquisition resolves overlaps with private parcels
  // (preview-direct requires_acquisition). The button stays enabled but opens confirmation before committing.
  const [directRequiresAcquisition, setDirectRequiresAcquisition] = useState(false);
  // Acquisition target count and estimated cost (non-null only when requires_acquisition=True).
  const [directAcquisition, setDirectAcquisition] = useState<{
    count: number;
    estimatedCost: number;
  } | null>(null);
  // Site check for parcel-based placement, including copy construction (preview site_invalid_reason).
  // A private parcel with a building can be confirmed with consent to acquisition (siteAcquisition non-null);
  // other reasons block confirmation. New construction only; edits keep their own site.
  const [siteInvalidReason, setSiteInvalidReason] = useState<string | null>(null);
  const [siteAcquisition, setSiteAcquisition] = useState<{
    count: number;
    estimatedCost: number;
  } | null>(null);
  /** Direct placement remains active before fixing a point, allowing numeric editing during position selection. */
  const isDirectMode = editAgentId == null && placementMode !== "parcel";
  // Acquisition the current placement requires (direct placement or the selected parcel).
  const pendingAcquisition = isDirectMode ? directAcquisition : siteAcquisition;
  const requiresAcquisition = isDirectMode ? directRequiresAcquisition : siteAcquisition !== null;
  const siteBlocked = siteInvalidReason !== null && siteAcquisition === null;
  const [buildingPreview, setBuildingPreview] = useState<BuildingPreviewData | null>(null);
  const [estimatedCost, setEstimatedCost] = useState<number | null>(null);
  // Standard cost on perfectly flat terrain (zero slope), used to show estimated cost as a multiple of standard cost.
  const [estimatedStandardCost, setEstimatedStandardCost] = useState<number | null>(null);
  // Estimated construction time (days). Uses the same display structure as the road panel.
  const [estimatedDurationDays, setEstimatedDurationDays] = useState<number | null>(null);
  // Construction service procurement fulfillment (0-1) and projected construction time accounting for it (days).
  // Used for warnings because low fulfillment prevents construction from following estimated schedules.
  const [supplyRatio, setSupplyRatio] = useState<number | null>(null);
  const [expectedDurationDays, setExpectedDurationDays] = useState<number | null>(null);
  // City funds available for orders (cash minus savings target minus existing commitments), used to preview the funding check.
  const [availableFunds, setAvailableFunds] = useState<number | null>(null);
  // Total required for funding check = construction cost plus land acquisition estimate (automatic private-parcel acquisition is also city spending).
  const requiredTotal =
    estimatedCost == null ? null : estimatedCost + (pendingAcquisition?.estimatedCost ?? 0);
  const isAffordable = isConstructionAffordable(requiredTotal, availableFunds);
  // Returned only for TYPE_STANDARD (ordinary facilities): estimated production, employment, and operating costs at full operation, all per simulation_days.
  const [estimatedMaxProduction, setEstimatedMaxProduction] = useState<number | null>(null);
  const [estimatedEmployment, setEstimatedEmployment] = useState<number | null>(null);
  const [estimatedOperatingCost, setEstimatedOperatingCost] = useState<number | null>(null);
  // Operating cost breakdown (labor/material costs) and source budget category (LEDGER_*).
  // Used for operating cost breakdown/category rows. Total = labor + materials (no rent for dedicated buildings).
  const [operatingWageCost, setOperatingWageCost] = useState<number | null>(null);
  const [operatingMaterialCost, setOperatingMaterialCost] = useState<number | null>(null);
  const [operatingCostCategory, setOperatingCostCategory] = useState<number | null>(null);
  // Days per epoch. Used in the operating cost note "per epoch (n days)".
  const [costSimulationDays, setCostSimulationDays] = useState<number | null>(null);
  const [isCostLoading, setIsCostLoading] = useState(false);
  // Reason preview shape/cost estimation failed (backend validation message).
  // Failure hides both the 3D preview and construction info card, so show the reason rather than appearing silently broken.
  const [estimateError, setEstimateError] = useState<string | null>(null);
  // Whether the selected facility supplies infrastructure with a service area, and that area is visible in 3D.
  // Used to choose the service area legend.
  const [effectRangeActive, setEffectRangeActive] = useState(false);
  // Proposed facility's service area (fetched from preview-effect-range after site/point selection).
  const [previewEffectRegion, setPreviewEffectRegion] = useState<EffectRangeRegion | null>(null);
  // Service areas of existing facilities supplying the same service good as the selected variant.
  // Display in 3D before site selection so covered locations can be compared with proposed sites.
  const [existingEffectRanges, setExistingEffectRanges] = useState<EffectRangeRegion[]>([]);

  // Copy construction: source agent_id. Non-null means copy mode.
  // This mode copies the original building's actual footprint unchanged. For a faithful copy (same variant/floors),
  // the backend discounts construction volume. It also checks whether the footprint fits the target parcel;
  // receive preview valid / invalid_reason in copyValid / copyInvalidReason.
  const [copySourceAgentId, setCopySourceAgentId] = useState<number | null>(null);
  const [copyValid, setCopyValid] = useState(false);
  const [copyInvalidReason, setCopyInvalidReason] = useState<string | null>(null);

  const [cityHallPlaced, setCityHallPlaced] = useState<boolean>(true);
  const playMode = usePlayMode(apiBaseUrl);
  const scenarioMode = playMode === PLAY_MODE_SCENARIO;
  const continueAfterConfirm = scenarioMode && editAgentId == null && !relocateCityHall;

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();

    const fetchStatus = async () => {
      try {
        const baseUrl = apiBaseUrl.replace(/\/+$/, "");
        
        const cityData = await fetchApi<{ city?: { city_hall_placed: boolean } }>(
          `${baseUrl}/api/v1/city/`, { signal: controller.signal },
        );
        if (!controller.signal.aborted) setCityHallPlaced(cityData.city?.city_hall_placed ?? false);
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch city status:", err);
        }
      }
    };

    void fetchStatus();

    return () => controller.abort();
  }, [apiBaseUrl]);

  const isCityHallPlacedGateActive = !cityHallPlaced && playMode === PLAY_MODE_NORMAL;
  /**
 * City Hall-only placement mode, used before city founding and when relocating from its detail panel.
 * Selection is fixed to City Hall. Relocation uses the same request as placement;
 * the backend treats replacing an already placed City Hall as relocation.
 */
  const cityHallOnly = isCityHallPlacedGateActive || relocateCityHall;

  /* ---- Fetch placed landmarks ---- */

  /**
 * Fetch IDs of landmarks already in the city (active, under construction, planned, or collapsed).
 * Disable matching selector options to prevent duplicate construction of a landmark.
 */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();

    fetchApi<{ landmark_ids: number[] }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/placed-landmarks`,
      { signal: controller.signal },
    )
      .then((data) => {
        if (!controller.signal.aborted) setPlacedLandmarkIds(data.landmark_ids ?? []);
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch placed landmarks:", err);
        }
      });

    return () => controller.abort();
  }, [apiBaseUrl]);

  /* ---- Existing building edit mode: data fetching ---- */
  useEffect(() => {
    if (editAgentId == null || !apiBaseUrl) return;
    const controller = new AbortController();

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/agents/${editAgentId}`, {
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{ economic_agent: { bd_type: number; variant: number; floors: number; parcel_id: number; landmark?: number; visual_variant?: number; visual_variant2?: number; visual_variant3?: number } }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        const agent = data.economic_agent;
        setEditBdType(agent.bd_type);
        setSelectedVariant(agent.variant);
        setFloors(agent.floors);
        setLandmark(agent.landmark != null && agent.landmark > 0 ? agent.landmark : null);
        setParcelId(agent.parcel_id);
        // Load the existing building's appearance variants as initial values.
        // If the single_agent schema omits visual_variant, the value is undefined;
        // keep null in that case (omitted on confirmation, preserving the current value).
        if (agent.visual_variant != null) setVisualVariant(agent.visual_variant);
        if (agent.visual_variant2 != null) setVisualVariant2(agent.visual_variant2);
        if (agent.visual_variant3 != null) setVisualVariant3(agent.visual_variant3);
        // Notify Unity of building construction mode when a variant is selected.
        sendChangeState("STATE_EDIT_BUILDING", {
          building_variant: agent.variant,
          agent_id: editAgentId,
          landmark: agent.landmark ?? 0,
        }).catch((err) => {
          console.error("[BuildingConstructionPanel] sendChangeState failed:", err);
        });
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch existing agent:", err);
        }
      });

    return () => controller.abort();
  }, [editAgentId, apiBaseUrl]);

  // Automatically select "City government" in City Hall-only placement mode.
  useEffect(() => {
    if (cityHallOnly && editAgentId == null) {
      // Auto-select City Hall only after master data loads. Selecting earlier cannot read min_floors etc.,
      // which would lock floors to an incorrect initial value;
      // once selectedVariant is set, the reselection guard below prevents correction.
      if (selectedVariant !== VARIANT_CITY && masterData) {
        setSelectedVariant(VARIANT_CITY);
        const entry = masterData.agents[VARIANT_CITY];
        // Initialize to City Hall's minimum floor count. Fixing it to one floor would let confirmation
        // with one floor be rejected by the backend when min_floors>1.
        setFloors(entry?.min_floors ?? 1);
        // Initialize direct-placement footprint to default_width × default_height
        // (square if default_height is unset).
        const initialWidth = entry?.default_width ?? DEFAULT_BUILDING_WIDTH;
        const initialDepth = entry?.default_height ?? initialWidth;
        setRectWidth(initialWidth);
        setRectDepth(initialDepth);
        setRectAngle(0);
        setVisualVariant(Math.random());
        setVisualVariant2(Math.random());
        setVisualVariant3(Math.random());
        // Start with free placement (Ctrl radial can return to parcel placement). Founding usually has no roads or parcels;
        // for relocation, moving the cursor onto a road snaps placement to its side.
        sendChangeState("STATE_EDIT_BUILDING", {
          building_variant: VARIANT_CITY,
          agent_id: null,
          placement_mode: "free",
        }).catch((err) => {
          console.error("[BuildingConstructionPanel] sendChangeState for city hall failed:", err);
        });
      }
    }
  }, [cityHallOnly, selectedVariant, masterData, editAgentId]);

  /* ---- Hint bar display ---- */

  /**
 * Show building construction controls in the hint bar when the panel opens;
 * clear the hints when it closes.
 */
  useEffect(() => {
    let cancelled = false;
    (async () => {
      await setHintbarText(editAgentId != null ? t("hint.buildingConstruction") :
        placementMode === "parcel" ? t("building_construction.placement.parcelHint") :
          `${t("building_construction.placement.placeHint")} · ${t("building_construction.placement.sizeHint")}`);
    })().catch((err) => {
      if (!cancelled) {
        console.error("[BuildingConstruction] setHintbarText failed:", err);
      }
    });
    return () => {
      cancelled = true;
      void setHintbarText("");
    };
  }, [t, placementMode, editAgentId]);

  /* ---- Derived from master data ---- */

  /**
 * Variants buildable as buildings (building === true).
 * Includes all buildable agents: TYPE_BUILDING (shared buildings), building=true TYPE_STANDARD
 * (ordinary facilities), TYPE_EDUCATION (education), TYPE_TRADE (import/export hubs), etc.
 * Display grouped into tiles by bd_category. Outside scenario mode, exclude template_only variants
 * (border checkpoints/landmarks), reserved for scenario authoring, and sprawl_only variants
 * (slum buildings), which only urban sprawl builds.
 * Always exclude earthworks, ordered by drawing an area in the dedicated earthworks window.
 * Exclude City Hall when already placed since there can be only one (relocation opens from its details).
 */
  const buildingVariants = useMemo(() => {
    if (!masterData) return [];
    return Object.entries(masterData.agents).filter(
      ([id, entry]) => entry.building && (scenarioMode || !(entry.template_only || entry.sprawl_only))
        && !isGradingVariant(Number(id))
        && !(Number(id) === VARIANT_CITY && cityHallPlaced && !relocateCityHall)
    ) as [string, AgentMasterEntry][];
  }, [masterData, scenarioMode, cityHallPlaced, relocateCityHall]);

  /** Categories for category tiles (ID order = display order). Omit categories without any buildable buildings. */
  const availableCategories = useMemo(
    () => [...new Set(buildingVariants.map(([, entry]) => categoryOf(entry)))].sort((a, b) => a - b),
    [buildingVariants],
  );

  /**
 * Variants in the selected category. Existing-building edits do not reselect a site and thus bypass
 * allowed_bd_mask checks, so allow only the same bd_type as the edited building, since that is
 * the unit used by building regulations.
 */
  const variantsForCategory = useMemo(() => {
    if (selectedCategory === null) return [];
    return buildingVariants.filter(([, entry]) =>
      categoryOf(entry) === selectedCategory && (editBdType === null || entry.bd_type === editBdType));
  }, [buildingVariants, selectedCategory, editBdType]);

  /** Master data for the selected variant. */
  const selectedVariantData = useMemo((): AgentMasterEntry | null => {
    if (selectedVariant === null || !masterData) return null;
    return masterData.agents[selectedVariant] ?? null;
  }, [masterData, selectedVariant]);

  /**
 * Landmarks in the selected variant (ascending ID). Empty for variants without a landmark axis.
 * placed indicates existing city landmarks; disable them as choices.
 */
  const landmarkOptions = useMemo((): { id: number; entry: LandmarkMasterEntry; placed: boolean }[] => {
    if (selectedVariant === null || !masterData?.landmarks) return [];
    return Object.entries(masterData.landmarks)
      .map(([idStr, entry]) => ({ id: Number(idStr), entry }))
      .filter(({ entry }) => entry.agent_variant === selectedVariant)
      .sort((a, b) => a.id - b.id)
      .map(({ id, entry }) => ({ id, entry, placed: placedLandmarkIds.includes(id) }));
  }, [selectedVariant, masterData, placedLandmarkIds]);

  /** Master entry for the selected landmark (null for non-landmark buildings). */
  const selectedLandmarkData = useMemo((): LandmarkMasterEntry | null => {
    if (landmark == null || !masterData) return null;
    return masterData.landmarks?.[landmark] ?? null;
  }, [landmark, masterData]);

  const directRequestKey = JSON.stringify([selectedVariant, placementRevision, pickedPosition, rectWidth, rectDepth, rectAngle, floors, landmark]);
  const directPreviewReady = pickedPosition !== null && validatedDirectKey === directRequestKey && !placementCommandPending;
  /** Whether copy construction mode is active (copies an existing building's actual footprint unchanged). */
  const isCopyMode = copySourceAgentId !== null;
  /**
 * Whether the selected variant has a variable footprint. False means a fixed-size rectangle.
 * Landmarks always use fixed footprints because each landmark defines its site dimensions.
 */
  const isResizable = (selectedVariantData?.resizable ?? true) && selectedLandmarkData === null;
  /** Initial direct-placement footprint size. Landmark dimensions take precedence for landmarks. */
  const defaultWidth = selectedLandmarkData?.width ?? selectedVariantData?.default_width ?? DEFAULT_BUILDING_WIDTH;
  const defaultHeight = selectedLandmarkData?.depth ?? selectedVariantData?.default_height ?? defaultWidth;

  /** Display names of building types allowed on the selected parcel (resolved from allowed_bd_mask). */
  const allowedBuildingNames = useMemo(() => {
    if (!parcelData || !masterData) return [];
    const mask = parcelData.allowed_bd_mask ?? 0;
    if (mask === 0) return [];
    const names: string[] = [];
    for (const [bdTypeIdStr, bdTypeEntry] of Object.entries(masterData.bd_types)) {
      const bdTypeId = parseInt(bdTypeIdStr);
      if ((mask & bdTypeId) !== 0) {
        names.push(bdTypeEntry.display_name);
      }
    }
    return names;
  }, [parcelData, masterData]);

  /**
 * Parcel type required by the selected variant (land if unset).
 * Offshore structures such as offshore wind farms require water parcels.
 * Parcel types must match exactly, as in backend is_building_compatible_with_parcel_type.
 */
  const requiredParcelType = selectedVariantData?.parcel_type ?? PARCEL_LAND;
  const requiredParcelTypeName =
    masterData?.parcel_types[requiredParcelType]?.display_name ?? String(requiredParcelType);
  /**
 * Whether the selected parcel differs from the required type.
 * Unity ignores clicks on incompatible parcels, but switching variants after selecting a parcel
 * can leave an incompatible selection, so reject it in the panel before confirmation.
 */
  const parcelTypeMismatch = parcelData != null && parcelData.parcel_type !== requiredParcelType;

  /* ---- Fetch preview ---- */

  const fetchPreview = useCallback(
    async (variant: number, pId: number, f: number, cr: number, copySrc: number | null, lm: number | null, signal?: AbortSignal) => {
      const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview`;
      // In copy mode, send copy_source_agent_id to copy the original building's actual footprint unchanged.
      // coverage_ratio is unused here; the response returns fit validation (valid/invalid_reason).
      // In edit mode, send exclude_agent_id to exclude the edited building itself from unique landmark checks.
      // Otherwise, merely opening an existing landmark marks it "already exists" and prevents a preview response.
      const body = copySrc != null
        ? { copy_source_agent_id: copySrc, parcel_id: pId, floors: f }
        : { variant, parcel_id: pId, floors: f, coverage_ratio: cr, ...(lm != null ? { landmark: lm } : {}) };
      try {
        const data = await fetchApi<BuildingPreviewData & {
          valid?: boolean;
          invalid_reason?: string | null;
          site_invalid_reason?: string | null;
          requires_acquisition?: boolean;
          acquisition?: { count: number; estimated_cost: number } | null;
        }>(url, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ ...body, ...(editAgentId != null ? { exclude_agent_id: editAgentId } : {}) }),
          signal,
        });
        if (!signal?.aborted) {
          setEstimateError(null);
          setBuildingPreview(data);
          if (copySrc != null) {
            setCopyValid(data.valid ?? false);
            setCopyInvalidReason(data.invalid_reason ?? null);
          }
          if (editAgentId == null) {
            setSiteInvalidReason(data.site_invalid_reason ?? null);
            setSiteAcquisition(
              data.requires_acquisition === true && data.acquisition
                ? { count: data.acquisition.count, estimatedCost: data.acquisition.estimated_cost }
                : null,
            );
          }
          // Send backend shape data to Unity to display the 3D preview.
          sendEditBuildingUpdateShape(
            data.polygon,
            f,
            data.center_point,
            data.height,
          ).catch((err) => {
            console.error("[BuildingConstruction] sendEditBuildingUpdateShape failed:", err);
          });
        }
      } catch (err) {
        if (!signal?.aborted) {
          console.error("Failed to fetch building preview:", err);
          setBuildingPreview(null);
          setEstimateError(err instanceof Error ? err.message : String(err));
        }
      }
    },
    [apiBaseUrl, editAgentId]
  );

  /* ---- Refetch preview when the variant, parcel, or floor count changes ---- */

  // Clear the site check until the new parcel's preview arrives, so an old parcel's acquisition is never committed.
  useEffect(() => {
    setSiteInvalidReason(null);
    setSiteAcquisition(null);
  }, [parcelId]);

  useEffect(() => {
    if (selectedVariant === null || parcelId === null) return;
    const controller = new AbortController();
    void fetchPreview(selectedVariant, parcelId, floors, coverageRatio, copySourceAgentId, landmark, controller.signal);
    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, coverageRatio, copySourceAgentId, landmark, fetchPreview]);

  /* ---- Fetch estimated construction cost ---- */
  useEffect(() => {
    if (selectedVariant === null || parcelId === null) {
      setEstimatedCost(null);
      setEstimatedStandardCost(null);
      setEstimatedDurationDays(null);
      setSupplyRatio(null);
      setAvailableFunds(null);
      setExpectedDurationDays(null);
      setEstimatedMaxProduction(null);
      setEstimatedEmployment(null);
      setEstimatedOperatingCost(null);
      setOperatingWageCost(null);
      setOperatingMaterialCost(null);
      setOperatingCostCategory(null);
      setCostSimulationDays(null);
      setEstimateError(null);
      return;
    }
    const controller = new AbortController();
    setIsCostLoading(true);

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/construction-cost`;
    fetchApi<Record<string, number | null | undefined>>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        parcel_id: parcelId,
        floors,
        construction_ratio: coverageRatio,
        ...(landmark != null ? { landmark } : {}),
        // In copy mode, send copy_source_agent_id and receive discounted costs for faithful copies.
        ...(copySourceAgentId != null ? { copy_source_agent_id: copySourceAgentId } : {}),
        // In edit mode, exclude the edited building from landmark duplication checks, as for the preview.
        ...(editAgentId != null ? { exclude_agent_id: editAgentId } : {}),
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted) {
          setEstimateError(null);
          setEstimatedCost(data.estimated_construction_cost ?? null);
          setEstimatedStandardCost(data.standard_construction_cost ?? null);
          setEstimatedDurationDays(data.estimated_duration_days ?? null);
          setSupplyRatio(data.construction_supply_ratio ?? null);
          setAvailableFunds(data.available_funds ?? null);
          setExpectedDurationDays(data.expected_duration_days ?? null);
          // Production fields are returned only for TYPE_STANDARD; reset undefined values to null.
          setEstimatedMaxProduction(data.estimated_max_production ?? null);
          setEstimatedEmployment(data.estimated_employment ?? null);
          setEstimatedOperatingCost(data.estimated_operating_cost ?? null);
          setOperatingWageCost(data.operating_wage_cost ?? null);
          setOperatingMaterialCost(data.operating_material_cost ?? null);
          setOperatingCostCategory(data.operating_cost_category ?? null);
          setCostSimulationDays(data.simulation_days ?? null);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch construction cost:", err);
          setEstimateError(err instanceof Error ? err.message : String(err));
          setEstimatedCost(null);
          setEstimatedStandardCost(null);
          setEstimatedDurationDays(null);
          setSupplyRatio(null);
          setAvailableFunds(null);
          setExpectedDurationDays(null);
          setEstimatedMaxProduction(null);
          setEstimatedEmployment(null);
          setEstimatedOperatingCost(null);
          setOperatingWageCost(null);
          setOperatingMaterialCost(null);
          setOperatingCostCategory(null);
          setCostSimulationDays(null);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsCostLoading(false);
      });

    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, coverageRatio, copySourceAgentId, landmark, apiBaseUrl, editAgentId]);

  /* ---- Fetch infrastructure service areas and visualize in 3D ---- */

  /**
 * After selecting an infrastructure supplier (service producers such as substations, sewage
 * plants, and radio towers) and a site, fetch its prospective service area from the backend.
 * The area depends on position, terrain, and floors (radio tower transmitter height),
 * so refetch whenever these change. Variants without service areas (ordinary buildings) return
 * restricted:false. The sending useEffect below sends both proposed and existing areas to 3D together.
 */
  useEffect(() => {
    if (selectedVariant === null || parcelId === null) {
      setEffectRangeActive(false);
      setPreviewEffectRegion(null);
      return;
    }
    const controller = new AbortController();

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-effect-range`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ variant: selectedVariant, parcel_id: parcelId, floors }),
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{
          restricted: boolean;
          region?: EffectRangeRegion | null;
        }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        if (data.restricted && data.region) {
          setEffectRangeActive(true);
          setPreviewEffectRegion(data.region);
        } else {
          setEffectRangeActive(false);
          setPreviewEffectRegion(null);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch effect range:", err);
          setEffectRangeActive(false);
          setPreviewEffectRegion(null);
        }
      });

    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, apiBaseUrl]);

  /* ---- Fetch existing facilities' service areas ---- */

  /**
 * On variant selection, fetch service areas of existing facilities supplying the same service good,
 * including those under construction. This does not depend on site or floors, so fetch once per
 * variant selection. Variants without area restrictions return restricted:false and empty ranges.
 */
  useEffect(() => {
    if (selectedVariant === null) {
      setExistingEffectRanges([]);
      return;
    }
    const controller = new AbortController();

    fetchApi<{
      restricted: boolean;
      ranges: { agent_id: number; variant: number; region: EffectRangeRegion }[];
    }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/effect-ranges?variant=${selectedVariant}`,
      { signal: controller.signal },
    )
      .then((data) => {
        if (controller.signal.aborted) return;
        setExistingEffectRanges(
          data.restricted ? data.ranges.map((r) => r.region) : [],
        );
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch existing effect ranges:", err);
          setExistingEffectRanges([]);
        }
      });

    return () => controller.abort();
  }, [selectedVariant, apiBaseUrl]);

  /* ---- Send proposed/existing service areas to 3D ---- */

  /**
 * When either proposed or existing areas change, send both together to Unity.
 * Centralize sending here, since separate updates would let the proposed area clear existing areas.
 * If neither exists, send a clear instruction (null, null).
 */
  useEffect(() => {
    void sendEditBuildingUpdateEffectRange(
      previewEffectRegion,
      existingEffectRanges.length > 0 ? existingEffectRanges : null,
    ).catch((err) => {
      console.error("[BuildingConstruction] sendEditBuildingUpdateEffectRange failed:", err);
    });
  }, [previewEffectRegion, existingEffectRanges]);

  /* ---- Clear service area display on unmount ---- */
  useEffect(() => {
    return () => {
      void sendEditBuildingUpdateEffectRange(null).catch(() => {});
    };
  }, []);

  /* ---- Direct placement: fetch footprint preview and validity ---- */

  /**
 * In direct placement mode (non-null pickedPosition), request a rectangular footprint preview
 * whenever variant, point, size, rotation, or floors change. Send the returned polygon to Unity's
 * 3D preview (edit_building:update_shape) and update buildingPreview for the 2D canvas.
 * Keep valid/invalid_reason. For resizable=false variants, the backend forces width/depth to
 * default_width, so reflect the returned effective size in the inputs.
 */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-direct`;
    setValidatedDirectKey(null);
    setDirectValid(false);
    setDirectRequiresAcquisition(false);
    fetchApi<{
      polygon: number[][];
      height: number;
      center_point: number[];
      area: number;
      valid: boolean;
      invalid_reason: string | null;
      requires_acquisition?: boolean;
      acquisition?: {
        blocking_parcel_ids: number[];
        count: number;
        estimated_cost: number;
      } | null;
      interference?: PathInterferenceHighlight | null;
      width: number;
      depth: number;
      angle: number;
      resizable: boolean;
    }>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        position: pickedPosition,
        floors,
        width: rectWidth,
        depth: rectDepth,
        angle: rectAngle,
        ...(landmark != null ? { landmark } : {}),
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        setEstimateError(null);
        setBuildingPreview({
          polygon: data.polygon,
          height: data.height,
          center_point: data.center_point,
          area: data.area,
        });
        setDirectValid(data.valid);
        setValidatedDirectKey(directRequestKey);
        setDirectInvalidReason(data.invalid_reason ?? null);
        setDirectRequiresAcquisition(data.requires_acquisition === true);
        setDirectAcquisition(
          data.requires_acquisition === true && data.acquisition
            ? { count: data.acquisition.count, estimatedCost: data.acquisition.estimated_cost }
            : null,
        );
        // Highlight overlapping parcels/buildings in 3D using the same colors as road construction (blocking=red, etc.).
        // If nothing overlaps, clear highlights from this source.
        const interference = data.interference;
        const hasInterference =
          interference != null &&
          (interference.parcels.blocking.length > 0 ||
            interference.parcels.removable.length > 0 ||
            interference.parcels.shavable.length > 0 ||
            interference.building_ids.length > 0);
        if (hasInterference) {
          void sendHighlightObjects(interference, HIGHLIGHT_SOURCE).catch(() => {});
        } else {
          void sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
        }
        // For resizable=false, the requested size is forced to default_width; update the inputs accordingly
        // only when values actually differ, to avoid an infinite loop.
        if (Math.abs(data.width - rectWidth) > 1e-6) setRectWidth(data.width);
        if (Math.abs(data.depth - rectDepth) > 1e-6) setRectDepth(data.depth);
        // Send the footprint shape to Unity's 3D preview.
        sendEditBuildingUpdateShape(
          data.polygon,
          floors,
          data.center_point,
          data.height,
          placementRevision,
        ).catch((err) => {
          console.error("[BuildingConstruction] sendEditBuildingUpdateShape (direct) failed:", err);
        });
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct preview:", err);
          setEstimateError(err instanceof Error ? err.message : String(err));
        }
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, landmark, apiBaseUrl, placementRevision, directRequestKey]);

  /* ---- Direct placement: fetch estimated construction cost ---- */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();
    setIsCostLoading(true);

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/construction-cost`;
    fetchApi<Record<string, number | null>>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        position: pickedPosition,
        floors,
        width: rectWidth,
        depth: rectDepth,
        angle: rectAngle,
        ...(landmark != null ? { landmark } : {}),
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        setEstimateError(null);
        setEstimatedCost(data.estimated_construction_cost ?? null);
        setEstimatedStandardCost(data.standard_construction_cost ?? null);
        setEstimatedDurationDays(data.estimated_duration_days ?? null);
        setSupplyRatio(data.construction_supply_ratio ?? null);
        setAvailableFunds(data.available_funds ?? null);
        setExpectedDurationDays(data.expected_duration_days ?? null);
        setEstimatedMaxProduction(data.estimated_max_production ?? null);
        setEstimatedEmployment(data.estimated_employment ?? null);
        setEstimatedOperatingCost(data.estimated_operating_cost ?? null);
        setOperatingWageCost(data.operating_wage_cost ?? null);
        setOperatingMaterialCost(data.operating_material_cost ?? null);
        setOperatingCostCategory(data.operating_cost_category ?? null);
        setCostSimulationDays(data.simulation_days ?? null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct construction cost:", err);
          setEstimateError(err instanceof Error ? err.message : String(err));
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsCostLoading(false);
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, landmark, apiBaseUrl]);

  /* ---- Synchronize landmark to Unity for preview appearance ---- */

  /**
 * When the landmark changes, send it to Unity to replace the preview mesh with its dedicated model.
 * Send 0 for variants without a landmark axis.
 */
  useEffect(() => {
    if (selectedVariant === null) return;
    void sendEditBuildingUpdateLandmark(landmark ?? 0).catch((err) => {
      console.error("[BuildingConstruction] sendEditBuildingUpdateLandmark failed:", err);
    });
  }, [selectedVariant, landmark]);

  /* ---- Direct placement: fetch infrastructure service area (the shared sending useEffect sends it to 3D) ---- */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();

    fetchApi<{ restricted: boolean; region?: EffectRangeRegion | null }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-effect-range`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ variant: selectedVariant, position: pickedPosition, floors }),
        signal: controller.signal,
      },
    )
      .then((data) => {
        if (controller.signal.aborted) return;
        if (data.restricted && data.region) {
          setEffectRangeActive(true);
          setPreviewEffectRegion(data.region);
        } else {
          setEffectRangeActive(false);
          setPreviewEffectRegion(null);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct effect range:", err);
          setEffectRangeActive(false);
          setPreviewEffectRegion(null);
        }
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, apiBaseUrl]);

  /* ---- Subscribe to Unity's placement method/rectangle state; send only panel changes ---- */
  useEffect(() => {
    if (selectedVariant === null || editAgentId != null) return;
    let disposed = false;
    let unlisten: (() => void) | undefined;
    const labelKeys = ["parcel", "free", "roadside", "frontage", "depth", "parcelHint", "committed", "placeHint", "snapped", "searching", "sizeHint", "fixed", "rotateHint"];
    void appWindow.listen<BuildingPlacementState>(IPC_EVENT_BUILDING_PLACEMENT_STATE, ({ payload }) => {
      if (disposed || payload.variant !== selectedVariant || payload.revision < latestPlacementRevision.current) return;
      latestPlacementRevision.current = payload.revision;
      setPlacementRevision(payload.revision);
      if (pendingPlacementRequest.current === null || payload.request_id === pendingPlacementRequest.current) {
        pendingPlacementRequest.current = null;
        setPlacementCommandPending(false);
      }
      setPlacementMode(payload.mode);
      setRectWidth(payload.width);
      setRectDepth(payload.depth);
      setRectAngle(payload.angle);
      // Do not request the preview API again for a response at the same position.
      setPickedPosition(previous => JSON.stringify(previous) === JSON.stringify(payload.position) ? previous : payload.position);
      if (payload.mode !== "parcel" || payload.parcel_id < 0) {
        setParcelId(null);
        setParcelData(null);
      }
      if (payload.position === null && payload.parcel_id < 0) {
        setBuildingPreview(null);
        setValidatedDirectKey(null);
        setDirectValid(false);
        setDirectInvalidReason(null);
        setDirectRequiresAcquisition(false);
        setDirectAcquisition(null);
        setEstimatedCost(null);
        setEstimateError(null);
        setPreviewEffectRegion(null);
        setEffectRangeActive(false);
        void sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
      }
    }).then(fn => {
      if (disposed) { fn(); return; }
      unlisten = fn;
      // Request state after subscription completes to avoid missing initialization notifications.
      return sendBuildingPlacementCommand(selectedVariant, {
        labels: Object.fromEntries(labelKeys.map(key => [key, t(`building_construction.placement.${key}`)])),
      });
    }).catch(console.error);
    return () => { disposed = true; unlisten?.(); };
  }, [appWindow, selectedVariant, editAgentId, t]);

  function changeDirectParameter(field: "width" | "depth" | "angle", value: number) {
    if (selectedVariant === null) return;
    const requestId = `${Date.now()}-${++placementRequestSequence.current}`;
    pendingPlacementRequest.current = requestId;
    setPlacementCommandPending(true);
    void sendBuildingPlacementCommand(selectedVariant, { [field]: value, request_id: requestId }).catch(err => {
      if (pendingPlacementRequest.current === requestId) {
        pendingPlacementRequest.current = null;
        setPlacementCommandPending(false);
      }
      console.error("[BuildingConstruction] Failed to change placement:", err);
    });
  }

  /* ---- Reset related state when the variant is deselected ---- */

  useEffect(() => {
    if (selectedVariant === null) {
      setParcelId(null);
      setBuildingPreview(null);
      // Also reset direct-placement state.
      setPickedPosition(null);
      setDirectValid(false);
      setDirectInvalidReason(null);
      setDirectRequiresAcquisition(false);
      setDirectAcquisition(null);
      setEstimateError(null);
      void sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
      setLandmark(null);
      // Also reset copy-mode state.
      setCopySourceAgentId(null);
      setCopyValid(false);
      setCopyInvalidReason(null);
    }
  }, [selectedVariant]);

  /* ---- IPC listener: parcel selection (Unity → UI) ---- */

  useEffect(() => {
    if (selectedVariant === null) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{
        parcel_id: number;
        position?: number[];
        is_edit_mode: boolean;
        editing_building_id: number;
        polygon?: number[][];
      }>(IPC_EVENT_EDIT_BUILDING_SELECT_PARCEL, (event) => {
        console.log("[BuildingConstruction] edit_building.select_parcel", event.payload);
        // Enter parcel mode. It is mutually exclusive with direct placement, so clear pickedPosition.
        setPickedPosition(null);
        setPlacementMode("parcel");
        setParcelId(event.payload.parcel_id);
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, selectedVariant]);

  /* ---- IPC listener: unbuildable parcel notification (Unity → UI) ---- */

  /**
 * Show a warning when a parcel incompatible with the building type is clicked.
 * Examples: a land parcel while selecting a port building, or a coastal parcel for an ordinary building.
 */
  useEffect(() => {
    if (selectedVariant === null) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ parcel_id: number }>(IPC_EVENT_EDIT_BUILDING_INVALID_PARCEL, () => {
        showNotification({
          message: t("building_construction.invalidParcel"),
          durationMs: 3000,
        }).catch((err) => {
          console.error("[BuildingConstruction] showNotification failed:", err);
        });
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, selectedVariant, t]);

  /* ---- IPC listener: copy source building selection (Unity → UI) ---- */

  /**
 * When a building is selected as a copy source, seed variant / floors / visual_variant into the
 * panel, enter copy mode, and proceed to parcel selection. This starts without a selected variant
 * (the state enabling the copy button), so subscribe unconditionally without a selectedVariant guard.
 * Do not import scenario-only variants (border checkpoints/landmarks), since they are not normal
 * construction choices, even if selected as copy sources.
 */
  useEffect(() => {
    if (!masterData) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{
        source_agent_id: number;
        variant: number;
        floors: number;
        visual_variant?: number;
        visual_variant2?: number;
        visual_variant3?: number;
      }>(IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE, (event) => {
        console.log("[BuildingConstruction] building_construction.copy_source", event.payload);
        const p = event.payload;
        const entry = masterData.agents[p.variant];
        // Discard buildings ineligible as copy sources without importing state. Importing them bypasses
        // variant list filtering and produces a dead-end selection that allows neither confirmation nor preview.
        // Each landmark is unique within a city, so it cannot be copied even in scenario mode.
        const isLandmarkVariant = Object.values(masterData.landmarks ?? {})
          .some((lm) => lm.agent_variant === p.variant);
        if (isLandmarkVariant || ((entry?.template_only || entry?.sprawl_only) && !scenarioMode)) {
          void showNotification({
            message: t("building_construction.copySourceNotAllowed"),
            durationMs: 4000,
          });
          return;
        }
        setSelectedVariant(p.variant);
        setFloors(p.floors);
        // Landmarks are unique within a city and excluded from copying. Always start with the landmark axis cleared.
        setLandmark(null);
        setCopySourceAgentId(p.source_agent_id);
        setCopyValid(false);
        setCopyInvalidReason(null);
        // Copy appearance exactly (visual_variant1/2/3). Later sent through buildVisualVariantFields.
        if (p.visual_variant != null) setVisualVariant(p.visual_variant);
        if (p.visual_variant2 != null) setVisualVariant2(p.visual_variant2);
        if (p.visual_variant3 != null) setVisualVariant3(p.visual_variant3);
        // Proceed to parcel selection (new construction, without agent_id).
        sendChangeState("STATE_EDIT_BUILDING", {
          building_variant: p.variant,
          copy_source_agent_id: p.source_agent_id,
          agent_id: null,
        }).catch((err) => {
          console.error("[BuildingConstruction] sendChangeState (copy) failed:", err);
        });
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, masterData, scenarioMode, t]);

  /* ---- Fetch parcel data ---- */

  /**
 * When a parcel is selected, fetch details (area, regulations, etc.) from the backend.
 * Use the data to display the site information panel.
 */
  useEffect(() => {
    if (parcelId === null) {
      setParcelData(null);
      return;
    }
    const controller = new AbortController();
    setIsParcelLoading(true);

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/parcels/${parcelId}`, {
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<ParcelData>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          setParcelData(data);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch parcel:", err);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsParcelLoading(false);
      });

    return () => controller.abort();
  }, [parcelId, apiBaseUrl]);

  /* ---- Calculate building area ---- */

  const buildingArea = useMemo(() => {
    if (!buildingPreview) return null;
    if (buildingPreview.area != null) return buildingPreview.area;
    if (!buildingPreview.polygon) return null;
    return polygonArea(buildingPreview.polygon);
  }, [buildingPreview]);

  /* ---- Handlers ---- */

  function handleVariantSelect(variantId: number) {
    // When selecting a building variant, initialize floors to the minimum defined in that variant's master data.
    // This produces a valid preview even for buildings that cannot have only one floor, such as high-rises.
    const entry = masterData?.agents[variantId];
    // For landmark variants, default to the lowest unplaced ID (lowest ID if all are placed;
    // the backend rejects confirmation). The landmark defines its own site dimensions and floor count.
    const initialLandmark = defaultLandmarkOf(masterData?.landmarks, variantId, placedLandmarkIds);
    const landmarkEntry = initialLandmark != null ? masterData?.landmarks?.[initialLandmark] : undefined;
    const initialFloors = landmarkEntry?.floors ?? entry?.min_floors ?? 1;
    setPlacementMode("parcel");
    pendingPlacementRequest.current = null;
    setPlacementCommandPending(false);
    setSelectedVariant(variantId);
    setFloors(initialFloors);
    setLandmark(initialLandmark);
    // Initialize direct-placement footprint to default_width × default_height.
    // (square if default_height is unset).
    const initialWidth = landmarkEntry?.width ?? entry?.default_width ?? DEFAULT_BUILDING_WIDTH;
    const initialDepth = landmarkEntry?.depth ?? entry?.default_height ?? initialWidth;
    setRectWidth(initialWidth);
    setRectDepth(initialDepth);
    setRectAngle(0);
    // Initial appearance variants for new construction. In edit mode, a separate useEffect loads and overrides with existing values.
    setVisualVariant(Math.random());
    setVisualVariant2(Math.random());
    setVisualVariant3(Math.random());
    sendChangeState("STATE_EDIT_BUILDING", {
      building_variant: variantId,
      agent_id: editAgentId ?? null,
      landmark: initialLandmark ?? 0,
    }).catch((err) => {
      console.error("[BuildingConstruction] sendChangeState failed:", err);
    });
  }

  /**
 * Return from construction settings to the building card list in the selected building's category.
 * The same rule applies when a building was chosen without category tiles, as with editing or copying.
 */
  function handleVariantDeselect() {
    if (selectedVariantData) setSelectedCategory(categoryOf(selectedVariantData));
    setSelectedVariant(null);
    setLandmark(null);
    // Reset appearance variants.
    setVisualVariant(null);
    setVisualVariant2(null);
    setVisualVariant3(null);
    sendChangeState("STATE_NORMAL").catch((err) => {
      console.error("[BuildingConstruction] sendChangeState failed:", err);
    });
  }

  /**
 * Change the landmark. Each landmark defines its site dimensions and floors, so replace both too.
 */
  function handleLandmarkChange(value: number) {
    setLandmark(value);
    const entry = masterData?.landmarks?.[value];
    if (!entry) return;
    setRectWidth(entry.width);
    setRectDepth(entry.depth);
    handleFloorChange(entry.floors);
  }

  function handleFloorChange(value: number) {
    setFloors(value);
    sendEditBuildingUpdateFloors(value).catch((err) => {
      console.error("[BuildingConstruction] sendEditBuildingUpdateFloors failed:", err);
    });
  }



  /**
 * Confirmation failure notification. Map ApiError codes to i18n keys;
 * if no translation exists, display the backend message unchanged.
 */
  function notifyConfirmError(err: unknown) {
    console.error("[BuildingConstruction] confirm failed:", err);
    let message = t("building_construction.confirmFailed");
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
  }

  /** Clear only the placement position and validation results while retaining building settings. */
  function clearPlacementSelection() {
    setParcelId(null);
    setParcelData(null);
    setPickedPosition(null);
    setBuildingPreview(null);
    setValidatedDirectKey(null);
    setDirectValid(false);
    setDirectInvalidReason(null);
    setDirectRequiresAcquisition(false);
    setDirectAcquisition(null);
    setCopyValid(false);
    setCopyInvalidReason(null);
    setEstimateError(null);
    setEstimatedCost(null);
    setPreviewEffectRegion(null);
    setEffectRangeActive(false);
  }

  /**
 * Shared processing after construction confirmation succeeds. New construction in scenario
 * creation mode clears the position, randomizes the next building's appearance, and continues placing.
 * Explicitly clear service area overlays (unmount cleanup may not arrive after closing the window),
 * then send Unity a session reload to draw the new building.
 * For normal/tutorial mode, existing-building edits, and City Hall relocation, return to STATE_NORMAL
 * before changing view mode. Otherwise view:change_view is not stored in _normalStateMemory,
 * and a later STATE_NORMAL from Rust would restore default.
 * Finally enable planned-building display so the player can inspect the result, then close the panel.
 */
  async function finishAfterConfirm() {
    await sendEditBuildingUpdateEffectRange(null).catch(() => {});
    await sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
    if (continueAfterConfirm && selectedVariant !== null) {
      clearPlacementSelection();
      // Copy construction retains the source appearance; otherwise randomize appearance for each building.
      if (copySourceAgentId == null) {
        setVisualVariant(Math.random());
        setVisualVariant2(Math.random());
        setVisualVariant3(Math.random());
      }
      if (landmark != null) {
        setPlacedLandmarkIds(previous => previous.includes(landmark) ? previous : [...previous, landmark]);
      }
      await sendBuildingPlacementCommand(selectedVariant, { reset: true });
      await sendSessionReload();
      return;
    }
    await sendSessionReload();
    await sendChangeState("STATE_NORMAL");
    await sendChangeDisplay("building", "default", null, ["plan"]);
    onClose();
  }

  /**
 * Build appearance variant request fields.
 * Omit null fields (current values unavailable in edit mode), preserving the current appearance
 * unless the player moves a slider.
 */
  function buildVisualVariantFields(): Record<string, number> {
    return {
      ...(visualVariant != null ? { visual_variant: visualVariant } : {}),
      ...(visualVariant2 != null ? { visual_variant2: visualVariant2 } : {}),
      ...(visualVariant3 != null ? { visual_variant3: visualVariant3 } : {}),
    };
  }

  /** After placing with a tag, refresh lists in open construction project windows and tag selectors. */
  function notifyTagAssigned() {
    if (projectTag) void emit(TAGS_CHANGED_EVENT, { tag: projectTag }).catch(console.error);
  }

  /**
 * Parcel-mode construction confirmation handler. UI sends directly to the backend, matching
 * road construction responsibilities: POST /buildings for new buildings, PATCH /agents/{id} for edits.
 *
 * Pass acknowledgeAcquisition=true only after the player consents in the confirmation window
 * to acquiring a private parcel with a building; the backend rejects that parcel without consent.
 */
  async function handleParcelConfirm(acknowledgeAcquisition = false) {
    if (selectedVariant === null || parcelId === null || selectedVariantData === null) return;
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");
    try {
      if (editAgentId != null) {
        await fetchApi(`${baseUrl}/api/v1/agents/${editAgentId}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            variant: selectedVariant,
            floors,
            ...buildVisualVariantFields(),
          }),
        });
      } else {
        const created = await fetchApi<{ agent_id: number }>(`${baseUrl}/api/v1/buildings/`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            parcel_id: parcelId,
            type: selectedVariantData.type,
            variant: selectedVariant,
            construction_ratio: coverageRatio,
            floors,
            ...(landmark != null ? { landmark } : {}),
            ...(acknowledgeAcquisition ? { acknowledge_acquisition: true } : {}),
            // Copy mode retains the original actual footprint and discounts faithful copies.
            ...(copySourceAgentId != null ? { copy_source_agent_id: copySourceAgentId } : {}),
            ...(projectTag ? { tag: projectTag } : {}),
            ...buildVisualVariantFields(),
          }),
        });
        notifyTagAssigned();
        if (!continueAfterConfirm && created && created.agent_id) {
          void invoke("open_registered_window", {
            windowType: "agent_detail",
            entityId: String(created.agent_id),
          });
        }
      }
      await finishAfterConfirm();
    } catch (err) {
      notifyConfirmError(err);
    }
  }

  /**
 * Direct-placement confirmation handler. UI sends directly to POST /buildings/direct.
 * The backend creates a rectangular parcel automatically and fills it with the building.
 *
 * Pass acknowledgeAcquisition=true only after the player consents in the confirmation window
 * to automatic acquisition of overlapping private parcels; the backend rejects blocking without consent.
 */
  async function handleDirectConfirm(acknowledgeAcquisition = false) {
    if (selectedVariant === null || pickedPosition === null || selectedVariantData === null || !directPreviewReady) return;
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");
    try {
      const created = await fetchApi<{ agent_id: number }>(`${baseUrl}/api/v1/buildings/direct`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          variant: selectedVariant,
          type: selectedVariantData.type,
          position: pickedPosition,
          floors,
          width: rectWidth,
          depth: rectDepth,
          // Rotation accepts negatives and values over 360, so normalize to [0,360) with mod 360 on confirmation.
          angle: normalizeAngle(rectAngle),
          ...(landmark != null ? { landmark } : {}),
          ...(acknowledgeAcquisition ? { acknowledge_acquisition: true } : {}),
          ...(projectTag ? { tag: projectTag } : {}),
          ...buildVisualVariantFields(),
        }),
      });
      notifyTagAssigned();
      if (!continueAfterConfirm && created && created.agent_id) {
        void invoke("open_registered_window", {
          windowType: "agent_detail",
          entityId: String(created.agent_id),
        });
      }
      await finishAfterConfirm();
    } catch (err) {
      notifyConfirmError(err);
    }
  }

  // Ref for calling the latest confirmation handler from the confirmation window's OK subscription.
  // Even with a persistent listen useEffect subscription, confirmation always uses the latest state.
  const acquisitionConfirmRef = useRef<() => Promise<void>>();
  acquisitionConfirmRef.current = async () => {
    if (!requiresAcquisition) return;
    await (isDirectMode ? handleDirectConfirm(true) : handleParcelConfirm(true));
  };

  /* ---- Cross-window event: acquisition confirmation window OK ---- */

  /**
 * Fired when the player clicks "Acquire and build" in BuildingDirectAcquisitionConfirmWindow.
 * Commit with consent only while the current placement (direct placement or the selected parcel)
 * still requires acquisition.
 */
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen(EVENT_ACQUISITION_CONFIRMED, () => {
      void acquisitionConfirmRef.current?.();
    })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);
    return () => { unlisten?.(); };
  }, []);

  /**
 * Shared click handler capturing events across the whole panel.
 * Clicking explicitly interactive elements such as buttons or variant tiles forces focus to Unity
 * so the player can immediately operate the 3D view. Restrict targets to avoid moving focus away
 * from elements that need it, such as floor-count inputs (NumberStepper inputs).
 */
  const handlePanelClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    const button = target.closest("button, [role='button']");
    if (button) {
      sendFocusGraphic().catch(() => {});
    }
  }, []);

  /* ---- Master data not loaded ---- */

  if (!masterData) {
    return <p className="bc-muted">{t("common.loading")}</p>;
  }

  /* ---- Calculated values ---- */

  // Landmarks define their own floor heights (e.g. Grand Observation Tower = 75 m/floor), so prefer the landmark value.
  const heightPerFloor =
    selectedLandmarkData?.height_per_floor ?? selectedVariantData?.height_per_floor ?? 3.5;
  const totalHeight = floors * heightPerFloor;
  const totalFloorArea = buildingArea != null ? buildingArea * floors : null;

  /* ---- Rendering ---- */

  return (
    <div className="bc-layout" onClick={handlePanelClick}>
      {/*
 * Category tiles → building cards. Omit in City Hall-only mode because selection is fixed.
 * Existing-building edits cannot change categories, so omit category tiles and prevent returning from building cards.
 */}
      {selectedVariant === null && !cityHallOnly && (
        <div className="bc-section">
          {editAgentId == null && (
            <div className="bc-selection-header">
              {selectedCategory !== null && (
                <button
                  className="bc-header-btn bc-back-btn"
                  type="button"
                  data-guide="building.back"
                  onClick={() => setSelectedCategory(null)}
                >
                  <img src={turnBackIcon} alt="" className="bc-header-btn-icon" draggable={false} />
                  <span>{t("building_construction.back")}</span>
                </button>
              )}
              {/*
 * Copy existing building: enter Unity's copy-source selection mode and import the selected
 * building's appearance, floors, and footprint.
 */}
              <button
                className="bc-header-btn bc-copy-btn"
                type="button"
                onClick={() => {
                  sendPickCopySource().catch((err) => {
                    console.error("[BuildingConstruction] sendPickCopySource failed:", err);
                  });
                }}
              >
                <img src={copyIcon} alt="" className="bc-header-btn-icon" draggable={false} />
                <span>{t("building_construction.copyExisting")}</span>
              </button>
            </div>
          )}
          {selectedCategory === null && editAgentId != null ? (
            <p className="bc-muted">{t("common.loading")}</p>
          ) : selectedCategory === null ? (
            <div className="bc-tile-grid">
              {availableCategories.map((category) => (
                <SelectionTile
                  key={category}
                  category={category}
                  icon={resolveBdCategoryIcon(category)}
                  name={t(`building_construction.category.${category}`)}
                  onClick={() => setSelectedCategory(category)}
                />
              ))}
            </div>
          ) : (
            <div className="bc-variant-card-list">
              {variantsForCategory.map(([idStr, entry]) => {
                const variantId = Number(idStr);
                const unaffordable = isVariantUnaffordable(variantId);
                return (
                  <UnaffordableOverlay key={idStr} unaffordable={unaffordable}>
                    <BuildingVariantCard
                      variantId={variantId}
                      entry={entry}
                      disabled={unaffordable}
                      onClick={() => handleVariantSelect(variantId)}
                    />
                  </UnaffordableOverlay>
                );
              })}
            </div>
          )}
        </div>
      )}

      {/* Selected building summary card. */}
      {selectedVariantData && (
        <div className="bc-section">
          {!cityHallOnly && (
            <button
              className="bc-header-btn bc-back-btn"
              type="button"
              data-guide="building.back"
              onClick={handleVariantDeselect}
            >
              <img src={turnBackIcon} alt="" className="bc-header-btn-icon" draggable={false} />
              <span>{t("building_construction.back")}</span>
            </button>
          )}
          <BuildingVariantCard
            variantId={selectedVariant!}
            entry={selectedVariantData}
            fixedFloors={selectedLandmarkData?.floors ?? null}
          />

          {/*
 * Landmark selection (variants with a landmark axis only). Each landmark is unique within the city,
 * so existing ones cannot be chosen. Site dimensions and floors are defined per landmark.
 * Display-only during existing-building edits, since replacing the landmark would rebuild the site dimensions.
 */}
          {landmarkOptions.length > 0 && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">{t("building_construction.landmark")}</div>
                <select
                  className="bc-select"
                  value={landmark ?? ""}
                  onChange={(e) => handleLandmarkChange(parseInt(e.target.value))}
                  disabled={editAgentId != null}
                >
                  {landmarkOptions.map(({ id, entry, placed }) => (
                    <option key={id} value={id} disabled={placed}>
                      {entry.display_name}
                      {placed ? t("building_construction.landmarkPlacedSuffix") : ""}
                    </option>
                  ))}
                </select>
                <div className="bc-description-text" style={{ marginTop: 6 }}>
                  {selectedLandmarkData?.description || t("building_construction.landmarkNote")}
                </div>
                {selectedLandmarkData && (
                  <div className="bc-description-text" style={{ marginTop: 4 }}>
                    {t("building_construction.landmarkDimensions", {
                      width: selectedLandmarkData.width,
                      depth: selectedLandmarkData.depth,
                      floors: selectedLandmarkData.floors,
                    })}
                  </div>
                )}
              </div>
            </div>
          )}

          {/*
 * Show the reason when fetching preview/cost estimates fails.
 * Failure hides both preview and cost card, so expose the reason instead of appearing silently broken.
 */}
          {estimateError !== null && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">
                  {t("building_construction.estimateFailed")}
                </div>
                <div className="bc-description-text bc-direct-invalid">{estimateError}</div>
              </div>
            </div>
          )}

          {/* Infrastructure service area legend (only while proposed or existing areas are displayed in 3D). */}
          {(effectRangeActive || existingEffectRanges.length > 0) && (
            <div className="bc-effect-range-legend">
              <div className="bc-effect-range-legend-title">
                {t("building_construction.effectRangeTitle")}
              </div>
              {effectRangeActive && (
                <div className="bc-effect-range-legend-row">
                  <span className="bc-effect-range-swatch bc-effect-range-swatch-area" />
                  {t("building_construction.effectRangeArea")}
                </div>
              )}
              {existingEffectRanges.length > 0 && (
                <div className="bc-effect-range-legend-row">
                  <span className="bc-effect-range-swatch bc-effect-range-swatch-existing" />
                  {t("building_construction.effectRangeExisting")}
                </div>
              )}
            </div>
          )}



          {/* Direct-placement information panel (reasons for placement validity). */}
          {isDirectMode && pickedPosition !== null && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">
                  {t("building_construction.directPlacement")}
                </div>
                <div className="bc-description-text">
                  {!directPreviewReady ? (
                    <span>{t("common.loading")}</span>
                  ) : directValid ? (
                    t("building_construction.directPlaceable")
                  ) : directRequiresAcquisition ? (
                    // Overlaps private parcels but can be confirmed after consenting to automatic land acquisition.
                    // The confirmation button opens a confirmation window.
                    <span className="bc-direct-acquisition">
                      {t("building_construction.directAcquisitionRequired", {
                        count: directAcquisition?.count ?? 0,
                      })}
                      {directAcquisition != null && (
                        <>
                          <br />
                          {t("building_construction.directAcquisitionCost", {
                            cost: Math.round(directAcquisition.estimatedCost).toLocaleString(),
                          })}
                        </>
                      )}
                    </span>
                  ) : (
                    <span className="bc-direct-invalid">
                      {directInvalidReason
                        ? t(`building_construction.directInvalid.${directInvalidReason}`, {
                            defaultValue: t("building_construction.directInvalid.generic"),
                          })
                        : t("building_construction.directInvalid.generic")}
                    </span>
                  )}
                </div>
              </div>
            </div>
          )}

          {/* Copy construction information panel (discount and footprint fit). */}
          {isCopyMode && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">
                  {t("building_construction.copyMode")}
                </div>
                <div className="bc-description-text">
                  {parcelId === null ? (
                    t("building_construction.copySelectParcelHint")
                  ) : copyValid ? (
                    t("building_construction.copyPlaceable")
                  ) : (
                    <span className="bc-direct-invalid">
                      {copyInvalidReason
                        ? t(`building_construction.copyInvalid.${copyInvalidReason}`, {
                            defaultValue: t("building_construction.copyInvalid.generic"),
                          })
                        : t("building_construction.copyInvalid.generic")}
                    </span>
                  )}
                </div>
              </div>
            </div>
          )}

          {/* Site information panel (parcel mode only). */}
          {!isDirectMode && (
          <div className="bc-description-frame" style={{ marginTop: 10 }}>
            {parcelId === null ? (
              <div className="bc-description-body bc-parcel-prompt">
                <div className="bc-description-text">
                  {t("building_construction.selectParcelHint")}
                  {/* For variants requiring non-land parcels (offshore structures, etc.), indicate which parcel type to seek in advance. */}
                  {requiredParcelType !== PARCEL_LAND && (
                    <>
                      <br />
                      {t("building_construction.parcelTypeRequired", { type: requiredParcelTypeName })}
                    </>
                  )}
                </div>
              </div>
            ) : (
              <>
                <img
                  src={parcelData ? resolveParcelIcon(parcelData.parcel_type) : resolveParcelIcon(0)}
                  alt=""
                  title={parcelData ? masterData?.parcel_types[parcelData.parcel_type]?.display_name : undefined}
                  className="bc-description-icon"
                  draggable={false}
                />
                <div className="bc-description-body">
                  <div className="bc-description-name">
                    {t("building_construction.parcelTitle", { id: parcelId })}
                  </div>
                  {isParcelLoading || !parcelData ? (
                    <div className="bc-description-text">{t("common.loading")}</div>
                  ) : (
                    <>
                      <ParcelSpecs parcel={parcelData} />
                      {allowedBuildingNames.length > 0 && (
                        <div className="bc-description-text bc-parcel-allowed">
                          {t("building_construction.allowedBuildings")}: {allowedBuildingNames.join(", ")}
                        </div>
                      )}
                      {parcelTypeMismatch && (
                        <div className="bc-description-text bc-direct-invalid">
                          {t("building_construction.parcelTypeRequired", { type: requiredParcelTypeName })}
                        </div>
                      )}
                      {siteAcquisition !== null ? (
                        // A private building stands on the parcel. The confirmation button opens the acquisition window.
                        <div className="bc-description-text bc-direct-acquisition">
                          {t("building_construction.directAcquisitionRequired", { count: siteAcquisition.count })}
                          <br />
                          {t("building_construction.directAcquisitionCost", {
                            cost: Math.round(siteAcquisition.estimatedCost).toLocaleString(),
                          })}
                        </div>
                      ) : siteInvalidReason !== null && (
                        <div className="bc-description-text bc-direct-invalid">
                          {t(`building_construction.directInvalid.${siteInvalidReason}`, {
                            defaultValue: t("building_construction.directInvalid.generic"),
                          })}
                        </div>
                      )}
                    </>
                  )}
                </div>
              </>
            )}
          </div>
          )}
        </div>
      )}

      {/* Floor count/building coverage settings (two columns) — shown after parcel selection. */}
      {selectedVariantData && parcelId !== null && (
        <div className="bc-section">
          <div className="bc-split-row">
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.floorCount")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={floors}
                  onChange={handleFloorChange}
                  min={selectedVariantData.min_floors ?? 1}
                  max={selectedVariantData.max_floors ?? 50}
                  step={1}
                  integerOnly={true}
                  disabled={selectedLandmarkData !== null}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitFloors")}</span>
              </div>
            </div>
            {/*
 * Building coverage ratio applies only to resizable footprints. Hide for fixed footprints and copy mode.
 * Copy mode retains the original actual footprint, so coverage ratio is unused.
 */}
            {isResizable && !isCopyMode && (
              <div className="bc-split-col">
                <div className="bc-section-title">{t("building_construction.coverageRatio")}</div>
                <div className="bc-stepper-row">
                  <NumberStepper
                    value={Math.round(coverageRatio * 100)}
                    onChange={(v) => setCoverageRatio(v / 100)}
                    min={10}
                    max={100}
                    step={5}
                    integerOnly={true}
                  />
                  <span className="bc-stepper-unit">%</span>
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {/*
 * Construction project tag, attached when creating a new building (existing tags change in details).
 * Omit for City Hall, which completes immediately on placement and cannot have a tag.
 */}
      {selectedVariantData && editAgentId == null && selectedVariant !== VARIANT_CITY && (
        <div className="bc-section">
          <div className="bc-section-title">{t("tag.label")}</div>
          <TagPicker apiBaseUrl={apiBaseUrl} value={projectTag} onChange={setProjectTag} />
        </div>
      )}

      {/*
 * Direct-placement settings: floors plus frontage/depth/rotation, shown before point selection.
 * The footprint is the rectangle itself, so there is no coverage ratio. For resizable=false variants,
 * fix width/depth (inputs disabled) and allow only rotation editing.
 */}
      {selectedVariantData && isDirectMode && (
        <div className="bc-section">
          <div className="bc-split-row">
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.floorCount")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={floors}
                  onChange={handleFloorChange}
                  min={selectedVariantData.min_floors ?? 1}
                  max={selectedVariantData.max_floors ?? 50}
                  step={1}
                  integerOnly={true}
                  disabled={selectedLandmarkData !== null}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitFloors")}</span>
              </div>
            </div>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.rotation")}</div>
              <div className="bc-stepper-row">
                {/* Allow negative and over-360 rotations without clamping. Normalize with mod 360 on confirmation. */}
                <NumberStepper
                  value={rectAngle}
                  onChange={value => changeDirectParameter("angle", value)}
                  disabled={placementMode === "roadside"}
                  step={5}
                  integerOnly={false}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitDegree")}</span>
              </div>
            </div>
          </div>
          <div className="bc-split-row" style={{ marginTop: 10 }}>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.placement.frontage")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={rectWidth}
                  onChange={value => changeDirectParameter("width", value)}
                  min={1}
                  max={500}
                  step={1}
                  integerOnly={false}
                  disabled={!isResizable}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitMeter")}</span>
              </div>
            </div>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.placement.depth")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={rectDepth}
                  onChange={value => changeDirectParameter("depth", value)}
                  min={1}
                  max={500}
                  step={1}
                  integerOnly={false}
                  disabled={!isResizable}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitMeter")}</span>
              </div>
            </div>
          </div>
          {!isResizable && (
            <div className="bc-muted" style={{ marginTop: 6 }}>
              {t("building_construction.fixedFootprintNote", { width: defaultWidth, height: defaultHeight })}
            </div>
          )}
        </div>
      )}

      {/* Construction information card (shared across all types, using the same shared card as detail panels). */}
      {buildingPreview && (
        <div className="bc-section">
          <ConstructionInfoCard
            title={t("construction.info")}
            buildingArea={buildingArea}
            totalHeight={totalHeight}
            totalFloorArea={totalFloorArea}
            estimatedCost={estimatedCost}
            standardCost={estimatedStandardCost}
            costRatioHelp={t("building_construction.costRatioHelp")}
            estimatedDurationDays={estimatedDurationDays}
            isCostLoading={isCostLoading}
            supplyRatio={supplyRatio}
            expectedDurationDays={expectedDurationDays}
            availableFunds={availableFunds}
            requiredTotal={requiredTotal}
          />
        </div>
      )}

      {/* Facility information card (contents vary by type; omit the card itself for types without content). */}
      {buildingPreview && (
        <BuildingFacilityInfoCard
          variantData={selectedVariantData}
          estimatedMaxProduction={estimatedMaxProduction}
          estimatedEmployment={estimatedEmployment}
          estimatedOperatingCost={estimatedOperatingCost}
          operatingWageCost={operatingWageCost}
          operatingMaterialCost={operatingMaterialCost}
          operatingCostCategory={operatingCostCategory}
          simulationDays={costSimulationDays}
          isCostLoading={isCostLoading}
        />
      )}



      {/* Action buttons (fixed at the panel bottom). */}
      {selectedVariant && (
        <div className="bc-section bc-action-bar">
          <div className="bc-actions">
            <button
              className="bc-btn"
              type="button"
              data-guide="building.cancel"
              onClick={() => {
                // Cancel only the provisional placement position (selected parcel in parcel mode or clicked point in direct placement).
                // Keep the variant selection and appearance variants so another position can be chosen immediately.
                clearPlacementSelection();
                void sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
                if (editAgentId == null) {
                  void sendBuildingPlacementCommand(selectedVariant, { reset: true }).catch(console.error);
                  return;
                }
                sendChangeState("STATE_EDIT_BUILDING", {
                  building_variant: selectedVariant,
                  agent_id: editAgentId ?? null,
                  landmark: landmark ?? 0,
                }).catch((err) => {
                  console.error("[BuildingConstruction] sendChangeState (cancel) failed:", err);
                });
              }}
            >
              {t("building_construction.cancel")}
            </button>
            <button
              className="bc-btn bc-btn-confirm"
              // Confirmation requires a selected parcel in parcel mode, or directValid in direct mode, or
              // an overlap resolvable through automatic private-parcel acquisition (directRequiresAcquisition).
              // Copy mode requires a selected parcel and a fitting footprint (copyValid).
              // Parcel and copy modes also stop on a site that acquisition cannot clear (siteBlocked).
              // Block confirmation if construction plus estimated acquisition cost exceeds available funds; the backend uses the same rule.
              // Also stop parcel type mismatches (e.g. offshore structures on land parcels), which the backend rejects.
              disabled={
                !isAffordable
                || (isDirectMode
                  ? !directPreviewReady || !(directValid || directRequiresAcquisition)
                  : isCopyMode
                    ? parcelId === null || !copyValid || parcelTypeMismatch || siteBlocked
                    : parcelId === null || parcelTypeMismatch || siteBlocked)
              }
              type="button"
              data-guide="building.confirm"
              onClick={async () => {
                if (requiresAcquisition) {
                  // Placements requiring automatic private-parcel acquisition show confirmation before committing.
                  // OK sends "building_direct:acquisition_confirmed",
                  // which executes confirmation with consent (acquisitionConfirmRef).
                  void invoke("open_registered_window", {
                    windowType: "building_direct_acquisition",
                    params: {
                      extra: {
                        pendingAcquisition: {
                          blockingCount: pendingAcquisition?.count ?? 0,
                          estimatedCost: pendingAcquisition?.estimatedCost ?? 0,
                        },
                      },
                    },
                  });
                } else if (isDirectMode) {
                  await handleDirectConfirm();
                } else {
                  await handleParcelConfirm();
                }
              }}
            >
              {t(relocateCityHall ? "building_construction.relocateConfirm" : "building_construction.confirm")}
            </button>
          </div>
        </div>
      )}

    </div>
  );
}

/** Category (bd_category) selection tile. Leave empty icon space when no image exists. */
function SelectionTile({ category, icon, name, disabled = false, onClick }: {
  category: number;
  icon: string;
  name: string;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <button className="bc-tile" type="button" data-guide="building.category" data-guide-value={category} disabled={disabled} onClick={onClick}>
      <span className="bc-tile-icon">{icon && <img src={icon} alt="" draggable={false} />}</span>
      <span className="bc-tile-name">{name}</span>
    </button>
  );
}

/* ---- Utilities ---- */

/** Construction panel categories. Buildings with blank bd_category in master data appear under "Other". */
function categoryOf(entry: AgentMasterEntry): number {
  return entry.bd_category ?? BD_CATEGORY_OTHER;
}

/**
 * Default landmark for a variant: lowest ID belonging to it that is not already in the city.
 * Null for variants without a landmark axis. If all are placed, return the lowest ID;
 * the backend rejects confirmation.
 */
function defaultLandmarkOf(
  landmarks: Record<number, LandmarkMasterEntry> | undefined,
  variantId: number,
  placedIds: number[],
): number | null {
  const ids = Object.entries(landmarks ?? {})
    .filter(([, entry]) => entry.agent_variant === variantId)
    .map(([idStr]) => Number(idStr))
    .sort((a, b) => a - b);
  if (ids.length === 0) return null;
  return ids.find((id) => !placedIds.includes(id)) ?? ids[0];
}

/** Calculate polygon area with the shoelace formula. */
function polygonArea(polygon: number[][]): number {
  let area = 0;
  const n = polygon.length;
  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n;
    area += polygon[i][0] * polygon[j][1];
    area -= polygon[j][0] * polygon[i][1];
  }
  return Math.abs(area) / 2;
}
