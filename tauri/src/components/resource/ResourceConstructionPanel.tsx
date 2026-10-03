/**
 * 資源編集パネル
 *
 * window_type="resource_edit" のポップアップウィンドウで表示する専用パネル。
 * テンプレート作成モード(play_mode=PLAY_MODE_SCEARIO)専用の機能で、アイコンパネルのボタンも
 * play_mode が PLAY_MODE_SCEARIO のときのみ表示される(hud/IconPanel.tsx参照)。
 * PathConstructionPanel と同じ単列レイアウト。
 *
 * 操作フロー:
 *   1. 資源種別プルダウンで種類を選択 → IPCでグラフィックプログラムに資源エリア編集モード
 *      (STATE_EDIT_RESOURCE)を通知する
 *   2. グラフィックプログラム側で地形をクリックして頂点を配置し、任意ポリゴンを作成する
 *      (ParcelShapeEditManagerと同じ操作感: 頂点ドラッグ・辺クリックで頂点追加・右クリックで頂点削除)
 *   3. 編集中のポリゴンはIPC(edit_resource:update_shape)で随時パネルに通知される
 *   4. 埋蔵量上限・回復率を設定する。初期値は種別ごとの目安(data/resourcePresets.ts)で、
 *      面積比例の目安なら形状の面積に追従する
 *   5. [確定] でバックエンドの POST /api/v1/resources に直接送信する(道路建設と同じく
 *      Tauri側が直接バックエンド送信を行い、成功後にUnity側の編集状態をリセットする)
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import { ringSignedArea } from "../terrain/riverGeometry";
import { ResourcePresetHint } from "./ResourcePresetHint";
import { useResourcePresetInputs } from "../../hooks/useResourcePresetInputs";
import { RESOURCE_MAX_AMOUNT_LIMIT } from "../../data/resourcePresets";

import { setHintbarText } from "../../utils/hintbar";
import { showNotification } from "../../utils/notification";
import { ApiError, buildApiUrl, fetchApi } from "../../utils/api";
import {
  sendChangeState,
  sendEditResourceFinish,
  IPC_EVENT_EDIT_RESOURCE_UPDATE_SHAPE,
} from "../../utils/ipc";
import "./ResourceConstructionPanel.css";

type ResourceConstructionPanelProps = {
  apiBaseUrl?: string;
  onClose: () => void;
};

export function ResourceConstructionPanel({ apiBaseUrl, onClose }: ResourceConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [selectedType, setSelectedType] = useState<number | null>(null);
  const [polygon, setPolygon] = useState<number[][]>([]);
  const [isConfirming, setIsConfirming] = useState(false);

  // 頂点は [x, z](m)で届く。3点未満は面積0として扱い、面積比例の目安は形状待ちになる。
  const areaM2 = polygon.length >= 3 ? Math.abs(ringSignedArea(polygon)) : 0;
  const productId = selectedType !== null ? masterData?.resource_types[selectedType]?.product_id ?? null : null;
  const presetInputs = useResourcePresetInputs(productId, areaM2);
  const { maxAmount, recoveryRate, reset: resetPresetInputs } = presetInputs;

  /* ---- Unity側で編集中のポリゴンが変化するたびに通知を受信する ---- */
  useEffect(() => {
    const appWindow = getCurrentWindow();
    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ polygon: number[][] }>(IPC_EVENT_EDIT_RESOURCE_UPDATE_SHAPE, (event) => {
        setPolygon(event.payload.polygon ?? []);
      })
      .then((fn) => {
        unlisten = fn;
      })
      .catch(console.error);

    return () => {
      unlisten?.();
    };
  }, []);

  /**
   * 資源種別を選択した際のハンドラ。
   * Unity側にSTATE_EDIT_RESOURCEへの遷移を通知し、頂点配置を開始できる状態にする。
   * 種別を選び直した場合は、それまでに配置したポリゴンをリセットする
   * (道路建設で種別変更時にバリアント・ノードをリセットするのと同じ考え方)。
   */
  const handleTypeChange = useCallback((e: React.ChangeEvent<HTMLSelectElement>) => {
    const val = e.target.value;
    const typeId = val ? parseInt(val) : null;
    setSelectedType(typeId);
    setPolygon([]);
    if (typeId !== null) {
      sendChangeState("STATE_EDIT_RESOURCE", { resource_type: typeId }, "resource_edit").catch((err) => {
        console.error("[ResourceConstructionPanel] Failed to enter edit mode:", err);
      });
    }
  }, []);

  /* ---- ヒントバー表示。パネルが閉じられたらクリアする ---- */
  useEffect(() => {
    setHintbarText(t("hint.resourceEdit")).catch((err) => {
      console.error("[ResourceConstructionPanel] setHintbarText failed:", err);
    });
    return () => {
      void setHintbarText("");
    };
  }, [t]);

  /* ---- コンポーネント破棄時に確実にUnity側をSTATE_NORMALへ戻す ---- */
  useEffect(() => {
    return () => {
      sendChangeState("STATE_NORMAL").catch(() => {});
    };
  }, []);

  const handleCancel = useCallback(() => {
    setSelectedType(null);
    setPolygon([]);
    resetPresetInputs();
    sendChangeState("STATE_NORMAL").catch(() => {});
  }, [resetPresetInputs]);

  /**
   * 確定ボタンを押した際のハンドラ。
   * Tauri側が直接バックエンドに資源データを送信する(道路建設と同じ責務分担)。
   * 成功時はUnityに編集終了を通知しパネルを閉じ、失敗時はエラーを通知して再試行を許可する。
   */
  const handleConfirm = useCallback(async () => {
    if (selectedType === null || polygon.length < 3 || !apiBaseUrl) return;

    setIsConfirming(true);
    try {
      await fetchApi<{ resource: { resource_id: number } }>(
        buildApiUrl(apiBaseUrl, "/api/v1/resources"),
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            resource_type: selectedType,
            max_amount: maxAmount,
            recovery_rate: recoveryRate,
            polygon,
          }),
        }
      );
      await sendEditResourceFinish();
      await sendChangeState("STATE_NORMAL");
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
        displayMessage = t("resource_construction.confirmFailedUnknown");
      }
      void showNotification({ message: displayMessage, durationMs: 8000 });
    }
  }, [selectedType, polygon, maxAmount, recoveryRate, apiBaseUrl, onClose, t]);

  if (!masterData) {
    return <p className="pc-muted">{t("common.loading")}</p>;
  }

  const resourceTypeEntries = Object.entries(masterData.resource_types);

  return (
    <div className="pc-layout">
      {/* 資源種別選択 */}
      <div className="pc-section">
        <select className="pc-select" value={selectedType ?? ""} onChange={handleTypeChange}>
          <option value="">{t("resource_construction.selectType")}</option>
          {resourceTypeEntries.map(([id, entry]) => (
            <option key={id} value={id}>
              {entry.display_name}
            </option>
          ))}
        </select>
      </div>

      {/* 埋蔵量上限・回復率設定 */}
      {selectedType !== null && (
        <div className="pc-section">
          <div className="pc-section-title">{t("resource_construction.settings")}</div>

          {presetInputs.presets.length > 1 && (
            <div className="pc-settings-row">
              <span className="pc-settings-label">{t("resourcePreset.label")}</span>
              <select
                className="pc-select"
                value={presetInputs.preset?.key ?? ""}
                onChange={(e) => presetInputs.selectPreset(e.target.value)}
              >
                {presetInputs.presets.map((preset) => (
                  <option key={preset.key} value={preset.key}>
                    {t(`resourcePreset.presets.${preset.key}.name`)}
                  </option>
                ))}
              </select>
            </div>
          )}

          <div className="pc-settings-row">
            <span className="pc-settings-label">{t("resource_construction.maxAmount")}</span>
            <div className="pc-stepper-row">
              <NumberStepper
                className="number-stepper--wide"
                value={maxAmount}
                onChange={presetInputs.setMaxAmount}
                min={1}
                max={RESOURCE_MAX_AMOUNT_LIMIT}
                step={100}
                integerOnly={true}
              />
            </div>
          </div>

          <div className="pc-settings-row">
            <span className="pc-settings-label">{t("resource_construction.recoveryRate")}</span>
            <div className="pc-stepper-row">
              <NumberStepper
                value={recoveryRate}
                onChange={presetInputs.setRecoveryRate}
                min={0}
                max={1}
                step={0.0001}
                integerOnly={false}
              />
            </div>
          </div>

          <ResourcePresetHint
            preset={presetInputs.preset}
            areaM2={areaM2}
            maxAmount={maxAmount}
            recoveryRate={recoveryRate}
            awaitingShape={presetInputs.awaitingShape}
            className="pc-hint"
          />
        </div>
      )}

      {/* ポリゴン情報 */}
      {selectedType !== null && (
        <div className="pc-section">
          <div className="pc-section-title">{t("resource_construction.shapeInfo")}</div>
          {polygon.length === 0 ? (
            <div className="pc-no-nodes-message">{t("resource_construction.noVerticesYet")}</div>
          ) : (
            <div className="pc-info-grid">
              <span className="pc-info-label">{t("resource_construction.vertexCount")}</span>
              <span className="pc-info-value">{polygon.length}</span>
            </div>
          )}
        </div>
      )}

      {/* アクションボタン */}
      {selectedType !== null && (
        <div className="pc-section pc-action-bar">
          <div className="pc-actions">
            <button className="pc-btn" type="button" onClick={handleCancel}>
              {t("resource_construction.cancel")}
            </button>
            <button
              className="pc-btn pc-btn-confirm"
              disabled={polygon.length < 3 || isConfirming}
              type="button"
              onClick={handleConfirm}
            >
              {isConfirming ? t("resource_construction.confirming") : t("resource_construction.confirm")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
