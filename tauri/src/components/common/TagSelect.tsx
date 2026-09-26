/**
 * 建設プロジェクトのタグ選択(建物・道路の詳細パネルで共用)
 *
 * 選択UIは `TagPicker` で、ここは「1件のエンティティの現在値を引き、選んだ時点で保存する」だけを担う。
 *
 * タグの実体はバックエンド側のプロパティで、保存は PATCH /agents/{id} ・ /paths/{id} の `tag`。
 * 現在値は詳細取得のレスポンスに載らない(自動生成スキーマに含めていない)ため、
 * 専用の取得口 GET /tags/object から引く。
 *
 * 保存に成功したらグローバルイベント `tags:changed` を送出し、開いている建設プロジェクト
 * ウィンドウが一覧を取り直す(ウィンドウをまたぐのでReactの状態では共有できない)。
 */

import { useCallback, useEffect, useState } from "react";
import { emit, listen } from "@tauri-apps/api/event";

import { TAGS_CHANGED_EVENT, fetchEntityTag, saveEntityTag } from "../../utils/constructionTags";
import { TagPicker } from "./TagPicker";

type TagSelectProps = {
  apiBaseUrl: string;
  kind: "agent" | "path";
  entityId: number;
};

export function TagSelect({ apiBaseUrl, kind, entityId }: TagSelectProps) {
  const [current, setCurrent] = useState<string>("");

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      const tag = await fetchEntityTag(apiBaseUrl, kind, entityId, signal);
      if (!signal?.aborted) setCurrent(tag);
    },
    [apiBaseUrl, kind, entityId],
  );

  useEffect(() => {
    const controller = new AbortController();
    reload(controller.signal).catch((err) => {
      if (!controller.signal.aborted) console.error("failed to load tag", err);
    });
    return () => controller.abort();
  }, [reload]);

  // 他のウィンドウ(建設プロジェクトのタグ削除など)でこの1件のタグが外れることがある。
  useEffect(() => {
    const unlistenPromise = listen(TAGS_CHANGED_EVENT, () => {
      reload().catch((err) => console.error("failed to reload tag", err));
    });
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [reload]);

  const commit = useCallback(
    async (nextTag: string) => {
      await saveEntityTag(apiBaseUrl, kind, entityId, nextTag);
      setCurrent(nextTag);
      await emit(TAGS_CHANGED_EVENT, { tag: nextTag });
    },
    [apiBaseUrl, kind, entityId],
  );

  return <TagPicker apiBaseUrl={apiBaseUrl} value={current} onChange={commit} />;
}
