/**
 * 市政府ウィンドウ
 *
 * window_type="city_government" のポップアップ。コックピットの都市名をクリックして開く。
 * 左に都市の概要(都市名・人口・失業率・平均賃金・平均家賃・都市スコア)、右に公的な経済主体の一覧を
 * カテゴリのタブで切り替えて並べる。公共サービスを供給する施設は公共サービスウィンドウが扱うので、ここには出ない。
 *
 * 都市名は `PATCH /api/v1/city/` の `name` で変更し、グローバルイベント `city:renamed` を送出する。
 * HUDの都市名はRust側のセッションポーリング(数秒間隔)で更新されるため、待たずに即時反映させるための通知。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { formatExactAmount } from "../../utils/money";
import type { GovernmentAgent } from "../../types";
import { EditableLabel } from "../common/EditableLabel";
import { FlatTabs } from "../common/FlatTabs";
import { SortableAgentTable, type SortableAgentColumn } from "../common/SortableAgentTable";
import { StatusBadge } from "../common/StatusBadge";
import "./CityGovernmentWindow.css";

/** 都市名の最大文字数。UI(コックピット)に収まる範囲かつセーブ名として現実的な長さに制限する。 */
const CITY_NAME_MAX_LENGTH = 40;

type CityOverview = {
  name: string;
  population: number;
  unemployment_rate: number;
  average_wage: number;
  average_rent: number;
  city_score: number;
};

type MetricKey = Exclude<keyof GovernmentAgent, "agent_id" | "name" | "status">;

type GroupKey = "real_estate" | "public_standard" | "open_space" | "education" | "port" | "intercity" | "city";

const STANDARD_METRICS: MetricKey[] = ["production", "sold_amount", "price", "balance", "utilization_rate"];

// key は `GET /api/v1/city/government-agents` が返すカテゴリのキーと一致させる。
const GROUPS: { key: GroupKey; labelKey: string; metrics: MetricKey[] }[] = [
  { key: "real_estate", labelKey: "cityGovernment.tabRealEstate", metrics: ["supply_floor", "occupancy_rate", "rent", "balance", "utilization_rate"] },
  { key: "public_standard", labelKey: "cityGovernment.tabPublicStandard", metrics: STANDARD_METRICS },
  { key: "open_space", labelKey: "cityGovernment.tabOpenSpace", metrics: [] },
  { key: "education", labelKey: "cityGovernment.tabEducation", metrics: [] },
  { key: "port", labelKey: "cityGovernment.tabPort", metrics: ["capacity", "import_handling", "export_handling", "utilization_rate"] },
  { key: "intercity", labelKey: "cityGovernment.tabIntercity", metrics: STANDARD_METRICS },
  { key: "city", labelKey: "cityGovernment.tabCity", metrics: [] },
];

type Format = "integer" | "decimal" | "percent" | "tons";

const METRICS: Record<MetricKey, { labelKey: string; format: Format }> = {
  supply_floor: { labelKey: "cityGovernment.supplyFloor", format: "integer" },
  occupancy_rate: { labelKey: "cityGovernment.occupancyRate", format: "percent" },
  rent: { labelKey: "cityGovernment.rent", format: "integer" },
  capacity: { labelKey: "cityGovernment.capacity", format: "tons" },
  import_handling: { labelKey: "cityGovernment.importHandling", format: "tons" },
  export_handling: { labelKey: "cityGovernment.exportHandling", format: "tons" },
  production: { labelKey: "cityGovernment.production", format: "decimal" },
  sold_amount: { labelKey: "cityGovernment.soldAmount", format: "decimal" },
  price: { labelKey: "cityGovernment.price", format: "decimal" },
  balance: { labelKey: "publicFacilities.balance", format: "integer" },
  utilization_rate: { labelKey: "publicFacilities.utilizationRate", format: "percent" },
};

function formatMetric(value: number, format: Format, locale: string): string {
  switch (format) {
    case "percent":
      return `${Math.round(value * 100)}%`;
    case "decimal":
      return value.toLocaleString(locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    case "tons":
      return `${value.toLocaleString(locale, { maximumFractionDigits: 0 })} t`;
    default:
      return value.toLocaleString(locale, { maximumFractionDigits: 0 });
  }
}

/** 一覧の列。全カテゴリ共通のステータス・名前に、カテゴリのメトリクス列を続ける。 */
function buildColumns(
  metrics: MetricKey[],
  t: ReturnType<typeof useTranslation>["t"],
  locale: string,
): SortableAgentColumn<GovernmentAgent>[] {
  return [
    {
      key: "status",
      label: t("agentList.colStatus"),
      align: "center",
      render: (a) => <StatusBadge status={a.status} />,
      sortValue: (a) => a.status,
    },
    {
      key: "name",
      label: t("agentList.colName"),
      render: (a) => a.name,
      sortValue: (a) => a.name,
    },
    ...metrics.map((key) => ({
      key,
      label: t(METRICS[key].labelKey),
      align: "right" as const,
      render: (a: GovernmentAgent) => {
        const value = a[key];
        return value == null ? "—" : formatMetric(value, METRICS[key].format, locale);
      },
      sortValue: (a: GovernmentAgent) => a[key] ?? 0,
    })),
  ];
}

export function CityGovernmentWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);

  const [overview, setOverview] = useState<CityOverview | null>(null);
  const [groups, setGroups] = useState<Record<string, GovernmentAgent[]>>({});
  const [activeGroup, setActiveGroup] = useState<GroupKey>(GROUPS[0].key);
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const [cityRes, agentsRes] = await Promise.all([
          fetchApi<{ city: CityOverview }>(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
            signal: controller.signal,
          }),
          fetchApi<{ groups: Record<string, GovernmentAgent[]> }>(
            buildApiUrl(apiBaseUrl, "/api/v1/city/government-agents"),
            { signal: controller.signal },
          ),
        ]);
        setOverview(cityRes.city);
        setGroups(agentsRes.groups);
      } catch (err) {
        if (controller.signal.aborted) return;
        console.error("[CityGovernment] failed to fetch", err);
        setError(t("common.fetchError"));
      }
    })();
    return () => {
      controller.abort();
    };
  }, [apiBaseUrl, t]);

  const handleRename = useCallback(
    async (name: string) => {
      await fetchApi(buildApiUrl(apiBaseUrl, "/api/v1/city/"), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name }),
      });
      setOverview((prev) => (prev ? { ...prev, name } : prev));
      await emit("city:renamed", { name });
    },
    [apiBaseUrl],
  );

  const group = GROUPS.find((g) => g.key === activeGroup) ?? GROUPS[0];
  const columns = useMemo(() => buildColumns(group.metrics, t, locale), [group, t, locale]);
  const tabs = useMemo(() => GROUPS.map((g) => ({ id: g.key, label: t(g.labelKey) })), [t]);

  if (error) {
    return <div className="city-gov__message city-gov__message--error">{error}</div>;
  }
  if (!overview) {
    return <div className="city-gov__message">{t("common.loading")}</div>;
  }

  const stats: { key: string; label: string; value: string; unit?: string }[] = [
    {
      key: "population",
      label: t("cityGovernment.population"),
      value: overview.population.toLocaleString(locale),
      unit: t("cityGovernment.unitPeople"),
    },
    {
      key: "unemployment",
      label: t("cityGovernment.unemploymentRate"),
      value: `${(overview.unemployment_rate * 100).toFixed(1)}%`,
    },
    {
      key: "wage",
      label: t("cityGovernment.averageWage"),
      value: formatExactAmount(Math.round(overview.average_wage), locale, true),
      unit: t("cityGovernment.unitPerPersonDay"),
    },
    {
      key: "rent",
      label: t("cityGovernment.averageRent"),
      value: formatExactAmount(Math.round(overview.average_rent), locale, true),
      unit: t("cityGovernment.unitPerAreaDay"),
    },
    {
      key: "score",
      label: t("cityGovernment.cityScore"),
      value: overview.city_score.toLocaleString(locale, { maximumFractionDigits: 1 }),
    },
  ];

  return (
    <div className="city-gov">
      <section className="city-gov__overview">
        <EditableLabel
          value={overview.name}
          maxLength={CITY_NAME_MAX_LENGTH}
          textClassName="city-gov__name"
          onSave={handleRename}
        />
        <dl className="city-gov__stats">
          {stats.map((stat) => (
            <div key={stat.key} className="city-gov__stat">
              <dt>{stat.label}</dt>
              <dd>
                {stat.value}
                {stat.unit && <span className="city-gov__unit">{stat.unit}</span>}
              </dd>
            </div>
          ))}
        </dl>
      </section>

      <section className="city-gov__agents">
        <FlatTabs<GroupKey>
          tabs={tabs}
          active={activeGroup}
          onChange={setActiveGroup}
          label={t("cityGovernment.categories")}
        />
        <SortableAgentTable
          key={activeGroup}
          columns={columns}
          rows={groups[activeGroup] ?? []}
          emptyText={t("cityGovernment.empty")}
        />
      </section>
    </div>
  );
}
