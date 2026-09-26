/**
 * 建設プロジェクトのタグのプルダウン(値を持つだけの制御コンポーネント)
 *
 * プルダウンで既存タグを選ぶか、「新しいタグ...」から名前を入力して新規作成する。
 * 1オブジェクトにつきタグは1つだけ(複数タグは持たせない)。
 *
 * 候補一覧は自分で取得し、他のウィンドウでタグが変わったら(`tags:changed`)取り直す。
 * 選んだ値の保存は呼び出し側の責務で、詳細パネル(`TagSelect`)は選んだ時点でPATCHし、
 * 建設パネルは設置の確定時に作成リクエストへ載せる。`onChange` が例外を投げたら
 * 保存失敗の表示を出し、入力欄はそのまま残す。
 *
 * 建設パネルで新しく入力したタグは設置するまでバックエンドの候補に無いので、
 * 現在値が候補に無ければその名前の選択肢を足して表示する。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";

import { TAGS_CHANGED_EVENT, fetchTags, type TagSummary } from "../../utils/constructionTags";
import "./TagSelect.css";

/**
 * プルダウンで「新しいタグを作る」を表す番兵値。
 * バックエンドの正規化が連続空白を1つに畳むため、二重空白を含むこの文字列は
 * 実在のタグ名とは決して一致しない。
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

  // 他のウィンドウ(建設プロジェクトのタグ削除・別の詳細パネルでの新規作成など)に追従する。
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
