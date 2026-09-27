import { useTranslation } from "react-i18next";
import "../achievement/ScenarioInfoSection.css";

export type ScenarioText = { scenario_title: string; scenario_description: string; scenario_objective: string };

export function ScenarioTextFields({ value, onChange, disabled = false }: {
  value: ScenarioText; onChange: (value: ScenarioText) => void; disabled?: boolean;
}) {
  const { t } = useTranslation();
  const fields = [
    ["scenario_title", "scenarioTitle", 160],
    ["scenario_description", "scenarioDescription", 8000],
    ["scenario_objective", "scenarioObjective", 4000],
  ] as const;
  return <>{fields.map(([key, label, maxLength]) => <label className="scenario-info__field" key={key}>
    <span className="scenario-info__label">{t(`achievements.${label}Label`)}</span>
    {key === "scenario_title" ? <input className="scenario-info__input" value={value[key]} maxLength={maxLength}
      disabled={disabled} onChange={e => onChange({ ...value, [key]: e.target.value })} />
      : <textarea className="scenario-info__textarea" value={value[key]} rows={3} maxLength={maxLength}
        disabled={disabled} onChange={e => onChange({ ...value, [key]: e.target.value })} />}
  </label>)}</>;
}
