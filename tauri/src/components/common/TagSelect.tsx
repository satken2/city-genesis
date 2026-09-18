/**
 * 建設プロジェクトのタグ選択(建物・道路の詳細パネルで共用)
 *
 * プルダウンで既存タグを選ぶか、「新しいタグ...」から名前を入力して新規作成する。
 * 1オブジェクトにつきタグは1つだけ(複数タグは持たせない)。
 *
 * タグの実体はバックエンド側のプロパティで、保存は PATCH /agents/{id} ・ /paths/{id} の `tag`。
 * 現在値は詳細取得のレスポンスに載らない(自動生成スキーマに含めていない)ため、
 * 専用の取得口 GET /tags/object から引く。
 *
 * 保存に成功したらグローバルイベント `tags:changed` を送出し、開いている建設プロジェクト
 * ウィンドウが一覧を取り直す(ウィンドウをまたぐのでReactの状態では共有できない)。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit, listen } from "@tauri-apps/api/event";

import {
  TAGS_CHANGED_EVENT,
  fetchEntityTag,
  fetchTags,
  saveEntityTag,
  type TagSummary,
} from "../../utils/constructionTags";
import "./TagSelect.css";

/**
 * プルダウンで「新しいタグを作る」を表す番兵値。
 * バックエンドの正規化が連続空白を1つに畳むため、二重空白を含むこの文字列は
 * 実在のタグ名とは決して一致しない。
 */
const NEW_TAG_OPTION = "new  tag";

type TagSelectProps = {
  apiBaseUrl: string;
  kind: "agent" | "path";
  entityId: number;
};

export function TagSelect({ apiBaseUrl, kind, entityId }: TagSelectProps) {
  const { t } = useTranslation();
  const [tags, setTags] = useState<TagSummary[]>([]);
  const [current, setCurrent] = useState<string>("");
  const [creating, setCreating] = useState(false);
  const [input, setInput] = useState("");
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState(false);

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      const [list, tag] = await Promise.all([
        fetchTags(apiBaseUrl, signal),
        fetchEntityTag(apiBaseUrl, kind, entityId, signal),
      ]);
      if (signal?.aborted) return;
      setTags(list);
      setCurrent(tag);
    },
    [apiBaseUrl, kind, entityId],
  );

  useEffect(() => {
    const controller = new AbortController();
    reload(controller.signal).catch((err) => {
      if (!controller.signal.aborted) console.error("failed to load tags", err);
    });
    return () => controller.abort();
  }, [reload]);

  // 他のウィンドウ(建設プロジェクトのタグ削除など)でタグが変わったら選択肢を取り直す。
  useEffect(() => {
    const unlistenPromise = listen(TAGS_CHANGED_EVENT, () => {
      reload().catch((err) => console.error("failed to reload tags", err));
    });
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [reload]);

  const commit = useCallback(
    async (nextTag: string) => {
      setSaving(true);
      setFailed(false);
      try {
        await saveEntityTag(apiBaseUrl, kind, entityId, nextTag);
        setCurrent(nextTag);
        setCreating(false);
        setInput("");
        await emit(TAGS_CHANGED_EVENT, { tag: nextTag });
        await reload();
      } catch (err) {
        console.error("failed to save tag", err);
        setFailed(true);
      } finally {
        setSaving(false);
      }
    },
    [apiBaseUrl, kind, entityId, reload],
  );

  const handleSelect = (value: string) => {
    if (value === NEW_TAG_OPTION) {
      setCreating(true);
      setInput("");
      return;
    }
    void commit(value);
  };

  if (creating) {
    return (
      <div className="tag-select">
        <input
          className="tag-select__input"
          type="text"
          value={input}
          autoFocus
          maxLength={32}
          placeholder={t("tag.newPlaceholder")}
          disabled={saving}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && input.trim()) void commit(input.trim());
            if (e.key === "Escape") setCreating(false);
          }}
        />
        <button
          className="tag-select__btn"
          type="button"
          disabled={saving || !input.trim()}
          onClick={() => void commit(input.trim())}
        >
          {t("tag.add")}
        </button>
        <button
          className="tag-select__btn"
          type="button"
          disabled={saving}
          onClick={() => setCreating(false)}
        >
          {t("tag.cancel")}
        </button>
        {failed && <span className="tag-select__error">{t("tag.saveFailed")}</span>}
      </div>
    );
  }

  return (
    <div className="tag-select">
      <select
        className="tag-select__select"
        value={current}
        disabled={saving}
        onChange={(e) => handleSelect(e.target.value)}
      >
        <option value="">{t("tag.none")}</option>
        {tags.map((tag) => (
          <option key={tag.name} value={tag.name}>
            {tag.name}
          </option>
        ))}
        <option value={NEW_TAG_OPTION}>{t("tag.createNew")}</option>
      </select>
      {failed && <span className="tag-select__error">{t("tag.saveFailed")}</span>}
    </div>
  );
}
