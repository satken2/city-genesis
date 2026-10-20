/**
 * Construction project tag dropdown (controlled value component)
 *
 * Choose an existing tag or enter one through "New tag...".
 * Each object has only one tag, never multiple tags.
 *
 * Fetch options internally; refetch when other windows emit `tags:changed`.
 * The caller saves selections: details (`TagSelect`) PATCH immediately, while construction
 * panels include the tag in the creation request on placement confirmation. If onChange throws,
 * show a save failure and retain the input.
 *
 * A new construction tag is absent from backend options until placement;
 * add an option for the current value whenever it is missing from the fetched list.
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";

import { TAGS_CHANGED_EVENT, fetchTags, type TagSummary } from "../../utils/constructionTags";
import "./TagSelect.css";

/**
 * Sentinel representing "Create new tag" in the dropdown.
 * Backend normalization collapses consecutive spaces, so this double-space string can never
 * match an actual tag name.
 */
const NEW_TAG_OPTION = "new  tag";

type TagPickerProps = {
  apiBaseUrl: string;
  value: string;
  onChange: (tag: string) => void | Promise<void>;
  disabled?: boolean;
};

export function TagPicker({ apiBaseUrl, value, onChange, disabled = false }: TagPickerProps) {
  const { t } = useTranslation();
  const [tags, setTags] = useState<TagSummary[]>([]);
  const [creating, setCreating] = useState(false);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      const list = await fetchTags(apiBaseUrl, signal);
      if (!signal?.aborted) setTags(list);
    },
    [apiBaseUrl],
  );

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    reload(controller.signal).catch((err) => {
      if (!controller.signal.aborted) console.error("failed to load tags", err);
    });
    return () => controller.abort();
  }, [apiBaseUrl, reload]);

  // Follow changes from other windows (project tag deletion, tag creation in another detail panel, etc.).
  useEffect(() => {
    const unlistenPromise = listen(TAGS_CHANGED_EVENT, () => {
      reload().catch((err) => console.error("failed to reload tags", err));
    });
    return () => {
      void unlistenPromise.then((unlisten) => unlisten());
    };
  }, [reload]);

  const submit = async (nextTag: string) => {
    setBusy(true);
    setFailed(false);
    try {
      await onChange(nextTag);
      setCreating(false);
      setInput("");
    } catch (err) {
      console.error("failed to save tag", err);
      setFailed(true);
    } finally {
      setBusy(false);
    }
  };

  const handleSelect = (next: string) => {
    if (next === NEW_TAG_OPTION) {
      setCreating(true);
      setInput("");
      return;
    }
    void submit(next);
  };

  const locked = disabled || busy;

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
          disabled={locked}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && input.trim()) void submit(input.trim());
            if (e.key === "Escape") setCreating(false);
          }}
        />
        <button
          className="tag-select__btn"
          type="button"
          disabled={locked || !input.trim()}
          onClick={() => void submit(input.trim())}
        >
          {t("tag.add")}
        </button>
        <button
          className="tag-select__btn"
          type="button"
          disabled={locked}
          onClick={() => setCreating(false)}
        >
          {t("tag.cancel")}
        </button>
        {failed && <span className="tag-select__error">{t("tag.saveFailed")}</span>}
      </div>
    );
  }

  const valueListed = !value || tags.some((tag) => tag.name === value);

  return (
    <div className="tag-select">
      <select
        className="tag-select__select"
        value={value}
        disabled={locked}
        onChange={(e) => handleSelect(e.target.value)}
      >
        <option value="">{t("tag.none")}</option>
        {tags.map((tag) => (
          <option key={tag.name} value={tag.name}>
            {tag.name}
          </option>
        ))}
        {!valueListed && <option value={value}>{value}</option>}
        <option value={NEW_TAG_OPTION}>{t("tag.createNew")}</option>
      </select>
      {failed && <span className="tag-select__error">{t("tag.saveFailed")}</span>}
    </div>
  );
}
