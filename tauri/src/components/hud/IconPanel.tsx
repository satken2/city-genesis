/**
 * アイコンパネル(画面左端に縦並びの常駐メニュー)
 *
 * 各種メニューウィンドウ(道路・建設・予算・統計…)を開くアイコンを縦一列に並べる。
 * 地区編集だけはウィンドウを開かず、Unityを地区編集ステートにする常駐モードのトグル
 * (ブルドーザと排他。状態は `hudMode` でコックピットと共有する)。
 *
 * ウィンドウ自体は `icon-panel` ラベルの専用ウィンドウで、Rust側が画面左端の上下中央に置く。
 * アイコン数はテンプレートモードの有無で変わるため、実測した高さを
 * Tauriコマンド `resize_icon_panel` で通知してウィンドウ丈を合わせる
 * (合わせないと透明な余白がクリックを吸ってUnityを操作できなくなる)。
 */

import { useCallback, useEffect, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";

import {
  IPC_EVENT_EDIT_ZONE_REQUEST_MERGE,
  sendChangeState,
  sendChangeDisplay,
  sendSessionReload,
} from "../../utils/ipc";
import { setHintbarText } from "../../utils/hintbar";
import {
  EDIT_MODE_WINDOW_TYPES,
  MENU_ICONS,
  iconUrl,
  openRegisteredWindow,
  type HudIconDef,
} from "./hudCommon";
import { setHudMode, useHudMode } from "./hudMode";
import { useHudSession, useTutorialHighlight } from "./useHudSession";
import "./hud.css";
import "./IconPanel.css";

export function IconPanel() {
  const { t } = useTranslation();
  const { visible, templateMode, isLocked, epochExecuting } = useHudSession();
  const mode = useHudMode();
  const getBtnClassName = useTutorialHighlight();
  const panelRef = useRef<HTMLElement | null>(null);

  /**
   * パネルの実高さ(枠線込み)をウィンドウ側へ通知する。
   * アイコンの増減(テンプレートモードの切替)でも呼ばれるよう ResizeObserver で監視する。
   * 同じ高さの再送はしない。ウィンドウのリサイズがパネル高に跳ね返ることは無い設計だが、
   * 端数の揺れで送信が往復し続けるのを確実に防ぐため。
   */
  useEffect(() => {
    const element = panelRef.current;
    if (!element) return;

    let lastSent = 0;
    const observer = new ResizeObserver(() => {
      const height = element.getBoundingClientRect().height;
      if (height <= 0 || Math.abs(height - lastSent) < 1) return;
      lastSent = height;
      void invoke("resize_icon_panel", { height }).catch(console.error);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [visible]);

  /**
   * 地区編集モードの「地区の統合」で、統合元・統合先が決まったときにUnityから届く要求を受け、
   * 確定ウィンドウ(`zone_merge`)を開く。
   *
   * 地区編集モードは自前のウィンドウを持たない常駐モードなので、Unityからは直接ウィンドウを
   * 開けない。常駐しているこのアイコンパネルが中継役になる。イベントを受けてからウィンドウを
   * 開く順序ではパネル側の購読が間に合わないため、統合内容はイベントではなく
   * `params.extra.pendingMerge` としてウィンドウコンテキストに載せて渡す。
   */
  useEffect(() => {
    const unlistenPromise = listen<{
      target_zone_id?: number;
      target_zone_name?: string;
      source_zone_id?: number;
      source_zone_name?: string;
    }>(IPC_EVENT_EDIT_ZONE_REQUEST_MERGE, (event) => {
      const payload = event.payload ?? {};
      if (!payload.target_zone_id || !payload.source_zone_id) return;
      void invoke("open_registered_window", {
        windowType: "zone_merge",
        params: {
          extra: {
            pendingMerge: {
              targetZoneId: payload.target_zone_id,
              targetZoneName: payload.target_zone_name ?? "",
              sourceZoneId: payload.source_zone_id,
              sourceZoneName: payload.source_zone_name ?? "",
            },
          },
        },
      }).catch(console.error);
    });
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, []);

  /**
   * 地区編集モードのトグル。ウィンドウを開かない常駐モード
   * (windowType "district" は送信元ラベルで実ウィンドウは無い)。
   * ON で Unity を STATE_EDIT_ZONE にして道路を地区色分け表示にし、ヒントバーに操作説明を出す。
   * OFF で STATE_NORMAL に戻し道路表示を既定へ戻してからセッションをリロードする
   * (地区の割当・拡張は3Dジェスチャで即時反映されるため、抜ける際に最新状態を確実に反映する)。
   * 3Dビュー内のツール切替(作成/拡張)・操作は Unity 側の Ctrl ラジアルメニューが担う。
   */
  const toggleDistrict = useCallback(async () => {
    const next = mode !== "district";
    try {
      if (next) {
        await setHintbarText(t("hint.districtEdit"));
        await sendChangeState("STATE_EDIT_ZONE", undefined, "district");
        await sendChangeDisplay("road", "zone_color");
      } else {
        await sendChangeState("STATE_NORMAL", undefined, "district");
        await sendChangeDisplay("road", "default");
        await setHintbarText("");
        await sendSessionReload();
      }
      await setHudMode(next ? "district" : null);
    } catch (error) {
      console.error("failed to toggle district edit mode", error);
    }
  }, [mode, t]);

  const handleIconClick = useCallback(
    async (icon: HudIconDef) => {
      if (icon.id === "district") {
        await toggleDistrict();
        return;
      }
      if (!icon.windowType) return;
      // 編集モード系ウィンドウを開くと Unity が STATE を上書きするため、常駐モードを解除する。
      if (EDIT_MODE_WINDOW_TYPES.has(icon.windowType)) {
        await setHudMode(null);
      }
      await setHintbarText("");
      await openRegisteredWindow(icon.windowType);
    },
    [toggleDistrict],
  );

  if (!visible) return null;

  return (
    <nav ref={panelRef} className={`icon-panel ${epochExecuting ? "hud-disabled" : ""}`}>
      {MENU_ICONS.map((icon) => {
        const active = icon.id === "district" && mode === "district";
        return (
          <button
            key={icon.id}
            id={icon.id}
            className={`${getBtnClassName(icon.id, "hud-icon-btn icon-panel-slot")}${active ? " hud-icon-btn--active" : ""}`}
            type="button"
            title={t(icon.tooltipKey)}
            aria-pressed={icon.id === "district" ? active : undefined}
            style={{ padding: icon.padding }}
            disabled={isLocked && icon.id !== "construction"}
            onClick={() => void handleIconClick(icon)}
          >
            <img src={iconUrl(icon.src)} alt={t(icon.tooltipKey)} className="hud-icon-img" draggable={false} />
          </button>
        );
      })}

      <button
        id="prop_placement"
        className={getBtnClassName("prop_placement", "hud-icon-btn icon-panel-slot")}
        type="button"
        title={t("taskbar.menuPropPlacement")}
        disabled={isLocked}
        onClick={async () => {
          await setHudMode(null);
          await setHintbarText(t("prop_placement.hint"));
          await openRegisteredWindow("prop_placement");
        }}
      >
        <img src={iconUrl("menu_land.png")} alt={t("taskbar.menuPropPlacement")} className="hud-icon-img" draggable={false} />
      </button>

      {templateMode && (
        <>
          <div className="icon-panel-divider" />
          <button
            id="resource_edit"
            className={getBtnClassName("resource_edit", "hud-icon-btn icon-panel-slot")}
            type="button"
            title={t("taskbar.menuResourceEdit")}
            disabled={isLocked}
            onClick={async () => {
              await setHudMode(null);
              await setHintbarText(t("hint.resourceEdit"));
              await openRegisteredWindow("resource_edit");
            }}
          >
            <img src={iconUrl("pixels.png")} alt={t("taskbar.menuResourceEdit")} className="hud-icon-img" draggable={false} />
          </button>
          <button
            id="parameter_edit"
            className={getBtnClassName("parameter_edit", "hud-icon-btn icon-panel-slot")}
            type="button"
            title={t("taskbar.menuParameterEdit")}
            disabled={isLocked}
            onClick={() => void invoke("open_parameter_edit_window")}
          >
            <img src={iconUrl("tray_settings.png")} alt={t("taskbar.menuParameterEdit")} className="hud-icon-img" draggable={false} />
          </button>
        </>
      )}
    </nav>
  );
}
