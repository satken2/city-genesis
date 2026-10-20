/**
 * Construction project tag selection (shared by building/road detail panels)
 *
 * TagPicker owns selection UI; this component only fetches an entity's current value and saves
 * on selection. Tags are backend properties, saved as tag via PATCH /agents/{id} or /paths/{id}.
 * Detail responses omit current tags (absent from generated schemas), so fetch through GET /tags/object.
 *
 * On successful save, emit global tags:changed so open construction project windows refresh lists.
 * React state cannot be shared across those windows.
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

  // Other windows (e.g. construction project tag deletion) may remove this object's tag.
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
