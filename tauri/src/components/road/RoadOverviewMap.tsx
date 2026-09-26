/**
 * 道路の全体図(道路詳細パネルの「全体図」タブ)
 *
 * 共通の都市2Dマップ部品 CityMapCanvas を埋め込み、道路の区間を道路色の帯で強調する
 * (focusPathIds・focusColor。他の道路・線路は focusDimOthers で薄くする)。直進方向の自動延長の
 * 候補は previewPathIds で同じ色の破線として重ねる。帯の描画規則は CityMapCanvas 側が持ち、
 * このコンポーネントは表示範囲の決定だけを担う。
 *
 * CityMapCanvas のベクターは表示範囲(bbox)で取得されるため、範囲に入っていない区間は
 * 読み込まれず帯も描けない。そのため表示範囲は道路の bbox(候補があれば候補の範囲も合わせたもの)に
 * 余白を足して合わせ、範囲が変わったら追従する。
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
  /** 追加候補の範囲 [minx, minz, maxx, maxz] */
  previewBbox?: [number, number, number, number] | null;
};

type Bbox = [number, number, number, number];

function unionBbox(a: Bbox | null, b: Bbox | null | undefined): Bbox | null {
  if (!a) return b ?? null;
  if (!b) return a;
  return [Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[2], b[2]), Math.max(a[3], b[3])];
}

function boundsFromBbox(bbox: [number, number, number, number] | null): CityMapBounds | null {
  if (!bbox) return null;
  const [minX, minZ, maxX, maxZ] = bbox;
  const margin = Math.max(FIT_MARGIN_MIN, Math.max(maxX - minX, maxZ - minZ) * FIT_MARGIN_RATIO);
  return { x1: minX - margin, z1: minZ - margin, x2: maxX + margin, z2: maxZ + margin };
}

export function RoadOverviewMap({ apiBaseUrl, bbox, pathIds, color, previewPathIds, previewBbox }: RoadOverviewMapProps) {
  const mapRef = useRef<CityMapCanvasHandle>(null);
  // 候補が出たら候補込みの範囲へ寄り、確定・取り消しで候補が消えたら道路の範囲へ戻る
  const fitBbox = unionBbox(bbox, previewPathIds && previewPathIds.length > 0 ? previewBbox : null);
  const fitKey = fitBbox ? fitBbox.join(",") : "";

  // 初期範囲は初回マウント時だけ渡し、以降の変化は fitBounds で追従する(初期化effectを再走させない)。
  // 候補は設定タブで出てから全体図タブを初めて開くことが多いので、初期範囲の時点で候補を含める。
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const initialBounds = useMemo(() => boundsFromBbox(fitBbox) ?? undefined, []);

  useEffect(() => {
    const bounds = boundsFromBbox(fitBbox);
    if (bounds) mapRef.current?.fitBounds(bounds);
    // bbox は取り直しのたびに新しい配列になるので、値の文字列で比較する
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fitKey]);

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
