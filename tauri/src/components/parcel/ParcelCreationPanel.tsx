/**
 * 土地区画作成パネル
 *
 * window_type="parcel_creation" のポップアップウィンドウで表示する専用パネル。
 * 作成・分割モードを選択し、パラメータ入力 → Unity上で地点/パス/区画選択 →
 * プレビュー → 確定/クリア のフローを行う。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";
import { setHintbarText } from "../../utils/hintbar";
import {
  sendChangeState,
  sendChangeDisplay,
  sendEditParcelUpdatePreview,
  sendSessionReload,
  IPC_EVENT_EDIT_PARCEL_SELECT_POINT,
  IPC_EVENT_EDIT_PARCEL_SELECT_PATH,
  IPC_EVENT_EDIT_PARCEL_SELECT_PARCEL,
  IPC_EVENT_EDIT_PARCEL_UPDATE_ROTATION,
  IPC_EVENT_EDIT_PARCEL_UPDATE_CREATION_SHAPE,
} from "../../utils/ipc";
import type { ParcelPreviewItem } from "./ParcelPreviewCanvas";
import { ParcelPreviewCanvas } from "./ParcelPreviewCanvas";
import { NumberStepper } from "../common/NumberStepper";
import { showNotification } from "../../utils/notification";
import { fetchApi, ApiError } from "../../utils/api";
import "./ParcelCreationPanel.css";

type CreationMode =
  | "empty_area"
  | "along_path"
  | "tile"
  | "random"
  | "block"
  | "manual"
  | "split_rectangle"
  | "split_random";

/** 地点選択で空き領域を探索するモード（Unity側は PARCEL_CREATION_POINT プリセット） */
const POINT_MODES: CreationMode[] = ["empty_area", "tile", "random", "block"];

function isPointCreationMode(mode: CreationMode | null): boolean {
  return mode !== null && POINT_MODES.includes(mode);
}

const CREATION_SECTIONS = [
  {
    titleKey: "parcel_creation.sectionCreate",
    modes: [
      {
        mode: "empty_area" as CreationMode,
        icon: "creation_1.png",
        tooltipKey: "parcel_creation.modeSingle",
      },
      {
        mode: "along_path" as CreationMode,
        icon: "creation_2.png",
        tooltipKey: "parcel_creation.modeAlongPath",
      },
      {
        mode: "tile" as CreationMode,
        icon: "creation_3.png",
        tooltipKey: "parcel_creation.modeTile",
      },
      {
        mode: "random" as CreationMode,
        icon: "creation_4.png",
        tooltipKey: "parcel_creation.modeRandom",
      },
      {
        // ブロック一括: 道路に囲まれたブロックを選択し、周縁の沿道区画+中央埋めを
        // 一括生成する。アイコンは暫定で流用中。
        mode: "block" as CreationMode,
        icon: "creation_2.png",
        tooltipKey: "parcel_creation.modeBlock",
      },
      {
        // 任意ポリゴン作成。頂点を打ってポリゴンを描く。アイコンは暫定で流用中。
        mode: "manual" as CreationMode,
        icon: "creation_1.png",
        tooltipKey: "parcel_creation.modeManual",
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

  const [selectedMode, setSelectedMode] = useState<CreationMode | null>(null);
  const [area, setArea] = useState(100);
  const [width, setWidth] = useState(10);
  const [height, setHeight] = useState(20);
  const [angle, setAngle] = useState(0);
  const [side, setSide] = useState<"left" | "right">("left");

  // block(ブロック一括)モード用: 沿道区画の幅・奥行き
  const [lotWidth, setLotWidth] = useState(10);
  const [lotDepth, setLotDepth] = useState(20);
  // random / split_random / block 共通: 区画幅のゆらぎ(%)
  const [jitterPct, setJitterPct] = useState(25);

  const [pickedPosition, setPickedPosition] = useState<number[] | null>(null);
  const [selectedPathId, setSelectedPathId] = useState<number | null>(null);
  const [selectedParcelId, setSelectedParcelId] = useState<number | null>(null);

  // manual(任意ポリゴン)モード: Unityで編集中のポリゴン頂点列([x, z])と仮確定フラグ。
  const [manualPolygon, setManualPolygon] = useState<number[][]>([]);
  const [manualFinalized, setManualFinalized] = useState(false);

  const [previewData, setPreviewData] = useState<ParcelPreviewItem[] | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [statusMessage, setStatusMessage] = useState<string | null>(null);

  const baseUrl = apiBaseUrl.replace(/\/+$/, "");

  const selectedModeRef = useRef(selectedMode);
  selectedModeRef.current = selectedMode;

  /* ---- ヒントバー表示 ---- */

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

  /* ---- プレビュー取得 ---- */

  const fetchPreview = useCallback(
    async (signal?: AbortSignal) => {
      if (!selectedMode) return;
      // manual(任意ポリゴン)はバックエンドのプレビュー取得を使わず、Unity上のオーバーレイと
      // パネル内キャンバスで直接プレビューするため、ここでは何もしない。
      if (selectedMode === "manual") return;

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
        payload = {
          mode: "along_path",
          path_id: selectedPathId,
          side,
          width,
          height,
          dry_run: true,
        };
      } else if (selectedMode === "tile" || selectedMode === "random") {
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
          average_area: area,
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
          void sendEditParcelUpdatePreview(selectedMode, previewPolygons, originalPolygon);
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
    [selectedMode, pickedPosition, selectedPathId, selectedParcelId, side, width, height, angle, area, lotWidth, lotDepth, jitterPct, baseUrl]
  );

  /* ---- 選択結果が届いたらプレビューを取得 ---- */

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

  /* ---- along_path のパラメータ変更監視 ---- */
  useEffect(() => {
    if (selectedMode !== "along_path" || selectedPathId == null) return;

    const controller = new AbortController();
    void fetchPreview(controller.signal);
    return () => controller.abort();
  }, [selectedMode, selectedPathId, side, width, height, fetchPreview]);

  /* ---- 地点指定系のパラメータ変更監視 (empty_area / tile / random / block) ---- */
  useEffect(() => {
    if (!isPointCreationMode(selectedMode) || pickedPosition == null) return;

    const controller = new AbortController();
    void fetchPreview(controller.signal);
    return () => controller.abort();
  }, [selectedMode, pickedPosition, width, height, angle, area, lotWidth, lotDepth, jitterPct, fetchPreview]);

  /* ---- IPC イベントリスナー ---- */

  useEffect(() => {
    if (!selectedMode) return;

    let unlistenPoint: (() => void) | undefined;
    let unlistenPath: (() => void) | undefined;
    let unlistenParcel: (() => void) | undefined;
    let unlistenRotation: (() => void) | undefined;
    let unlistenShape: (() => void) | undefined;

    appWindow
      .listen<{ position: number[] }>(IPC_EVENT_EDIT_PARCEL_SELECT_POINT, (event) => {
        setPickedPosition(event.payload.position);
      })
      .then((fn) => {
        unlistenPoint = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ path_id: number }>(IPC_EVENT_EDIT_PARCEL_SELECT_PATH, (event) => {
        setSelectedPathId(event.payload.path_id);
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

    // manual(任意ポリゴン)モードで、Unity上で編集中のポリゴン頂点列を受信する。
    appWindow
      .listen<{ polygon: number[][]; finalized?: boolean }>(IPC_EVENT_EDIT_PARCEL_UPDATE_CREATION_SHAPE, (event) => {
        setManualPolygon(event.payload?.polygon ?? []);
        setManualFinalized(event.payload?.finalized ?? false);
      })
      .then((fn) => {
        unlistenShape = fn;
      })
      .catch(console.error);

    return () => {
      unlistenPoint?.();
      unlistenPath?.();
      unlistenParcel?.();
      unlistenRotation?.();
      unlistenShape?.();
    };
  }, [appWindow, selectedMode]);

  /* ---- モード選択解除時のリセット ---- */

  useEffect(() => {
    if (selectedMode === null) {
      setPickedPosition(null);
      setSelectedPathId(null);
      setSelectedParcelId(null);
      setPreviewData(null);
      setStatusMessage(null);
      setAngle(0);
      setManualPolygon([]);
      setManualFinalized(false);
    }
  }, [selectedMode]);

  /* ---- パネル閉じた時のクリーンアップ ---- */

  useEffect(() => {
    return () => {
      const mode = selectedModeRef.current ?? "empty_area";
      void sendEditParcelUpdatePreview(mode, [], null);

      if (selectedModeRef.current !== null) {
        void sendChangeState("STATE_NORMAL", undefined, "parcel_creation");
      }
      void sendChangeDisplay("parcel", "hidden");
    };
  }, []);

  /* ---- ハンドラ ---- */

  function handleModeClick(mode: CreationMode) {
    if (selectedMode === mode) {
      setSelectedMode(null);
      sendChangeState("STATE_NORMAL", undefined, "parcel_creation").catch((err) => {
        console.error("[ParcelCreation] sendChangeState failed:", err);
      });
      sendChangeDisplay("parcel", "hidden").catch((err) => {
        console.error("[ParcelCreation] sendChangeDisplay failed:", err);
      });
      void sendEditParcelUpdatePreview(mode, [], null);
    } else {
      setSelectedMode(mode);
      setPreviewData(null);
      setPickedPosition(null);
      setSelectedPathId(null);
      setSelectedParcelId(null);
      setStatusMessage(null);
      setAngle(0);
      setManualPolygon([]);
      setManualFinalized(false);
      // 探索矩形サイズのモード別既定値。block はブロック全体が探索矩形に
      // 収まる必要があるため、他の地点指定系より大きめにする。
      if (mode === "block") {
        setWidth(150);
        setHeight(150);
      } else if (mode === "empty_area" || mode === "tile" || mode === "random") {
        setWidth(10);
        setHeight(20);
      }
      sendChangeState("STATE_EDIT_PARCEL", { parcel_creation_mode: mode }, "parcel_creation").catch((err) => {
        console.error("[ParcelCreation] sendChangeState failed:", err);
      });
      sendChangeDisplay("parcel", "border").catch((err) => {
        console.error("[ParcelCreation] sendChangeDisplay failed:", err);
      });
    }
  }

  function handleClear() {
    setPickedPosition(null);
    setSelectedPathId(null);
    setSelectedParcelId(null);
    setPreviewData(null);
    setStatusMessage(null);
    setAngle(0);

    if (selectedMode === "manual") {
      // 任意ポリゴンはUnity側(ParcelAreaEditManager)が頂点を保持しているため、
      // 編集モードへの再遷移で InitializeCreateMode を呼び直してポリゴンをリセットする。
      setManualPolygon([]);
      setManualFinalized(false);
      sendChangeState("STATE_EDIT_PARCEL", { parcel_creation_mode: "manual" }, "parcel_creation").catch((err) => {
        console.error("[ParcelCreation] sendChangeState failed:", err);
      });
      return;
    }

    if (selectedMode) {
      void sendEditParcelUpdatePreview(selectedMode, [], null);
    }
  }

  async function handleConfirm() {
    if (!selectedMode) return;

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
      payload = {
        mode: "along_path",
        path_id: selectedPathId,
        side,
        width,
        height,
      };
    } else if (selectedMode === "tile" || selectedMode === "random") {
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
        average_area: area,
        jitter: jitterPct / 100,
      };
    } else if (selectedMode === "manual") {
      if (manualPolygon.length < 3) return;
      payload = {
        mode: "manual",
        polygon: manualPolygon,
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

    setStatusMessage(null);
    try {
      await fetchApi<Record<string, unknown>>(`${baseUrl}/api/v1/parcels`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      setStatusMessage("success");

      void sendEditParcelUpdatePreview(selectedMode, [], null);
      void sendSessionReload();
      setSelectedMode(null);

      await sendChangeState("STATE_NORMAL", undefined, "parcel_creation");
      await sendChangeDisplay("parcel", "border");
    } catch (err) {
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

  /* ---- 選択状態の判定 ---- */

  const isPointMode = isPointCreationMode(selectedMode);
  const isPathMode = selectedMode === "along_path";
  const isParcelMode = selectedMode === "split_rectangle" || selectedMode === "split_random";
  const isManualMode = selectedMode === "manual";

  const hasSelection =
    (isPointMode && !!pickedPosition) ||
    (isPathMode && selectedPathId != null) ||
    (isParcelMode && selectedParcelId != null) ||
    (isManualMode && manualPolygon.length > 0);

  // 確定可能条件: 通常モードはバックエンドのプレビュー取得済み、
  // manual(任意ポリゴン)は頂点3個以上かつ右クリックで仮確定済みであること。
  const canConfirm = isManualMode
    ? manualPolygon.length >= 3 && manualFinalized
    : !!previewData && previewData.length > 0;

  const originalPolygonForPreview = useMemo(() => {
    return null;
  }, []);

  /* ---- レンダリング ---- */

  return (
    <div className="pc-layout">
      {/* セクション分けされたバリアント選択 */}
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

      {/* 入力 + プレビュー */}
      {selectedMode && (
        <div className="pc-body">
          {/* 上: 入力欄 */}
          <div className="pc-input-section">
            {(selectedMode === "empty_area" || selectedMode === "tile" || selectedMode === "random") && (
              <>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.width")}</span>
                  <NumberStepper
                    value={width}
                    onChange={setWidth}
                    min={1}
                    max={200}
                    step={1}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.height")}</span>
                  <NumberStepper
                    value={height}
                    onChange={setHeight}
                    min={1}
                    max={200}
                    step={1}
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
                    min={20}
                    max={400}
                    step={10}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.blockSearchHeight")}</span>
                  <NumberStepper
                    value={height}
                    onChange={setHeight}
                    min={20}
                    max={400}
                    step={10}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.lotWidth")}</span>
                  <NumberStepper
                    value={lotWidth}
                    onChange={setLotWidth}
                    min={4}
                    max={50}
                    step={1}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.lotDepth")}</span>
                  <NumberStepper
                    value={lotDepth}
                    onChange={setLotDepth}
                    min={5}
                    max={100}
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
                    onChange={setWidth}
                    min={1}
                    max={100}
                    step={1}
                    integerOnly={false}
                  />
                  <span className="pc-input-unit">{t("parcel_creation.unitMeter")}</span>
                </div>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.alongPathDepth")}</span>
                  <NumberStepper
                    value={height}
                    onChange={setHeight}
                    min={1}
                    max={100}
                    step={1}
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
              </>
            )}

            {(selectedMode === "tile" || selectedMode === "random" || selectedMode === "block" || selectedMode === "split_rectangle" || selectedMode === "split_random") && (
              <div className="pc-input-row">
                <span className="pc-input-label">{t("parcel_creation.area")}</span>
                <NumberStepper
                  value={area}
                  onChange={setArea}
                  min={10}
                  max={10000}
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

            {/* 選択状態 */}
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
                  : t("parcel_creation.selectPath")}
              </div>
            )}
            {(selectedMode === "split_rectangle" || selectedMode === "split_random") && (
              <div className={`pc-selection-message ${selectedParcelId != null ? "active" : "waiting"}`}>
                {selectedParcelId != null
                  ? t("parcel_creation.parcelSelected", { id: selectedParcelId })
                  : t("parcel_creation.selectParcel")}
              </div>
            )}
            {isManualMode && (
              <div className={`pc-selection-message ${canConfirm ? "active" : "waiting"}`}>
                {manualPolygon.length === 0
                  ? t("parcel_creation.drawPolygon")
                  : !manualFinalized
                    ? t("parcel_creation.polygonPlacing", { count: manualPolygon.length })
                    : t("parcel_creation.polygonReady", { count: manualPolygon.length })}
              </div>
            )}
          </div>

          {/* 下: プレビュー */}
          <div className="pc-preview-section">
            {isManualMode ? (
              manualPolygon.length >= 3 ? (
                <ParcelPreviewCanvas
                  parcels={[{ polygon: manualPolygon, center_point: [0, 0], area: 0 }]}
                  originalPolygon={null}
                  size={280}
                />
              ) : (
                <div className="pc-preview-message">
                  {t("parcel_creation.drawPolygon")}
                </div>
              )
            ) : previewLoading ? (
              <div className="pc-preview-message">
                {t("parcel_creation.previewLoading")}
              </div>
            ) : previewData && previewData.length > 0 ? (
              <ParcelPreviewCanvas
                parcels={previewData}
                originalPolygon={originalPolygonForPreview}
                size={280}
              />
            ) : (
              <div className="pc-preview-message">
                {isPointMode && t("parcel_creation.selectPoint")}
                {selectedMode === "along_path" && t("parcel_creation.selectPath")}
                {(selectedMode === "split_rectangle" || selectedMode === "split_random") && t("parcel_creation.selectParcel")}
              </div>
            )}
          </div>
        </div>
      )}

      {/* アクションボタン */}
      {hasSelection && (
        <div className="pc-actions">
          <button className="pc-btn" type="button" onClick={handleClear}>
            {t("parcel_creation.clear")}
          </button>
          <button
            className="pc-btn pc-btn-confirm"
            disabled={!canConfirm}
            type="button"
            onClick={() => void handleConfirm()}
          >
            {t("parcel_creation.confirm")}
          </button>
        </div>
      )}

      {/* ステータスメッセージ */}
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

/* ---- ユーティリティ ---- */

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
