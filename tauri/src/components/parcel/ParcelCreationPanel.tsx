/**
 * Parcel creation panel
 *
 * Dedicated panel in the window_type="parcel_creation" popup.
 *
 * The 3D view drives interaction; the panel handles values and confirmation:
 * Opening immediately enters STATE_EDIT_PARCEL. Unity's Ctrl radial menu switches creation modes,
 * as in road construction/district editing. The panel handles width/depth/average-area inputs
 * and confirmation; shape previews appear in 3D. Confirmation keeps the mode active,
 * resetting only selection to allow consecutive creation.
 *
 * Unity (ParcelEditorManager.CurrentMode) is authoritative for mode. Panel mode tiles are
 * another entry point: request changes with world:change_state and receive actual results
 * via edit_parcel:set_mode. Do not echo them back.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";
import { setHintbarText } from "../../utils/hintbar";
import {
  sendChangeState,
  sendEditParcelUpdatePreview,
  sendEditParcelResetSelection,
  sendEditParcelSetRectParams,
  sendSessionReload,
  IPC_EVENT_EDIT_PARCEL_SELECT_POINT,
  IPC_EVENT_EDIT_PARCEL_SELECT_PATH,
  IPC_EVENT_EDIT_PARCEL_SELECT_PARCEL,
  IPC_EVENT_EDIT_PARCEL_SET_MODE,
  IPC_EVENT_EDIT_PARCEL_UPDATE_ROTATION,
} from "../../utils/ipc";
import { NumberStepper } from "../common/NumberStepper";
import { showNotification } from "../../utils/notification";
import { fetchApi, ApiError } from "../../utils/api";
import "./ParcelCreationPanel.css";

type CreationMode =
  | "empty_area"
  | "along_path"
  | "random"
  | "block"
  | "split_rectangle"
  | "split_random";

/** One parcel returned by POST /api/v1/parcels/preview. */
type ParcelPreviewItem = {
  polygon: number[][];
  center_point: number[];
  area: number;
};

/** Modes that search empty space from a point selection (Unity uses the PARCEL_CREATION_POINT preset). */
const POINT_MODES: CreationMode[] = ["empty_area", "random", "block"];

function isPointCreationMode(mode: CreationMode | null): boolean {
  return mode !== null && POINT_MODES.includes(mode);
}

/**
 * Default values and input ranges for width / height by mode.
 *
 * The same inputs mean different things: empty_area and along_path specify parcel dimensions;
 * random / block specify a search area generating multiple parcels, an order of magnitude larger.
 * Always restore defaults on mode changes so previous values do not remain out of range.
 * empty_area / along_path limits accommodate the largest preset (150×150) stretched to a 1:3
 * aspect ratio (about 87×260m).
 */
const SIZE_INPUT_PRESETS: Record<string, { width: number; height: number; min: number; max: number; step: number }> = {
  empty_area: { width: 10, height: 20, min: 1, max: 300, step: 1 },
  along_path: { width: 20, height: 20, min: 1, max: 300, step: 1 },
  random: { width: 200, height: 200, min: 1, max: 400, step: 10 },
  block: { width: 200, height: 200, min: 20, max: 400, step: 10 },
};

/** Initial creation mode on window opening (must match Unity ParcelEditorManager.DefaultMode). */
const DEFAULT_CREATION_MODE: CreationMode = "along_path";

const DEFAULT_SIZE_INPUT = SIZE_INPUT_PRESETS[DEFAULT_CREATION_MODE];

/**
 * Maximum parcels created around a click in one roadside operation.
 * Also clamped by backend geo_parcels.PATH_PARCEL_SPOT_MAX_COUNT.
 */
const ALONG_PATH_MAX_COUNT = 5;

/**
 * Parcel size presets: typical frontage × depth (m) per use.
 * All modes share presets; PRESET_FIELD chooses which inputs receive them.
 */
const SIZE_PRESETS = [
  { nameKey: "parcel_creation.presetSmallHouse", width: 10, depth: 10 },
  { nameKey: "parcel_creation.presetHouseShop", width: 15, depth: 15 },
  { nameKey: "parcel_creation.presetMidRise", width: 30, depth: 30 },
  { nameKey: "parcel_creation.presetHighRise", width: 60, depth: 60 },
  { nameKey: "parcel_creation.presetLargeFactory", width: 150, depth: 150 },
];

/**
 * Preset target fields: size=width/depth, lot=frontage/depth (whole block), area=average area.
 * Random/whole-block width/depth specify search area and are excluded from presets.
 * The aspect ratio slider is available only in size modes.
 */
type PresetField = "size" | "lot" | "area";

const PRESET_FIELD: Record<CreationMode, PresetField> = {
  empty_area: "size",
  along_path: "size",
  block: "lot",
  random: "area",
  split_rectangle: "area",
  split_random: "area",
};

/**
 * Steps on each side of the aspect ratio slider. Depth/width = 3^(step/ASPECT_STEPS),
 * logarithmically dividing 1:3 through 3:1 with 1:1 at the center. Preserve the preset area.
 */
const ASPECT_STEPS = 10;
const ASPECT_MAX_RATIO = 3;

function aspectRatioOf(step: number): number {
  return ASPECT_MAX_RATIO ** (step / ASPECT_STEPS);
}

/** Display "width : depth" with the shorter side normalized to 1. */
function formatAspect(step: number): string {
  const ratio = aspectRatioOf(step);
  const longer = String(Math.round(Math.max(ratio, 1 / ratio) * 10) / 10);
  return ratio >= 1 ? `1 : ${longer}` : `${longer} : 1`;
}

const CREATION_SECTIONS = [
  {
    titleKey: "parcel_creation.sectionCreate",
    modes: [
      {
        mode: "along_path" as CreationMode,
        icon: "creation_2.png",
        tooltipKey: "parcel_creation.modeAlongPath",
      },
      {
        mode: "block" as CreationMode,
        icon: "creation_2.png",
        tooltipKey: "parcel_creation.modeBlock",
      },
      {
        mode: "random" as CreationMode,
        icon: "creation_4.png",
        tooltipKey: "parcel_creation.modeRandom",
      },
      {
        mode: "empty_area" as CreationMode,
        icon: "creation_1.png",
        tooltipKey: "parcel_creation.modeSingle",
      },
    ],
  },
  {
    titleKey: "parcel_creation.sectionSplit",
    modes: [
      {
        mode: "split_rectangle" as CreationMode,
        icon: "creation_3.png",
        tooltipKey: "parcel_creation.modeSplitRect",
      },
      {
        mode: "split_random" as CreationMode,
        icon: "creation_4.png",
        tooltipKey: "parcel_creation.modeSplitRandom",
      },
    ],
  },
];

const parcelCreationIconModules = import.meta.glob<{ default: string }>(
  "../../assets/images/parcel_creation/*.png",
  { eager: true }
);

function resolveParcelCreationIcon(filename: string): string {
  return (
    parcelCreationIconModules[`../../assets/images/parcel_creation/${filename}`]
      ?.default ?? ""
  );
}

type ParcelCreationPanelProps = {
  apiBaseUrl: string;
};

export function ParcelCreationPanel({ apiBaseUrl }: ParcelCreationPanelProps) {
  const { t } = useTranslation();
  const appWindow = useMemo(() => getCurrentWindow(), []);

  // Exactly one creation mode is always selected; there is no unselected state. Unity is authoritative.
  const [selectedMode, setSelectedMode] = useState<CreationMode>(DEFAULT_CREATION_MODE);
  const [area, setArea] = useState(100);
  const [width, setWidth] = useState(DEFAULT_SIZE_INPUT.width);
  const [height, setHeight] = useState(DEFAULT_SIZE_INPUT.height);
  const [angle, setAngle] = useState(0);
  const [side, setSide] = useState<"left" | "right">("left");

  // For block (whole-block) mode: roadside parcel width/depth.
  const [lotWidth, setLotWidth] = useState(10);
  const [lotDepth, setLotDepth] = useState(20);
  // Shared by random / split_random / block: parcel width variation (%).
  const [jitterPct, setJitterPct] = useState(25);

  // Selected size preset (SIZE_PRESETS index). Manual editing of target fields clears it.
  const [selectedPreset, setSelectedPreset] = useState<number | null>(null);
  const [aspectStep, setAspectStep] = useState(0);

  const [pickedPosition, setPickedPosition] = useState<number[] | null>(null);
  const [selectedPathId, setSelectedPathId] = useState<number | null>(null);
  const [selectedParcelId, setSelectedParcelId] = useState<number | null>(null);

  // along_path creation scope: "whole" = entire path; "spot" = a few parcels near the click.
  // In "spot", the clicked 3D point (pathAnchor) anchors parcel creation.
  const [alongPathScope, setAlongPathScope] = useState<"whole" | "spot">("whole");
  const [alongPathCount, setAlongPathCount] = useState(1);
  const [pathAnchor, setPathAnchor] = useState<number[] | null>(null);

  const [previewData, setPreviewData] = useState<ParcelPreviewItem[] | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [statusMessage, setStatusMessage] = useState<string | null>(null);

  const baseUrl = apiBaseUrl.replace(/\/+$/, "");

  const selectedModeRef = useRef(selectedMode);
  selectedModeRef.current = selectedMode;

  /* ---- Reset selection/mode ---- */

  /**
 * Discard only the 3D selection (point/path/parcel) and preview.
 * Keep width/depth and other inputs, since consecutive creation usually reuses dimensions.
 */
  const clearSelectionState = useCallback(() => {
    setPickedPosition(null);
    setSelectedPathId(null);
    setSelectedParcelId(null);
    setPathAnchor(null);
    setPreviewData(null);
    setAngle(0);
  }, []);

  /**
 * Apply mode changes to panel state without sending IPC.
 * Since width/height meaning and scale vary by mode, restore mode defaults only when the mode
 * changes; reselecting within the same mode retains values. Clear presets too, because modes
 * target different fields.
 */
  const applyModeSelection = useCallback(
    (mode: CreationMode) => {
      const changed = selectedModeRef.current !== mode;
      selectedModeRef.current = mode;
      setSelectedMode(mode);
      setStatusMessage(null);
      clearSelectionState();
      if (changed) {
        const preset = SIZE_INPUT_PRESETS[mode];
        if (preset) {
          setWidth(preset.width);
          setHeight(preset.height);
        }
        setSelectedPreset(null);
      }
    },
    [clearSelectionState]
  );

  /* ---- Enter creation mode on opening ---- */

  /**
 * Enter STATE_EDIT_PARCEL as soon as the window opens.
 * The 3D Ctrl radial menu for mode switching opens only during creation mode;
 * waiting for a panel tile click would leave the radial unavailable.
 */
  useEffect(() => {
    sendChangeState(
      "STATE_EDIT_PARCEL",
      { parcel_creation_mode: DEFAULT_CREATION_MODE },
      "parcel_creation"
    ).catch((err) => {
      console.error("[ParcelCreation] sendChangeState failed:", err);
    });
  }, []);

  /* ---- Hint bar display ---- */

  useEffect(() => {
    let cancelled = false;
    (async () => {
      await setHintbarText(t("hint.parcelCreation"));
    })().catch((err) => {
      if (!cancelled) {
        console.error("[ParcelCreation] setHintbarText failed:", err);
      }
    });
    return () => {
      cancelled = true;
      void setHintbarText("");
    };
  }, [t]);

  /* ---- Forward preview ---- */

  /**
 * Send preview shapes to Unity's 3D overlay.
 *
 * MMF limits message size, so too many parcels can cause sending to fail.
 * Always report failure: suppressing it would leave a stale 3D preview with confirmation enabled.
 */
  const pushPreviewToUnity = useCallback(
    async (mode: CreationMode, polygons: number[][][], original: number[][] | null) => {
      try {
        await sendEditParcelUpdatePreview(mode, polygons, original);
      } catch (err) {
        console.error("[ParcelCreation] sendEditParcelUpdatePreview failed:", err);
        showNotification({ message: t("parcel_creation.previewSendFailed"), durationMs: 5000 });
      }
    },
    [t]
  );

  /* ---- Fetch preview ---- */

  const fetchPreview = useCallback(
    async (signal?: AbortSignal) => {
      let payload: Record<string, unknown>;
      if (selectedMode === "empty_area") {
        if (!pickedPosition) return;
        payload = {
          mode: "empty_area",
          picked_position: pickedPosition,
          width,
          height,
          angle,
          dry_run: true,
        };
      } else if (selectedMode === "along_path") {
        if (selectedPathId == null) return;
        if (alongPathScope === "spot" && !pathAnchor) return;
        payload = {
          mode: "along_path",
          path_id: selectedPathId,
          side,
          width,
          height,
          dry_run: true,
        };
        if (alongPathScope === "spot") {
          payload.anchor = pathAnchor;
          payload.count = alongPathCount;
        }
      } else if (selectedMode === "random") {
        if (!pickedPosition) return;
        payload = {
          mode: selectedMode,
          picked_position: pickedPosition,
          width,
          height,
          angle,
          average_area: area,
          dry_run: true,
        };
        if (selectedMode === "random") {
          payload.jitter = jitterPct / 100;
        }
      } else if (selectedMode === "block") {
        if (!pickedPosition) return;
        payload = {
          mode: "block",
          picked_position: pickedPosition,
          width,
          height,
          angle,
          lot_width: lotWidth,
          lot_depth: lotDepth,
          jitter: jitterPct / 100,
          dry_run: true,
        };
      } else {
        if (selectedParcelId == null) return;
        payload = {
          mode: selectedMode,
          parcel_id: selectedParcelId,
          average_area: area,
          dry_run: true,
        };
        if (selectedMode === "split_random") {
          payload.jitter = jitterPct / 100;
        }
      }

      setPreviewLoading(true);
      try {
        const data = await fetchApi<Record<string, unknown>>(
          `${baseUrl}/api/v1/parcels/preview`,
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload),
            signal,
          }
        );
        if (!signal?.aborted) {
          const items = normalizePreviewResponse(data);
          setPreviewData(items);
          setPreviewLoading(false);

          const previewPolygons = items.map((item) => item.polygon);
          const originalPolygon =
            !isPointCreationMode(selectedMode) && selectedMode !== "along_path"
              ? (data.original_parcel as { polygon?: number[][] } | undefined)?.polygon ?? null
              : null;
          void pushPreviewToUnity(selectedMode, previewPolygons, originalPolygon);
        }
      } catch (err) {
        if (!signal?.aborted) {
          console.error("Failed to fetch parcel preview:", err);
          setPreviewData(null);
          setPreviewLoading(false);
          void sendEditParcelUpdatePreview(selectedMode, [], null);

          let message = "Failed to fetch parcel preview";
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
          showNotification({ message: displayMessage, durationMs: 5000 });
        }
      }
    },
    [selectedMode, pickedPosition, selectedPathId, selectedParcelId, pathAnchor, alongPathScope, alongPathCount, side, width, height, angle, area, lotWidth, lotDepth, jitterPct, baseUrl, pushPreviewToUnity, t]
  );

  /* ---- Fetch preview after receiving selection ---- */

  useEffect(() => {
    const isPointMode = isPointCreationMode(selectedMode);
    const isPathMode = selectedMode === "along_path";
    const isParcelMode = selectedMode === "split_rectangle" || selectedMode === "split_random";

    const hasSelection =
      (isPointMode && pickedPosition) ||
      (isPathMode && selectedPathId != null) ||
      (isParcelMode && selectedParcelId != null);

    if (!hasSelection) return;

    const controller = new AbortController();
    void fetchPreview(controller.signal);
    return () => controller.abort();
  }, [selectedMode, pickedPosition, selectedPathId, selectedParcelId, fetchPreview]);

  /* ---- Watch along_path parameter changes ---- */
  useEffect(() => {
    if (selectedMode !== "along_path" || selectedPathId == null) return;

    const controller = new AbortController();
    void fetchPreview(controller.signal);
    return () => controller.abort();
  }, [selectedMode, selectedPathId, pathAnchor, alongPathScope, alongPathCount, side, width, height, fetchPreview]);

  /* ---- Watch point-based parameter changes (empty_area / random / block) ---- */
  useEffect(() => {
    if (!isPointCreationMode(selectedMode) || pickedPosition == null) return;

    const controller = new AbortController();
    void fetchPreview(controller.signal);
    return () => controller.abort();
  }, [selectedMode, pickedPosition, width, height, angle, area, lotWidth, lotDepth, jitterPct, fetchPreview]);

  /* ---- IPC event listeners ---- */

  useEffect(() => {
    let unlistenPoint: (() => void) | undefined;
    let unlistenPath: (() => void) | undefined;
    let unlistenParcel: (() => void) | undefined;
    let unlistenRotation: (() => void) | undefined;
    let unlistenMode: (() => void) | undefined;

    // Notification that the 3D Ctrl radial menu changed creation mode (the authoritative state changed).
    // The same mode means Esc in 3D or reselecting the same mode: reset selection.
    appWindow
      .listen<{ mode: string }>(IPC_EVENT_EDIT_PARCEL_SET_MODE, (event) => {
        const mode = event.payload?.mode as CreationMode | undefined;
        if (!mode) return;
        applyModeSelection(mode);
      })
      .then((fn) => {
        unlistenMode = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ position: number[] }>(IPC_EVENT_EDIT_PARCEL_SELECT_POINT, (event) => {
        setPickedPosition(event.payload.position);
      })
      .then((fn) => {
        unlistenPoint = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ path_id: number; position?: number[] }>(IPC_EVENT_EDIT_PARCEL_SELECT_PATH, (event) => {
        setSelectedPathId(event.payload.path_id);
        // Refetch the preview if another location on the same path is clicked and the position changes.
        // A fresh array reference each time makes this work as a dependency for parameter monitoring.
        setPathAnchor(event.payload.position ?? null);
      })
      .then((fn) => {
        unlistenPath = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ parcel_id: number }>(IPC_EVENT_EDIT_PARCEL_SELECT_PARCEL, (event) => {
        setSelectedParcelId(event.payload.parcel_id);
      })
      .then((fn) => {
        unlistenParcel = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ angle: number }>(IPC_EVENT_EDIT_PARCEL_UPDATE_ROTATION, (event) => {
        setAngle(event.payload.angle);
      })
      .then((fn) => {
        unlistenRotation = fn;
      })
      .catch(console.error);

    return () => {
      unlistenPoint?.();
      unlistenPath?.();
      unlistenParcel?.();
      unlistenRotation?.();
      unlistenMode?.();
    };
  }, [appWindow, applyModeSelection]);

  /* ---- Synchronize rectangle preview parameters ---- */

  /**
 * Point-based modes draw a locally calculated rectangle in 3D while dragging its angle after
 * point selection. Send dimensions to match panel inputs. The official backend preview
 * separately overwrites it through edit_parcel:update_preview.
 */
  useEffect(() => {
    if (!isPointCreationMode(selectedMode)) return;
    void sendEditParcelSetRectParams(width, height);
  }, [selectedMode, width, height]);

  /* ---- Cleanup when the panel closes ---- */

  useEffect(() => {
    return () => {
      void sendEditParcelUpdatePreview(selectedModeRef.current, [], null);
      void sendChangeState("STATE_NORMAL", undefined, "parcel_creation");
    };
  }, []);

  /* ---- Handlers ---- */

  /**
 * Mode tile click. Update panel state first, then request Unity switch via world:change_state.
 * UI-originated switches do not return through edit_parcel:set_mode, so selection would not
 * update without this. Only Unity-originated radial switches send edit_parcel:set_mode.
 * Clicking the selected tile again means reselecting: reset selection.
 */
  function handleModeClick(mode: CreationMode) {
    if (selectedMode === mode) {
      handleClear();
      return;
    }
    applyModeSelection(mode);
    sendChangeState("STATE_EDIT_PARCEL", { parcel_creation_mode: mode }, "parcel_creation").catch((err) => {
      console.error("[ParcelCreation] sendChangeState failed:", err);
    });
  }

  /**
 * Adjust preset dimensions by aspect ratio and write to the selected mode's fields.
 * Scale width and depth inversely by the square root of the ratio to preserve preset area.
 */
  function applyPreset(index: number, step: number) {
    const preset = SIZE_PRESETS[index];
    const scale = Math.sqrt(aspectRatioOf(step));
    const w = Math.max(1, Math.round(preset.width / scale));
    const d = Math.max(1, Math.round(preset.depth * scale));
    const writers: Record<PresetField, () => void> = {
      size: () => {
        setWidth(w);
        setHeight(d);
      },
      lot: () => {
        setLotWidth(w);
        setLotDepth(d);
      },
      area: () => setArea(w * d),
    };
    writers[PRESET_FIELD[selectedMode]]();
  }

  /** Clicking a preset restores aspect ratio 1:1 and uses the dimensions shown on its button. */
  function handlePresetClick(index: number) {
    setSelectedPreset(index);
    setAspectStep(0);
    applyPreset(index, 0);
  }

  function handleAspectChange(step: number) {
    if (selectedPreset === null) return;
    setAspectStep(step);
    applyPreset(selectedPreset, step);
  }

  /** Manual edit of preset-targeted fields. Clear selection if the field is a preset target in the current mode. */
  function presetFieldInput(field: PresetField, set: (value: number) => void) {
    return (value: number) => {
      set(value);
      if (PRESET_FIELD[selectedMode] === field) setSelectedPreset(null);
    };
  }

  /**
 * Reset selection. Send edit_parcel:reset_selection to discard 3D selection, preview,
 * and unfinished polygons together, allowing reselection in the same mode.
 */
  function handleClear() {
    clearSelectionState();
    setStatusMessage(null);

    void sendEditParcelUpdatePreview(selectedMode, [], null);
    void sendEditParcelResetSelection();
  }

  async function handleConfirm() {
    let payload: Record<string, unknown>;
    if (selectedMode === "empty_area") {
      if (!pickedPosition) return;
      payload = {
        mode: "empty_area",
        picked_position: pickedPosition,
        width,
        height,
        angle,
      };
    } else if (selectedMode === "along_path") {
      if (selectedPathId == null) return;
      if (alongPathScope === "spot" && !pathAnchor) return;
      payload = {
        mode: "along_path",
        path_id: selectedPathId,
        side,
        width,
        height,
      };
      if (alongPathScope === "spot") {
        payload.anchor = pathAnchor;
        payload.count = alongPathCount;
      }
    } else if (selectedMode === "random") {
      if (!pickedPosition) return;
      payload = {
        mode: selectedMode,
        picked_position: pickedPosition,
        width,
        height,
        angle,
        average_area: area,
      };
      if (selectedMode === "random") {
        payload.jitter = jitterPct / 100;
      }
    } else if (selectedMode === "block") {
      if (!pickedPosition) return;
      payload = {
        mode: "block",
        picked_position: pickedPosition,
        width,
        height,
        angle,
        lot_width: lotWidth,
        lot_depth: lotDepth,
        jitter: jitterPct / 100,
      };
    } else {
      if (selectedParcelId == null) return;
      payload = {
        mode: selectedMode,
        parcel_id: selectedParcelId,
        average_area: area,
      };
      if (selectedMode === "split_random") {
        payload.jitter = jitterPct / 100;
      }
    }

    const diagnosticStarted = performance.now();
    const diagnosticContext = {
      attempt: Date.now(),
      url: `${baseUrl}/api/v1/parcels`,
      origin: window.location.origin,
      payload,
      previewCount: previewData?.length ?? null,
      previewLoading,
    };
    if (import.meta.env.DEV) console.error("[ParcelDiagnostic] confirm begin", diagnosticContext);
    setStatusMessage(null);
    try {
      const result = await fetchApi<Record<string, unknown>>(`${baseUrl}/api/v1/parcels`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (import.meta.env.DEV) {
        console.error("[ParcelDiagnostic] confirm accepted", {
          attempt: diagnosticContext.attempt,
          elapsedMs: Math.round(performance.now() - diagnosticStarted),
          result,
        });
      }
      setStatusMessage("success");

      // Confirmation does not leave creation mode. Discard only preview and selection;
      // the player can click in 3D immediately to create the next parcel (consecutive creation).
      // Retain inputs such as width/depth.
      void sendEditParcelUpdatePreview(selectedMode, [], null);
      void sendSessionReload();
      clearSelectionState();
      void sendEditParcelResetSelection();
    } catch (err) {
      if (import.meta.env.DEV) {
        console.error("[ParcelDiagnostic] confirm failed", {
          ...diagnosticContext,
          elapsedMs: Math.round(performance.now() - diagnosticStarted),
          errorName: err instanceof Error ? err.name : typeof err,
          errorMessage: err instanceof Error ? err.message : String(err),
          status: err instanceof ApiError ? err.status : null,
          code: err instanceof ApiError ? err.code : null,
        });
      }
      console.error("Failed to create parcels:", err);
      setStatusMessage("error");

      let message = "Failed to create parcels";
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
      showNotification({ message: displayMessage, durationMs: 5000 });
    }
  }

  /* ---- Determine selection state ---- */

  const isPointMode = isPointCreationMode(selectedMode);
  const isPathMode = selectedMode === "along_path";
  const isParcelMode = selectedMode === "split_rectangle" || selectedMode === "split_random";

  const hasSelection =
    (isPointMode && !!pickedPosition) ||
    (isPathMode && selectedPathId != null) ||
    (isParcelMode && selectedParcelId != null);

  const canConfirm = !previewLoading && !!previewData && previewData.length > 0;

  const sizeInput =
    (selectedMode && SIZE_INPUT_PRESETS[selectedMode]) || DEFAULT_SIZE_INPUT;

  // Roadside creation instructions. In click-point mode, where the player clicks affects the result.
  const alongPathSelectPromptKey =
    alongPathScope === "spot"
      ? "parcel_creation.selectPathSpot"
      : "parcel_creation.selectPath";

  /* ---- Rendering ---- */

  return (
    <div className="pc-layout">
      {/* Grouped variant selection (represents the same state as the 3D Ctrl radial menu). */}
      <div className="pc-sections-container">
        {CREATION_SECTIONS.map((section) => (
          <div key={section.titleKey} className="pc-section-group">
            <h3 className="pc-section-title">{t(section.titleKey)}</h3>
            <div className="pc-variant-grid">
              {section.modes.map((item) => (
                <div
                  key={item.mode}
                  className={`pc-variant-tile ${
                    selectedMode === item.mode ? "selected" : ""
                  }`}
                  data-guide="parcel.mode"
                  data-guide-value={item.mode}
                  onClick={() => handleModeClick(item.mode)}
                  title={t(item.tooltipKey)}
                >
                  <img
                    src={resolveParcelCreationIcon(item.icon)}
                    alt={t(item.tooltipKey)}
                    className="pc-variant-icon-img"
                    draggable={false}
                  />
                  <div className="pc-variant-name">{t(item.tooltipKey)}</div>
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>

      {/* Inputs and messages. */}
      {selectedMode && (
        <div className="pc-body">
          {/* Top: inputs. */}
          <div className="pc-input-section">
            <div className="pc-preset-list">
              {SIZE_PRESETS.map((preset, index) => (
                <button
                  key={preset.nameKey}
                  type="button"
                  className={`pc-preset-btn ${selectedPreset === index ? "selected" : ""}`}
                  onClick={() => handlePresetClick(index)}
                >
                  {t("parcel_creation.presetLabel", {
                    name: t(preset.nameKey),
                    width: preset.width,
                    depth: preset.depth,
                  })}
                </button>
              ))}
            </div>
            <div className="pc-input-row">
              <span className="pc-input-label">{t("parcel_creation.aspectRatio")}</span>
              <input
                type="range"
                className="pc-aspect-slider"
                min={-ASPECT_STEPS}
                max={ASPECT_STEPS}
                step={1}
                value={aspectStep}
                disabled={selectedPreset === null || PRESET_FIELD[selectedMode] !== "size"}
                onChange={(e) => handleAspectChange(Number(e.target.value))}
              />
              <span className="pc-input-unit">{formatAspect(aspectStep)}</span>
            </div>

            {(selectedMode === "empty_area" || selectedMode === "random") && (
              <>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.width")}</span>
                  <NumberStepper
                    value={width}
                    onChange={presetFieldInput("size", setWidth)}
                    min={sizeInput.min}
                    max={sizeInput.max}
                    step={sizeInput.step}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.height")}</span>
                  <NumberStepper
                    value={height}
                    onChange={presetFieldInput("size", setHeight)}
                    min={sizeInput.min}
                    max={sizeInput.max}
                    step={sizeInput.step}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.angle")}</span>
                  <NumberStepper
                    value={angle}
                    onChange={setAngle}
                    min={0}
                    max={359}
                    step={5}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitDegree")}</span>
                </div>
              </>
            )}

            {selectedMode === "block" && (
              <>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.blockSearchWidth")}</span>
                  <NumberStepper
                    value={width}
                    onChange={setWidth}
                    min={sizeInput.min}
                    max={sizeInput.max}
                    step={sizeInput.step}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.blockSearchHeight")}</span>
                  <NumberStepper
                    value={height}
                    onChange={setHeight}
                    min={sizeInput.min}
                    max={sizeInput.max}
                    step={sizeInput.step}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.lotWidth")}</span>
                  <NumberStepper
                    value={lotWidth}
                    onChange={presetFieldInput("lot", setLotWidth)}
                    min={4}
                    max={150}
                    step={1}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.lotDepth")}</span>
                  <NumberStepper
                    value={lotDepth}
                    onChange={presetFieldInput("lot", setLotDepth)}
                    min={5}
                    max={150}
                    step={1}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
              </>
            )}

            {selectedMode === "along_path" && (
              <>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.alongPathWidth")}</span>
                  <NumberStepper
                    value={width}
                    onChange={presetFieldInput("size", setWidth)}
                    min={sizeInput.min}
                    max={sizeInput.max}
                    step={sizeInput.step}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.alongPathDepth")}</span>
                  <NumberStepper
                    value={height}
                    onChange={presetFieldInput("size", setHeight)}
                    min={sizeInput.min}
                    max={sizeInput.max}
                    step={sizeInput.step}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.side")}</span>
                  <div className="pc-radio-group">
                    <label className="pc-radio-label">
                      <input
                        type="radio"
                        name="side"
                        checked={side === "left"}
                        onChange={() => setSide("left")}
                      />
                      {t("parcel_creation.sideLeft")}
                    </label>
                    <label className="pc-radio-label">
                      <input
                        type="radio"
                        name="side"
                        checked={side === "right"}
                        onChange={() => setSide("right")}
                      />
                      {t("parcel_creation.sideRight")}
                    </label>
                  </div>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.alongPathScope")}</span>
                  <div className="pc-radio-group">
                    <label className="pc-radio-label">
                      <input
                        type="radio"
                        name="alongPathScope"
                        checked={alongPathScope === "whole"}
                        onChange={() => setAlongPathScope("whole")}
                      />
                      {t("parcel_creation.alongPathScopeWhole")}
                    </label>
                    <label className="pc-radio-label">
                      <input
                        type="radio"
                        name="alongPathScope"
                        checked={alongPathScope === "spot"}
                        onChange={() => setAlongPathScope("spot")}
                      />
                      {t("parcel_creation.alongPathScopeSpot")}
                    </label>
                  </div>
                </div>
                {alongPathScope === "spot" && (
                  <div className="pc-input-row">
                    <span className="pc-input-label">{t("parcel_creation.alongPathCount")}</span>
                    <NumberStepper
                      value={alongPathCount}
                      onChange={setAlongPathCount}
                      min={1}
                      max={ALONG_PATH_MAX_COUNT}
                      step={1}
                      integerOnly={true}
                    />
                    <span className="pc-input-unit">{t("parcel_creation.unitCount")}</span>
                  </div>
                )}
              </>
            )}

            {(selectedMode === "random" || selectedMode === "split_rectangle" || selectedMode === "split_random") && (
              <div className="pc-input-row">
                <span className="pc-input-label">{t("parcel_creation.area")}</span>
                <NumberStepper
                  value={area}
                  onChange={presetFieldInput("area", setArea)}
                  min={10}
                  max={30000}
                  step={10}
                  integerOnly={false}
                />
                <span className="pc-input-unit">{t("parcel_creation.unitSqm")}</span>
              </div>
            )}

            {(selectedMode === "random" || selectedMode === "block" || selectedMode === "split_random") && (
              <div className="pc-input-row">
                <span className="pc-input-label">{t("parcel_creation.jitter")}</span>
                <NumberStepper
                  value={jitterPct}
                  onChange={setJitterPct}
                  min={0}
                  max={50}
                  step={5}
                  integerOnly={true}
                />
                <span className="pc-input-unit">{t("parcel_creation.unitPercent")}</span>
              </div>
            )}

            {/* Selection state. */}
            {isPointMode && (
              <div className={`pc-selection-message ${pickedPosition ? "active" : "waiting"}`}>
                {pickedPosition
                  ? t("parcel_creation.pointSelected")
                  : t("parcel_creation.selectPoint")}
              </div>
            )}
            {selectedMode === "along_path" && (
              <div className={`pc-selection-message ${selectedPathId != null ? "active" : "waiting"}`}>
                {selectedPathId != null
                  ? t("parcel_creation.pathSelected", { id: selectedPathId })
                  : t(alongPathSelectPromptKey)}
              </div>
            )}
            {(selectedMode === "split_rectangle" || selectedMode === "split_random") && (
              <div className={`pc-selection-message ${selectedParcelId != null ? "active" : "waiting"}`}>
                {selectedParcelId != null
                  ? t("parcel_creation.parcelSelected", { id: selectedParcelId })
                  : t("parcel_creation.selectParcel")}
              </div>
            )}
          </div>

          {/* Bottom: preview retrieval status. Shapes themselves appear in 3D. */}
          {previewLoading ? (
            <div className="pc-preview-message">
              {t("parcel_creation.previewLoading")}
            </div>
          ) : previewData?.length === 0 ? (
            <div className="pc-preview-message">{t("parcel_creation.noParcels")}</div>
          ) : (
            !previewData && (
              <div className="pc-preview-message">
                {isPointMode && t("parcel_creation.selectPoint")}
                {selectedMode === "along_path" && t(alongPathSelectPromptKey)}
                {(selectedMode === "split_rectangle" || selectedMode === "split_random") && t("parcel_creation.selectParcel")}
              </div>
            )
          )}
        </div>
      )}

      {/* Action buttons */}
      {hasSelection && (
        <div className="pc-actions">
          <button className="pc-btn" type="button" data-guide="parcel.clear" onClick={handleClear}>
            {t("parcel_creation.clear")}
          </button>
          <button
            className="pc-btn pc-btn-confirm"
            disabled={!canConfirm}
            type="button"
            data-guide="parcel.confirm"
            onClick={() => void handleConfirm()}
          >
            {t("parcel_creation.confirm")}
          </button>
        </div>
      )}

      {/* Status messages. */}
      {statusMessage && (
        <div className={`pc-status ${statusMessage}`}>
          {statusMessage === "success"
            ? t("parcel_creation.createSuccess")
            : t("parcel_creation.createFailed")}
        </div>
      )}

    </div>
  );
}

/* ---- Utilities ---- */

function normalizePreviewResponse(data: Record<string, unknown>): ParcelPreviewItem[] {
  const preview = data.preview;
  if (Array.isArray(preview)) {
    return preview.map((item: Record<string, unknown>) => ({
      polygon: item.polygon as number[][],
      center_point: item.center_point as number[],
      area: item.area as number,
    }));
  }
  if (preview && typeof preview === "object") {
    const p = preview as Record<string, unknown>;
    return [
      {
        polygon: p.polygon as number[][],
        center_point: p.center_point as number[],
        area: p.area as number,
      },
    ];
  }
  return [];
}
