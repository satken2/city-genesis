import { forwardRef } from "react";
import type { CityMapCanvasHandle, CityMapCanvasProps } from "../../src/components/map/CityMapCanvas";

export { DEFAULT_CITY_MAP_LAYERS, pathCenterline } from "../../src/components/map/CityMapCanvas";

/** 地区編集が渡した描画・操作コールバックを、地形取得やアニメーションループなしで呼び出す。 */
export let mapProps: CityMapCanvasProps;
export const CityMapCanvas = forwardRef<CityMapCanvasHandle, CityMapCanvasProps>((props, _ref) => {
  mapProps = props;
  return null;
});
