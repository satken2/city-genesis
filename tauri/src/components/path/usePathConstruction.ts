/**
 * State management hook for the path construction panel.
 *
 * Node placement occurs in the graphics program (Unity), so this contains no mouse handlers and manages settings
 * and node data received through IPC.
 *
 * Placement behavior depends only on the selected variant type (categories affect panel presentation only).
 *   - Roads/infrastructure/station buildings: place the selected variant as one path.
 *   - Tracks (railway/monorail): structure (normal/bridge/station) and electrification are variant differences.
 *     Configure the chosen single-track variant's count, directions, and spacing. Default spacing is master width,
 *     adjustable within limits with widen/narrow buttons and reset after each confirmed path.
 *     Expand the centerline into parallel single-track paths and place them together (railComponents).
 *     People movers (e.g. ropeways) and combined double-track variants (rail/monorail with lanes >= 2) use one
 *     bidirectional DIR_BOTH path like roads, without expansion (railBuild.isSingleRailPathVariant).
 *     Combined double-track stations have no platform side selection.
 *
 * For new paths, placement methods (pathBuildMethods) are available by type. Single placement builds request geometry
 * from Unity's polyline as above. Other methods (grids) pass Unity's points/settings to core expansion (POST /paths/layout)
 * and use the returned paths directly. Return expansion results with the edit generation to Unity for preview.
 *
 * State:
 *   selectedVariant: single variant ID to place (representative centerline variant for tracks).
 *   curveMode: straight/curved. Unity is the source of truth (radial-menu selection); update only from
 *              is_curved in edit_path.update_shape.
 *   height/embankmentHeight: elevation/embankment of the selected or next node. Retained in the panel for precise
 *              numeric adjustments to existing nodes. Synchronize from select_node; send stepper changes through
 *              change_node/change_embankment for two-way synchronization with Unity.
 *   nodes: node array received through IPC (edit_path.update_shape).
 *
 * Unity's Ctrl radial menu handles curve/straight and snapping settings, so this hook does not manage them.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import type { MasterData, PathMasterEntry, CombinedPathComponent } from "../../types";
import type {
  BuildPoint,
  ControlPoint,
  CurveMode,
  PathShapeUpdatePayload,
  PathMaxSpanViolation,
} from "./pathBuildConstants";
import {
  sendChangeState,
  sendSetPlatformSide,
  sendSetHouseEnds,
  sendSetPassageMode,
  sendEditPathChangeNode,
  sendEditPathChangeEmbankment,
  sendEditPathChangeLateralOffsets,
  sendEditPathSetFollowTerrain,
  sendHighlightObjects,
  sendClearHighlights,
  sendEditPathAlignment,
  sendEditPathChangeGridDivisions,
  sendEditPathShapeCommand,
  IPC_EVENT_EDIT_PATH_SELECT_NODE,
  IPC_EVENT_EDIT_PATH_UPDATE_SHAPE,
} from "../../utils/ipc";
import { describeJunctionLabels, describeMaxSpanViolations, describePathValidation, pathValidationShapeKey, type PathValidationResult } from "./pathValidation";
import { fetchApi } from "../../utils/api";
import { allowsPathBody, GUIDE_QUERY, observe, snapshot } from "../../tutorial/bus";
import { useGuideSnapshot } from "../../tutorial/TutorialLayer";
import type { PathAlignmentSummary } from "../../types";
import { notifyRoadsChanged } from "../road/roadApi";
import {
  DIR_BOTH,
  PATH_BUILD_METHOD_SINGLE,
  PATH_GRID_BUILD_MAX_DIVISIONS,
  PATH_STATION_HOUSE,
  PATH_TRACK,
  PLATFORM_SIDE_OUTSIDE,
  TRAFFIC_HAND_LEFT,
} from "../../constants";
import { buildMethodsFor, DEFAULT_GRID_DIVISIONS } from "./pathBuildMethods";
import { isInfraPathType } from "../../local-constants";
import {
  computeAnchorOffsetDirs,
  offsetControlPoint,
  offsetPolyline3D,
} from "./pathOffset";
import { computePathLength } from "./pathLength";
import {
  MIN_TRACK_COUNT,
  MAX_TRACK_COUNT,
  FALLBACK_TRACK_SPACING,
  TRACK_SPACING_STEP,
  TRACK_SPACING_MIN_FACTOR,
  TRACK_SPACING_MAX_FACTOR,
  clampTrackSpacing,
  isRailType,
  isSingleRailPathVariant,
  hasPlatformSideChoice,
  defaultTravelDirections,
  cycleDirection,
  buildRailComponents,
  computeLateralOffsets,
} from "./railBuild";

/** Sentinel for an unset terrain-following override (use the variant default). Matches backend VALUE_NONE. */
export const FOLLOW_TERRAIN_UNSET = -1;

function clampInt(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, Math.round(value)));
}

/* ---- Control point calculation (same logic as Unity RecomputeAllControlPoints) ---- */

function computeSplineOrBezierControls(
  points: BuildPoint[],
  curveMode: CurveMode
): { start: ControlPoint; end: ControlPoint } {
  if (curveMode === "curved") {
    return computeSplineControls(points);
  }
  return { start: null, end: null };
}

function computeSplineControls(points: BuildPoint[]): {
  start: ControlPoint;
  end: ControlPoint;
} {
  if (points.length < 2) {
    return { start: null, end: null };
  }

  if (points.length === 2) {
    const s = points[0];
    const e = points[1];
    return {
      start: { x: s.x + (e.x - s.x) / 3, z: s.z + (e.z - s.z) / 3 },
      end: { x: e.x - (e.x - s.x) / 3, z: e.z - (e.z - s.z) / 3 },
    };
  }

  let t0x = (points[1].x - points[0].x) * 0.5;
  let t0z = (points[1].z - points[0].z) * 0.5;
  if (points.length > 2) {
    t0x = (points[2].x - points[0].x) / 6;
    t0z = (points[2].z - points[0].z) / 6;
  }
  const start = { x: points[0].x + t0x, z: points[0].z + t0z };

  const n = points.length;
  let tnx = (points[n - 1].x - points[n - 2].x) * 0.5;
  let tnz = (points[n - 1].z - points[n - 2].z) * 0.5;
  if (n > 2) {
    tnx = (points[n - 1].x - points[n - 3].x) / 6;
    tnz = (points[n - 1].z - points[n - 3].z) / 6;
  }
  const end = { x: points[n - 1].x - tnx, z: points[n - 1].z - tnz };

  return { start, end };
}

/* ---- Hook implementation ---- */

/**
 * Bézier controls for one waypoint (intermediate anchor), as [x, relative height, z].
 * null means unset in Unity (the backend completes it automatically on confirmation).
 */
export type WaypointControls = {
  control_in: number[] | null;
  control_out: number[] | null;
};

/** alignment from POST /paths/construction-cost (backend geo_path_alignment.alignment_for_api shape). */
export type PathAlignmentResponse = {
  base_speed: number;
  max_speed: number;
  min_curve_radius: number | null;
  max_grade: number;
  curve_radius_limit: number;
  grade_limit: number;
  curve_violations: number[][];
  grade_violations: number[][];
  alignment_ok: boolean;
  preview?: {
    samples: Array<[number, number, number | null, number, number, number, number]>;
    warnings: Array<{ curve: boolean; value: number; limit: number; position: number[]; points: number[][] }>;
  };
};

/**
 * Combine alignment metrics across parallel tracks using worst values.
 * Passing speed is the slowest, minimum curve radius the smallest (ignore straight=null), and maximum grade the steepest.
 * Thresholds use the first track's standard (parallel tracks share a standard). Any track's violation makes the result true.
 */
function summarizeAlignment(items: Array<PathAlignmentResponse | undefined>): PathAlignmentSummary | null {
  const valid = items.filter((a): a is PathAlignmentResponse => a != null);
  if (valid.length === 0) return null;
  const radii = valid.map((a) => a.min_curve_radius).filter((r): r is number => r != null);
  return {
    baseSpeed: Math.min(...valid.map((a) => a.base_speed)),
    maxSpeed: valid[0].max_speed,
    minCurveRadius: radii.length > 0 ? Math.min(...radii) : null,
    maxGrade: Math.max(...valid.map((a) => a.max_grade)),
    curveRadiusLimit: valid[0].curve_radius_limit,
    gradeLimit: valid[0].grade_limit,
    curveViolated: valid.some((a) => a.curve_violations.length > 0),
    gradeViolated: valid.some((a) => a.grade_violations.length > 0),
  };
}

export function usePathConstruction(
  masterData: MasterData | null,
  apiBaseUrl?: string,
  pathId?: number | null,
) {
  const { t } = useTranslation();
  useGuideSnapshot();
  const [selectedVariant, setSelectedVariant] = useState<number | null>(null);
  // Unity is the curve-mode source of truth. Update only from is_curved in update_shape;
  // confirmation (curved), shape preview, and cost estimates use this value.
  const [curveMode, setCurveMode] = useState<CurveMode>("curved");
  // Keep elevation/embankment in the panel for precise numeric adjustments to selected existing nodes.
  // Unity supplies displayed values through select_node; send stepper changes back through
  // change_node/change_embankment for two-way sync. selectedNodeIndex=-1 refers to the next node to place.
  const [height, setHeight] = useState(0);
  const [embankmentHeight, setEmbankmentHeight] = useState(0);
  const [selectedNodeIndex, setSelectedNodeIndex] = useState<number>(-1);
  // Platform side for station paths (PLATFORM_SIDE_OUTSIDE/INSIDE/BOTH).
  // Meaningful only when a station variant with platform side selection (hasPlatformSide) is selected.
  // Set only during construction; cannot change afterward (backend restriction).
  const [platformSide, setPlatformSide] = useState<number>(PLATFORM_SIDE_OUTSIDE);
  const [houseEnds, setHouseEnds] = useState("both");
  const [passageMode, setPassageMode] = useState("elevated");
  useEffect(() => {
    if (pathId == null || !apiBaseUrl) return;
    const controller = new AbortController();
    fetchApi<{ house_ends: string; passage_mode: string }>(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${pathId}`, {
      signal: controller.signal,
    }).then((path) => {
      if (!controller.signal.aborted) {
        setHouseEnds(path.house_ends);
        setPassageMode(path.passage_mode);
      }
    }).catch((err) => {
      if (!controller.signal.aborted) console.error("station settings load failed", err);
    });
    return () => controller.abort();
  }, [apiBaseUrl, pathId]);
  // Terrain-following override (-1 = unset/use variant default, 0 = disabled, 1 = enabled). Unity is the source of truth;
  // send toggles through sendEditPathSetFollowTerrain and update the value from follow_terrain
  // in edit_path:update_shape. Meaningful only for height_editable variants.
  const [followTerrain, setFollowTerrainState] = useState<number>(FOLLOW_TERRAIN_UNSET);
  const [nodes, setNodes] = useState<BuildPoint[]>([]);
  const [rawPoints, setRawPoints] = useState<number[][]>([]);
  const [rawEmbankmentHeights, setRawEmbankmentHeights] = useState<number[]>([]);
  const [startControl, setStartControl] = useState<ControlPoint>(null);
  const [endControl, setEndControl] = useState<ControlPoint>(null);
  // Intermediate anchor controls received from Unity, with the same order/count as rawPoints.slice(1,-1).
  // Include them in waypoints as {position, control_in, control_out} on confirmation
  // so curves adjusted by dragging persist in the backend.
  const [rawWaypointControls, setRawWaypointControls] = useState<WaypointControls[]>([]);
  // Unity edit phase (placing/editing/inactive), synchronized through update_shape phase.
  const [shapePhase, setShapePhase] = useState<string>();

  /* ---- Placement method ---- */
  // New path placement method (PATH_BUILD_METHOD_*). Existing shape editing always uses single placement.
  const [buildMethod, setBuildMethod] = useState<number>(PATH_BUILD_METHOD_SINGLE);
  const [gridDivisions, setGridDivisions] = useState<[number, number]>(DEFAULT_GRID_DIVISIONS);
  // Core expansion result. key is the request body; use only when it matches the request for current geometry/settings.
  const [layout, setLayout] = useState<{ key: string; paths?: Record<string, unknown>[]; error?: string } | null>(null);
  const isLayoutBuild = pathId == null && buildMethod !== PATH_BUILD_METHOD_SINGLE;
  // Unity advances to editing only after all points required by the method are placed.
  const layoutRequest = isLayoutBuild && selectedVariant !== null && shapePhase === "editing"
    ? JSON.stringify({ build_method: buildMethod, path_variant: selectedVariant, points: rawPoints, divisions: gridDivisions })
    : "";
  const currentLayout = layoutRequest !== "" && layout?.key === layoutRequest ? layout : null;
  const isLayoutBuildRef = useRef(isLayoutBuild);
  isLayoutBuildRef.current = isLayoutBuild;

  /* ---- Parallel track placement settings ---- */
  const [trackCount, setTrackCount] = useState<number>(1);
  // Spacing adjustment (m). Effective spacing = master width + adjustment, clamped. Reset to 0 after each confirmed path.
  const [trackSpacingDelta, setTrackSpacingDelta] = useState<number>(0);
  // User direction overrides per track (null = defaults). Keep length equal to trackCount.
  const [directionOverrides, setDirectionOverrides] = useState<(number | null)[]>([null]);
  // City traffic direction, used to assign default forward/backward sides. Defaults to left-hand traffic.
  const [trafficHand, setTrafficHand] = useState<number>(TRAFFIC_HAND_LEFT);

  const [estimatedCost, setEstimatedCost] = useState<number | null>(null);
  // Standard cost for entirely flat placement at zero elevation, used to show the estimated/standard cost ratio.
  const [estimatedStandardCost, setEstimatedStandardCost] = useState<number | null>(null);
  const [estimatedDurationDays, setEstimatedDurationDays] = useState<number | null>(null);
  const [costPerSimulationDays, setCostPerSimulationDays] = useState<number | null>(null);
  const [costSimulationDays, setCostSimulationDays] = useState<number | null>(null);
  // Long-term average maintenance cost per epoch after completion (sum over all paths).
  const [maintenanceCost, setMaintenanceCost] = useState<number | null>(null);
  // Total city income in the latest completed epoch, used as the denominator for cost/maintenance ratios.
  const [epochRevenue, setEpochRevenue] = useState<number | null>(null);
  // City funds available for orders (cash minus savings target minus existing commitments), used to preview the funding check.
  const [availableFunds, setAvailableFunds] = useState<number | null>(null);
  // Construction service procurement fulfillment (0-1) and projected construction time accounting for it (days).
  // For parallel tracks, use the worst conditions (minimum fulfillment, longest expected time).
  const [supplyRatio, setSupplyRatio] = useState<number | null>(null);
  const [expectedDurationDays, setExpectedDurationDays] = useState<number | null>(null);
  const [isCostLoading, setIsCostLoading] = useState(false);
  // Alignment metrics (standard thresholds and violations), used to block confirmation. Aggregate construction-cost alignment
  // by worst values across parallel tracks. null means not yet fetched.
  const [alignment, setAlignment] = useState<PathAlignmentSummary | null>(null);
  const costAbortControllerRef = useRef<AbortController | null>(null);

  /* ---- Derived data ---- */

  const selectedVariantData = useMemo((): PathMasterEntry | null => {
    if (selectedVariant === null || !masterData?.paths) return null;
    return masterData.paths[selectedVariant] ?? null;
  }, [selectedVariant, masterData]);

  /**
 * Representative variant passed to Unity (graphics program).
 * Even for parallel tracks, Unity node placement edits one centerline. confirmPath and cost estimation perform
 * parallel expansion. Supply individual offsets separately through lateral_offsets in STATE_EDIT_PATH params
 * and edit_path:change_lateral_offsets to draw parallel tracks in the 3D preview.
 */
  const unityPathVariant = selectedVariant;
  const unityPathType = useMemo((): number | null => {
    if (selectedVariant === null) return null;
    return masterData?.paths?.[selectedVariant]?.type ?? null;
  }, [selectedVariant, masterData]);

  const heightEditable = useMemo(() => {
    return selectedVariantData?.height_editable ?? false;
  }, [selectedVariantData]);

  // Whether this is an infrastructure path (power line, water main, etc.).
  // Infrastructure paths do not deform terrain or use embankment_heights, so
  // use this to hide embankment inputs in the panel.
  const isInfrastructure = useMemo(() => {
    return isInfraPathType(selectedVariantData?.type);
  }, [selectedVariantData]);

  const curveAllowed = useMemo(() => {
    return selectedVariantData?.curve_allowed !== false;
  }, [selectedVariantData]);

  /**
 * Whether the selected variant is a station path offering platform side selection (outside/inside/both)
 * during construction (railBuild.hasPlatformSideChoice). Only then show the toggle, send platform_side, and
 * notify Unity of changes. False for combined double-track stations with variant-defined side/island platforms.
 */
  const hasPlatformSide = useMemo(() => hasPlatformSideChoice(selectedVariantData), [selectedVariantData]);

  /**
 * Effective directions per track (defaults plus user overrides).
 * Defaults depend on city traffic direction and track count; override only tracks whose arrows users click.
 * When trafficHand or trackCount changes, tracks without overrides follow the defaults automatically.
 */
  const trackDirections = useMemo(() => {
    const defaults = defaultTravelDirections(trackCount, trafficHand);
    return defaults.map((d, i) => directionOverrides[i] ?? d);
  }, [trackCount, trafficHand, directionOverrides]);

  /**
 * Default spacing (m), directly from master width (adjacent corridors without gaps).
 * Use fallback only if width is missing abnormally.
 */
  const trackSpacingBase = useMemo(() => {
    const w = selectedVariantData?.width;
    return typeof w === "number" && w > 0 ? w : FALLBACK_TRACK_SPACING;
  }, [selectedVariantData]);

  /**
 * Effective spacing (m). Add widen/narrow adjustments to the default, then clamp to the default multiplied
 * by [TRACK_SPACING_MIN_FACTOR, TRACK_SPACING_MAX_FACTOR].
 */
  const trackSpacing = useMemo(
    () => clampTrackSpacing(trackSpacingBase, trackSpacingDelta),
    [trackSpacingBase, trackSpacingDelta]
  );

  /** Widen (+1) or narrow (-1) spacing by one button step (TRACK_SPACING_STEP). */
  const adjustTrackSpacing = useCallback((direction: 1 | -1) => {
    setTrackSpacingDelta(
      (prev) =>
        clampTrackSpacing(trackSpacingBase, prev + direction * TRACK_SPACING_STEP) -
        trackSpacingBase
    );
  }, [trackSpacingBase]);

  // Detect clamped endpoints to disable the corresponding buttons, avoiding floating-point equality comparisons.
  const canShrinkTrackSpacing = trackSpacing > trackSpacingBase * TRACK_SPACING_MIN_FACTOR + 1e-9;
  const canWidenTrackSpacing = trackSpacing < trackSpacingBase * TRACK_SPACING_MAX_FACTOR - 1e-9;

  /**
 * Components for expanding parallel tracks from the centerline.
 * Generated only for new track placement; null for roads, infrastructure, station buildings, and existing path editing.
 * null means a single path for confirmation, costs, and preview. Even trackCount=1 uses components to send travel_direction.
 * People movers (e.g. ropeways) and combined double-track variants use one bidirectional path and do not expand.
 */
  const railComponents = useMemo((): CombinedPathComponent[] | null => {
    if (selectedVariant === null || pathId != null) return null;
    if (!isRailType(selectedVariantData?.type) || isSingleRailPathVariant(selectedVariantData)) return null;
    return buildRailComponents(selectedVariant, trackCount, trackSpacing, trackDirections);
  }, [selectedVariant, selectedVariantData, pathId, trackCount, trackSpacing, trackDirections]);

  /**
 * Horizontal centerline length (m) during placement. null with fewer than two nodes.
 * Used in construction costs; curves use approximate Bézier arc lengths.
 * For expanding placement methods, sum the lengths of expanded straight paths.
 */
  const pathLength = useMemo(() => {
    if (!isLayoutBuild) return computePathLength(nodes, curveMode, startControl, endControl, rawWaypointControls);
    if (!currentLayout?.paths?.length) return null;
    return currentLayout.paths.reduce((sum, path) => {
      const [sx, , sz] = path.start_point as number[];
      const [ex, , ez] = path.end_point as number[];
      return sum + Math.hypot(ex - sx, ez - sz);
    }, 0);
  }, [isLayoutBuild, currentLayout, nodes, curveMode, startControl, endControl, rawWaypointControls]);

  /* ---- Selection and placement mode control ---- */

  /**
 * Compute individual lateral offsets (m) for composite parallel-track previews in Unity.
 * Nonempty only for new track placement; roads, infrastructure, people movers, combined double track, and existing
 * path editing return [] (one centerline preview). Uses the same conditions/calculation as railComponents
 * (spacing = master width + adjustment), but computes directly from variantId because selectedVariant has not
 * updated when STATE_EDIT_PATH is sent. Continued placement also uses current spacing.
 */
  const lateralOffsetsForVariant = useCallback((variantId: number, spacingDelta: number = trackSpacingDelta): number[] => {
    const data = masterData?.paths?.[variantId];
    if (pathId != null || !data || !isRailType(data.type) || isSingleRailPathVariant(data)) {
      return [];
    }
    const base = typeof data.width === "number" && data.width > 0 ? data.width : FALLBACK_TRACK_SPACING;
    return computeLateralOffsets(trackCount, clampTrackSpacing(base, spacingDelta));
  }, [masterData, pathId, trackCount, trackSpacingDelta]);

  /**
 * Compute directions (DIR_*) for each track in Unity's composite parallel-track preview.
 * Use the same conditions as lateralOffsetsForVariant; when nonempty, return trackDirections with matching count/order.
 * Send alongside offsets because station preview platform extents (TrackPath.ResolvePlatformExtents) depend on direction.
 */
  const travelDirectionsForVariant = useCallback((variantId: number): number[] => {
    const data = masterData?.paths?.[variantId];
    if (pathId != null || !data || !isRailType(data.type) || isSingleRailPathVariant(data)) {
      return [];
    }
    return trackDirections;
  }, [masterData, pathId, trackDirections]);

  /**
 * Select a variant and switch Unity to STATE_EDIT_PATH.
 * Called by variant cards and existing path loading. Unity InitializeRoadPlacement initializes curve mode
 * (curved if allowed, otherwise straight), synchronized through is_curved in update_shape.
 */
  const selectVariant = useCallback((variantId: number) => {
    setSelectedVariant(variantId);
    const variantData = masterData?.paths?.[variantId];
    // Apply defaults only when selecting a type for new construction. Preserve continuous-placement choices and saved existing-path values.
    const initialPassageMode = pathId == null && variantData?.type === PATH_STATION_HOUSE
      ? variantData.default_passage_mode ?? "elevated" : passageMode;
    setPassageMode(initialPassageMode);
    // Revert to single placement if the selected type does not support the current method.
    const method = pathId == null && buildMethodsFor(variantData?.type).includes(buildMethod)
      ? buildMethod : PATH_BUILD_METHOD_SINGLE;
    setBuildMethod(method);
    sendChangeState("STATE_EDIT_PATH", {
      path_type: variantData?.type ?? null,
      path_variant: variantId,
      path_id: pathId ?? null,
      // Initial station platform side, passed when initializing Unity placement.
      // The effect below subsequently applies changes through edit_path:change_platform_side.
      // For variants without side selection, pass the same outside default used when platform_side is omitted.
      platform_side: hasPlatformSideChoice(variantData) ? platformSide : PLATFORM_SIDE_OUTSIDE,
      house_ends: houseEnds,
      passage_mode: initialPassageMode,
      // Initial offsets/directions for parallel tracks, making the 3D preview show N parallel tracks.
      // The effect below applies later count/direction changes through edit_path:change_lateral_offsets.
      lateral_offsets: lateralOffsetsForVariant(variantId),
      travel_directions: travelDirectionsForVariant(variantId),
      // Placement method determines Unity's click sequence. Apply later subdivision changes through edit_path:change_grid_divisions.
      build_method: method,
      grid_divisions: gridDivisions,
    }).catch((err) => {
      console.error("[usePathConstruction] sendChangeState failed:", err);
    });
  }, [masterData, pathId, houseEnds, passageMode, platformSide, lateralOffsetsForVariant, travelDirectionsForVariant, buildMethod, gridDivisions]);

  /** Clear variant selection and placement state, restoring Unity's normal state (panel back button). */
  const deselectVariant = useCallback(() => {
    setSelectedVariant(null);
    setTrackSpacingDelta(0);
    setHeight(0);
    setEmbankmentHeight(0);
    setSelectedNodeIndex(-1);
    setCurveMode("curved");
    setNodes([]);
    setRawPoints([]);
    setStartControl(null);
    setEndControl(null);
    setRawWaypointControls([]);
    setEstimatedCost(null);
    // Resetting/canceling placement also clears interference highlights.
    sendClearHighlights("path_build").catch((err) => {
      console.error("[usePathConstruction] sendClearHighlights failed:", err);
    });
    sendChangeState("STATE_NORMAL").catch((err) => {
      console.error("[usePathConstruction] sendChangeState (clear) failed:", err);
    });
  }, []);

  /** Tracks: count (1–10). Recompute default directions and clear overrides. */
  const changeTrackCount = useCallback((n: number) => {
    const c = clampInt(n, MIN_TRACK_COUNT, MAX_TRACK_COUNT);
    setTrackCount(c);
    setDirectionOverrides(Array(c).fill(null));
  }, []);

  /** Tracks: cycle track index's direction forward → backward → both. */
  const cycleTrackDirection = useCallback((index: number) => {
    setDirectionOverrides((prev) => {
      const defaults = defaultTravelDirections(trackCount, trafficHand);
      const current = prev[index] ?? defaults[index];
      const next = prev.slice();
      // Fit the array length to trackCount before overriding the specified index.
      while (next.length < trackCount) next.push(null);
      next[index] = cycleDirection(current);
      return next;
    });
  }, [trackCount, trafficHand]);

  /**
 * Apply the panel elevation stepper to the selected or next node.
 * Send change_node to Unity to update the node's relative height.
 */
  const updateHeight = useCallback((value: number) => {
    setHeight(value);
    sendEditPathChangeNode(selectedNodeIndex, value).catch((err) => {
      console.error("[usePathConstruction] sendEditPathChangeNode failed:", err);
    });
  }, [selectedNodeIndex]);

  /**
 * Apply the panel embankment stepper to the selected or next node.
 * Clamp offsets to -5 to +5 m and send change_embankment to Unity.
 */
  const updateEmbankmentHeight = useCallback((value: number) => {
    const clamped = Math.max(-5, Math.min(5, value));
    setEmbankmentHeight(clamped);
    sendEditPathChangeEmbankment(selectedNodeIndex, clamped).catch((err) => {
      console.error("[usePathConstruction] sendEditPathChangeEmbankment failed:", err);
    });
  }, [selectedNodeIndex]);

  /**
 * Send terrain-following toggles to Unity. Since Unity is the source of truth, do not update state directly;
 * apply follow_terrain from Unity's edit_path:update_shape response.
 * Values: -1 unset, 0 disabled, 1 enabled.
 */
  const setFollowTerrain = useCallback((value: number) => {
    sendEditPathSetFollowTerrain(value).catch((err) => {
      console.error("[usePathConstruction] sendEditPathSetFollowTerrain failed:", err);
    });
  }, []);

  /**
 * Continuous placement: after confirming a path, clear only placement state while retaining the variant/track
 * settings, then return Unity to STATE_EDIT_PATH. Carry elevation, embankment, curve, terrain-following, spacing,
 * and placement method settings into the next placement. When switching methods, restart with the new method
 * because the placed points have different meanings.
 */
  const continuePlacement = useCallback((method: number = buildMethod) => {
    if (selectedVariant === null) return;
    const variantData = masterData?.paths?.[selectedVariant];
    setSelectedNodeIndex(-1);
    setNodes([]);
    setRawPoints([]);
    setRawEmbankmentHeights([]);
    setStartControl(null);
    setEndControl(null);
    setRawWaypointControls([]);
    setEstimatedCost(null);
    setEstimatedDurationDays(null);
    setCostPerSimulationDays(null);
    setCostSimulationDays(null);
    setMaintenanceCost(null);
    setEpochRevenue(null);
    setAvailableFunds(null);
    sendChangeState("STATE_EDIT_PATH", {
      path_type: variantData?.type ?? null,
      path_variant: selectedVariant,
      path_id: pathId ?? null,
      platform_side: hasPlatformSideChoice(variantData) ? platformSide : PLATFORM_SIDE_OUTSIDE,
      house_ends: houseEnds,
      passage_mode: passageMode,
      initial_height: height,
      initial_embankment_height: embankmentHeight,
      initial_curved: curveMode === "curved",
      initial_follow_terrain: followTerrain,
      lateral_offsets: lateralOffsetsForVariant(selectedVariant),
      travel_directions: travelDirectionsForVariant(selectedVariant),
      build_method: method,
      grid_divisions: gridDivisions,
    }).catch((err) => {
      console.error("[usePathConstruction] sendChangeState (continue) failed:", err);
    });
  }, [selectedVariant, masterData, pathId, houseEnds, passageMode, platformSide, height, embankmentHeight, curveMode, followTerrain, lateralOffsetsForVariant, travelDirectionsForVariant, buildMethod, gridDivisions]);

  /** Switch placement methods and ask Unity to restart placement with the new method. */
  const changeBuildMethod = useCallback((method: number) => {
    if (method === buildMethod) return;
    setBuildMethod(method);
    continuePlacement(method);
  }, [buildMethod, continuePlacement]);

  /** Change grid subdivisions (index 0 = first side, 1 = second side) and redraw Unity's provisional grid. */
  const changeGridDivision = useCallback((index: 0 | 1, value: number) => {
    const next: [number, number] = [...gridDivisions];
    next[index] = clampInt(value, 1, PATH_GRID_BUILD_MAX_DIVISIONS);
    setGridDivisions(next);
    sendEditPathChangeGridDivisions(next).catch((err) => {
      console.error("[usePathConstruction] sendEditPathChangeGridDivisions failed:", err);
    });
  }, [gridDivisions]);

  /**
 * Send the current path shape to the backend for confirmation, using HTTP directly from Tauri without Unity.
 *
 * For parallel tracks (nonnull railComponents), expand the centerline by each component's lateral/height offsets,
 * set travel_direction, and create all paths in one POST. Other cases (roads, infrastructure, single paths,
 * existing path editing) send one path. Explicitly use travel_direction=DIR_BOTH for single-path track variants
 * (people movers/combined double track); the backend rejects other directions for combined double track.
 */
  // Build request geometry once so prevalidation and confirmation send identical full paths and controls.
  const constructionBody = useMemo(() => {
    if (isLayoutBuild) {
      return currentLayout?.paths?.length ? { paths: currentLayout.paths, align_station_tracks: false } : null;
    }
    if (selectedVariant === null || rawPoints.length < 2) return null;
    const isCurved = curveMode !== "straight";
    if (railComponents) {
      const anchorDirs = computeAnchorOffsetDirs(
        rawPoints.map((p) => ({ x: p[0], z: p[2] }))
      );
      const tempIdBase = "build";

      const pathsPayload = railComponents.map((comp, idx) => {
        const pts = offsetPolyline3D(
          rawPoints,
          comp.lateral_offset ?? 0,
          comp.height_offset ?? 0
        );
        const data: Record<string, unknown> = {
          path_variant: comp.path_variant,
          start_point: pts[0],
          end_point: pts[pts.length - 1],
          curved: isCurved,
          travel_direction: comp.travel_direction ?? DIR_BOTH,
          path_temp_id: `${tempIdBase}_${idx}`,
        };
        // For station variants with platform side selection, apply the same side to all tracks.
        if (hasPlatformSide) {
          data.platform_side = platformSide;
        }
        if (isCurved && startControl) {
          const oc = offsetControlPoint(startControl, anchorDirs[0], comp.lateral_offset ?? 0);
          if (oc) data.start_control = [oc.x, (startControl.y ?? rawPoints[0][1]) + (comp.height_offset ?? 0), oc.z];
        }
        if (isCurved && endControl) {
          const oc = offsetControlPoint(
            endControl,
            anchorDirs[anchorDirs.length - 1],
            comp.lateral_offset ?? 0
          );
          if (oc) data.end_control = [oc.x, (endControl.y ?? rawPoints[rawPoints.length - 1][1]) + (comp.height_offset ?? 0), oc.z];
        }
        // Waypoints. When centerline controls were received, translate controls in the same offset directions
        // as their anchors to keep each track's curve similar to the centerline.
        const waypointPts = pts.slice(1, -1);
        if (waypointPts.length > 0) {
          data.waypoints = waypointPts.map((pt, i) => {
            const wc = isCurved ? rawWaypointControls[i] : undefined;
            if (!wc || (!wc.control_in && !wc.control_out)) return pt;
            const dir = anchorDirs[i + 1];
            const ci = wc.control_in
              ? offsetControlPoint(
                  { x: wc.control_in[0], z: wc.control_in[wc.control_in.length - 1] },
                  dir,
                  comp.lateral_offset ?? 0
                )
              : null;
            const co = wc.control_out
              ? offsetControlPoint(
                  { x: wc.control_out[0], z: wc.control_out[wc.control_out.length - 1] },
                  dir,
                  comp.lateral_offset ?? 0
                )
              : null;
            return {
              position: pt,
              control_in: ci ? [ci.x, (wc.control_in?.[1] ?? rawPoints[i + 1][1]) + (comp.height_offset ?? 0), ci.z] : null,
              control_out: co ? [co.x, (wc.control_out?.[1] ?? rawPoints[i + 1][1]) + (comp.height_offset ?? 0), co.z] : null,
            };
          });
        }
        if (rawEmbankmentHeights.length > 0) {
          data.embankment_heights = rawEmbankmentHeights;
        }
        // Terrain-following override (omit when unset, inheriting the variant default).
        if (followTerrain !== FOLLOW_TERRAIN_UNSET) {
          data.follow_terrain = followTerrain;
        }
        return data;
      });

      return {
        paths: pathsPayload,
        align_station_tracks: (selectedVariantData?.platform_width ?? 0) > 0 && selectedVariantData?.type === PATH_TRACK,
      };
    }
    const pathData: Record<string, unknown> = {
      path_variant: selectedVariant,
      house_ends: selectedVariantData?.type === PATH_STATION_HOUSE ? houseEnds : "both",
      passage_mode: selectedVariantData?.type === PATH_STATION_HOUSE ? passageMode : "elevated",
      start_point: [rawPoints[0][0], rawPoints[0][1], rawPoints[0][2]],
      end_point: [rawPoints[rawPoints.length - 1][0], rawPoints[rawPoints.length - 1][1], rawPoints[rawPoints.length - 1][2]],
      curved: isCurved,
    };

    if (isSingleRailPathVariant(selectedVariantData)) {
      pathData.travel_direction = DIR_BOTH;
    }

    if (pathId != null) delete pathData.house_ends;

    // Assign temporary IDs only for new creation.
    if (pathId == null) {
      pathData.path_temp_id = "build";
    }

    // Station platform side can be set only during construction; omit for existing path updates.
    // Combined double-track stations have no selectable side, so omit it.
    if (pathId == null && hasPlatformSide) {
      pathData.platform_side = platformSide;
    }

    // Preserve Unity's designed control heights so saving does not change longitudinal tangents.
    if (isCurved && startControl) {
      pathData.start_control = [startControl.x, startControl.y ?? rawPoints[0][1], startControl.z];
    }
    if (isCurved && endControl) {
      pathData.end_control = [endControl.x, endControl.y ?? rawPoints[rawPoints.length - 1][1], endControl.z];
    }

    // Intermediate waypoints. If controls were received from Unity, send
    // {position, control_in, control_out} to persist curves adjusted by dragging.
    // Without controls, send positions only and let the backend complete them automatically with Catmull-Rom.
    const waypoints = rawPoints.slice(1, -1);
    if (waypoints.length > 0) {
      pathData.waypoints = waypoints.map((pt, i) => {
        const wc = isCurved ? rawWaypointControls[i] : undefined;
        if (wc && (wc.control_in || wc.control_out)) {
          return {
            position: [pt[0], pt[1], pt[2]],
            control_in: wc.control_in,
            control_out: wc.control_out,
          };
        }
        return [pt[0], pt[1], pt[2]];
      });
    }

    // Embankment offsets (embankment/cutting heights for each anchor, -5 to +5 m).
    if (rawEmbankmentHeights.length > 0) {
      pathData.embankment_heights = rawEmbankmentHeights;
    }

    // Terrain-following override (omit when unset, inheriting the variant default).
    if (followTerrain !== FOLLOW_TERRAIN_UNSET) {
      pathData.follow_terrain = followTerrain;
    }

    return { paths: [pathData], align_station_tracks: false };
  }, [isLayoutBuild, currentLayout, selectedVariant, selectedVariantData, rawPoints, rawEmbankmentHeights, curveMode, startControl, endControl, rawWaypointControls, pathId, railComponents, hasPlatformSide, houseEnds, passageMode, platformSide, followTerrain]);

  const [localCanConfirm, setLocalCanConfirm] = useState(false);
  const [maxSpanViolations, setMaxSpanViolations] = useState<PathMaxSpanViolation[]>([]);
  const localCanConfirmRef = useRef(false);
  const [validation, setValidation] = useState<{
    key: string; result?: PathValidationResult; error?: string;
  } | null>(null);
  const [validationAttempt, setValidationAttempt] = useState(0);
  const [shapeRevision, setShapeRevision] = useState("");
  const currentShapeRevision = useRef("");
  const receivedShapeKey = useRef("");
  const retryValidation = useCallback(() => {
    setValidation(null);
    setValidationAttempt((value) => value + 1);
  }, []);

  // Request placement-method expansion from the core to build request geometry, then return it to Unity with the queried edit generation.
  // Unity accepts only current-generation results, so refetch even when settings advance the generation without changing geometry.
  useEffect(() => {
    if (!apiBaseUrl || layoutRequest === "") return;
    const revision = shapeRevision;
    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        const result = await fetchApi<{ paths: Record<string, unknown>[] }>(
          `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/layout`,
          { method: "POST", headers: { "Content-Type": "application/json" }, body: layoutRequest }
        );
        if (cancelled) return;
        setLayout({ key: layoutRequest, paths: result.paths });
        void sendEditPathShapeCommand({
          op: "apply_layout",
          revision,
          layout_json: JSON.stringify(result.paths.map((p) => [p.start_point, p.end_point])),
        }).catch(console.error);
      } catch (error) {
        if (!cancelled) setLayout({ key: layoutRequest, error: error instanceof Error ? error.message : String(error) });
      }
    }, 150);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [apiBaseUrl, layoutRequest, shapeRevision, validationAttempt]);

  const validationKey = JSON.stringify({ ...constructionBody, exclude_path_id: pathId ?? null });
  useEffect(() => {
    const win = getCurrentWindow();
    let disposed = false, stop: (() => void) | undefined;
    const report = async () => {
      if (await win.isVisible() && !disposed) await observe({ pathVariant: selectedVariant ?? -1, pathShape: { selected_points: rawPoints, phase: shapePhase, is_curved: curveMode === "curved" } });
    };
    void win.listen(GUIDE_QUERY, () => void report()).then(fn => { if (disposed) fn(); else stop = fn; });
    void report();
    return () => { disposed = true; stop?.(); };
  }, [selectedVariant, rawPoints, shapePhase, curveMode]);
  const currentValidationKey = useRef(validationKey);
  currentValidationKey.current = validationKey;
  const canConfirm = localCanConfirm && constructionBody !== null
    && validation?.key === validationKey && validation.result?.is_valid === true && allowsPathBody(constructionBody);
  // Show pending/failed expansion and the absence of buildable sections before placement-condition validation.
  const layoutMessage = layoutRequest === "" ? null
    : currentLayout === null ? t("path_construction.collisionChecking")
    : currentLayout.error !== undefined ? t("path_construction.validationFailed", { reason: currentLayout.error })
    : currentLayout.paths?.length === 0 ? t("path_construction.layoutEmpty") : null;
  const validationMessages = maxSpanViolations.length > 0
    ? describeMaxSpanViolations(maxSpanViolations, t)
    : !localCanConfirm
    ? [t(shapePhase === "placing" ? "path_construction.shapeUnready" : "path_construction.collisionUnready")]
    : !apiBaseUrl
      ? [t("path_construction.validationUnavailable")]
      : layoutMessage !== null
        ? [layoutMessage]
      : validation?.key !== validationKey
        ? [t("path_construction.collisionChecking")]
        : validation.error !== undefined
          ? [t("path_construction.validationFailed", { reason: validation.error })]
          : validation.result ? describePathValidation(validation.result, t) : [];
  // Reasons in the confirmation area. Per-section geometry violations (node distance, curve radius, grade) are located by
  // shape-editor callouts, so collapse them into one "Shape contains errors" line; use validationMessages wording for other reasons.
  const shapeErrorMessage = t("path_construction.shapeError");
  const confirmMessages = maxSpanViolations.length > 0
    ? [shapeErrorMessage]
    : localCanConfirm && apiBaseUrl && validation?.key === validationKey && validation.result
      ? describePathValidation(validation.result, t, shapeErrorMessage)
      : validationMessages;
  const validatedAlignment = validation?.key === validationKey && validation.result?.alignments
    ? summarizeAlignment(validation.result.alignments) : null;
  const displayAlignment = validatedAlignment ?? alignment;
  const canRetryValidation = localCanConfirm && !!apiBaseUrl
    && ((validation?.key === validationKey && validation.error !== undefined) || currentLayout?.error !== undefined);

  useEffect(() => {
    void sendClearHighlights("path_build");
    if (shapeRevision) void sendEditPathAlignment(shapeRevision, null).catch(console.error);
    if (!apiBaseUrl || !constructionBody || shapePhase !== "editing") return;
    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        const result = await fetchApi<PathValidationResult>(
          `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/validate`,
          { method: "POST", headers: { "Content-Type": "application/json" }, body: validationKey }
        );
        if (!cancelled && currentValidationKey.current === validationKey && currentShapeRevision.current === shapeRevision) {
          setValidation({ key: validationKey, result });
          if (shapeRevision) void sendEditPathAlignment(shapeRevision, result.alignments, describeJunctionLabels(result, t)).catch(console.error);
          void sendHighlightObjects(result, "path_build").catch(console.error);
        }
      } catch (error) {
        if (!cancelled && currentValidationKey.current === validationKey && currentShapeRevision.current === shapeRevision) {
          setValidation({ key: validationKey, error: error instanceof Error ? error.message : String(error) });
        }
      }
    }, 150);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [apiBaseUrl, shapePhase, shapeRevision, localCanConfirm, validationKey, validationAttempt, t]);

  /**
 * Confirm submission. For new paths, add extraFields to each path (e.g. construction project tag; exclude these
 * from prevalidation and estimate geometry) and return created path IDs. Existing updates return an empty array.
 */
  const confirmPath = useCallback(async (extraFields?: Record<string, unknown>): Promise<number[]> => {
    const tutorialState = snapshot();
    if (!allowsPathBody(constructionBody)) throw new Error(t("tutorial.guide.placementRequired"));
    if (!apiBaseUrl || !constructionBody || !canConfirm || !localCanConfirmRef.current
        || currentValidationKey.current !== validationKey || currentShapeRevision.current !== shapeRevision) {
      throw new Error(validationMessages.join("\n") || t("path_construction.collisionChecking"));
    }
    // Run read-only validation again immediately before sending to catch city changes from other edits.
    let checked: PathValidationResult;
    try {
      checked = await fetchApi<PathValidationResult>(
        `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/validate`,
        { method: "POST", headers: { "Content-Type": "application/json" }, body: validationKey }
      );
    } catch (error) {
      const reason = error instanceof Error ? error.message : String(error);
      if (currentValidationKey.current === validationKey && currentShapeRevision.current === shapeRevision)
        setValidation({ key: validationKey, error: reason });
      throw new Error(t("path_construction.validationFailed", { reason }));
    }
    if (!localCanConfirmRef.current || currentValidationKey.current !== validationKey || currentShapeRevision.current !== shapeRevision) {
      throw new Error(t("path_construction.collisionChecking"));
    }
    setValidation({ key: validationKey, result: checked });
    if (shapeRevision) void sendEditPathAlignment(shapeRevision, checked.alignments, describeJunctionLabels(checked, t)).catch(console.error);
    void sendHighlightObjects(checked, "path_build").catch(console.error);
    if (!checked.is_valid) {
      throw new Error(describePathValidation(checked, t).join("\n"));
    }
    const submission = { ...constructionBody, paths: constructionBody.paths.map(p => ({ ...p, ...extraFields })) };
    if (!allowsPathBody(submission) || (tutorialState?.active && snapshot()?.runId !== tutorialState.runId)) throw new Error(t("tutorial.guide.placementRequired"));
    const response = await fetchApi<{ paths: Array<{ path_id: number }> }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/${pathId ?? ""}`,
      { method: pathId == null ? "POST" : "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(pathId == null
          ? submission
          : constructionBody.paths[0]) }
    );
    if (pathId == null && tutorialState?.active && snapshot()?.runId === tutorialState.runId) {
      await observe({ savedPaths: (response?.paths ?? []).map(p => p.path_id) });
    }
    setValidation(null);
    localCanConfirmRef.current = false;
    setLocalCanConfirm(false);
    void sendClearHighlights("path_build");
    // Notify regardless of type. Placement may split existing road sections through intersections/snapping, adding member sections.
    // Existing edits change length/shape, and changing away from street/highway variants also removes membership.
    void notifyRoadsChanged(undefined, "path_build");
    if (pathId == null && response?.paths?.length) {
      void invoke("open_registered_window", { windowType: "path_detail", entityId: String(response.paths[0].path_id) });
    }
    return pathId == null ? (response?.paths ?? []).map((p) => p.path_id) : [];
  }, [apiBaseUrl, constructionBody, canConfirm, validationKey, shapeRevision, pathId, t, validationMessages]);

  /* ---- Fetch city traffic direction for default direction assignment ---- */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/city/`, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{ traffic_hand?: number }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        if (typeof data.traffic_hand === "number") {
          setTrafficHand(data.traffic_hand);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[usePathConstruction] fetch city traffic_hand failed:", err);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl]);

  /* ---- Notify Unity of parallel-track offset/direction changes ---- */
  // Do not reinitialize Unity placement on trackCount/direction changes, preserving placed nodes.
  // Besides initial STATE_EDIT_PATH values, send change_lateral_offsets whenever railComponents
  // offsets/directions change to update the 3D preview's parallel track count, platform sides,
  // and direction arrows.
  // For nontracks (railComponents=null), send [] to restore a single preview (Unity ignores unchanged values).
  useEffect(() => {
    if (selectedVariant === null) return;
    const offsets = railComponents
      ? railComponents.map((c) => c.lateral_offset ?? 0)
      : [];
    const directions = railComponents
      ? railComponents.map((c) => c.travel_direction ?? DIR_BOTH)
      : [];
    sendEditPathChangeLateralOffsets(offsets, directions).catch((err) => {
      console.error("[usePathConstruction] sendEditPathChangeLateralOffsets failed:", err);
    });
  }, [railComponents, selectedVariant]);

  useEffect(() => {
    if (pathId != null || selectedVariantData?.type !== PATH_STATION_HOUSE) return;
    sendSetPassageMode(passageMode).catch(console.error);
    sendSetHouseEnds(houseEnds).catch((err) => console.error("station preview update failed", err));
  }, [houseEnds, passageMode, selectedVariantData, pathId]);

  /* ---- Notify Unity of platform side changes ---- */
  // When platform side toggles change for a station variant, immediately update
  // the preview platform overhangs. Do not send for variants without side selection (hasPlatformSide=false)
  // or when unselected (initial values are passed in selectVariant's STATE_EDIT_PATH).
  useEffect(() => {
    if (selectedVariant === null || !hasPlatformSide) return;
    sendSetPlatformSide(platformSide).catch((err) => {
      console.error("[usePathConstruction] sendSetPlatformSide failed:", err);
    });
  }, [platformSide, selectedVariant, hasPlatformSide]);

  /* ---- Tauri event listeners (Unity → UI) ---- */

  useEffect(() => {
    const appWindow = getCurrentWindow();

    let unlistenSelectNode: (() => void) | undefined;
    let unlistenUpdateShape: (() => void) | undefined;

    // Receive elevation/embankment for the selected or next node; synchronize steppers and edit target.
    // node_id >= 0 means selected node; node_id = -1 means the next node to place.
    appWindow
      .listen<{
        path_id: number;
        node_id: number;
        position: number[];
        node_type: string;
        height?: number;
        embankment_height?: number;
      }>(IPC_EVENT_EDIT_PATH_SELECT_NODE, (event) => {
        const payload = event.payload;
        setSelectedNodeIndex(payload.node_id >= 0 ? payload.node_id : -1);
        if (typeof payload.height === "number") {
          setHeight(payload.height);
        }
        if (typeof payload.embankment_height === "number") {
          setEmbankmentHeight(payload.embankment_height);
        }
      })
      .then((fn) => { unlistenSelectNode = fn; })
      .catch(console.error);

    appWindow
      .listen<PathShapeUpdatePayload>(IPC_EVENT_EDIT_PATH_UPDATE_SHAPE, (event) => {
        console.log("[usePathConstruction] edit_path.update_shape", event.payload);

        const sourceKey = pathValidationShapeKey(event.payload);
        if (receivedShapeKey.current === sourceKey) return;
        receivedShapeKey.current = sourceKey;
        currentShapeRevision.current = event.payload.revision ?? "";
        setShapeRevision(event.payload.revision ?? "");

        currentValidationKey.current = "";
        setValidation(null);
        const spanViolations = event.payload.max_span_violations ?? [];
        setMaxSpanViolations(spanViolations);
        localCanConfirmRef.current = event.payload.can_confirm === true && spanViolations.length === 0;
        setLocalCanConfirm(localCanConfirmRef.current);
        setShapePhase(event.payload.phase);

        // Unity is the curve-mode source of truth. Always synchronize, even without points
        // (immediately after placement initialization, only empty points and is_curved arrive).
        const isCurved = event.payload.is_curved === true;
        setCurveMode(isCurved ? "curved" : "straight");

        // After shape confirmation in curve mode (right-click in 3D), automatically open
        // the 2D shape editor for control adjustments (open manually by button in straight mode).
        // Do not open for expanding placement methods, whose points specify an area.
        if (event.payload.is_finalized === true && isCurved && !isLayoutBuildRef.current) {
          void invoke("open_registered_window", { windowType: "path_shape_editor" }).catch(console.error);
        }

        // Unity also owns terrain-following overrides. Synchronize every time because the value is always sent.
        if (typeof event.payload.follow_terrain === "number") {
          setFollowTerrainState(event.payload.follow_terrain);
        }

        const points = event.payload.selected_points;
        if (points && points.length > 0) {
          setRawPoints(points);
          if (event.payload.embankment_heights && Array.isArray(event.payload.embankment_heights)) {
            setRawEmbankmentHeights(event.payload.embankment_heights);
          }
          // Intermediate anchor controls, in selected_points order excluding endpoints.
          // Reset to empty if omitted (e.g. only two points).
          const wcs = event.payload.waypoint_controls;
          setRawWaypointControls(
            Array.isArray(wcs)
              ? wcs.map((w) => ({
                  control_in: w?.control_in ?? null,
                  control_out: w?.control_out ?? null,
                }))
              : []
          );
          const newNodes: BuildPoint[] = points.map((pt, i) => ({
            x: pt[0],
            z: pt[2] ?? pt[1],
            role: i === 0 ? "start" as const : i === points.length - 1 ? "end" as const : "waypoint" as const,
          }));
          setNodes(newNodes);

          // Check controls against is_curved in the current payload, rather than state curveMode
          // (the previous received value may be stale in the closure).
          if (newNodes.length >= 2 && isCurved) {
            const sc = event.payload.start_control;
            const ec = event.payload.end_control;
            if (sc && sc.length >= 2) {
              setStartControl({ x: sc[0], y: sc.length > 2 ? sc[1] : undefined, z: sc[2] ?? sc[1] });
            } else {
              const controls = computeSplineOrBezierControls(newNodes, "curved");
              setStartControl(controls.start);
            }
            if (ec && ec.length >= 2) {
              setEndControl({ x: ec[0], y: ec.length > 2 ? ec[1] : undefined, z: ec[2] ?? ec[1] });
            } else {
              const controls = computeSplineOrBezierControls(newNodes, "curved");
              setEndControl(controls.end);
            }
          } else {
            setStartControl(null);
            setEndControl(null);
          }
        } else {
          // Reset controls as well when points are empty (after placement initialization/reset).
          setRawPoints([]);
          setRawEmbankmentHeights([]);
          setRawWaypointControls([]);
          setNodes([]);
          setStartControl(null);
          setEndControl(null);
        }
      })
      .then((fn) => { unlistenUpdateShape = fn; })
      .catch(console.error);

    return () => {
      unlistenSelectNode?.();
      unlistenUpdateShape?.();
      // Always clear placement interference highlights on placement-panel unmount.
      // Clear only source="path_build", retaining highlights from other windows such as detail panels.
      void sendClearHighlights("path_build");
    };
  }, []);

  /* ---- Fetch estimated construction cost ---- */
  useEffect(() => {
    if (!apiBaseUrl || selectedVariant === null || !constructionBody) {
      setEstimatedCost(null);
      setEstimatedDurationDays(null);
      setCostPerSimulationDays(null);
      setCostSimulationDays(null);
      setMaintenanceCost(null);
      setEpochRevenue(null);
      setAvailableFunds(null);
      setSupplyRatio(null);
      setExpectedDurationDays(null);
      setAlignment(null);
      setIsCostLoading(false);
      return;
    }

    costAbortControllerRef.current?.abort();
    const controller = new AbortController();
    costAbortControllerRef.current = controller;
    setIsCostLoading(true);
    setAlignment(null);

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/paths/construction-cost`;

    // Estimate and sum using the same geometry as confirmation (offset centerlines for parallel tracks,
    // including controls, embankments, and terrain following; use the longest construction time). Controls and terrain following
    // affect alignment evaluation (curve radius/grade), so do not send anchors alone for automatic completion. Extra keys
    // such as travel_direction are ignored by the backend.
    const requests = constructionBody.paths;

    if (requests.length === 0) {
      setIsCostLoading(false);
      return;
    }

    Promise.all(
      requests.map((req) =>
        fetch(url, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(req),
          signal: controller.signal,
        }).then(async (res) => {
          if (!res.ok) throw new Error(`HTTP ${res.status}`);
          return res.json();
        })
      )
    )
      .then((results) => {
        if (controller.signal.aborted) return;
        const sum = (key: string) =>
          results.reduce((acc, r) => acc + (typeof r[key] === "number" ? r[key] : 0), 0);
        const maxOf = (key: string) =>
          results.reduce((acc, r) => Math.max(acc, typeof r[key] === "number" ? r[key] : 0), 0);
        setEstimatedCost(results.some((r) => r.estimated_construction_cost != null)
          ? sum("estimated_construction_cost")
          : null);
        setEstimatedStandardCost(results.some((r) => r.standard_construction_cost != null)
          ? sum("standard_construction_cost")
          : null);
        setEstimatedDurationDays(results.some((r) => r.estimated_duration_days != null)
          ? maxOf("estimated_duration_days")
          : null);
        setCostPerSimulationDays(results.some((r) => r.cost_per_simulation_days != null)
          ? sum("cost_per_simulation_days")
          : null);
        setCostSimulationDays(results[0]?.simulation_days ?? null);
        setMaintenanceCost(results.some((r) => r.estimated_maintenance_cost != null)
          ? sum("estimated_maintenance_cost")
          : null);
        // Income and available funds are citywide, identical for every track; use the first values.
        setEpochRevenue(results[0]?.last_epoch_revenue ?? null);
        setAvailableFunds(results[0]?.available_funds ?? null);
        // Use the lowest fulfillment across tracks. All tracks share the district's construction proxy agent, so actual
        // allocations differ little, but take the pessimistic value to avoid missing warnings.
        setSupplyRatio(results.some((r) => r.construction_supply_ratio != null)
          ? results.reduce(
              (acc: number, r) => Math.min(acc, typeof r.construction_supply_ratio === "number" ? r.construction_supply_ratio : 1),
              1,
            )
          : null);
        setExpectedDurationDays(results.some((r) => r.expected_duration_days != null)
          ? maxOf("expected_duration_days")
          : null);
        setAlignment(summarizeAlignment(results.map((r) => r.alignment as PathAlignmentResponse | undefined)));
      })
      .catch((err) => {
        if ((err as Error).name !== "AbortError") {
          console.error("[usePathConstruction] fetch construction cost failed:", err);
        }
        if (!controller.signal.aborted) {
          setEstimatedCost(null);
          setEstimatedStandardCost(null);
          setEstimatedDurationDays(null);
          setCostPerSimulationDays(null);
          setCostSimulationDays(null);
          setMaintenanceCost(null);
          setEpochRevenue(null);
          setAvailableFunds(null);
          setSupplyRatio(null);
          setExpectedDurationDays(null);
          setAlignment(null);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setIsCostLoading(false);
        }
      });

    return () => controller.abort();
  }, [apiBaseUrl, selectedVariant, constructionBody]);

  return {
    selectedVariant,
    height,
    embankmentHeight,
    selectedNodeIndex,
    curveMode,
    nodes,
    rawPoints,
    startControl,
    endControl,
    waypointControls: rawWaypointControls,
    selectedVariantData,
    unityPathType,
    unityPathVariant,
    heightEditable,
    isInfrastructure,
    curveAllowed,
    isStationHouse: selectedVariantData?.type === PATH_STATION_HOUSE,
    houseEnds,
    setHouseEnds,
    passageMode,
    setPassageMode,
    hasPlatformSide,
    platformSide,
    setPlatformSide,
    followTerrain,
    setFollowTerrain,
    // Placement method.
    buildMethod,
    isLayoutBuild,
    gridDivisions,
    changeBuildMethod,
    changeGridDivision,
    // Parallel track settings.
    trackCount,
    trackSpacing,
    canWidenTrackSpacing,
    canShrinkTrackSpacing,
    adjustTrackSpacing,
    trackDirections,
    trafficHand,
    railComponents,
    changeTrackCount,
    cycleTrackDirection,
    // Costs.
    estimatedCost,
    estimatedStandardCost,
    estimatedDurationDays,
    costPerSimulationDays,
    costSimulationDays,
    maintenanceCost,
    epochRevenue,
    availableFunds,
    supplyRatio,
    expectedDurationDays,
    isCostLoading,
    pathLength,
    // Alignment.
    alignmentOk: displayAlignment == null || (!displayAlignment.curveViolated && !displayAlignment.gradeViolated),
    // Selection/placement.
    selectVariant,
    deselectVariant,
    updateHeight,
    updateEmbankmentHeight,
    continuePlacement,
    confirmPath,
    canConfirm,
    validationMessages,
    confirmMessages,
    canRetryValidation,
    retryValidation,
  };
}
