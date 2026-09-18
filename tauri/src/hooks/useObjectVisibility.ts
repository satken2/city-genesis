/**
 * 建物・道路の「オブジェクト単位の表示・非表示」を扱う共通フック。
 *
 * 表示状態の正はUnity側(ObjectVisibilityFilter)にあり、バックエンドは一切保持しない
 * (セーブにも残らない=ゲームを開き直すと全部表示に戻る)。UIは非表示IDの全集合を
 * `view:set_object_filter` で送り、現在値は `view:object_filter_state` で受け取る。
 * 表示オプションパネル(レイヤー単位)が `view:query_state` でUnityに問い合わせるのと同じ形。
 *
 * 建設プロジェクトウィンドウ(タグ単位・行単位の切替)と建物/道路の詳細パネル(個別の切替)が
 * 共用する。どのウィンドウから変えてもUnityが全ウィンドウへ現在値を返すため、表示が揃う。
 */

import { useCallback, useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";

import {
  IPC_EVENT_VIEW_OBJECT_FILTER_STATE,
  sendQueryObjectFilter,
  sendSetObjectFilter,
} from "../utils/ipc";

export type ObjectKind = "agent" | "path";

type ObjectFilterStatePayload = {
  hidden_building_ids?: number[];
  hidden_path_ids?: number[];
};

export function useObjectVisibility(windowType: string) {
  const [hiddenBuildings, setHiddenBuildings] = useState<Set<number>>(new Set());
  const [hiddenPaths, setHiddenPaths] = useState<Set<number>>(new Set());

  // リスナー登録を待ってから問い合わせる(応答を取りこぼさないため)。
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    let cancelled = false;

    const setup = async () => {
      unlisten = await listen<ObjectFilterStatePayload>(
        IPC_EVENT_VIEW_OBJECT_FILTER_STATE,
        (event) => {
          setHiddenBuildings(new Set(event.payload?.hidden_building_ids ?? []));
          setHiddenPaths(new Set(event.payload?.hidden_path_ids ?? []));
        },
      );
      if (cancelled) {
        unlisten();
        unlisten = undefined;
        return;
      }
      await sendQueryObjectFilter(windowType);
    };

    setup().catch((err) => {
      console.warn("[useObjectVisibility] setup failed:", err);
    });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [windowType]);

  /**
   * 非表示集合を送信する。楽観的にローカル状態も更新するが、正はUnityから返る
   * `view:object_filter_state` なので、失敗しても次の応答で必ず整合する。
   */
  const applyHidden = useCallback(
    (buildings: Set<number>, paths: Set<number>) => {
      setHiddenBuildings(buildings);
      setHiddenPaths(paths);
      sendSetObjectFilter(Array.from(buildings), Array.from(paths), windowType).catch((err) => {
        console.error("[useObjectVisibility] sendSetObjectFilter failed:", err);
      });
    },
    [windowType],
  );

  /** 指定したオブジェクトが非表示か。 */
  const isHidden = useCallback(
    (kind: ObjectKind, id: number) =>
      kind === "path" ? hiddenPaths.has(id) : hiddenBuildings.has(id),
    [hiddenBuildings, hiddenPaths],
  );

  /** 指定したオブジェクトの表示・非表示を反転する。 */
  const toggleObject = useCallback(
    (kind: ObjectKind, id: number) => {
      const buildings = new Set(hiddenBuildings);
      const paths = new Set(hiddenPaths);
      const target = kind === "path" ? paths : buildings;
      if (target.has(id)) target.delete(id); else target.add(id);
      applyHidden(buildings, paths);
    },
    [applyHidden, hiddenBuildings, hiddenPaths],
  );

  /** 複数オブジェクトをまとめて表示/非表示にする(タグ単位の切替で使う)。 */
  const setObjectsHidden = useCallback(
    (targets: { kind: ObjectKind; id: number }[], hidden: boolean) => {
      const buildings = new Set(hiddenBuildings);
      const paths = new Set(hiddenPaths);
      for (const target of targets) {
        const set = target.kind === "path" ? paths : buildings;
        if (hidden) set.add(target.id); else set.delete(target.id);
      }
      applyHidden(buildings, paths);
    },
    [applyHidden, hiddenBuildings, hiddenPaths],
  );

  /** すべて表示に戻す。 */
  const showAll = useCallback(() => {
    applyHidden(new Set(), new Set());
  }, [applyHidden]);

  return {
    hiddenBuildings,
    hiddenPaths,
    hiddenCount: hiddenBuildings.size + hiddenPaths.size,
    isHidden,
    toggleObject,
    setObjectsHidden,
    showAll,
  };
}
