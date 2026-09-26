/**
 * 路線の収支見込み(必要車両数・運営原価・想定乗車率での運賃収入・収支)を取得するフック。
 *
 * 算出はバックエンド(POST /api/v1/transit/lines/estimate・/lines/{id}/estimate)に一元化されており、
 * 編成数・便数・輸送容量はエポック実行と同じ式で求まる。UI 側では計算しない。
 * 路線の新設・経路編集パネル(Unity で置いた巡回点)と路線詳細の設定タブ(既存路線の編集中の値)が共用する。
 *
 * 巡回点の追加やステッパーの連打のたびにバックエンドが下地経路を計算し直さないよう、入力が
 * ESTIMATE_DEBOUNCE_MS だけ落ち着いてから取得し、古い要求は中断する。取得中も直前の結果を残し、
 * 表示が空になって跳ねるのを防ぐ。request が null の間は取得せず結果を消す。
 */

import { useEffect, useState } from "react";
import { buildApiUrl, fetchApi } from "../utils/api";
import type { TransitLineEstimate, TransitRoutePoint } from "../types";

export type TransitEstimateRequest =
  | {
      kind: "line";
      lineAgentId: number;
      farePrice: number;
      targetHeadway: number;
      carsPerTrain: number;
    }
  | {
      kind: "planned";
      /** 路線種別(transit_master の transit_path_id) */
      pathVariant: number;
      /** 巡回点(停留所・経由点を巡回順に並べたもの) */
      points: TransitRoutePoint[];
      /** 経路を編集中の路線。線路容量の判定でその路線の現在の便数を合算から外させる(新設は省略) */
      editedLineId?: number;
      farePrice: number;
      targetHeadway: number;
      carsPerTrain: number;
    };

const ESTIMATE_DEBOUNCE_MS = 300;

export function useTransitEstimate(
  apiBaseUrl: string,
  request: TransitEstimateRequest | null,
  refreshKey = 0,
): { estimate: TransitLineEstimate | null; loading: boolean; error: string | null } {
  const [estimate, setEstimate] = useState<TransitLineEstimate | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // 呼び出し側が毎描画で作り直すオブジェクトではなく、内容が変わったときだけ取り直す。
  const requestKey = request ? JSON.stringify(request) : null;

  useEffect(() => {
    if (requestKey === null) {
      setEstimate(null);
      setLoading(false);
      setError(null);
      return;
    }
    const req = JSON.parse(requestKey) as TransitEstimateRequest;
    const controller = new AbortController();
    setLoading(true);
    const timer = window.setTimeout(() => {
      const settings = {
        fare_price: req.farePrice,
        target_headway: req.targetHeadway,
        cars_per_train: req.carsPerTrain,
      };
      const [path, body] =
        req.kind === "line"
          ? [`/api/v1/transit/lines/${req.lineAgentId}/estimate`, settings]
          : ["/api/v1/transit/lines/estimate", {
              ...settings,
              path_variant: req.pathVariant,
              points: req.points,
              ...(req.editedLineId != null ? { line_agent_id: req.editedLineId } : {}),
            }];
      fetchApi<TransitLineEstimate>(buildApiUrl(apiBaseUrl, path), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal: controller.signal,
      })
        .then((data) => {
          if (controller.signal.aborted) return;
          setEstimate(data);
          setError(null);
        })
        .catch((err: unknown) => {
          if (controller.signal.aborted) return;
          setEstimate(null);
          setError(err instanceof Error ? err.message : String(err));
        })
        .finally(() => {
          if (!controller.signal.aborted) setLoading(false);
        });
    }, ESTIMATE_DEBOUNCE_MS);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [apiBaseUrl, requestKey, refreshKey]);

  return { estimate, loading, error };
}
