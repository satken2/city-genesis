/**
 * 開発用の地図ページのウィンドウの置き方。exe・PC 向けページの規則が求めた位置は使わず、種類ごとの定位置へ置く。
 *
 * - 上部バーは画面上端の全幅、トップメニューは全画面に置く。
 * - アイコンパネルは左下、システムトレイは右下、通知は上部バーの下に置く(大きさは各パネルが測った値)。
 *   地図の上端のツールバーを隠さないよう、アイコンパネルは下端に寄せる。
 * - それ以外のウィンドウ(詳細パネル・各種ウィンドウ・Tips など)は、都市を見せている間(上部バーの表示中)は地図が見えるよう
 *   画面の下半分のシートに、都市が無い間(トップメニュー・読み込み中)は全画面に置く。
 *   ポップアップは同じ場所に重なり、最後に開いたものが前面に出る。閉じると下のものが見える。
 */
import * as host from "../shell/host";
import type { Placement } from "../shell/host";
import { DESKTOP_HUD, LABEL_ICON_PANEL, LABEL_SYSTEM_TRAY, type HudWindowSpec } from "../shell/windows";
import { DEBUG_FRAME_PAGE, LABEL_DEBUG_BAR } from "./labels";

export const BAR_HEIGHT = 48;
const MARGIN = 8;

/** コックピットと表示トレイの代わりに上部バーを置く。表示トレイの切り替え先は 3D 画面なので置かない */
export const DEBUG_HUD: HudWindowSpec[] = [
  { label: LABEL_DEBUG_BAR, title: "Genesis Debug Bar", width: 0, height: BAR_HEIGHT, page: DEBUG_FRAME_PAGE },
  ...DESKTOP_HUD.filter((spec) => spec.label === LABEL_ICON_PANEL || spec.label === LABEL_SYSTEM_TRAY),
];

export function arrangeForDebugMap(label: string, requested: Placement): Placement {
  const m = host.monitor();
  switch (label) {
    case LABEL_DEBUG_BAR:
      return { x: m.x, y: m.y, width: m.width, height: BAR_HEIGHT };
    case "topmenu":
      return m;
    case LABEL_ICON_PANEL: {
      const height = Math.min(requested.height, m.height - BAR_HEIGHT - MARGIN * 2);
      return { x: m.x + MARGIN, y: m.y + m.height - height - MARGIN, width: requested.width, height };
    }
    case LABEL_SYSTEM_TRAY:
      return { x: m.x + m.width - requested.width - MARGIN, y: m.y + m.height - requested.height - MARGIN, width: requested.width, height: requested.height };
    case "notification":
      return { x: m.x + MARGIN, y: m.y + BAR_HEIGHT + MARGIN, width: m.width - MARGIN * 2, height: requested.height };
    default: {
      if (!host.isVisible(LABEL_DEBUG_BAR)) return m;
      const top = Math.round(m.height / 2);
      return { x: m.x, y: m.y + top, width: m.width, height: m.height - top };
    }
  }
}
