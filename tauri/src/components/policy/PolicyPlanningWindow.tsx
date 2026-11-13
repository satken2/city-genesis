import { describeError } from "../../utils/errors";
/**
 * 政策立案は、都市で起きている問題(L1通知)と指標から調査先を選び、その原因の木と対策を表示する。
 * コックピットのアラートのアイコンから開く。原因はコアの観測結果で、対策は既存の操作画面へ案内する。
 * 左に調査先の一覧(指標・稼働できていない施設・その他の問題)、右に選んだものの木を並べる。
 * 右は事象・原因・別の調査への参照を色とラベルで区別し、対策は対象のカード内にまとめる。
 * 施設の不調は、どの木の中でも「その施設が稼働できていない」という節で止め、原因はその施設を選んで調べる。
 * 別の指標で調べる要因は参照ノードで止め、そのボタンで調査先を切り替える。
 * 詳細画面のあるカードは全体をクリックして開ける。カード内の対策・調査ボタンはそれぞれの操作を優先する。
 * 施設の不調をたどる節(domain が diagnosis)はコアの施設診断の節で、文言も施設診断のものを使う。
 * どの施設の不調かは subject が持ち、節の文の主語として出す。
 * 再表示は App の open_seq で初期化され、エポック完了時にも値を取り直す。
 */
import { useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";
import { PLAY_MODE_READ_ONLY, PRODUCT_ELECTRICITY, PRODUCT_HIGH_ELECTRICITY, PRODUCT_HIGH_WATER,
  PRODUCT_INTERNET, PRODUCT_WATER_SUPPLY } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { usePlayMode } from "../../hooks/usePlayMode";
import { useEpochPeriodUnit } from "../../hooks/useEpochPeriodUnit";
import { buildApiUrl, fetchApi } from "../../utils/api";
import {
  formatL1NotificationMessage,
  isCautionNotification,
  NOTIFICATION_ENTITY_WINDOW_TYPES,
  type L1Notification,
} from "../../utils/cityNotifications";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import crisisIcon from "../../assets/images/ui/crisis.png";
import infoIcon from "../../assets/images/ui/info.png";
import adviceIcon from "../../assets/images/menu/policy.svg";
import crimeIcon from "../../assets/images/alerts/shooting.png";
import sanitationIcon from "../../assets/images/alerts/sanitation.png";
import infrastructureIcon from "../../assets/images/alerts/electricity.png";
import facilityIcon from "../../assets/images/alerts/facility-inoperative.svg";
import collapseIcon from "../../assets/images/alerts/rubble.png";
import resourceIcon from "../../assets/images/alerts/resource-warning.png";
import "./PolicyPlanningWindow.css";

type Finding = {
  type: string;
  domain: "policy" | "diagnosis";
  params: Record<string, number>;
  actions: string[];
  subject?: { id: number; name?: string } | null;
  indicator?: Indicator["id"] | null;
  children: Finding[];
};
type Indicator = {
  id: "unemployment" | "income" | "tourism" | "crime" | "sanitation" | "water" | "electricity" | "internet"
    | "high_water" | "high_electricity" | "upkeep";
  value: number | null;
  unit: "percent" | "money" | "per_thousand" | "households" | "persons";
  sample: number;
  /** 調べる問題があるか。コアが指標ごとの基準で決める。 */
  issue: boolean;
  /** 根拠の文に差し込む、値のほかの観測値。 */
  params?: Record<string, number>;
  roots: Finding[];
};
/** 通知1件と、その原因の節・通知そのものへの対策。 */
type Problem = L1Notification & { actions: string[]; roots: Finding[] };
/** 稼働できていない施設と、その原因の節。 */
type Facility = { id: number; name: string; variant: number; severity: "warning" | "caution"; params: Record<string, number>; roots: Finding[] };
type Report = { epoch: number; pending: boolean; problems: Problem[]; facilities: Facility[]; indicators: Indicator[] };
type Selection = { kind: "problem"; id: string } | { kind: "indicator"; id: Indicator["id"] } | { kind: "facility"; id: number };

const ACTIONS: Record<string, { window: string; edits?: boolean; params?: Record<string, unknown> }> = {
  public_jobs: { window: "building_construction", edits: true },
  population: { window: "citizen_list", edits: true },
  budget: { window: "city_budget", edits: true },
  education: { window: "building_construction", edits: true },
  higher_education: { window: "building_construction", edits: true },
  welfare: { window: "city_regulation", edits: true, params: { extra: { initialTab: "labor" } } },
  immigration_skills: { window: "city_regulation", edits: true, params: { extra: { initialTab: "immigration" } } },
  immigration_limit: { window: "city_regulation", edits: true, params: { extra: { initialTab: "immigration" } } },
  employment: { window: "city_statistics", params: { extra: { initialTab: "employment" } } },
  police: { window: "building_construction", edits: true },
  service_facility: { window: "building_construction", edits: true },
  facilities: { window: "public_facilities" },
  criminality: { window: "citizen_list" },
  parcel: { window: "parcel_creation", edits: true },
  transit: { window: "transit_management", edits: true },
};

const INDICATOR_ORDER: Indicator["id"][] = [
  "crime", "unemployment", "income", "tourism", "sanitation", "water", "electricity", "internet", "high_water", "high_electricity",
  "upkeep",
];
const INDICATOR_ACTIONS: Partial<Record<Indicator["id"], string[]>> = { income: ["welfare"] };
const INDICATOR_DETAILS: Partial<Record<Indicator["id"], { initialTab: string; initialSubTab?: string }>> = {
  unemployment: { initialTab: "employment" },
  income: { initialTab: "income" },
  tourism: { initialTab: "tourism" },
  crime: { initialTab: "accidents", initialSubTab: "crime" },
  sanitation: { initialTab: "health" },
};
const INDICATOR_PRODUCTS: Partial<Record<Indicator["id"], number>> = {
  water: PRODUCT_WATER_SUPPLY, electricity: PRODUCT_ELECTRICITY, internet: PRODUCT_INTERNET,
  high_water: PRODUCT_HIGH_WATER, high_electricity: PRODUCT_HIGH_ELECTRICITY,
};
const SERVICE_FINDINGS = new Set(["service_shortage", "service_no_facility", "service_facility_limited"]);
// Unity の AlertIconFactory と表示設定のアラートに対応する素材。専用の表示がない事象には付けない。
const EVENT_ICONS: Partial<Record<string, string>> = {
  crime: crimeIcon, sanitation: sanitationIcon,
  electricity: infrastructureIcon, high_electricity: infrastructureIcon, high_water: infrastructureIcon,
  facility_inoperative: facilityIcon, infra_facility_down: infrastructureIcon,
  collapse: collapseIcon, resource_runway: resourceIcon, resource_depleted: resourceIcon,
};
const FINDING_INDICATORS: Record<string, Indicator["id"]> = {
  household_unemployment: "unemployment",
  unemployment_pressure: "unemployment",
  no_vacancies: "unemployment",
  unfilled_jobs: "unemployment",
};
// 施設診断の値のうち割合で届くもの。施設診断の文言は百分率で書かれている。
const DIAGNOSIS_FRACTIONS = new Set(["ratio", "unemployment_rate"]);

/** 木の一番上に置く、選んだ問題または指標。 */
type DiagramRoot = { key: string; indicatorId?: Indicator["id"]; title: string; detail: string; icon?: string; roots: Finding[]; buttons: RowButton[]; openDetail?: () => void };
type RowButton = { key: string; label: string; guide: string; icon?: string; disabled?: boolean; onClick: () => void };
type DiagramRow = { id: string; parentId: string | null; depth: number; kind: "event" | "cause" | "reference"; title: string; detail: string; buttons: RowButton[]; openDetail?: () => void };
type Connection = { id: string; path: string; x: number; y: number };

function PolicyDiagram({ root, rows }: { root: DiagramRoot; rows: DiagramRow[] }) {
  const { t, i18n } = useTranslation();
  const diagramRef = useRef<HTMLDivElement>(null);
  const nodeRefs = useRef(new Map<string, HTMLElement>());
  const arrowId = `policy-arrow-${useId().replace(/:/g, "")}`;
  const [geometry, setGeometry] = useState({ width: 0, height: 0, connections: [] as Connection[] });

  // 接続点はカードの実寸から求める。折り返し・文字サイズによる変化は ResizeObserver で追従する。
  // rows は親の描画ごとに作り直されるので、並びが変わったときだけ測り直すよう、行の識別子の並びを見る。
  const layout = rows.map(row => row.id).join(" ");
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [layout, root.key, i18n.language]);

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
      {rows.map(row => <li key={row.id} className="policy-row" data-parent-id={row.parentId ?? undefined} data-depth={row.depth}
        style={{ "--policy-depth": row.depth } as CSSProperties}>
        <article className={`policy-finding policy-finding--${row.kind}${row.openDetail ? " policy-finding--interactive" : ""}`}
          ref={element => { if (element) nodeRefs.current.set(row.id, element); else nodeRefs.current.delete(row.id); }}>
          {row.openDetail && <button type="button" className="policy-finding-open" data-guide="policy.action.entity_detail"
            aria-label={`${t("policy.actions.entity_detail.title")}: ${row.title}`}
            title={t("policy.actions.entity_detail.title")} onClick={row.openDetail} />}
          <div className={`policy-finding-content${row.kind === "event" && !root.icon ? " policy-finding-content--plain" : ""}`}>
            {row.kind === "event"
              ? root.icon && <img src={root.icon} alt="" className="policy-event-icon" draggable={false} />
              : <span className="policy-finding-badge">{t("policy.causeTitle")}</span>}
            <h3>{row.title}</h3>
            {row.detail && <p className="policy-evidence">{row.detail}</p>}
            {row.buttons.length > 0 && <footer className="policy-finding-footer">
              <div className="policy-actions">
                {row.buttons.map(button => <button type="button" key={button.key} disabled={button.disabled}
                  data-guide={button.guide} onClick={button.onClick}>
                  {button.icon && <img src={button.icon} alt="" className="policy-advice-icon" draggable={false} />}
                  <span className="policy-action-label">{button.label}</span><span className="policy-action-arrow" aria-hidden="true" />
                </button>)}
              </div>
            </footer>}
          </div>
        </article>
      </li>)}
    </ol>
  </div>;
}

export function PolicyPlanningWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const master = useMasterData();
  const playMode = usePlayMode(apiBaseUrl);
  const periodUnit = useEpochPeriodUnit(apiBaseUrl);
  const [report, setReport] = useState<Report | null>(null);
  const [selected, setSelected] = useState<Selection | null>(null);
  const [revision, setRevision] = useState(0);
  const [loading, setLoading] = useState(true);
  const [executing, setExecuting] = useState(false);
  const [error, setError] = useState("");
  const [actionError, setActionError] = useState(false);
  const locale = resolveIntlLocale(i18n.language);
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 });
  const percent = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 1 });

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
      .catch(e => { if (!controller.signal.aborted) setError(describeError(e)); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [apiBaseUrl, revision, executing]);

  const problems = [...(report?.problems ?? [])].sort((a, b) => Number(isCautionNotification(a)) - Number(isCautionNotification(b)));
  const facilities = report?.facilities ?? [];
  const indicators = INDICATOR_ORDER.map(id => report?.indicators.find(item => item.id === id))
    .filter((item): item is Indicator => !!item);
  const canEdit = playMode != null && playMode !== PLAY_MODE_READ_ONLY && !executing;
  const pending = executing || report?.pending;

  // 選んだものが無くなったら(解消した通知など)、左の並びのとおり、問題のある指標・施設・その他の問題の順に最初のものを開く。
  const problem = selected?.kind === "problem" ? problems.find(item => item.id === selected.id) : undefined;
  const indicator = selected?.kind === "indicator" ? indicators.find(item => item.id === selected.id) : undefined;
  const facility = selected?.kind === "facility" ? facilities.find(item => item.id === selected.id) : undefined;
  const firstIssue = indicators.find(item => item.issue);
  const current: { problem?: Problem; indicator?: Indicator; facility?: Facility } = problem || indicator || facility
    ? { problem, indicator, facility }
    : firstIssue ? { indicator: firstIssue }
    : facilities[0] ? { facility: facilities[0] }
    : problems[0] ? { problem: problems[0] }
    : { indicator: indicators[0] };
  const facilityOf = new Map(facilities.map(item => [item.id, item]));

  async function open(windowType: string, args: Record<string, unknown>) {
    setActionError(false);
    try {
      await invoke("open_registered_window", { windowType, ...args });
    } catch (e) {
      console.error("failed to open policy action", e);
      setActionError(true);
    }
  }

  // 市内の既存施設だけでなくマスターから引くので、まだ建っていない場合も建設対象を示せる。
  const supplierNames = (productId?: number) => productId == null ? "" :
    [...new Set(Object.values(master?.agents ?? {}).filter(agent => agent.supply === productId)
      .map(agent => agent.display_name).filter(Boolean))].join("・");
  const actionButtons = (ids: string[], productId?: number, findingType?: string): RowButton[] => {
    const suppliers = supplierNames(productId);
    return ids.filter(id => id in ACTIONS).map(id => ({
      key: id, label: t(`policy.actions.${id}.title`, {
        context: suppliers ? "named" : findingType, suppliers,
      }), guide: `policy.action.${id}`,
      icon: adviceIcon,
      disabled: ACTIONS[id].edits && !canEdit,
      onClick: () => void open(ACTIONS[id].window, { params: ACTIONS[id].params }),
    }));
  };
  const serviceDetail = (productId: number) => () => void open("public_facilities", { params: { extra: { initialProductId: productId } } });
  const mapDetail = (productId: number) => () => void open("city_map", { params: { extra: { initialInfraProductId: productId } } });
  const indicatorDetail = (id: Indicator["id"]) => {
    const productId = INDICATOR_PRODUCTS[id];
    if (productId != null) return serviceDetail(productId);
    const extra = INDICATOR_DETAILS[id];
    return extra ? () => void open("city_statistics", { params: { extra } }) : undefined;
  };
  const facilityDetail = (id: number) => () => void open("agent_detail", { entityId: String(id) });
  const findingDetail = (finding: Finding, productId?: number) => {
    if (productId != null && (finding.type === "service_out_of_range" || finding.type.startsWith("infra_"))) {
      return mapDetail(productId);
    }
    if (finding.subject) return facilityDetail(finding.subject.id);
    return productId != null && SERVICE_FINDINGS.has(finding.type) ? serviceDetail(productId) : undefined;
  };

  const formatIndicator = (item: Indicator) => item.unit === "percent" ? percent.format(item.value!)
    : item.unit === "households" || item.unit === "persons" ? number.format(item.value!)
    : t(`policy.values.${item.unit}`, { value: number.format(item.value!), context: periodUnit });
  // 指標も他の節と同じく、現象の文と根拠の数値で出す。
  const stateIndicator = (item: Indicator) => ({
    title: t(`policy.indicators.${item.id}.issue`),
    detail: item.value == null ? "" : t(`policy.indicators.${item.id}.evidence`, {
      ...Object.fromEntries(Object.entries(item.params ?? {}).map(([key, value]) => [key, number.format(value)])),
      value: formatIndicator(item),
    }),
  });
  const stateProblem = (item: Problem) => formatL1NotificationMessage(t, item, "policy.problems");
  const stateFacility = (item: Facility) => ({
    title: t("policy.facility.title", { facility: item.name }),
    detail: item.params.percent == null ? "" : t("policy.facility.evidence", { percent: number.format(item.params.percent) }),
  });

  const describe = (finding: Finding, productId?: number) => {
    const diagnosis = finding.domain === "diagnosis";
    const values: Record<string, string> = Object.fromEntries(Object.entries(finding.params).map(([key, value]) =>
      [key, number.format(diagnosis && DIAGNOSIS_FRACTIONS.has(key) ? value * 100 : value)]));
    const facility = finding.subject?.name ?? "";
    // 下に施設が並ぶ節は、その施設の種類の名前(変電所など)を差し込めるようにする。
    const kinds = [...new Set(finding.children.filter(child => child.type === "facility_ref")
      .map(child => master?.agents[facilityOf.get(child.subject!.id)?.variant ?? -1]?.display_name).filter(Boolean))].join("・");
    const suppliers = supplierNames(productId);
    const names = { ...values, facility, kinds, suppliers, product_name: master?.products[finding.params.product_id]?.display_name ?? "" };
    if (!diagnosis) {
      return {
        title: t(`policy.nodes.${finding.type}.title`, { ...names, context: suppliers ? "named" : undefined }),
        detail: t(`policy.nodes.${finding.type}.evidence`, { ...names, context: periodUnit, defaultValue: "" }),
      };
    }
    const title = t(`diagnosis.nodes.${finding.type}.title`, names);
    return {
      title: facility ? t("policy.facilityFinding", { facility, title }) : title,
      detail: t(`diagnosis.nodes.${finding.type}.description`, names),
    };
  };

  // 問題が無い指標はカードを出さず、問題が無いことだけを領域の中央に書く。
  let root: DiagramRoot | null = null;
  let clearMessage = "";
  const indicatorRoot = (item: Indicator, key: string): DiagramRoot => ({
    key, indicatorId: item.id, ...stateIndicator(item), roots: item.roots, buttons: actionButtons(INDICATOR_ACTIONS[item.id] ?? []),
    icon: EVENT_ICONS[item.id],
    openDetail: indicatorDetail(item.id),
  });
  if (current.problem) {
    const item = current.problem;
    const detailWindow = item.entity ? NOTIFICATION_ENTITY_WINDOW_TYPES[item.entity.type] : undefined;
    root = {
      key: `problem:${item.id}`, title: stateProblem(item), detail: "", roots: item.roots,
      icon: EVENT_ICONS[item.type],
      buttons: actionButtons(item.actions),
      openDetail: detailWindow ? () => void open(detailWindow, { entityId: String(item.entity!.id) }) : undefined,
    };
  } else if (current.facility) {
    const item = current.facility;
    root = {
      key: `facility:${item.id}`, ...stateFacility(item), roots: item.roots, buttons: [], openDetail: facilityDetail(item.id),
      icon: facilityIcon,
    };
  } else if (current.indicator?.issue) {
    root = indicatorRoot(current.indicator, `indicator:${current.indicator.id}`);
  } else if (current.indicator) {
    clearMessage = t(current.indicator.value == null ? "policy.noData" : `policy.indicators.${current.indicator.id}.clear`);
  }

  const rows: DiagramRow[] = [];
  if (root) {
    const indicatorId = root.indicatorId;
    rows.push({ id: "root", parentId: null, depth: 0, kind: "event", title: root.title, detail: root.detail,
      buttons: root.buttons, openDetail: root.openDetail });
    const append = (finding: Finding, parentId: string, depth: number, id: string, parentProductId?: number) => {
      // 供給範囲など品目を省略した原因は親の財を引き継ぎ、別の原材料をたどったらその財へ切り替える。
      const productId = finding.params.product_id ?? parentProductId;
      // 別の指標で調べる原因は、その指標に問題があるときだけ案内する。案内だけの節は、案内先に問題が無ければ出さない。
      const target = finding.indicator ?? FINDING_INDICATORS[finding.type];
      const reference = target !== indicatorId && indicators.find(item => item.id === target && item.issue);
      if (!reference && finding.type === "indicator_reference") return;
      // 別の施設の不調はここで止め、その施設を選んで原因を調べる。
      const referenced = finding.type === "facility_ref" ? facilityOf.get(finding.subject!.id) : undefined;
      if (referenced) {
        rows.push({ id, parentId, depth, kind: "reference", ...stateFacility(referenced), openDetail: facilityDetail(referenced.id), buttons: [{
          key: "investigate", label: t("policy.investigate.facility"), guide: "policy.investigate.facility",
          onClick: () => setSelected({ kind: "facility", id: referenced.id }),
        }] });
        return;
      }
      if (reference) {
        rows.push({ id, parentId, depth, kind: "reference", ...stateIndicator(reference), openDetail: indicatorDetail(reference.id), buttons: [{
          key: "investigate", label: t(`policy.investigate.${reference.id}`), guide: `policy.investigate.${reference.id}`,
          onClick: () => setSelected({ kind: "indicator", id: reference.id }),
        }] });
        return;
      }
      rows.push({ id, parentId, depth, kind: "cause", ...describe(finding, productId), buttons: actionButtons(finding.actions, productId, finding.type),
        openDetail: findingDetail(finding, productId) });
      finding.children.forEach((child, index) => append(child, id, depth + 1, `${id}.${index}`, productId));
    };
    root.roots.forEach((finding, index) => append(finding, "root", 1, String(index),
      indicatorId ? INDICATOR_PRODUCTS[indicatorId] : undefined));
  }

  // 施設とその他の問題は同じ形の行で並べ、警告は赤、注意は黄にする。
  const listRow = (key: string, guide: string, caution: boolean, pressed: boolean, message: string, date: string, onClick: () => void) =>
    <li key={key}>
      <button type="button" aria-pressed={pressed} data-guide={guide} onClick={onClick}
        className={`policy-problem${caution ? " policy-problem--caution" : ""}${pressed ? " policy-problem--selected" : ""}`}>
        <img src={caution ? infoIcon : crisisIcon} alt="" className="policy-problem-icon" draggable={false} />
        <span className="policy-problem-message">{message}</span>
        {date && <span className="policy-problem-date">{date}</span>}
      </button>
    </li>;

  return <div className="policy-planning" data-guide="policy.panel">
    {error && <p className="policy-error" role="alert">{t("common.fetchError")}: {error}</p>}
    {actionError && <p className="policy-error" role="alert">{t("policy.actionError")}</p>}
    {pending ? <p className="policy-message" role="status">{t("policy.pending")}</p>
      : loading ? <p className="policy-message" role="status">{t("common.loading")}</p>
      : report && <div className="policy-body">
        <nav className="policy-selector" aria-label={t("policy.selectorTitle")}>
          <h2 className="policy-selector-title">{t("policy.selectorTitle")}</h2>
          <section className="policy-section">
            <h3 className="policy-section-title">{t("policy.indicatorsTitle")}</h3>
            <div className="policy-indicators" role="group" aria-label={t("policy.indicatorsTitle")}>
              {indicators.map(item =>
                <button type="button" key={item.id} className={`policy-indicator${item.id === current.indicator?.id ? " policy-indicator--selected" : ""}`}
                  aria-pressed={item.id === current.indicator?.id} data-guide={`policy.indicator.${item.id}`}
                  onClick={() => setSelected({ kind: "indicator", id: item.id })}>
                  {t(`policy.indicators.${item.id}.tab`)}
                  {item.issue && <span role="img" aria-label={t("policy.needsAttention")}>⚠️</span>}
                </button>)}
            </div>
          </section>
          <div className="policy-problems">
            {facilities.length === 0 && problems.length === 0 && <p className="policy-message">{t("notifications.empty")}</p>}
            {facilities.length > 0 && <section className="policy-section">
              <h3 className="policy-section-title">{t("policy.facilitiesTitle")}</h3>
              <ul className="policy-problem-list">
                {facilities.map(item => listRow(`facility:${item.id}`, "policy.facility", item.severity === "caution",
                  current.facility?.id === item.id, stateFacility(item).title, "",
                  () => setSelected({ kind: "facility", id: item.id })))}
              </ul>
            </section>}
            {problems.length > 0 && <section className="policy-section">
              <h3 className="policy-section-title">{t("policy.otherProblemsTitle")}</h3>
              <ul className="policy-problem-list">
                {problems.map(item => listRow(item.id, `policy.problem.${item.type}`, isCautionNotification(item),
                  current.problem?.id === item.id, stateProblem(item), formatEpochDate(item.created_date, locale),
                  () => setSelected({ kind: "problem", id: item.id })))}
              </ul>
            </section>}
          </div>
        </nav>
        {root && <section className="policy-investigation" aria-label={root.title}>
          <div className="policy-tree-scroll" key={root.key}>
            <PolicyDiagram root={root} rows={rows} />
          </div>
        </section>}
        {clearMessage && <section className="policy-investigation policy-clear"><p role="status">{clearMessage}</p></section>}
      </div>}
  </div>;
}
