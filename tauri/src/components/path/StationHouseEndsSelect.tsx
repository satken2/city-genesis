import { useTranslation } from "react-i18next";

export function StationHouseEndsSelect({ value, onChange, disabled = false }: {
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  return (
    <div>
      <label>
        <span>{t("station.houseEnds")} </span>
        <select value={value} onChange={(event) => onChange(event.target.value)} disabled={disabled}>
          {["both", "start", "end"].map((end) => (
            <option key={end} value={end}>{t(`station.houseEnds_${end}`)}</option>
          ))}
        </select>
      </label>
      <p className="agent-detail-muted">{t("station.houseEndsHelp")}</p>
    </div>
  );
}
