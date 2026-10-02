/**
 * 公共サービス充足度メーター(コックピット常駐)
 *
 * 電力・水道といった公共サービスが足りているかを、ウィンドウを開かずに常時見えるようにする小型の縦棒グラフ。
 * 公共施設ウィンドウの需給ドーナツ(budget/PublicFacilitiesWindow の SupplyDonut)と同じ数値を、
 * 一目で逼迫が分かる最小限の情報だけに絞って描く。
 *
 * - 棒1本の枠(グラフエリア)の高さ = 供給キャパシティ、塗りつぶしの高さ = 需要量。
 *   生産量はドーナツと違い描かない(コックピットでは面積が小さく、線を増やすと読めなくなるため)。
 * - 逼迫は色で示す: 需要/キャパシティが 80%以上で黄、100%以上(キャパ超過)で赤。
 * - 文字は一切出さない(棒の下はアイコンのみ)。数値はツールチップで補う。
 * - 棒の隙間を含むウィジェット全体が1つのボタンで、押すと公共施設ウィンドウを開く。
 *
 * 棒の高さは4本とも同じにする(サービスごとに単位が違い、絶対量を横に比べても意味が無いため。
 * 読み取れるのは各サービスの「キャパシティに対してどれだけ需要が迫っているか」だけでよい)。
 *
 * 数値はエポック実行でしか変わらないため、ポーリングは長めの間隔で回し、
 * エポック実行中は止めて実行完了時に即時取得し直す(コックピットのL1通知と同じ方針)。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { COCKPIT_PUBLIC_SERVICE_PRODUCTS } from "../../local-constants";
import {
  PRODUCT_ELECTRICITY,
  PRODUCT_HIGH_ELECTRICITY,
  PRODUCT_HIGH_WATER,
  PRODUCT_WATER_SUPPLY,
} from "../../constants";
import type { PublicServiceSupply, PublicServiceSupplyData } from "../../types";
import "./PublicServiceGauges.css";

/** バックエンドのポーリング間隔(ミリ秒)。需給はエポックでしか変わらないため長めでよい。 */
const POLL_INTERVAL_MS = 30_000;

/** 逼迫と見なす需要/キャパシティ比。ここから黄色にする。 */
const RATIO_WARN = 0.8;
/** キャパシティ超過(需要を賄えていない)と見なす比。ここから赤にする。 */
const RATIO_DANGER = 1.0;

const COLOR_OK = "#22c55e";
const COLOR_WARN = "#eab308";
const COLOR_DANGER = "#ef4444";

/**
 * 製品IDごとのアイコン形状。
 *
 * 専用のアイコン画像がまだ無いため、暫定でインラインSVGのプレースホルダーを描く
 * (稲妻=電力系、水滴=水道系。高圧の2種は同じ図形を二重にして区別する)。
 * 画像を用意したら、この対応表を `<img src={iconUrl("...")} />`(hudCommon.ts の既存パターン)へ
 * 差し替えれば済むよう、製品IDとアイコンの対応はこの1箇所に閉じてある。
 */
const SERVICE_ICON_SHAPE: Record<number, "bolt" | "drop"> = {
  [PRODUCT_ELECTRICITY]: "bolt",
  [PRODUCT_HIGH_ELECTRICITY]: "bolt",
  [PRODUCT_WATER_SUPPLY]: "drop",
  [PRODUCT_HIGH_WATER]: "drop",
};

/** 高圧(インフラ財)の製品。アイコンを二重にして通常の電力・水道と区別する。 */
const HIGH_PRESSURE_PRODUCTS: readonly number[] = [
  PRODUCT_HIGH_ELECTRICITY,
  PRODUCT_HIGH_WATER,
];

/** 稲妻・水滴のパス定義(16x16のビューボックス内に収める) */
const ICON_PATHS: Record<"bolt" | "drop", string> = {
  bolt: "M9.5 1 3.5 9.5h3.2L6 15l6.2-8.8H8.9L9.5 1Z",
  drop: "M8 1.5c3 3.6 4.6 6.1 4.6 8.1a4.6 4.6 0 1 1-9.2 0c0-2 1.6-4.5 4.6-8.1Z",
};

/**
 * 公共サービスのアイコン(プレースホルダー)。
 * 高圧の製品は同じ図形をひと回り小さくして2つ並べ、通常版と一目で区別できるようにする。
 */
function ServiceIcon({ productId }: { productId: number }) {
  const shape = SERVICE_ICON_SHAPE[productId] ?? "bolt";
  const isHigh = HIGH_PRESSURE_PRODUCTS.includes(productId);
  const path = ICON_PATHS[shape];

  return (
    <svg className="psg-icon" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      {isHigh ? (
        // 0.62倍に縮めた図形を左右に2つ並べる。translate は縮小後の座標系での平行移動なので、
        // 図形の実寸(x が概ね 2.1〜7.8、y が 0.6〜9.3)を 16x16 の中央へ収まるよう寄せている。
        <>
          <path d={path} transform="translate(-0.2 3.1) scale(0.62)" />
          <path d={path} transform="translate(6.3 3.1) scale(0.62)" />
        </>
      ) : (
        <path d={path} />
      )}
    </svg>
  );
}

/**
 * 需要/キャパシティ比を求める。
 * キャパシティが無いのに需要がある状態は「まったく賄えていない」ので満杯(1.0)扱いにする。
 */
function demandRatio(supply: PublicServiceSupply | undefined): number {
  if (!supply) return 0;
  if (supply.supply_capacity > 0) return supply.demand / supply.supply_capacity;
  return supply.demand > 0 ? 1 : 0;
}

function ratioColor(ratio: number): string {
  if (ratio >= RATIO_DANGER) return COLOR_DANGER;
  if (ratio >= RATIO_WARN) return COLOR_WARN;
  return COLOR_OK;
}

export function PublicServiceGauges({
  apiBaseUrl,
  epochExecuting,
  onOpen,
}: {
  apiBaseUrl: string;
  epochExecuting: boolean;
  onOpen: () => void;
}) {
  const { t } = useTranslation();
  // 製品IDをキーにした需給。未取得のうちは空で、棒は塗りの無い枠だけになる。
  const [supplies, setSupplies] = useState<Record<number, PublicServiceSupply>>({});

  useEffect(() => {
    if (!apiBaseUrl || epochExecuting) return;
    const controller = new AbortController();
    let cancelled = false;

    async function refresh() {
      try {
        const res = await fetchApi<PublicServiceSupplyData>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/public-service-supply"),
          { signal: controller.signal },
        );
        if (cancelled) return;
        const map: Record<number, PublicServiceSupply> = {};
        for (const row of res.services) {
          map[row.product_id] = row;
        }
        setSupplies(map);
      } catch {
        // 取得失敗(中断含む)は前回値を保持し、画面をちらつかせない(ScoreMiniMeterと同方針)。
      }
    }

    void refresh();
    const timer = setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      clearInterval(timer);
    };
  }, [apiBaseUrl, epochExecuting]);

  return (
    <button
      type="button"
      className="psg"
      data-guide="hud.publicServices"
      title={t("publicServiceGauge.tooltip")}
      onClick={onOpen}
    >
      {COCKPIT_PUBLIC_SERVICE_PRODUCTS.map((productId) => {
        const supply = supplies[productId];
        const ratio = demandRatio(supply);
        const name = t(`publicServiceGauge.services.${productId}`);
        return (
          <span
            key={productId}
            className="psg-col"
            title={
              supply
                ? `${name} ${Math.round(ratio * 100)}%`
                : name
            }
          >
            <span className="psg-track">
              <span
                className="psg-fill"
                style={{
                  height: `${Math.min(ratio, 1) * 100}%`,
                  background: ratioColor(ratio),
                }}
              />
            </span>
            <ServiceIcon productId={productId} />
          </span>
        );
      })}
    </button>
  );
}
