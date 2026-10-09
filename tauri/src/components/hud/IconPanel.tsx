/**
 * アイコンパネル(画面左端に縦並びの常駐メニュー)
 *
 * 各種メニューウィンドウ(道路・建設・規制・統計…)を開くアイコンを縦一列に並べる。
 * 並び順・開くウィンドウはすべて `hudCommon.ts` の `MENU_ICONS` が正で、
 * アイコン画像は `assets/images/panel` 配下の画像を優先して描く(未定義時はMENU_ICONSの画像へフォールバック)。
 *
 * 読み取り専用モードでは、都市に物を作る操作(`hiddenWhenReadOnly`)を出さない。
 *
 * ウィンドウ自体は `icon-panel` ラベルの専用ウィンドウで、Rust側が画面左端の上下中央に置く。
 * アイコン数はプレイモードで変わるため、実測した高さを
 * Tauriコマンド `resize_icon_panel` で通知してウィンドウ丈を合わせる
 * (合わせないと透明な余白がクリックを吸ってUnityを操作できなくなる)。
 */
import { PLAY_MODE_SCEARIO, PLAY_MODE_READ_ONLY } from "../../constants";


import { Fragment, useCallback, useEffect, useRef } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";

import { IPC_EVENT_EDIT_ZONE_REQUEST_MERGE } from "../../utils/ipc";
import { setHintbarText } from "../../utils/hintbar";
import {
  MENU_ICONS,
  iconUrl,
  openRegisteredWindow,
  type HudIconDef,
} from "./hudCommon";
import { useHudSession } from "./useHudSession";
import "./hud.css";
import "./IconPanel.css";

const panelIconModules = import.meta.glob<{ default: string }>(
  "../../assets/images/panel/*.png",
  { eager: true }
);

function resolveIconSrc(icon: HudIconDef): string {
  const fileKey = icon.panelIcon ?? icon.id;
  return (
    panelIconModules[`../../assets/images/panel/${fileKey}.png`]?.default ??
    iconUrl(icon.src)
  );
}

export function IconPanel() {
  const { t } = useTranslation();
  const { visible, playMode, isLocked, epochExecuting } = useHudSession();
  const godMode = playMode === PLAY_MODE_SCEARIO;
  const readOnly = playMode === PLAY_MODE_READ_ONLY;
  const panelRef = useRef<HTMLElement | null>(null);

  /**
   * パネルの実高さ(枠線込み)をウィンドウ側へ通知する。
   * アイコンの増減(プレイモードの切替)でも呼ばれるよう ResizeObserver で監視する。
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
   * 3Dビューの地区編集モード(Unity)の「地区の統合」で、統合元・統合先が決まったときにUnityから届く要求を受け、
   * 確定ウィンドウ(`zone_merge`)を開く。現在はUIから地区編集モードへ入る入口が無いが、Unity側の
   * 地区編集モードと対で残している。
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

  const handleIconClick = useCallback(
    async (icon: HudIconDef) => {
      // Tauriコマンド直呼びのアイコンは Unity の状態に触れないため、
      // 常駐モードもヒントバーもそのまま維持する。
      if (icon.command) {
        await invoke(icon.command).catch(console.error);
        return;
      }
      if (!icon.windowType) return;
      // 編集モードのウィンドウなら、開く処理(Rust の apply_mutex_rules)が常駐モードを解除する。
      await setHintbarText(icon.hintKey ? t(icon.hintKey) : "");
      await openRegisteredWindow(icon.windowType);
    },
    [t],
  );

  if (!visible) return null;

  const icons = MENU_ICONS.filter((icon) => (godMode || !icon.templateOnly) && !(readOnly && icon.hiddenWhenReadOnly));

  return (
    <nav ref={panelRef} className={`icon-panel ${epochExecuting ? "hud-disabled" : ""}`}>
      {icons.map((icon, index) => {
        // 製作者向け操作の先頭に区切り線を入れて、通常プレイのメニューと見た目で分ける。
        const startsTemplateGroup = icon.templateOnly && !icons[index - 1]?.templateOnly;
        return (
          <Fragment key={icon.id}>
            {startsTemplateGroup && <div className="icon-panel-divider" />}
            <button
              id={icon.id}
              className="hud-icon-btn icon-panel-slot"
              type="button"
              title={t(icon.tooltipKey)}
              disabled={isLocked && !icon.enabledWhenLocked}
              onClick={() => void handleIconClick(icon)}
            >
              <img src={resolveIconSrc(icon)} alt={t(icon.tooltipKey)} className="hud-icon-img" draggable={false} />
            </button>
          </Fragment>
        );
      })}
    </nav>
  );
}
