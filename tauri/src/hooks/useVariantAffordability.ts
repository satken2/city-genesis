/**
 * 建設パネルの「バリアント単位の資金目安」を取得するフック。
 *
 * バックエンドの GET /api/v1/paths/variant-affordability または
 * GET /api/v1/buildings/variant-affordability を呼び、全バリアントの基準費用
 * (道路=平地・基準延長、建物=最小規模)と現在の利用可能額での可否をまとめて受け取る。
 * 建設パネルはこれを使って、今の資金では明らかに建てられない規格・施設を
 * 選ばせない(先回りの目安。実際の発注可否はバックエンドが形状の総額で審査する)。
 *
 * 取得タイミング: マウント時、refresh() の明示呼び出し(確定成功後)、エポック実行の終了
 * (epoch_confirm ウィンドウが送る "epoch:executing" の executing=false)。
 * テンプレートモード(神モード)では unlimited=true が返り、全バリアントが有効になる。
 */

import { useCallback, useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";

export type VariantAffordabilityEntry = {
  /** 基準費用(基準延長の平地道路 / 最小規模の建物) */
  referenceCost: number;
  /** 利用可能額で基準費用を賄えるか */
  affordable: boolean;
  /** 不足額(0以上) */
  fundsShortfall: number;
};

export type VariantAffordability = {
  /** 発注に使える市の資金。unlimited のときは null */
  availableFunds: number | null;
  /** 神モードなど資金審査なしのとき true */
  unlimited: boolean;
  /** バリアントID → 可否 */
  variants: Record<number, VariantAffordabilityEntry>;
};

type ApiResponse = {
  available_funds: number | null;
  unlimited: boolean;
  reference_length: number;
  variants: Record<string, { reference_cost: number; affordable: boolean; funds_shortfall: number }>;
};

export function useVariantAffordability(
  kind: "paths" | "buildings",
  apiBaseUrl: string | undefined,
): { affordability: VariantAffordability | null; refresh: () => void } {
  const [affordability, setAffordability] = useState<VariantAffordability | null>(null);
  const [tick, setTick] = useState(0);

  const refresh = useCallback(() => setTick((n) => n + 1), []);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    const base = apiBaseUrl.replace(/\/+$/, "");
    fetch(`${base}/api/v1/${kind}/variant-affordability`, { signal: controller.signal })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<ApiResponse>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        const variants: Record<number, VariantAffordabilityEntry> = {};
        for (const [id, v] of Object.entries(data.variants ?? {})) {
          variants[parseInt(id)] = {
            referenceCost: v.reference_cost,
            affordable: v.affordable,
            fundsShortfall: v.funds_shortfall,
          };
        }
        setAffordability({
          availableFunds: data.available_funds ?? null,
          unlimited: data.unlimited === true,
          variants,
        });
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          // 取得できないときは目安を出さない(発注時審査はバックエンドが行うため安全側)
          console.error("[useVariantAffordability] fetch failed:", err);
          setAffordability(null);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl, kind, tick]);

  // エポック実行が終わると市の現金が変わるため再取得する
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    getCurrentWindow()
      .listen<{ executing: boolean }>("epoch:executing", (event) => {
        if (!event.payload.executing) refresh();
      })
      .then((fn) => { unlisten = fn; })
      .catch(() => {});
    return () => { unlisten?.(); };
  }, [refresh]);

  return { affordability, refresh };
}
