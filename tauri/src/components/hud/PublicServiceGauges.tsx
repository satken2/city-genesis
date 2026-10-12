/**
 * 公共サービス充足度メーター(コックピット常駐)。
 * 電力・水道を各1本で表示し、サービスと高圧財のうち小さい方の容量を棒の上限にする。
 * 高圧財はサービス側の単位に揃え、サービスの需要量を緑で下端から塗る。
 * 生産実績や高圧財の需要は使わない。上限を超える需要は満杯の棒とツールチップの比率で示す。
 * 単位の異なるサービス同士の絶対量は比較せず、棒の高さは2本とも同じにする。
 * ウィジェット全体が1つのボタンで、押すと公共施設ウィンドウを開く。
 * 数値は長めの間隔で取得し、シミュレーション中は停止して完了時に取得し直す。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { COCKPIT_PUBLIC_SERVICE_PRODUCTS } from "../../local-constants";
import {
  PRODUCT_ELECTRICITY,
  PRODUCT_WATER_SUPPLY,
} from "../../constants";
import type { PublicServiceSupply, PublicServiceSupplyData } from "../../types";
import { getPublicServiceCapacity } from "../budget/publicServiceInfrastructure";
import "./PublicServiceGauges.css";

/** バックエンドのポーリング間隔(ミリ秒)。需給はエポックでしか変わらないため長めでよい。 */
const POLL_INTERVAL_MS = 30_000;

/** 製品IDごとのアイコン形状。 */
const SERVICE_ICON_SHAPE: Record<number, "bolt" | "drop"> = {
  [PRODUCT_ELECTRICITY]: "bolt",
  [PRODUCT_WATER_SUPPLY]: "drop",
};

/** 稲妻・水滴のパス定義(16x16のビューボックス内に収める) */
const ICON_PATHS: Record<"bolt" | "drop", string> = {
  bolt: "M9.5 1 3.5 9.5h3.2L6 15l6.2-8.8H8.9L9.5 1Z",
  drop: "M8 1.5c3 3.6 4.6 6.1 4.6 8.1a4.6 4.6 0 1 1-9.2 0c0-2 1.6-4.5 4.6-8.1Z",
};

function ServiceIcon({ productId }: { productId: number }) {
  const shape = SERVICE_ICON_SHAPE[productId] ?? "bolt";
  const path = ICON_PATHS[shape];

  return (
    <svg className="psg-icon" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
      <path d={path} />
    </svg>
  );
}

/**
 * 需要/キャパシティ比を求める。
 * キャパシティが無いのに需要がある状態は「まったく賄えていない」ので満杯(1.0)扱いにする。
 */
function demandRatio(supply: PublicServiceSupply | undefined, capacity: number): number {
  if (!supply) return 0;
  if (capacity > 0) return supply.demand / capacity;
  return supply.demand > 0 ? 1 : 0;
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
        const ratio = demandRatio(supply, getPublicServiceCapacity(productId, supplies));
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
