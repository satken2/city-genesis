/**
 * 土地区画作成パネル
 *
 * window_type="parcel_creation" のポップアップウィンドウで表示する専用パネル。
 *
 * 操作フローは「3Dビュー主導・パネルは数値と確定」:
 *   ウィンドウを開くと即座に土地区画作成モード(STATE_EDIT_PARCEL)へ入り、
 *   **作成モードの切り替えはUnity側のCtrlラジアルメニュー**が行う(道路敷設・地区編集と同型)。
 *   パネルは幅・奥行き・平均面積などの数値入力とプレビュー、確定ボタンを担当する。
 *   確定してもモードから抜けず、選択だけリセットして連続作成できる。
 *
 * 作成モードの真実源はUnity側(`ParcelEditorManager.CurrentMode`)にあるため、パネルの
 * モードタイルは「もう一つの入口」であり、押すと `world:change_state` でUnityへ依頼し、
 * 実際の切り替え結果は `edit_parcel:set_mode` で返ってくる(逆流させないこと)。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";
import { setHintbarText } from "../../utils/hintbar";
import {
  sendChangeState,
  sendChangeDisplay,
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
import type { ParcelPreviewItem } from "./ParcelPreviewCanvas";
import { ParcelPreviewCanvas } from "./ParcelPreviewCanvas";
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

/** 地点選択で空き領域を探索するモード（Unity側は PARCEL_CREATION_POINT プリセット） */
const POINT_MODES: CreationMode[] = ["empty_area", "random", "block"];

function isPointCreationMode(mode: CreationMode | null): boolean {
  return mode !== null && POINT_MODES.includes(mode);
}

/**
 * width / height 入力のモード別の初期値と入力レンジ。
 *
 * 同じ2つの入力欄でも意味がモードによって違う。empty_area と along_path は
 * 区画そのものの寸法だが、random / block は周囲から複数区画を生成する
 * 「探索範囲」なので桁が1つ違う。モード切替時に必ず初期値へ戻し、前のモードの値が
 * レンジ外のまま残らないようにする。
 */
const SIZE_INPUT_PRESETS: Record<string, { width: number; height: number; min: number; max: number; step: number }> = {
  empty_area: { width: 10, height: 20, min: 1, max: 200, step: 1 },
  along_path: { width: 10, height: 20, min: 1, max: 100, step: 1 },
  random: { width: 200, height: 200, min: 1, max: 400, step: 10 },
  block: { width: 200, height: 200, min: 20, max: 400, step: 10 },
};

/** ウィンドウを開いた直後の作成モード（Unity側 ParcelEditorManager.DefaultMode と一致させる） */
const DEFAULT_CREATION_MODE: CreationMode = "along_path";

const DEFAULT_SIZE_INPUT = SIZE_INPUT_PRESETS[DEFAULT_CREATION_MODE];

/**
 * 道路沿い作成で、クリック地点まわりに1回で作れる区画数の上限。
 * バックエンド側でも `geo_parcels.PATH_PARCEL_SPOT_MAX_COUNT` でクランプされる。
 */
const ALONG_PATH_MAX_COUNT = 5;

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

  // 作成モードは常にどれか1つが選ばれている(未選択状態を持たない)。真実源はUnity側。
  const [selectedMode, setSelectedMode] = useState<CreationMode>(DEFAULT_CREATION_MODE);
  const [area, setArea] = useState(100);
  const [width, setWidth] = useState(DEFAULT_SIZE_INPUT.width);
  const [height, setHeight] = useState(DEFAULT_SIZE_INPUT.height);
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

  // along_path(道路沿い)の作成範囲。"whole"=パス全体、"spot"=クリック地点まわりの数区画。
  // "spot" では 3Dビューでクリックした地点(pathAnchor)が作成位置の基準になる。
  const [alongPathScope, setAlongPathScope] = useState<"whole" | "spot">("whole");
  const [alongPathCount, setAlongPathCount] = useState(1);
  const [pathAnchor, setPathAnchor] = useState<number[] | null>(null);

  const [previewData, setPreviewData] = useState<ParcelPreviewItem[] | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [statusMessage, setStatusMessage] = useState<string | null>(null);

  const baseUrl = apiBaseUrl.replace(/\/+$/, "");

  const selectedModeRef = useRef(selectedMode);
  selectedModeRef.current = selectedMode;

  /* ---- 選択・モードのリセット ---- */

  /**
   * 3Dビューでの選択（地点/パス/区画）とプレビューだけを捨てる。
   * 幅・奥行きなどの入力値は保持する（連続作成では同じ寸法で作り続けるのが普通のため）。
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
   * 作成モードの切り替えをパネルの状態へ反映する（IPCは送らない）。
   * モードによって width/height の意味と桁が変わるため、モードが変わったときだけ
   * 入力値をそのモードの初期値へ戻す（同じモードでの「選び直し」では保持する）。
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
      }
    },
    [clearSelectionState]
  );

  /* ---- 起動時に作成モードへ入る ---- */

  /**
   * ウィンドウを開いた時点で STATE_EDIT_PARCEL に入る。
   * 3Dビュー側のCtrlラジアルメニュー（作成モードの切り替え）は作成モード中しか開かないため、
   * パネルのタイルを押すまでモードに入らないと「ホイールが出ない」状態になってしまう。
   */
  useEffect(() => {
    sendChangeState(
      "STATE_EDIT_PARCEL",
      { parcel_creation_mode: DEFAULT_CREATION_MODE },
      "parcel_creation"
    ).catch((err) => {
      console.error("[ParcelCreation] sendChangeState failed:", err);
    });
    sendChangeDisplay("parcel", "border").catch((err) => {
      console.error("[ParcelCreation] sendChangeDisplay failed:", err);
    });
  }, []);

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

  /* ---- プレビュー転送 ---- */

  /**
   * プレビュー形状をUnityの3Dオーバーレイへ送る。
   *
   * MMFは1メッセージあたりの上限を持つため、区画数が多すぎると送信が失敗する。
   * 握り潰すとパネル内の2Dプレビューだけが更新され「3Dだけ止まった」ように見えるので、
   * 失敗は必ず通知する。
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

  /* ---- プレビュー取得 ---- */

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
  }, [selectedMode, selectedPathId, pathAnchor, alongPathScope, alongPathCount, side, width, height, fetchPreview]);

  /* ---- 地点指定系のパラメータ変更監視 (empty_area / random / block) ---- */
  useEffect(() => {
    if (!isPointCreationMode(selectedMode) || pickedPosition == null) return;

    const controller = new AbortController();
    void fetchPreview(controller.signal);
    return () => controller.abort();
  }, [selectedMode, pickedPosition, width, height, angle, area, lotWidth, lotDepth, jitterPct, fetchPreview]);

  /* ---- IPC イベントリスナー ---- */

  useEffect(() => {
    let unlistenPoint: (() => void) | undefined;
    let unlistenPath: (() => void) | undefined;
    let unlistenParcel: (() => void) | undefined;
    let unlistenRotation: (() => void) | undefined;
    let unlistenMode: (() => void) | undefined;

    // 3DビューのCtrlラジアルメニューで作成モードが切り替わった（＝真実源が動いた）通知。
    // 同じモードで届いた場合は「3DビューでEscを押した／同じモードを選び直した」= 選択のリセット。
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
        // 同じパスの別の場所をクリックし直したときも位置が変わればプレビューを取り直す。
        // 配列は毎回新しい参照になるため、パラメータ監視の依存として機能する。
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

  /* ---- 矩形プレビューのパラメータ同期 ---- */

  /**
   * 地点指定系モードでは、地点を選んだあとの角度ドラッグ中に3Dビューがローカル計算で
   * 矩形プレビューを描く。その寸法をパネルの入力値と一致させるために送る
   * （バックエンド由来の正式なプレビューは `edit_parcel:update_preview` が別途上書きする）。
   */
  useEffect(() => {
    if (!isPointCreationMode(selectedMode)) return;
    void sendEditParcelSetRectParams(width, height);
  }, [selectedMode, width, height]);

  /* ---- パネル閉じた時のクリーンアップ ---- */

  useEffect(() => {
    return () => {
      void sendEditParcelUpdatePreview(selectedModeRef.current, [], null);
      void sendChangeState("STATE_NORMAL", undefined, "parcel_creation");
      void sendChangeDisplay("parcel", "hidden");
    };
  }, []);

  /* ---- ハンドラ ---- */

  /**
   * モードタイルのクリック。パネル側の状態を先に切り替えてから `world:change_state` で
   * Unityへ切り替えを依頼する（UI起点の切り替えは `edit_parcel:set_mode` で折り返らないため、
   * ここで反映しないとタイルの選択表示が動かない。逆にUnity起点＝ラジアルメニューでの
   * 切り替えだけが `edit_parcel:set_mode` で届く）。
   * 選択中のタイルをもう一度押した場合は「選び直し」= 選択のリセットになる。
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
   * 選択のリセット。3Dビュー側の選択・プレビュー・描きかけのポリゴンも
   * `edit_parcel:reset_selection` で同時に捨てて、同じモードのまま選び直せる状態に戻す。
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

    setStatusMessage(null);
    try {
      await fetchApi<Record<string, unknown>>(`${baseUrl}/api/v1/parcels`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      setStatusMessage("success");

      // 確定しても作成モードから抜けない。プレビューと選択だけ捨てて、
      // ユーザーはそのまま3Dビューをクリックして次の区画を作れる（連続作成）。
      // 幅・奥行きなどの入力値は保持する。
      void sendEditParcelUpdatePreview(selectedMode, [], null);
      void sendSessionReload();
      clearSelectionState();
      void sendEditParcelResetSelection();
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

  const hasSelection =
    (isPointMode && !!pickedPosition) ||
    (isPathMode && selectedPathId != null) ||
    (isParcelMode && selectedParcelId != null);

  const canConfirm = !previewLoading && !!previewData && previewData.length > 0;

  const originalPolygonForPreview = useMemo(() => {
    return null;
  }, []);

  const sizeInput =
    (selectedMode && SIZE_INPUT_PRESETS[selectedMode]) || DEFAULT_SIZE_INPUT;

  // 道路沿い作成の案内文。クリック地点モードでは「どこを」クリックするかが結果を左右する。
  const alongPathSelectPromptKey =
    alongPathScope === "spot"
      ? "parcel_creation.selectPathSpot"
      : "parcel_creation.selectPath";

  /* ---- レンダリング ---- */

  return (
    <div className="pc-layout">
      {/* セクション分けされたバリアント選択（3DビューのCtrlラジアルメニューと同じ状態を指す） */}
      <div className="pc-sections-container">
        <p className="pc-mode-hint">{t("parcel_creation.modeWheelHint")}</p>
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
            {(selectedMode === "empty_area" || selectedMode === "random") && (
              <>
                <div className="pc-input-row">
                  <span className="pc-input-label">{t("parcel_creation.width")}</span>
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
                  <span className="pc-input-label">{t("parcel_creation.height")}</span>
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
                    onChange={setHeight}
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

          {/* 下: プレビュー */}
          <div className="pc-preview-section">
            {previewLoading ? (
              <div className="pc-preview-message">
                {t("parcel_creation.previewLoading")}
              </div>
            ) : previewData && previewData.length > 0 ? (
              <ParcelPreviewCanvas
                parcels={previewData}
                originalPolygon={originalPolygonForPreview}
                size={280}
              />
            ) : previewData?.length === 0 ? (
              <div className="pc-preview-message">{t("parcel_creation.noParcels")}</div>
            ) : (
              <div className="pc-preview-message">
                {isPointMode && t("parcel_creation.selectPoint")}
                {selectedMode === "along_path" && t(alongPathSelectPromptKey)}
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
