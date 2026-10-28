/**
 * 政策立案はコアの観測結果を表示し、各階層の対応案から既存の操作画面へ案内する。
 * 原因の寄与率・政策の効果は推定しない。カードを切り替えても同じ調査時点の木を使う。
 * 再表示は App の open_seq で初期化され、エポック完了と明示的な再調査で値を取り直す。
 */
import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";
import { PLAY_MODE_READ_ONLY } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import "./PolicyPlanningWindow.css";

type Finding = { type: string; params: Record<string, number>; actions: string[]; children: Finding[] };
type Indicator = {
  id: "unemployment" | "income" | "crime";
  value: number | null;
  unit: "percent" | "money" | "per_thousand";
  sample: number;
  roots: Finding[];
};
type Report = { epoch: number; pending: boolean; indicators: Indicator[] };

const ACTIONS: Record<string, { window: string; edits?: boolean }> = {
  public_jobs: { window: "building_construction", edits: true },
  population: { window: "citizen_list", edits: true },
  budget: { window: "city_budget", edits: true },
  education: { window: "building_construction", edits: true },
  higher_education: { window: "building_construction", edits: true },
  welfare: { window: "city_regulation", edits: true },
  employment: { window: "city_statistics" },
  police: { window: "building_construction", edits: true },
  facilities: { window: "public_facilities" },
};

function FindingTree({ nodes, canEdit, onAction }: {
  nodes: Finding[]; canEdit: boolean; onAction: (id: string) => void;
}) {
  const { t, i18n } = useTranslation();
  const number = new Intl.NumberFormat(resolveIntlLocale(i18n.language), { maximumFractionDigits: 1 });
  return <ul className="policy-branches">
    {nodes.map(node => {
      const values = Object.fromEntries(Object.entries(node.params).map(([key, value]) => [key, number.format(value)]));
      return <li key={node.type}>
        <article className="policy-finding">
          <h3>{t(`policy.nodes.${node.type}.title`)}</h3>
          <p className="policy-evidence">{t(`policy.nodes.${node.type}.evidence`, values)}</p>
          {node.actions.length > 0 && <div className="policy-actions">
            <span className="policy-label">{t("policy.actionsLabel")}</span>
            {node.actions.filter(id => id in ACTIONS).map(id => <div key={id} className="policy-action">
              <button type="button" disabled={ACTIONS[id].edits && !canEdit}
                data-guide={`policy.action.${id}`} onClick={() => onAction(id)}>
                {t(`policy.actions.${id}.title`)} <span aria-hidden="true">↗</span>
              </button>
              <p>{t(`policy.actions.${id}.description`)}</p>
            </div>)}
          </div>}
        </article>
        {node.children.length > 0 && <FindingTree nodes={node.children} canEdit={canEdit} onAction={onAction} />}
      </li>;
    })}
  </ul>;
}

export function PolicyPlanningWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const playMode = usePlayMode(apiBaseUrl);
  const [report, setReport] = useState<Report | null>(null);
  const [selected, setSelected] = useState<Indicator["id"]>("unemployment");
  const [revision, setRevision] = useState(0);
  const [loading, setLoading] = useState(true);
  const [executing, setExecuting] = useState(false);
  const [error, setError] = useState("");
  const [actionError, setActionError] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const unlisten = listen<{ executing: boolean }>("epoch:executing", event => {
      void getCurrentWindow().isVisible().then(visible => {
        if (!visible || cancelled) return;
        setExecuting(event.payload.executing);
        if (event.payload.executing) setReport(null);
        else setRevision(value => value + 1);
      }).catch(console.error);
    });
    return () => { cancelled = true; void unlisten.then(fn => fn()); };
  }, []);

  useEffect(() => {
    if (executing) return;
    const controller = new AbortController();
    setLoading(true);
    setError("");
    setReport(null);
    void fetchApi<Report>(buildApiUrl(apiBaseUrl, "/api/v1/city/policy-planning"), { signal: controller.signal })
      .then(result => { if (!controller.signal.aborted) setReport(result); })
      .catch(e => { if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [apiBaseUrl, revision, executing]);

  const locale = resolveIntlLocale(i18n.language);
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
  const percent = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 1 });
  const current = report?.indicators.find(item => item.id === selected);
  const canEdit = playMode != null && playMode !== PLAY_MODE_READ_ONLY && !executing;
  const pending = executing || report?.pending;

  async function openAction(id: string) {
    setActionError(false);
    try {
      await invoke("open_registered_window", { windowType: ACTIONS[id].window });
    } catch (e) {
      console.error("failed to open policy action", e);
      setActionError(true);
    }
  }

  return <div className="policy-planning" data-guide="policy.panel">
    <div className="policy-toolbar">
      <p>{t("policy.intro")}</p>
      <button type="button" disabled={loading || executing} onClick={() => setRevision(value => value + 1)}>
        {t("policy.refresh")}
      </button>
    </div>
    {error && <p className="policy-error" role="alert">{t("common.fetchError")}: {error}</p>}
    {actionError && <p className="policy-error" role="alert">{t("policy.actionError")}</p>}
    {pending ? <p className="policy-message" role="status">{t("policy.pending")}</p>
      : loading ? <p className="policy-message" role="status">{t("common.loading")}</p>
      : report && <>
        <div className="policy-indicators" role="group" aria-label={t("policy.indicatorsLabel")}>
          {report.indicators.map(item => <button type="button" key={item.id}
            className={`policy-indicator${item.id === selected ? " policy-indicator--selected" : ""}`}
            aria-pressed={item.id === selected} data-guide={`policy.indicator.${item.id}`}
            onClick={() => setSelected(item.id)}>
            <span className="policy-label">{t(`policy.indicators.${item.id}.title`)}</span>
            <strong>{item.value == null ? "—" : item.unit === "percent" ? percent.format(item.value) : number.format(item.value)}</strong>
            <span className="policy-label">{t(`policy.indicators.${item.id}.unit`)}</span>
            <span>{t("policy.sample", { n: number.format(item.sample) })}</span>
          </button>)}
        </div>
        {current && <section className="policy-investigation" aria-label={t(`policy.indicators.${selected}.title`)}>
          <div className="policy-tree-heading">
            <h2>{t(`policy.indicators.${selected}.title`)} <span aria-hidden="true">↓</span> {t("policy.causes")}</h2>
            <p>{t("policy.candidateNote")}</p>
          </div>
          <div className="policy-tree-scroll" key={selected}>
            {current.roots.length > 0
              ? <FindingTree nodes={current.roots} canEdit={canEdit} onAction={id => void openAction(id)} />
              : <p className="policy-message">{t(current.value == null ? "policy.noData" : "policy.noFindings")}</p>}
          </div>
        </section>}
      </>}
  </div>;
}
