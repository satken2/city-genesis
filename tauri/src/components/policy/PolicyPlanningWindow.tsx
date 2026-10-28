/**
 * 政策立案はコアの観測結果を表示し、各階層の対応案から既存の操作画面へ案内する。
 * 別の指標で調べる要因は参照ノードで止め、そのボタンで調査先を切り替える。
 * 再表示は App の open_seq で初期化され、エポック完了と明示的な再調査で値を取り直す。
 */
import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";
import { PLAY_MODE_READ_ONLY } from "../../constants";
import { usePlayMode } from "../../hooks/usePlayMode";
import { useEpochPeriodUnit } from "../../hooks/useEpochPeriodUnit";
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

const ACTIONS: Record<string, { window: string; edits?: boolean; params?: Record<string, unknown> }> = {
  public_jobs: { window: "building_construction", edits: true },
  population: { window: "citizen_list", edits: true },
  budget: { window: "city_budget", edits: true },
  education: { window: "building_construction", edits: true },
  higher_education: { window: "building_construction", edits: true },
  welfare: { window: "city_regulation", edits: true, params: { extra: { initialTab: "welfare" } } },
  immigration_skills: { window: "city_regulation", edits: true, params: { extra: { initialTab: "immigration" } } },
  immigration_limit: { window: "city_regulation", edits: true, params: { extra: { initialTab: "immigration" } } },
  employment: { window: "city_statistics" },
  police: { window: "building_construction", edits: true },
  facilities: { window: "public_facilities" },
  criminality: { window: "citizen_list" },
};

const INDICATOR_ORDER: Indicator["id"][] = ["crime", "unemployment", "income"];
const INDICATOR_ACTIONS: Partial<Record<Indicator["id"], string[]>> = { income: ["welfare"] };
const FINDING_INDICATORS: Record<string, Indicator["id"]> = {
  household_unemployment: "unemployment",
  unemployment_pressure: "unemployment",
  no_vacancies: "unemployment",
  unfilled_jobs: "unemployment",
};

type DiagramRow = {
  id: string;
  parentId: string | null;
  depth: number;
  finding?: Finding;
  indicator?: Indicator;
  reference?: Indicator["id"];
};
type Connection = { id: string; path: string; x: number; y: number };

function PolicyDiagram({ indicator, indicators, periodUnit, canEdit, onAction, onSelect }: {
  indicator: Indicator; indicators: Indicator[]; canEdit: boolean;
  periodUnit: ReturnType<typeof useEpochPeriodUnit>;
  onAction: (id: string) => void; onSelect: (id: Indicator["id"]) => void;
}) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
  const percent = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 1 });
  const diagramRef = useRef<HTMLDivElement>(null);
  const nodeRefs = useRef(new Map<string, HTMLElement>());
  const arrowId = `policy-arrow-${useId().replace(/:/g, "")}`;
  const [geometry, setGeometry] = useState({ width: 0, height: 0, connections: [] as Connection[] });
  const rows = useMemo(() => {
    const result: DiagramRow[] = [{ id: "root", parentId: null, depth: 0, indicator }];
    const append = (finding: Finding, parentId: string, depth: number, id: string) => {
      const target = FINDING_INDICATORS[finding.type];
      const reference = target !== indicator.id && indicators.find(item => item.id === target);
      if (reference) {
        result.push({ id, parentId, depth, indicator: reference, reference: reference.id });
        return;
      }
      result.push({ id, parentId, depth, finding });
      finding.children.forEach((child, index) => append(child, id, depth + 1, `${id}.${index}`));
    };
    indicator.roots.forEach((finding, index) => append(finding, "root", 1, String(index)));
    return result;
  }, [indicator, indicators]);

  // 接続点はカードの実寸から求める。折り返し・文字サイズによる変化は ResizeObserver で追従する。
  useLayoutEffect(() => {
    const diagram = diagramRef.current;
    if (!diagram) return;
    const measure = () => {
      const bounds = diagram.getBoundingClientRect();
      const connections: Connection[] = [];
      for (const row of rows) {
        if (!row.parentId) continue;
        const node = nodeRefs.current.get(row.id)?.getBoundingClientRect();
        const parent = nodeRefs.current.get(row.parentId)?.getBoundingClientRect();
        if (!node?.width || !parent?.width) continue;
        const x = node.left - bounds.left;
        const y = node.top + node.height / 2 - bounds.top;
        const targetX = (parent.left + node.left) / 2 - bounds.left;
        const targetY = parent.bottom - bounds.top;
        const radius = Math.min(10, (x - targetX) / 2, (y - targetY) / 2);
        connections.push({ id: row.id, x, y,
          path: `M ${x} ${y} H ${targetX + radius} Q ${targetX} ${y} ${targetX} ${y - radius} V ${targetY}` });
      }
      setGeometry(previous => {
        const next = { width: bounds.width, height: bounds.height, connections };
        return JSON.stringify(previous) === JSON.stringify(next) ? previous : next;
      });
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(diagram);
    for (const element of nodeRefs.current.values()) observer.observe(element);
    return () => observer.disconnect();
  }, [rows, locale]);

  const formatIndicator = (item: Indicator) => {
    if (item.value == null) return "—";
    return item.unit === "percent" ? percent.format(item.value)
      : t(`policy.values.${item.unit}`, { value: number.format(item.value), context: periodUnit });
  };

  const actionsFor = (row: DiagramRow) => row.finding?.actions
    ?? (row.indicator?.roots.length ? INDICATOR_ACTIONS[row.indicator.id] ?? [] : []);

  return <div className="policy-diagram" ref={diagramRef}>
    <svg className="policy-connections" aria-hidden="true" width={geometry.width} height={geometry.height}>
      <defs>
        <marker id={arrowId} viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto">
          <path d="M 0 0 L 8 4 L 0 8 Z" fill="currentColor" />
        </marker>
      </defs>
      {geometry.connections.map(connection => <g key={connection.id}>
        <path d={connection.path} fill="none" stroke="currentColor" strokeWidth="1.5"
          strokeLinecap="round" strokeLinejoin="round" markerEnd={`url(#${arrowId})`} />
        <circle cx={connection.x} cy={connection.y} r="3.5" fill="currentColor" />
      </g>)}
    </svg>
    <ol className="policy-rows">
      {rows.map(row => {
        const values = Object.fromEntries(Object.entries(row.finding?.params ?? {}).map(([key, value]) => [key, number.format(value)]));
        const title = row.indicator ? t(`policy.indicators.${row.indicator.id}.title`) : t(`policy.nodes.${row.finding!.type}.title`);
        const detail = row.indicator ? formatIndicator(row.indicator)
          : Object.keys(values).length ? t(`policy.nodes.${row.finding!.type}.evidence`, { ...values, context: periodUnit }) : "";
        return <li key={row.id} className="policy-row" data-parent-id={row.parentId ?? undefined} data-depth={row.depth}
          style={{ "--policy-depth": row.depth } as CSSProperties}>
          <div className="policy-event-cell">
            <article className={`policy-finding${row.indicator ? " policy-finding--indicator" : ""}`}
              ref={element => { if (element) nodeRefs.current.set(row.id, element); else nodeRefs.current.delete(row.id); }}>
              <h3>{title}</h3>
              {detail && <p className="policy-evidence">{detail}</p>}
            </article>
          </div>
          <div className="policy-actions">
            {row.reference ? <button type="button" data-guide={`policy.investigate.${row.reference}`}
              onClick={() => onSelect(row.reference!)}>{t(`policy.investigate.${row.reference}`)}</button>
              : actionsFor(row).filter(id => id in ACTIONS).map(id => <button type="button" key={id}
                disabled={ACTIONS[id].edits && !canEdit} data-guide={`policy.action.${id}`} onClick={() => onAction(id)}>
                {t(`policy.actions.${id}.title`)}
              </button>)}
          </div>
        </li>;
      })}
    </ol>
  </div>;
}

export function PolicyPlanningWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const playMode = usePlayMode(apiBaseUrl);
  const periodUnit = useEpochPeriodUnit(apiBaseUrl);
  const [report, setReport] = useState<Report | null>(null);
  const [selected, setSelected] = useState<Indicator["id"]>("crime");
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

  const current = report?.indicators.find(item => item.id === selected);
  const canEdit = playMode != null && playMode !== PLAY_MODE_READ_ONLY && !executing;
  const pending = executing || report?.pending;

  async function openAction(id: string) {
    setActionError(false);
    try {
      await invoke("open_registered_window", { windowType: ACTIONS[id].window, params: ACTIONS[id].params });
    } catch (e) {
      console.error("failed to open policy action", e);
      setActionError(true);
    }
  }

  return <div className="policy-planning" data-guide="policy.panel">
    <h2 className="policy-selector-title">{t("policy.selectorTitle")}</h2>
    <div className="policy-toolbar">
      <div className="policy-indicators" role="group" aria-label={t("policy.selectorTitle")}>
        {INDICATOR_ORDER.map(id => report?.indicators.find(item => item.id === id)).filter((item): item is Indicator => !!item).map(item =>
          <button type="button" key={item.id} className={`policy-indicator${item.id === selected ? " policy-indicator--selected" : ""}`}
            aria-pressed={item.id === selected} data-guide={`policy.indicator.${item.id}`} onClick={() => setSelected(item.id)}>
            {t(`policy.indicators.${item.id}.tab`)}
            {item.roots.length > 0 && <span role="img" aria-label={t("policy.needsAttention")}>⚠️</span>}
          </button>)}
      </div>
      <button type="button" disabled={loading || executing} onClick={() => setRevision(value => value + 1)}>
        {t("policy.refresh")}
      </button>
    </div>
    {error && <p className="policy-error" role="alert">{t("common.fetchError")}: {error}</p>}
    {actionError && <p className="policy-error" role="alert">{t("policy.actionError")}</p>}
    {pending ? <p className="policy-message" role="status">{t("policy.pending")}</p>
      : loading ? <p className="policy-message" role="status">{t("common.loading")}</p>
      : current && <section className="policy-investigation" aria-label={t(`policy.indicators.${selected}.title`)}>
        <div className="policy-tree-scroll" key={selected}>
          <div className="policy-column-headings">
            <h2>{t("policy.eventsTitle")}</h2>
            <h2>{t("policy.adviceTitle")}</h2>
          </div>
          <PolicyDiagram indicator={current} indicators={report!.indicators} periodUnit={periodUnit} canEdit={canEdit}
            onAction={id => void openAction(id)} onSelect={setSelected} />
          {current.roots.length === 0 && <p className="policy-message">{t(current.value == null ? "policy.noData" : "policy.noFindings")}</p>}
        </div>
      </section>}
  </div>;
}
