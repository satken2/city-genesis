/**
 * 道路の全体図(道路詳細パネルの「全体図」タブ)
 *
 * 共通の都市2Dマップ部品 CityMapCanvas を埋め込み、道路の区間を道路色の帯で強調する
 * (focusPathIds・focusColor。他の道路・線路は focusDimOthers で薄くする)。直進方向の自動延長の
 * 候補は previewPathIds で同じ色の破線として重ねる。帯の描画規則は CityMapCanvas 側が持ち、
 * このコンポーネントは表示範囲の決定だけを担う。
 *
 * CityMapCanvas のベクターは表示範囲(bbox)で取得されるため、範囲に入っていない区間は
 * 読み込まれず帯も描けない。そのため表示範囲は道路の bbox に余白を足して合わせ、bbox が変わったら追従する。
 */

import { useEffect, useMemo, useRef } from "react";
import {
  CityMapCanvas,
  type CityMapCanvasHandle,
  type CityMapBounds,
  type CityMapLayerVisibility,
} from "../map/CityMapCanvas";
import "./RoadOverviewMap.css";

/** bbox に足す余白の最小値(m)。短い道路でも周囲の街並みが見えるようにする */
const FIT_MARGIN_MIN = 80;
/** bbox の長辺に対する余白の比率 */
const FIT_MARGIN_RATIO = 0.08;

/** 周辺の道路・線路・区画・建物は道路の位置関係が分かるよう残し、地形の色分けや格子は消す */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  transitLines: false,
  parcels: false,
  buildings: true,
  stations: true,
  interchanges: true,
  zoneBorders: false,
  elevation: false,
  contours: false,
  grid: false,
};

type RoadOverviewMapProps = {
  apiBaseUrl: string;
  bbox: [number, number, number, number] | null;
  pathIds: number[];
  color: string;
  /** 確定前の追加候補(直進方向の自動延長) */
  previewPathIds: number[] | null;
};

function boundsFromBbox(bbox: [number, number, number, number] | null): CityMapBounds | null {
  if (!bbox) return null;
  const [minX, minZ, maxX, maxZ] = bbox;
  const margin = Math.max(FIT_MARGIN_MIN, Math.max(maxX - minX, maxZ - minZ) * FIT_MARGIN_RATIO);
  return { x1: minX - margin, z1: minZ - margin, x2: maxX + margin, z2: maxZ + margin };
}

export function RoadOverviewMap({ apiBaseUrl, bbox, pathIds, color, previewPathIds }: RoadOverviewMapProps) {
  const mapRef = useRef<CityMapCanvasHandle>(null);
  const bboxKey = bbox ? bbox.join(",") : "";

  // 初期範囲は初回マウント時だけ渡し、以降の変化は fitBounds で追従する(初期化effectを再走させない)。
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const initialBounds = useMemo(() => boundsFromBbox(bbox) ?? undefined, []);

  useEffect(() => {
    const bounds = boundsFromBbox(bbox);
    if (bounds) mapRef.current?.fitBounds(bounds);
    // bbox は取り直しのたびに新しい配列になるので、値の文字列で比較する
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bboxKey]);

  return (
    <div className="road-overview-map">
      <CityMapCanvas
        ref={mapRef}
        apiBaseUrl={apiBaseUrl}
        layers={MAP_LAYERS}
        initialBounds={initialBounds}
        interactive={true}
        showCompass={true}
        focusPathIds={pathIds}
        focusColor={color}
        focusDimOthers={true}
        previewPathIds={previewPathIds}
        className="road-overview-map__canvas"
      />
    </div>
  );
}
