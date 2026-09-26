/**
 * 道路建設パネルの「所属道路」の状態
 *
 * 新しく敷く街路・高速道路を、既存の道路(名前付き道路)・新しい道路・どれにも属さない(名もなき道)の
 * どれにするかを持つ。既存の道路は確定送信の各パスに road_id として載せ、新しい道路は確定で
 * 作成されたパスIDの一覧で道路を作る(区間の無い道路は存在できないため、作成は敷設の後になる)。
 *
 * 延長の提案: 敷設中の形状の始点・終点を POST /api/v1/roads/extension-candidates に問い合わせ、
 * その位置に端を持つ区間のうち同じ道路のものがちょうど1本(=道路の端)なら、その道路の延長として
 * 提案される。ユーザーがまだ選択を触っていなければ最初の候補を自動で選ぶ。端点が変わったときだけ
 * 問い合わせる(中間の形状の編集では問い合わせない)。
 */

import { useCallback, useEffect, useRef, useState } from "react";
import { createRoad, fetchRoadExtensionCandidates, notifyRoadsChanged } from "../road/roadApi";
import type { RoadExtensionCandidate } from "../road/roadTypes";
import { useRoadList, type RoadChoice } from "../road/RoadMembershipRow";

/** 端点の座標がこの幅(m)の格子で同じなら問い合わせ直さない。バックエンドの端点一致の許容(1m)に合わせる */
const ENDPOINT_KEY_GRID_M = 1;
/** 形状の更新が落ち着いてから問い合わせるまでの待ち時間(ms) */
const CANDIDATE_DEBOUNCE_MS = 250;

export function usePathRoadAssignment(apiBaseUrl: string | undefined, enabled: boolean, rawPoints: number[][]) {
  const { roads } = useRoadList(apiBaseUrl, enabled);
  const [choice, setChoiceState] = useState<RoadChoice>({ kind: "none" });
  const [candidates, setCandidates] = useState<RoadExtensionCandidate[]>([]);
  // ユーザーが自分で選び直したか。選び直した後は候補で上書きしない。
  const touchedRef = useRef(false);

  const setChoice = useCallback((next: RoadChoice) => {
    touchedRef.current = true;
    setChoiceState(next);
  }, []);

  const first = rawPoints[0];
  const last = rawPoints[rawPoints.length - 1];
  const keyOf = (p: number[] | undefined) =>
    p ? `${Math.round(p[0] / ENDPOINT_KEY_GRID_M)},${Math.round(p[p.length - 1] / ENDPOINT_KEY_GRID_M)}` : "";
  const endpointKey = enabled && rawPoints.length >= 2 ? `${keyOf(first)}|${keyOf(last)}` : "";

  useEffect(() => {
    if (!apiBaseUrl || !endpointKey || !first || !last) {
      setCandidates([]);
      if (!touchedRef.current) setChoiceState({ kind: "none" });
      return;
    }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      fetchRoadExtensionCandidates(apiBaseUrl, [first, last], controller.signal)
        .then((data) => {
          if (controller.signal.aborted) return;
          const found = data.candidates ?? [];
          setCandidates(found);
          if (!touchedRef.current) {
            setChoiceState(found.length > 0 ? { kind: "road", roadId: found[0].road_id } : { kind: "none" });
          }
        })
        .catch((err) => {
          if (!controller.signal.aborted) console.error("failed to fetch road extension candidates", err);
        });
    }, CANDIDATE_DEBOUNCE_MS);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
    // 端点は endpointKey に畳んでいるので、配列の同一性の変化では問い合わせ直さない。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [apiBaseUrl, endpointKey]);

  /** 選択中の道路が延長の候補ならその候補(「〇〇を延長します」の表示に使う) */
  const extending = choice.kind === "road" ? candidates.find((c) => c.road_id === choice.roadId) ?? null : null;

  /** 確定送信の各パスへ足すフィールド */
  const postFields: Record<string, unknown> | undefined =
    enabled && choice.kind === "road" ? { road_id: choice.roadId } : undefined;

  /**
   * 確定送信の成功後に呼ぶ。新しい道路なら作成されたパスで道路を作り、道路の変更を知らせる。
   * 次の1本は提案からやり直す(続けて敷く区間は、いま作った道路の端から延長として提案される)。
   */
  const finalize = useCallback(
    async (createdPathIds: number[]): Promise<void> => {
      if (!enabled || !apiBaseUrl) return;
      const current = choice;
      touchedRef.current = false;
      setChoiceState({ kind: "none" });
      setCandidates([]);
      if (current.kind === "new" && createdPathIds.length > 0) {
        await createRoad(apiBaseUrl, { name: current.name.trim(), path_ids: createdPathIds });
        await notifyRoadsChanged(undefined, "path_build");
      } else if (current.kind === "road") {
        await notifyRoadsChanged(current.roadId, "path_build");
      }
    },
    [enabled, apiBaseUrl, choice],
  );

  return { roads, choice, setChoice, candidates, extending, postFields, finalize };
}

export type PathRoadAssignment = ReturnType<typeof usePathRoadAssignment>;
