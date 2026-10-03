/**
 * プロップ配置パネル
 *
 * window_type="prop_placement" の左ドックパネル。地形プロップ(森・岩・茂みなどの装飾)を
 * 3Dマップ上に配置する。ResourceConstructionPanel(範囲=ポリゴン描画)と
 * TransitCreationPanel(個別=点配置)のハイブリッド。
 *
 * 配置モードは2種類:
 *   - 個別(instance): 1クリック=1個。回転角・スケールを設定して配置する(PropInstance)。
 *   - 範囲(scatter): ポリゴン範囲を描き、密度・seed で散布する(PropScatter。個々の座標は
 *     グラフィック側が seed から生成する)。
 *
 * 操作フロー:
 *   1. モード(個別/範囲)を選ぶ
 *   2. カテゴリで絞り込み、プロップのタイルを選択 → IPC で Unity を配置モード(STATE_EDIT_PROP)にする
 *   3. 個別: 3Dビューをクリックして配置(配置済みは不透明、カーソル追従は半透明ゴースト)
 *      範囲: 3Dビューをクリックして頂点を置きポリゴンを描く(散布は半透明でプレビュー)
 *   4. 設定(回転/スケール/高さ、または密度/seed)を調整する(Unity のプレビューに即反映)。
 *      個別配置は3D側でも中ボタンドラッグで回転・PageUp/PageDown で高さを変えられ、その結果は
 *      edit_prop:placement_settings でステッパーへ書き戻される(パネルからはエコーしない)
 *   5. [確定] で UI が直接バックエンドに POST する(道路建設・資源編集と同じ責務分担)
 *      個別: POST /api/v1/props/instances、範囲: POST /api/v1/props/scatters
 *
 * バックエンドへの送信は Tauri(UI)側が直接行い、成功後に edit_prop:finish で Unity の
 * 配置状態をリセットする。既存プロップの削除はコックピットのブルドーザで行う(本パネルは配置のみ)。
 */
import { PLAY_MODE_GOD } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";


import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import { PropTypeSelector } from "./PropTypeSelector";
import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import {
  sendChangeState,
  sendEditPropChangeSettings,
  sendEditPropFinish,
  sendEditPropReset,
  IPC_EVENT_EDIT_PROP_UPDATE_INSTANCES,
  IPC_EVENT_EDIT_PROP_UPDATE_SHAPE,
  IPC_EVENT_EDIT_PROP_PLACEMENT_SETTINGS,
} from "../../utils/ipc";
// 基本レイアウト(pc-*)は資源編集パネルと共有する。
import "../resource/ResourceConstructionPanel.css";
import "./PropPlacementPanel.css";

type PropPlacementPanelProps = {
  apiBaseUrl?: string;
  onClose: () => void;
};

/** 配置モード。個別配置(PropInstance) / 範囲散布(PropScatter)。 */
type PlacementMode = "instance" | "scatter";

/** Unity から受け取る個別配置インスタンス。 */
type PropInstance = {
  x: number;
  y: number;
  z: number;
  rotation: number;
  scale: number;
};

const DEFAULT_ROTATION = 0;
const DEFAULT_SCALE = 1.0;
const DEFAULT_DENSITY = 400;
const DEFAULT_SEED = 12345;

export function PropPlacementPanel({ apiBaseUrl, onClose }: PropPlacementPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const godMode = usePlayMode(apiBaseUrl ?? "") === PLAY_MODE_GOD;
  const [mode, setMode] = useState<PlacementMode>("instance");
  const [selectedType, setSelectedType] = useState<number | null>(null);

  // 個別モードの配置設定。
  const [rotation, setRotation] = useState<number>(DEFAULT_ROTATION);
  const [scale, setScale] = useState<number>(DEFAULT_SCALE);
  const [height, setHeight] = useState<number>(0);

  // 範囲モードの散布設定。
  const [density, setDensity] = useState<number>(DEFAULT_DENSITY);
  const [seed, setSeed] = useState<number>(DEFAULT_SEED);

  // Unity 側で配置中のデータ。個別=インスタンス列、範囲=ポリゴン頂点列([x, z])。
  const [instances, setInstances] = useState<PropInstance[]>([]);
  const [polygon, setPolygon] = useState<number[][]>([]);
  const [isShapeFinalized, setIsShapeFinalized] = useState<boolean>(false);

  const [isConfirming, setIsConfirming] = useState(false);

  const propTypeEntries = useMemo(() => {
    if (!masterData) return [];
    return Object.entries(masterData.prop_types);
  }, [masterData]);

  /**
   * 現在のモードと設定で Unity を STATE_EDIT_PROP にする。
   * 種別未選択なら何もしない。モード/種別を切り替えたときは配置済みデータをリセットする。
   */
  const enterEditMode = useCallback(
    (nextMode: PlacementMode, typeId: number | null) => {
      if (typeId === null) return;
      void sendChangeState(
        "STATE_EDIT_PROP",
        {
          prop_type: typeId,
          placement_mode: nextMode,
          height,
          rotation,
          scale,
          density,
          seed,
        },
        "prop_placement",
      );
    },
    [rotation, scale, height, density, seed],
  );

  /* ---- Unity 側で配置中のデータとセッション状態を受信する ---- */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlistenInstances: (() => void) | undefined;
    let unlistenShape: (() => void) | undefined;
    let unlistenSettings: (() => void) | undefined;

    // 3D側の操作(中ボタン回転・PageUp/PageDown)で変わった設定をステッパーへ反映する。
    // Unity が権威なので change_settings でエコーしない(setter だけを呼ぶ)。
    appWindow
      .listen<{ rotation?: number; height?: number }>(IPC_EVENT_EDIT_PROP_PLACEMENT_SETTINGS, (event) => {
        const payload = event.payload ?? {};
        if (typeof payload.rotation === "number") setRotation(Math.round(payload.rotation));
        if (typeof payload.height === "number") setHeight(Math.round(payload.height * 100) / 100);
      })
      .then((fn) => {
        unlistenSettings = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ instances: PropInstance[] }>(IPC_EVENT_EDIT_PROP_UPDATE_INSTANCES, (event) => {
        setInstances(event.payload?.instances ?? []);
      })
      .then((fn) => {
        unlistenInstances = fn;
      })
      .catch(console.error);

    appWindow
      .listen<{ polygon: number[][]; finalized?: boolean }>(IPC_EVENT_EDIT_PROP_UPDATE_SHAPE, (event) => {
        setPolygon(event.payload?.polygon ?? []);
        setIsShapeFinalized(event.payload?.finalized ?? false);
      })
      .then((fn) => {
        unlistenShape = fn;
      })
      .catch(console.error);

    return () => {
      unlistenInstances?.();
      unlistenShape?.();
      unlistenSettings?.();
    };
  }, []);

  // テンプレートモードがオフのときは、強制的に個別(instance)モードにする。
  useEffect(() => {
    if (!godMode && mode === "scatter") {
      setMode("instance");
      setInstances([]);
      setPolygon([]);
      setIsShapeFinalized(false);
      enterEditMode("instance", selectedType);
    }
  }, [godMode, mode, selectedType, enterEditMode]);

  /* ---- ヒントバー表示。パネルが閉じられたらクリアする ---- */
  useEffect(() => {
    void setHintbarText(t("prop_placement.hint"));
    return () => {
      void setHintbarText("");
    };
  }, [t]);

  /* ---- アンマウント時に確実に Unity を STATE_NORMAL へ戻す ---- */
  useEffect(() => {
    return () => {
      void sendChangeState("STATE_NORMAL", {}, "prop_placement");
    };
  }, []);

  const handleModeChange = useCallback(
    (nextMode: PlacementMode) => {
      if (nextMode === mode) return;
      setMode(nextMode);
      setInstances([]);
      setPolygon([]);
      setIsShapeFinalized(false);
      enterEditMode(nextMode, selectedType);
    },
    [mode, selectedType, enterEditMode],
  );

  const handleTypeChange = useCallback(
    (typeId: number) => {
      if (typeId === selectedType || isConfirming) return;
      setSelectedType(typeId);
      setInstances([]);
      setPolygon([]);
      setIsShapeFinalized(false);
      enterEditMode(mode, typeId);
    },
    [mode, enterEditMode, selectedType, isConfirming],
  );

  /* ---- 数値設定変更を Unity のプレビューに即反映する ---- */
  const handleRotationChange = useCallback((v: number) => {
    setRotation(v);
    void sendEditPropChangeSettings({ rotation: v });
  }, []);

  const handleScaleChange = useCallback((v: number) => {
    setScale(v);
    void sendEditPropChangeSettings({ scale: v });
  }, []);

  const handleHeightChange = useCallback((v: number) => {
    setHeight(v);
    void sendEditPropChangeSettings({ height: v });
  }, []);

  const handleDensityChange = useCallback((v: number) => {
    setDensity(v);
    void sendEditPropChangeSettings({ density: v });
  }, []);

  const handleSeedChange = useCallback((v: number) => {
    setSeed(v);
    void sendEditPropChangeSettings({ seed: v });
  }, []);

  const handleCancel = useCallback(() => {
    setSelectedType(null);
    setInstances([]);
    setPolygon([]);
    setIsShapeFinalized(false);
    void sendChangeState("STATE_NORMAL", {}, "prop_placement");
  }, []);

  const handleReset = useCallback(async () => {
    setInstances([]);
    setPolygon([]);
    setIsShapeFinalized(false);
    await sendEditPropReset();
  }, []);

  const handleConfirm = useCallback(async () => {
    if (selectedType === null || !apiBaseUrl) return;

    setIsConfirming(true);
    try {
      if (mode === "instance") {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/props/instances"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            prop_type: selectedType,
            instances,
          }),
        });
      } else {
        await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/props/scatters"), {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            prop_type: selectedType,
            polygon,
            density,
            seed,
          }),
        });
      }
      await sendEditPropFinish();
      await sendChangeState("STATE_NORMAL", {}, "prop_placement");
      onClose();
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
        displayMessage = t("prop_placement.confirmFailedUnknown");
      }
      void showNotification({ message: displayMessage, durationMs: 8000 });
    }
  }, [mode, selectedType, instances, polygon, density, seed, apiBaseUrl, onClose, t]);

  if (!masterData) {
    return <p className="pc-muted">{t("common.loading")}</p>;
  }

  // 確定可能条件: 個別は1個以上、範囲はポリゴン頂点3個以上かつ仮確定済みであること。
  const canConfirm =
    selectedType !== null &&
    (mode === "instance" ? instances.length > 0 : (polygon.length >= 3 && isShapeFinalized));

  return (
    <div className="pc-layout">
      {/* プロップ種別選択 */}
      <div className="pc-section pp-type-section">
        {propTypeEntries.length === 0 ? (
          <div className="pc-no-nodes-message">{t("prop_placement.noVariants")}</div>
        ) : (
          <PropTypeSelector entries={propTypeEntries} selectedType={selectedType}
            onSelect={handleTypeChange} disabled={isConfirming} />
        )}
      </div>

      {/* 配置モード切替(個別 / 範囲) */}
      {godMode && (
        <div className="pc-section">
          <div className="pc-section-title">{t("prop_placement.modeLabel")}</div>
          <div className="pp-mode-toggle">
            <button
              className={`pp-mode-btn ${mode === "instance" ? "active" : ""}`}
              type="button"
              onClick={() => handleModeChange("instance")}
            >
              {t("prop_placement.modeInstance")}
            </button>
            <button
              className={`pp-mode-btn ${mode === "scatter" ? "active" : ""}`}
              type="button"
              onClick={() => handleModeChange("scatter")}
            >
              {t("prop_placement.modeScatter")}
            </button>
          </div>
        </div>
      )}

      {/* 設定(モードごと) */}
      {selectedType !== null && (
        <div className="pc-section">
          <div className="pc-section-title">{t("prop_placement.settings")}</div>

          {mode === "instance" ? (
            <>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.height")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={height}
                    onChange={handleHeightChange}
                    step={0.1}
                    integerOnly={false}
                    label={t("prop_placement.height")}
                  />
                </div>
              </div>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.rotation")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={rotation}
                    onChange={handleRotationChange}
                    min={0}
                    max={360}
                    step={15}
                    integerOnly={true}
                  />
                </div>
              </div>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.scale")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={scale}
                    onChange={handleScaleChange}
                    min={0.1}
                    max={5}
                    step={0.1}
                    integerOnly={false}
                  />
                </div>
              </div>
            </>
          ) : (
            <>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.density")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={density}
                    onChange={handleDensityChange}
                    min={1}
                    max={100000}
                    step={10}
                    integerOnly={true}
                  />
                </div>
              </div>
              <div className="pc-settings-row">
                <span className="pc-settings-label">{t("prop_placement.seed")}</span>
                <div className="pc-stepper-row">
                  <NumberStepper
                    value={seed}
                    onChange={handleSeedChange}
                    min={0}
                    max={2147483647}
                    step={1}
                    integerOnly={true}
                  />
                </div>
              </div>
            </>
          )}
        </div>
      )}


      {/* アクションボタン */}
      {selectedType !== null && (
        <div className="pc-section pc-action-bar">
          <div className="pc-actions">
            <button className="pc-btn" type="button" onClick={handleCancel}>
              {t("prop_placement.cancel")}
            </button>
            <button
              className="pc-btn"
              disabled={isConfirming}
              type="button"
              onClick={handleReset}
            >
              {t("prop_placement.reset")}
            </button>
            <button
              className="pc-btn pc-btn-confirm"
              disabled={!canConfirm || isConfirming}
              type="button"
              onClick={handleConfirm}
            >
              {isConfirming ? t("prop_placement.confirming") : t("prop_placement.confirm")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
