/**
 * 路線地図(実地図上の路線)
 *
 * 路線詳細パネル(TransitDetailPanel)に埋め込む、1路線を主役にした地図。
 * 共通の都市2Dマップ部品 CityMapCanvas を `interactive={false}` で埋め込み、
 * 公共交通路線レイヤー(transitLines)を ON にしたうえで `transitFocusLineId` に
 * 当該路線を渡す。路線の帯(実際の下地道路・線路に沿った走行形状)・進行方向矢印・
 * 停留所リング(始点=緑・終点=赤)・運休の破線表示・周辺の他路線の薄表示は
 * すべてレイヤー側(map/transitLineDraw.ts)の描画規則で、このコンポーネントは
 * 独自の描画を持たない。周辺の道路・線路・区画・建物も CityMapCanvas のレイヤーが描く。
 *
 * 表示範囲は停留所の外接矩形に余白を付けてフィットさせる。停留所は路線詳細APIの
 * `stops` を使う(走行形状の取得はレイヤーが GET /api/v1/transit/lines/geometry で行う)。
 * 設定保存後の路線カラー変更は "transit:lines_changed" をレイヤーが受けて取り直す。
 */

import { useEffect, useMemo, useRef } from "react";
import {
  CityMapCanvas,
  type CityMapCanvasHandle,
  type CityMapBounds,
  type CityMapLayerVisibility,
} from "../map/CityMapCanvas";
import type { TransitStop } from "./TransitDetailPanel";
import "./TransitLineMap.css";

/** 停留所外接矩形に加える余白(ワールドm)。単一停留所でも一定範囲が見えるようにする */
const FIT_MARGIN = 60;

type TransitLineMapProps = {
  apiBaseUrl: string;
  /** 主役にする路線の line_agent_id */
  lineAgentId: number;
  /** 表示範囲の決定に使う停留所(路線詳細APIの stops) */
  stops: TransitStop[];
};

/**
 * 路線地図で表示するレイヤー。周辺の道路(幹線含む)・線路・土地区画・建物は表示して
 * 路線の地理的文脈が分かるようにする一方、標高色分け・等高線・グリッド・地区枠線は
 * 路線を見やすくするため非表示にする。海岸線は位置把握の参考として残す。
 */
const MAP_LAYERS: Partial<CityMapLayerVisibility> = {
  coastline: true,
  trunkRoads: true,
  allRoads: true,
  railways: true,
  transitLines: true,
  parcels: true,
  buildings: true,
  stations: true,
  zoneBorders: false,
  elevation: false,
  contours: false,
  grid: false,
};

/** 停留所配列を包含するワールド座標範囲(余白付き)を求める。空なら null */
function computeStopsBounds(stops: TransitStop[]): CityMapBounds | null {
  if (stops.length === 0) return null;
  let minX = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxZ = -Infinity;
  for (const s of stops) {
    const x = s.position[0];
    const z = s.position[2];
    if (x < minX) minX = x;
    if (z < minZ) minZ = z;
    if (x > maxX) maxX = x;
    if (z > maxZ) maxZ = z;
  }
  return {
    x1: minX - FIT_MARGIN,
    z1: minZ - FIT_MARGIN,
    x2: maxX + FIT_MARGIN,
    z2: maxZ + FIT_MARGIN,
  };
}

export function TransitLineMap({ apiBaseUrl, lineAgentId, stops }: TransitLineMapProps) {
  const mapRef = useRef<CityMapCanvasHandle>(null);

  // 初回マウント時のフィット範囲。以降のデータ変更は fitBounds() で追従するため、
  // この値は変えず CityMapCanvas の初期化effectを再走させない(意図的に空依存)。
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const initialBounds = useMemo(() => computeStopsBounds(stops) ?? undefined, []);

  /* ---- 停留所が変わったらビューを追従させる ---- */
  useEffect(() => {
    const bounds = computeStopsBounds(stops);
    if (bounds) {
      mapRef.current?.fitBounds(bounds);
    }
  }, [stops]);

  return (
    <div className="transit-line-map">
      <CityMapCanvas
        ref={mapRef}
        apiBaseUrl={apiBaseUrl}
        layers={MAP_LAYERS}
        transitFocusLineId={lineAgentId}
        initialBounds={initialBounds}
        interactive={false}
        className="transit-line-map__canvas"
      />
    </div>
  );
}
