import { useTranslation } from "react-i18next";
import "./PathConstructionPanel.css";

export function StationPassageModeSelect({ value, onChange, disabled = false }: {
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  return (
      <label className="pc-settings-row">
        <span className="pc-settings-label">{t("station.passageMode")}</span>
        <select className="pc-select" value={value} onChange={(event) => onChange(event.target.value)} disabled={disabled}>
          {["elevated", "ground", "underground"].map((mode) => (
            <option key={mode} value={mode}>{t(`station.passageMode_${mode}`)}</option>
          ))}
        </select>
      </label>
  );
}
