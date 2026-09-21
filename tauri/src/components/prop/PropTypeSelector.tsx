import { useState } from "react";
import { useTranslation } from "react-i18next";
import categories from "../../data/propCategories.json";
import type { PropMasterEntry } from "../../types";

const thumbnails = import.meta.glob<string>("../../assets/images/props/*.png", {
  eager: true,
  query: "?url",
  import: "default",
});
const categoryByType = new Map(categories.flatMap(({ id, types }) => types.map((type) => [type, id] as const)));

function PropThumbnail({ id }: { id: number }) {
  const { t } = useTranslation();
  const [failed, setFailed] = useState(false);
  const url = thumbnails[`../../assets/images/props/${id}.png`];
  return url && !failed ? (
    <img src={url} alt="" loading="lazy" draggable={false} onError={() => setFailed(true)} />
  ) : (
    <span className="pp-thumbnail-missing">{t("prop_placement.noThumbnail")}</span>
  );
}

export function PropTypeSelector({ entries, selectedType, onSelect, disabled }: {
  entries: [string, PropMasterEntry][];
  selectedType: number | null;
  onSelect: (id: number) => void;
  disabled: boolean;
}) {
  const { t } = useTranslation();
  const [category, setCategory] = useState("all");
  const categoryOf = (id: string) => categoryByType.get(Number(id)) ?? "other";
  const availableCategories = [...categories.map(({ id }) => id), "other"]
    .filter((id) => entries.some(([type]) => categoryOf(type) === id));
  const activeCategory = availableCategories.includes(category) ? category : "all";
  const visible = entries.filter(([id]) => activeCategory === "all" || categoryOf(id) === activeCategory);
  const selected = entries.find(([id]) => Number(id) === selectedType)?.[1];

  return (
    <>
      <label className="pc-section-title" htmlFor="prop-category">{t("prop_placement.category")}</label>
      <select id="prop-category" className="pc-select" value={activeCategory}
        onChange={(event) => setCategory(event.target.value)} disabled={disabled}>
        {["all", ...availableCategories].map((id) => (
          <option key={id} value={id}>{t(`prop_placement.categories.${id}`)}</option>
        ))}
      </select>
      <div className="pp-type-grid" role="group" aria-label={t("prop_placement.selectType")}>
        {visible.map(([id, entry]) => (
          <button key={id} type="button" className="pp-type-tile" disabled={disabled}
            aria-pressed={selectedType === Number(id)} title={entry.display_name}
            onClick={() => onSelect(Number(id))}>
            <span className="pp-thumbnail"><PropThumbnail key={id} id={Number(id)} /></span>
            <span className="pp-type-name">{entry.display_name}</span>
          </button>
        ))}
      </div>
      {selected && <div className="pp-selected-type" role="status">
        {t("prop_placement.selectedType", { name: selected.display_name })}
      </div>}
    </>
  );
}
