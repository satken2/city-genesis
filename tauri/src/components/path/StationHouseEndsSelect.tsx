import { useTranslation } from "react-i18next";
import "./PathConstructionPanel.css";

export function StationHouseEndsSelect({ value, onChange, disabled = false }: {
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  return (
      <label className="pc-settings-row">
        <span className="pc-settings-label">{t("station.houseEnds")}</span>
        <select className="pc-select" value={value} onChange={(event) => onChange(event.target.value)} disabled={disabled}>
          {["both", "start", "end"].map((end) => (
            <option key={end} value={end}>{t(`station.houseEnds_${end}`)}</option>
          ))}
        </select>
      </label>
  );
}
