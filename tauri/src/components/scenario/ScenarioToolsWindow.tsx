import { describeError } from "../../utils/errors";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { PLAY_MODE_NORMAL, PLAY_MODE_SCENARIO } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";
import { useMasterData } from "../../contexts/MasterDataContext";
import { FlatTabs } from "../common/FlatTabs";
import { MasterOverrideEditor } from "./MasterOverrideEditor";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { sendSessionReload } from "../../utils/ipc";
import { setHintbarText } from "../../utils/hintbar";
import { apportionSkillCounts, type ScenarioSkillDistribution } from "../../utils/scenarioSkills";
import { resolveIntlLocale } from "../../utils/locale";
import "./ScenarioToolsWindow.css";

type CompleteConstructionResult = {
  completed_paths: number; completed_buildings: number; remaining_paths: number; remaining_buildings: number;
};

export function ScenarioToolsWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const playMode = usePlayMode(apiBaseUrl);
  const master = useMasterData();
  const [tab, setTab] = useState<"city" | "skills" | "master">("city");
  const [debugExecution, setDebugExecution] = useState(false);
  const [busy, setBusy] = useState(false);
  const busyRef = useRef(false);
  const [epochExecuting, setEpochExecuting] = useState(false);
  const [distribution, setDistribution] = useState<ScenarioSkillDistribution | null>(null);
  const [highRatio, setHighRatio] = useState(0);
  const [majorRatios, setMajorRatios] = useState<number[]>([]);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);
  const enabled = playMode === PLAY_MODE_SCENARIO && !busy && !epochExecuting;

  const errorMessage = useCallback((error: unknown) => describeError(error, t), [t]);

  const showError = useCallback((error: unknown) => setMessage({ text: errorMessage(error), error: true }), [errorMessage]);

  const acceptDistribution = useCallback((value: ScenarioSkillDistribution) => {
    setDistribution(value);
    // Retain the current value without rounding so display rounding alone does not change target population in large cities.
    setHighRatio(value.high_skill_ratio);
    setMajorRatios(value.high_skill_citizens > 0 ? value.majors.map(major => major.ratio) : value.majors.map(() => 100 / value.majors.length));
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void fetchApi<{ debug_execution: boolean }>(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { signal: controller.signal })
      .then(session => { if (!controller.signal.aborted) setDebugExecution(session.debug_execution); }).catch(() => {});
    return () => controller.abort();
  }, [apiBaseUrl]);

  useEffect(() => {
    let disposed = false;
    const subscription = listen<{ executing: boolean }>("epoch:executing", event => {
      if (!disposed) setEpochExecuting(event.payload.executing);
    });
    return () => { disposed = true; void subscription.then(unlisten => unlisten()); };
  }, []);

  useEffect(() => {
    if (playMode !== PLAY_MODE_SCENARIO || tab !== "skills") return;
    const controller = new AbortController();
    setDistribution(null);
    void fetchApi<ScenarioSkillDistribution>(buildApiUrl(apiBaseUrl, "/api/v1/scenario-tools/citizen-skills"), { signal: controller.signal })
      .then(value => { if (!controller.signal.aborted) acceptDistribution(value); })
      .catch(error => { if (!controller.signal.aborted) setMessage({ text: errorMessage(error), error: true }); });
    return () => controller.abort();
  }, [apiBaseUrl, playMode, tab, acceptDistribution, errorMessage]);

  async function runOperation(operation: () => Promise<string>) {
    if (!enabled || busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setMessage(null);
    try {
      await emit("epoch:executing", { executing: true });
      const text = await operation();
      await sendSessionReload();
      setMessage({ text, error: false });
    } catch (error) {
      setMessage({ text: errorMessage(error), error: true });
    } finally {
      busyRef.current = false;
      setBusy(false);
      await emit("epoch:executing", { executing: false });
    }
  }

  async function openTool(windowType: string, hintKey?: string) {
    if (!enabled) return;
    try {
      await setHintbarText(hintKey ? t(hintKey) : "");
      await invoke("open_registered_window", { windowType });
    } catch (error) { setMessage({ text: errorMessage(error), error: true }); }
  }

  const validNumber = (value: number) => Number.isFinite(value) && value >= 0 && value <= 100;
  const ratioSum = majorRatios.reduce((a, b) => a + b, 0);
  const validRatios = validNumber(highRatio) && majorRatios.every(validNumber) && Math.abs(ratioSum - 100) < 1e-6;
  const highCount = distribution && validNumber(highRatio) ? Math.floor(distribution.eligible_citizens * highRatio / 100 + 0.5) : 0;
  const targetCounts = validRatios ? apportionSkillCounts(highCount, majorRatios) : majorRatios.map(() => 0);
  const locale = resolveIntlLocale(i18n.language);
  const formatRatio = (value: number) => Number.isFinite(value) ? value.toLocaleString(locale, { maximumFractionDigits: 2 }) : "—";

  return <div className="scenario-tools">
    {/* Master override editing is only for DLC development (core in debug mode), under the same condition as official signing. */}
    <FlatTabs tabs={(debugExecution ? ["city", "skills", "master"] as const : ["city", "skills"] as const)
      .map(id => ({ id, label: t(`scenarioTools.editor.${id}Tab`), disabled: busy }))}
      active={tab} onChange={value => { setTab(value); setMessage(null); }} idPrefix="scenario-tools" />
    {playMode !== null && playMode !== PLAY_MODE_SCENARIO && <p>{t("scenarioTools.editor.scenarioOnly")}</p>}
    {tab === "city" ? <div className="scenario-tools__actions" role="tabpanel" id="scenario-tools-panel-city" aria-labelledby="scenario-tools-tab-city">
      <button type="button" disabled={!enabled} onClick={() => void runOperation(async () => {
        const result = await fetchApi<CompleteConstructionResult>(buildApiUrl(apiBaseUrl, "/api/v1/scenario-tools/complete-construction"), { method: "POST" });
        const remaining = result.remaining_paths + result.remaining_buildings;
        return t(remaining > 0 ? "scenarioTools.completedWithRemaining" : "scenarioTools.completed", {
          paths: result.completed_paths, buildings: result.completed_buildings, remaining,
        });
      })}>{t("scenarioTools.editor.complete")}</button>
      <button type="button" disabled={!enabled} onClick={() => void runOperation(async () => {
        const result = await fetchApi<{ privatized_agents: number }>(buildApiUrl(apiBaseUrl, "/api/v1/scenario-tools/privatize-facilities"), { method: "POST" });
        return t("scenarioTools.privatized", { count: result.privatized_agents });
      })}>{t("scenarioTools.editor.privatize")}</button>
      <button type="button" disabled={!enabled} onClick={() => void openTool("street_grid")}>{t("scenarioTools.editor.streetGrid")}</button>
      <button type="button" disabled={!enabled} onClick={() => void openTool("resource_edit", "hint.resourceEdit")}>{t("taskbar.menuResourceEdit")}</button>
      <button type="button" disabled={!enabled} onClick={() => void openTool("agent_creation")}>{t("taskbar.menuAgentCreation")}</button>
      <button type="button" disabled={!enabled} onClick={() => {
        void invoke("open_parameter_edit_window").catch(error => setMessage({ text: errorMessage(error), error: true }));
      }}>{t("taskbar.menuParameterEdit")}</button>
    </div> : tab === "master" ? <div role="tabpanel" id="scenario-tools-panel-master" aria-labelledby="scenario-tools-tab-master">
      <MasterOverrideEditor apiBaseUrl={apiBaseUrl} enabled={enabled} onError={showError} />
    </div> : <div className="scenario-tools__skills" role="tabpanel" id="scenario-tools-panel-skills" aria-labelledby="scenario-tools-tab-skills">
      <p>{t("scenarioTools.editor.skillHelp", { age: distribution?.min_working_age ?? "…" })}</p>
      {distribution ? <>
        <p>{t("scenarioTools.editor.population", {
          total: distribution.eligible_citizens, high: distribution.high_skill_citizens, ratio: formatRatio(distribution.high_skill_ratio),
        })}</p>
        <label className="scenario-tools__ratio">{t("scenarioTools.editor.highRatio")}
          <input type="number" min={0} max={100} step="any" disabled={!enabled} value={Number.isFinite(highRatio) ? highRatio : ""}
            onChange={event => setHighRatio(event.currentTarget.valueAsNumber)} />%
        </label>
        <table><thead><tr>
          <th>{t("scenarioTools.editor.major")}</th><th>{t("scenarioTools.editor.current")}</th>
          <th>{t("scenarioTools.editor.targetRatio")}</th><th>{t("scenarioTools.editor.targetCount")}</th>
        </tr></thead><tbody>{distribution.majors.map((major, index) => <tr key={major.skill_id}>
          <th scope="row">{master?.skills[major.skill_id]?.display_name ?? major.skill_id}</th>
          <td>{t("scenarioTools.editor.currentMajor", { count: major.citizens, ratio: formatRatio(major.ratio) })}</td>
          <td><input type="number" aria-label={`${master?.skills[major.skill_id]?.display_name ?? major.skill_id} ${t("scenarioTools.editor.targetRatio")}`}
            min={0} max={100} step="any" disabled={!enabled} value={Number.isFinite(majorRatios[index]) ? majorRatios[index] : ""}
            onChange={event => { const value = event.currentTarget.valueAsNumber; setMajorRatios(values => values.map((old, i) => i === index ? value : old)); }} />%</td>
          <td>{validRatios ? targetCounts[index].toLocaleString(locale) : "—"}</td>
        </tr>)}</tbody></table>
        <p className={validRatios ? "" : "scenario-tools__error"}>{t("scenarioTools.editor.ratioSum", { ratio: formatRatio(ratioSum) })}</p>
        <p>{t("scenarioTools.editor.applyHelp")}</p>
        <button type="button" disabled={!enabled || !validRatios || distribution.eligible_citizens === 0}
          onClick={() => void runOperation(async () => {
            const result = await fetchApi<ScenarioSkillDistribution>(buildApiUrl(apiBaseUrl, "/api/v1/scenario-tools/citizen-skills"), {
              method: "POST", headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ high_skill_ratio: highRatio, major_ratios: Object.fromEntries(distribution.majors.map((major, index) => [major.skill_id, majorRatios[index]])) }),
            });
            acceptDistribution(result);
            return t("scenarioTools.editor.applied", { count: result.updated_citizens });
          })}>{t("scenarioTools.editor.apply")}</button>
      </> : <p>{t("common.loading")}</p>}
    </div>}
    {busy && <p role="status">{t("scenarioTools.editor.running")}</p>}
    {message && <p role={message.error ? "alert" : "status"} className={message.error ? "scenario-tools__error" : ""}>{message.text}</p>}
    {debugExecution && <button className="scenario-tools__exit" type="button" disabled={!enabled} onClick={() => {
      void fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/sessions"), { method: "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ play_mode: PLAY_MODE_NORMAL }) }).catch(error => setMessage({ text: errorMessage(error), error: true }));
    }}>{t("scenarioTools.editor.exitScenario")}</button>}
  </div>;
}
