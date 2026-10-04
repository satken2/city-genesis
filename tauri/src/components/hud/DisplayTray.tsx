/**
 * 表示トレイ(画面左下の常駐パネル)
 *
 * よく使う表示レイヤーのON/OFFだけを集めた簡易版の表示切替。
 * 色モードの選択やアラート種別のフィルタなど細かい設定は
 * 表示オプションパネル(`display/DisplayOptionsPanel`)が受け持ち、
 * ここは「今見えているものを一時的に消す/出す」用途に絞る。
 *
 * 状態の正はUnity側(`IpcViewCoordinator`)で、`useDisplayOptions` が
 * `view:current_state` の受信で同期する。道路敷設モードなどUnityが自発的に
 * レイヤーを切り替えたときも、そのブロードキャストを受けて表示が追従する。
 */

import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { sendViewQueryState } from "../../utils/ipc";
import { useDisplayOptions } from "../display/useDisplayOptions";
import { useHudSession } from "./useHudSession";
import buildingIcon from "../../assets/images/menu/display-building.svg";
import roadIcon from "../../assets/images/menu/display-road.svg";
import propIcon from "../../assets/images/menu/display-prop.svg";
import parcelIcon from "../../assets/images/menu/display-parcel.svg";
import terrainIcon from "../../assets/images/menu/display-terrain.svg";
import districtIcon from "../../assets/images/menu/display-district.svg";
import "./hud.css";
import "./DisplayTray.css";

/** トレイに並べる切替ボタン1個の定義 */
type DisplayTrayItem = {
  /** 対象の表示レイヤーID(`displayConstants.DISPLAY_LAYERS` のID) */
  layerId: string;
  /** ボタンに敷くアイコン画像のURL */
  icon: string;
  /** ツールチップに出すレイヤー名のi18nキー */
  labelKey: string;
  /** ONにするときの色モード。OFFは全レイヤー共通で "hidden" */
  visibleMode: string;
  /** ON/OFFそれぞれの状態を表すツールチップのi18nキー */
  onStateKey: string;
  offStateKey: string;
};

/**
 * 並び順はそのまま左から右へのボタンの並びになる。
 * ボタン数は `window.rs` の `DISPLAY_TRAY_WIDTH` と連動するため、増減させたら幅も直すこと。
 *
 * 地形だけはON/OFFではなく「通常表示 / 等高線表示」の切替で、色モードではなく
 * サブモード(`contour_on` / `contour_off`)を差し替える(表示オプションパネルと同じ仕組み)。
 */
const TRAY_ITEMS: DisplayTrayItem[] = [
  {
    layerId: "building",
    icon: buildingIcon,
    labelKey: "display.layerBuilding",
    visibleMode: "default",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
  {
    layerId: "road",
    icon: roadIcon,
    labelKey: "display.layerRoad",
    visibleMode: "default",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
  {
    layerId: "prop",
    icon: propIcon,
    labelKey: "display.layerProp",
    visibleMode: "visible",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
  {
    layerId: "parcel",
    icon: parcelIcon,
    labelKey: "display.layerParcel",
    visibleMode: "border",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
  {
    layerId: "terrain",
    icon: terrainIcon,
    labelKey: "display.layerTerrain",
    visibleMode: "default",
    onStateKey: "display.trayStateContour",
    offStateKey: "display.trayStateTerrainNormal",
  },
  {
    layerId: "district",
    icon: districtIcon,
    labelKey: "display.layerDistrict",
    visibleMode: "border_icon",
    onStateKey: "display.trayStateVisible",
    offStateKey: "display.trayStateHidden",
  },
];

export function DisplayTray() {
  const { t } = useTranslation();
  const { visible, epochExecuting } = useHudSession();
  const { selections, selectMode } = useDisplayOptions();

  /**
   * OFFにする直前の色モードをレイヤーごとに覚えておき、ONに戻すときに復元する。
   * これが無いと、表示オプションパネルで地価色分けなどを選んでいた状態から
   * トレイで一度消しただけで通常表示に戻ってしまう。
   */
  const lastVisibleModeRef = useRef<Record<string, string>>({});

  /**
   * セッション開始でHUDが出たタイミングでUnityへ現在の表示状態を問い合わせ直す。
   * このトレイはアプリ起動時からマウントされたままなので、`useDisplayOptions` が
   * マウント時に投げる問い合わせはUnityの準備前に飛んでしまうことがある。
   * OFFにする直前の色モードの記憶も、前の都市のものなので捨てる(Unity側は読み込みの開始時に既定へ戻る)。
   */
  useEffect(() => {
    if (!visible) return;
    lastVisibleModeRef.current = {};
    sendViewQueryState().catch((err) => {
      console.warn("[DisplayTray] sendViewQueryState failed:", err);
    });
  }, [visible]);

  function isOn(item: DisplayTrayItem): boolean {
    const selection = selections[item.layerId];
    if (!selection) return false;
    if (item.layerId === "terrain") return selection.subMode === "contour_on";
    return selection.mode !== "hidden";
  }

  function handleToggle(item: DisplayTrayItem) {
    const selection = selections[item.layerId];
    if (!selection) return;

    if (item.layerId === "terrain") {
      selectMode(item.layerId, selection.mode, isOn(item) ? "contour_off" : "contour_on");
      return;
    }

    // サブモード(建設可能色分けの対象種別など)は消しても保ったまま戻す
    const subMode = selection.subMode ?? undefined;
    if (isOn(item)) {
      lastVisibleModeRef.current[item.layerId] = selection.mode;
      selectMode(item.layerId, "hidden", subMode);
    } else {
      selectMode(item.layerId, lastVisibleModeRef.current[item.layerId] ?? item.visibleMode, subMode);
    }
  }

  if (!visible) return null;

  return (
    <div className={`hud-panel display-tray ${epochExecuting ? "hud-disabled" : ""}`}>
      {TRAY_ITEMS.map((item) => {
        const on = isOn(item);
        const tooltip = t("display.trayTooltip", {
          name: t(item.labelKey),
          state: t(on ? item.onStateKey : item.offStateKey),
        });
        return (
          <button
            key={item.layerId}
            id={`display_tray_${item.layerId}`}
            className={`hud-icon-btn display-tray-icon${on ? "" : " display-tray-icon--off"}`}
            type="button"
            title={tooltip}
            aria-label={tooltip}
            aria-pressed={on}
            onClick={() => handleToggle(item)}
          >
            <img src={item.icon} alt="" className="hud-icon-img" draggable={false} />
          </button>
        );
      })}
    </div>
  );
}
