import { useTranslation } from "react-i18next";

export function WorkHoursTab({ value, onChange }: { value: number; onChange: (hours: number) => void }) {
  const { t } = useTranslation();
  return <>
    <p className="regulation-pane__description">{t("wellbeing.workDescription")}</p>
    <div className="regulation-building-row">
      <label htmlFor="work-hours">{t("wellbeing.work_hours")}</label>
      <input id="work-hours" type="range" min={1} max={16} step={0.5} value={value}
        onChange={e => onChange(Number(e.target.value))} />
      <output htmlFor="work-hours">{value.toFixed(1)} {t("wellbeing.hours")}</output>
    </div>
  </>;
}
