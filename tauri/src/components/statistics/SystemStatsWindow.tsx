/**
 * システム統計ウィンドウ
 *
 * window_type="system_stats" のポップアップ。システムトレイから開く。
 * `GET /api/v1/stats/system` の内容を1枚に並べる(タブは持たない)。
 *   - 計算負荷 … 直近エポックの計算時間を大きな処理ごとに色分けした1本の積み上げ棒と、その内訳の表
 *   - 都市の規模 … プレイで増えるエンティティの数
 *   - メモリ・実行環境 … シミュレーター(コア)のプロセスのメモリと、CPU・OS
 *   - シミュレーションの近似 … 提供前の予告表示(値の保持・送信はしない)
 *
 * 計算時間はコアが記録済みのフェーズ別の時間を読むだけで、全体はフェーズの合計。計測値はセーブに
 * 含まれないので、ロード直後やエポックを実行する前は「データなし」を出す。問題規模は、主な比較の
 * 組み合わせ数をエンティティ数から見積もった桁の目安。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { listen } from "@tauri-apps/api/event";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { Meter } from "../common/Meter";
import { SettingsRow } from "../common/SettingsRow";
import "./SystemStatsWindow.css";

type GroupKey =
  | "market" | "realestate" | "labor" | "building_spawn" | "agent_spawn"
  | "routing" | "transit" | "environment" | "other";

type EpochGroup = {
  key: GroupKey;
  ms: number;
  workload: number | null;
  terms: Record<string, number>;
};

type SystemStatsResponse = {
  epoch: { number: number; simulation_days: number; total_ms: number; groups: EpochGroup[] } | null;
  entities: {
    agents: { households: number; businesses: number; public_facilities: number; buildings: number };
    citizens: number;
    population: number;
    zones: number;
    parcels: number;
    paths: { road: number; track: number; infra: number };
    nodes: number;
    transit_lines: number;
    epochs: number;
  };
  memory: { current_bytes: number | null; peak_bytes: number | null; system_total_bytes: number | null };
  environment: { cpu_name: string | null; cpu_count: number | null; os: string; device: string };
};

/**
 * 区分の色。dataviz の既定カテゴリ色(暗色面向け)を固定順で割り当て、--flat-surface(#16202e)で
 * 色覚多様性・コントラストの検証を通してある。色は区分に付くので、並びや件数が変わっても塗り替えない。
 */
const GROUP_COLORS: Record<GroupKey, string> = {
  market: "#3987e5",
  realestate: "#d95926",
  labor: "#199e70",
  building_spawn: "#c98500",
  agent_spawn: "#d55181",
  routing: "#008300",
  transit: "#9085e9",
  environment: "#e66767",
  other: "#4b5b6e",
};

const APPROXIMATION_CANDIDATES = ["goods", "jobs", "housing"] as const;

type SystemStatsWindowProps = {
  apiBaseUrl: string;
};

export function SystemStatsWindow({ apiBaseUrl }: SystemStatsWindowProps) {
  const { t, i18n } = useTranslation();
  const [stats, setStats] = useState<SystemStatsResponse | null>(null);
  const [error, setError] = useState(false);

  const reload = useCallback(async (signal?: AbortSignal) => {
    try {
      const res = await fetchApi<SystemStatsResponse>(buildApiUrl(apiBaseUrl, "/api/v1/stats/system"), { signal });
      setStats(res);
      setError(false);
    } catch (err) {
      if (signal?.aborted) return;
      console.error("[SystemStats] failed to fetch", err);
      setError(true);
    }
  }, [apiBaseUrl]);

  useEffect(() => {
    const controller = new AbortController();
    void reload(controller.signal);
    return () => controller.abort();
  }, [reload]);

  useEffect(() => {
    const unlisten = listen<{ executing: boolean }>("epoch:executing", (event) => {
      if (!event.payload.executing) void reload();
    });
    return () => {
      void unlisten.then((fn) => fn());
    };
  }, [reload]);

  const locale = resolveIntlLocale(i18n.language);
  const formatters = useMemo(() => ({
    count: new Intl.NumberFormat(locale),
    compact: new Intl.NumberFormat(locale, { notation: "compact", maximumSignificantDigits: 3 }),
    ms: new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }),
    seconds: new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
    percent: new Intl.NumberFormat(locale, { style: "percent", minimumFractionDigits: 1, maximumFractionDigits: 1 }),
  }), [locale]);

  if (error && !stats) {
    return <div className="system-stats system-stats--message">{t("common.fetchError")}</div>;
  }
  if (!stats) {
    return <div className="system-stats system-stats--message">{t("common.loading")}</div>;
  }

  return (
    <div className="system-stats">
      <EpochLoadSection stats={stats} formatters={formatters} />
      <EntitySection stats={stats} formatters={formatters} />
      <div className="system-stats-columns">
        <MemorySection stats={stats} formatters={formatters} />
        <EnvironmentSection stats={stats} formatters={formatters} />
      </div>
      <ApproximationSection />
    </div>
  );
}

type Formatters = {
  count: Intl.NumberFormat;
  compact: Intl.NumberFormat;
  ms: Intl.NumberFormat;
  seconds: Intl.NumberFormat;
  percent: Intl.NumberFormat;
};

type SectionProps = {
  stats: SystemStatsResponse;
  formatters: Formatters;
};

function EpochLoadSection({ stats, formatters }: SectionProps) {
  const { t } = useTranslation();
  const [hovered, setHovered] = useState<GroupKey | null>(null);
  const epoch = stats.epoch;

  if (!epoch || epoch.total_ms <= 0) {
    return (
      <section className="system-stats-section">
        <h3 className="system-stats-section-title">{t("systemStats.load.title")}</h3>
        <p className="system-stats-empty">{t("systemStats.load.noData")}</p>
      </section>
    );
  }

  const share = (ms: number) => ms / epoch.total_ms;
  const describe = (group: EpochGroup) =>
    `${t(`systemStats.group.${group.key}`)}: ${t("systemStats.load.ms", { value: formatters.ms.format(group.ms) })} (${formatters.percent.format(share(group.ms))})`;
  const formula = (group: EpochGroup) => {
    const terms = Object.fromEntries(Object.entries(group.terms).map(([key, value]) => [key, formatters.count.format(value)]));
    return t(`systemStats.workload.${group.key}`, terms);
  };

  return (
    <section className="system-stats-section">
      <h3 className="system-stats-section-title">{t("systemStats.load.title")}</h3>
      <div className="system-stats-headline">
        <span className="system-stats-headline-label">{t("systemStats.load.heading", { days: epoch.simulation_days })}</span>
        <span className="system-stats-headline-value">
          {t("systemStats.load.seconds", { value: formatters.seconds.format(epoch.total_ms / 1000) })}
        </span>
      </div>

      <div className="system-stats-bar" role="img" aria-label={epoch.groups.map(describe).join(", ")}>
        {epoch.groups.filter((group) => group.ms > 0).map((group) => (
          <div
            key={group.key}
            className={`system-stats-bar-segment${hovered && hovered !== group.key ? " is-dimmed" : ""}`}
            style={{ flexGrow: share(group.ms) * 100, backgroundColor: GROUP_COLORS[group.key] }}
            title={describe(group)}
            onMouseEnter={() => setHovered(group.key)}
            onMouseLeave={() => setHovered(null)}
          />
        ))}
      </div>

      <table className="system-stats-table">
        <thead>
          <tr>
            <th>{t("systemStats.load.colGroup")}</th>
            <th className="is-number">{t("systemStats.load.colTime")}</th>
            <th className="is-number">{t("systemStats.load.colShare")}</th>
            <th className="is-number" title={t("systemStats.load.workloadHint")}>{t("systemStats.load.colWorkload")}</th>
          </tr>
        </thead>
        <tbody>
          {epoch.groups.map((group) => (
            <tr
              key={group.key}
              className={hovered === group.key ? "is-hovered" : undefined}
              onMouseEnter={() => setHovered(group.key)}
              onMouseLeave={() => setHovered(null)}
            >
              <td>
                <span className="system-stats-swatch" style={{ backgroundColor: GROUP_COLORS[group.key] }} aria-hidden="true" />
                {t(`systemStats.group.${group.key}`)}
              </td>
              <td className="is-number">{t("systemStats.load.ms", { value: formatters.ms.format(group.ms) })}</td>
              <td className="is-number">{formatters.percent.format(share(group.ms))}</td>
              <td className="is-number" title={group.workload != null ? formula(group) : undefined}>
                {group.workload != null ? formatters.compact.format(group.workload) : "—"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="system-stats-note">{t("systemStats.load.note")}</p>
    </section>
  );
}

function EntitySection({ stats, formatters }: SectionProps) {
  const { t } = useTranslation();
  const { entities } = stats;
  const agents = entities.agents;
  const paths = entities.paths;
  const fmt = (value: number) => formatters.count.format(value);

  const tiles: { key: string; value: number; detail?: string; wide?: boolean }[] = [
    {
      key: "agents",
      wide: true,
      value: agents.households + agents.businesses + agents.public_facilities + agents.buildings,
      detail: [
        `${t("systemStats.entities.households")} ${fmt(agents.households)}`,
        `${t("systemStats.entities.businesses")} ${fmt(agents.businesses)}`,
        `${t("systemStats.entities.publicFacilities")} ${fmt(agents.public_facilities)}`,
        `${t("systemStats.entities.buildings")} ${fmt(agents.buildings)}`,
      ].join(" · "),
    },
    { key: "citizens", value: entities.citizens, detail: `${t("systemStats.entities.population")} ${fmt(entities.population)}` },
    { key: "zones", value: entities.zones },
    { key: "parcels", value: entities.parcels },
    {
      key: "paths",
      wide: true,
      value: paths.road + paths.track + paths.infra,
      detail: [
        `${t("systemStats.entities.roads")} ${fmt(paths.road)}`,
        `${t("systemStats.entities.tracks")} ${fmt(paths.track)}`,
        `${t("systemStats.entities.infra")} ${fmt(paths.infra)}`,
      ].join(" · "),
    },
    { key: "nodes", value: entities.nodes },
    { key: "transitLines", value: entities.transit_lines },
    { key: "epochs", value: entities.epochs },
  ];

  return (
    <section className="system-stats-section">
      <h3 className="system-stats-section-title">{t("systemStats.entities.title")}</h3>
      <div className="system-stats-tiles">
        {tiles.map((tile) => (
          <div key={tile.key} className={`system-stats-tile${tile.wide ? " is-wide" : ""}`}>
            <span className="system-stats-tile-label">{t(`systemStats.entities.${tile.key}`)}</span>
            <span className="system-stats-tile-value">{fmt(tile.value)}</span>
            {tile.detail && <span className="system-stats-tile-detail">{tile.detail}</span>}
          </div>
        ))}
      </div>
    </section>
  );
}

/** バイト数を GB(1GB以上)か MB で表す。単位記号は言語によらず共通。 */
function formatBytes(bytes: number | null, formatters: Formatters): string {
  if (bytes == null) return "—";
  if (bytes >= 1e9) return `${formatters.seconds.format(bytes / 1e9)} GB`;
  return `${formatters.ms.format(bytes / 1e6)} MB`;
}

function MemorySection({ stats, formatters }: SectionProps) {
  const { t } = useTranslation();
  const { current_bytes: current, peak_bytes: peak, system_total_bytes: total } = stats.memory;
  const peakRatio = peak != null && total ? peak / total : null;

  return (
    <section className="system-stats-section">
      <h3 className="system-stats-section-title">{t("systemStats.memory.title")}</h3>
      <dl className="system-stats-list">
        <dt>{t("systemStats.memory.current")}</dt>
        <dd>{formatBytes(current, formatters)}</dd>
        <dt>{t("systemStats.memory.peak")}</dt>
        <dd>{formatBytes(peak, formatters)}</dd>
        <dt>{t("systemStats.memory.total")}</dt>
        <dd>{formatBytes(total, formatters)}</dd>
      </dl>
      {peakRatio != null && (
        <div className="system-stats-meter">
          <Meter value={peakRatio} polarity="negative" />
          <span className="system-stats-note">{t("systemStats.memory.ofTotal", { percent: formatters.percent.format(peakRatio) })}</span>
        </div>
      )}
      <p className="system-stats-note">{t("systemStats.memory.note")}</p>
    </section>
  );
}

function EnvironmentSection({ stats, formatters }: SectionProps) {
  const { t } = useTranslation();
  const env = stats.environment;
  return (
    <section className="system-stats-section">
      <h3 className="system-stats-section-title">{t("systemStats.environment.title")}</h3>
      <dl className="system-stats-list">
        <dt>{t("systemStats.environment.cpu")}</dt>
        <dd>{env.cpu_name ?? "—"}</dd>
        <dt>{t("systemStats.environment.cores")}</dt>
        <dd>{env.cpu_count != null ? formatters.count.format(env.cpu_count) : "—"}</dd>
        <dt>{t("systemStats.environment.os")}</dt>
        <dd>{env.os || "—"}</dd>
        <dt>{t("systemStats.environment.device")}</dt>
        <dd>{t(`systemStats.environment.devices.${env.device}`, { defaultValue: env.device.toUpperCase() })}</dd>
      </dl>
    </section>
  );
}

/**
 * シミュレーションの近似(提供前の予告表示)。数値は未定のプレースホルダーで、値の保持・送信はしない。
 * 行は設定フォームの共通部品で描くので、この節だけ文字の寸法が px で固定になる(SettingsForm.css)。
 */
function ApproximationSection() {
  const { t } = useTranslation();
  const comingSoon = t("settings.compute.comingSoon");
  return (
    <section className="system-stats-section">
      <h3 className="system-stats-section-title">{t("systemStats.approximation.title")}</h3>
      <p className="system-stats-note">{t("systemStats.approximation.intro")}</p>
      <div className="system-stats-warning" role="note">
        <strong>{t("systemStats.approximation.warning")}</strong>
        <p>{t("systemStats.approximation.irreversible")}</p>
      </div>
      <div className="settings-card">
        {APPROXIMATION_CANDIDATES.map((key) => (
          <SettingsRow
            key={key}
            label={t(`systemStats.approximation.${key}.label`)}
            description={t(`systemStats.approximation.${key}.description`)}
            badge={comingSoon}
            disabled
          >
            <input
              className="settings-number"
              type="number"
              aria-label={t(`systemStats.approximation.${key}.label`)}
              placeholder={t("systemStats.approximation.undecided")}
              disabled
            />
            <span className="settings-unit">{t("systemStats.approximation.candidates")}</span>
          </SettingsRow>
        ))}
        <SettingsRow
          label={t("systemStats.approximation.interval.label")}
          description={t("systemStats.approximation.interval.description")}
          badge={comingSoon}
          disabled
        >
          <input
            className="settings-number"
            type="number"
            aria-label={t("systemStats.approximation.interval.label")}
            placeholder={t("systemStats.approximation.undecided")}
            disabled
          />
          <span className="settings-unit">{t("systemStats.approximation.epochs")}</span>
        </SettingsRow>
        <SettingsRow
          label={t("systemStats.approximation.aggregation.label")}
          description={t("systemStats.approximation.aggregation.description")}
          badge={comingSoon}
          disabled
        >
          <div className="settings-seg" role="group" aria-label={t("systemStats.approximation.aggregation.label")}>
            <button className="settings-seg-btn active" type="button" aria-pressed disabled>{t("systemStats.approximation.off")}</button>
            <button className="settings-seg-btn" type="button" aria-pressed={false} disabled>{t("systemStats.approximation.on")}</button>
          </div>
        </SettingsRow>
      </div>
    </section>
  );
}
