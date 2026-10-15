/**
 * システム統計ウィンドウ
 *
 * window_type="system_stats" のポップアップ。システムトレイから開く。
 * コアの `GET /api/v1/stats/system` と、Tauri が自分のPCで測る値(`get_client_system_stats`)を合わせて
 * 左右2列に並べる(タブは持たない)。コアは別のマシンで動くこともあるので、3DとPC全体の値はコアに頼まない。
 *   - 計算負荷 … 直近エポックの計算時間を大きな処理ごとに色分けした1本の積み上げ棒と、その内訳の表
 *   - エンティティ数 … プレイで増えるエンティティの数
 *   - システムステータス … ハードウェア情報、コアと3Dのメモリ使用量、PC全体と本ゲームのRAM/VRAM使用量の横棒
 *   - シミュレーションの近似 … 提供前の予告表示(値の保持・送信はしない)。設定領域全体をComing Soonのオーバーレイで覆う
 *
 * 計算時間はコアが記録済みのフェーズ別の時間を読むだけで、全体はフェーズの合計。計測値はセーブに
 * 含まれないので、ロード直後やエポックを実行する前は「データなし」を出す。問題規模は、主な比較の
 * 組み合わせ数をエンティティ数から見積もった桁の目安。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import { SettingsRow } from "../common/SettingsRow";
import "./SystemStatsWindow.css";

type GroupKey =
  | "market" | "realestate" | "labor" | "building_spawn" | "agent_spawn"
  | "routing" | "transit" | "environment" | "other";

type EpochGroup = {
  key: GroupKey;
  phase?: string | null; // コアのデバッグ機能が有効なときだけ、other をフェーズごとに分けたそのフェーズ名
  ms: number;
  workload: number | null;
  terms: Record<string, number>;
};

/** コアの `GET /api/v1/stats/system`。メモリはコアのプロセスの値。 */
type CoreSystemStats = {
  epoch: { number: number; simulation_days: number; total_ms: number; groups: EpochGroup[] } | null;
  entities: {
    agents: number;
    citizens: number;
    zones: number;
    parcels: number;
    paths: number;
    nodes: number;
  };
  memory: { current_bytes: number | null; peak_bytes: number | null };
  environment: { cpu_count: number | null; os: string; device: string };
};

/** Tauri の `get_client_system_stats`。Windows 以外では各値が null。 */
type ClientSystemStats = {
  memory: {
    system_total_bytes: number | null;
    used_bytes: number | null;
    graphic: { current_bytes: number; peak_bytes: number | null } | null;
  };
  vram: { used_bytes: number; graphic_bytes: number | null } | null;
  environment: { cpu_name: string | null; gpu_name: string | null; gpu_memory_bytes: number | null };
};

type SystemStats = {
  epoch: CoreSystemStats["epoch"];
  entities: CoreSystemStats["entities"];
  memory: CoreSystemStats["memory"] & ClientSystemStats["memory"];
  vram: ClientSystemStats["vram"];
  environment: CoreSystemStats["environment"] & ClientSystemStats["environment"];
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
  const [stats, setStats] = useState<SystemStats | null>(null);
  const [error, setError] = useState(false);

  const reload = useCallback(async (signal?: AbortSignal) => {
    try {
      const [core, client] = await Promise.all([
        fetchApi<CoreSystemStats>(buildApiUrl(apiBaseUrl, "/api/v1/stats/system"), { signal }),
        invoke<ClientSystemStats>("get_client_system_stats"),
      ]);
      if (signal?.aborted) return;
      setStats({
        epoch: core.epoch,
        entities: core.entities,
        memory: { ...core.memory, ...client.memory },
        vram: client.vram,
        environment: { ...core.environment, ...client.environment },
      });
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
    <div className="system-stats-frame">
      <div className="system-stats">
        <div className="system-stats-analysis">
          <div className="system-stats-overview">
            <SystemStatusSection stats={stats} formatters={formatters} />
            <EntitySection stats={stats} formatters={formatters} />
          </div>
          <EpochLoadSection stats={stats} formatters={formatters} />
        </div>
        <div className="system-stats-sidebar-frame">
          <aside className="system-stats-sidebar" aria-label={t("systemStats.approximation.title")}>
            <ApproximationSection />
          </aside>
          <div className="system-stats-coming-soon">
            <span className="system-stats-coming-soon-title">{t("systemStats.approximation.title")}</span>
            <span className="system-stats-coming-soon-label">{t("settings.compute.comingSoon")}</span>
          </div>
        </div>
      </div>
    </div>
  );
}

type Formatters = {
  count: Intl.NumberFormat;
  ms: Intl.NumberFormat;
  seconds: Intl.NumberFormat;
  percent: Intl.NumberFormat;
};

type SectionProps = {
  stats: SystemStats;
  formatters: Formatters;
};

function EpochLoadSection({ stats, formatters }: SectionProps) {
  const { t } = useTranslation();
  const [hovered, setHovered] = useState<string | null>(null);
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
  const rowId = (group: EpochGroup) => group.phase ?? group.key;
  const label = (group: EpochGroup) => group.phase ?? t(`systemStats.group.${group.key}`);
  const describe = (group: EpochGroup) =>
    `${label(group)}: ${t("systemStats.load.ms", { value: formatters.ms.format(group.ms) })} (${formatters.percent.format(share(group.ms))})`;
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
            key={rowId(group)}
            className={`system-stats-bar-segment${hovered && hovered !== rowId(group) ? " is-dimmed" : ""}`}
            style={{ flexGrow: share(group.ms) * 100, backgroundColor: GROUP_COLORS[group.key] }}
            title={describe(group)}
            onMouseEnter={() => setHovered(rowId(group))}
            onMouseLeave={() => setHovered(null)}
          />
        ))}
      </div>

      <table className="system-stats-table">
        <thead>
          <tr>
            <th scope="col">{t("systemStats.load.colGroup")}</th>
            <th scope="col" className="is-number">{t("systemStats.load.colTime")}</th>
            <th scope="col" className="is-number" title={t("systemStats.load.workloadHint")}>{t("systemStats.load.colWorkload")}</th>
          </tr>
        </thead>
        <tbody>
          {epoch.groups.map((group) => (
            <tr
              key={rowId(group)}
              className={hovered === rowId(group) ? "is-hovered" : undefined}
              onMouseEnter={() => setHovered(rowId(group))}
              onMouseLeave={() => setHovered(null)}
            >
              <td>
                <span className="system-stats-swatch" style={{ backgroundColor: GROUP_COLORS[group.key] }} aria-hidden="true" />
                {label(group)}
              </td>
              <td className="is-number">{t("systemStats.load.ms", { value: formatters.ms.format(group.ms) })}</td>
              <td className="is-number" title={group.workload != null ? formula(group) : undefined}>
                {group.workload != null ? formatters.count.format(group.workload) : "—"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function EntitySection({ stats, formatters }: SectionProps) {
  const { t } = useTranslation();
  const { entities } = stats;
  const fmt = (value: number) => formatters.count.format(value);

  const rows: { key: string; value: number }[] = [
    { key: "agents", value: entities.agents },
    { key: "citizens", value: entities.citizens },
    { key: "zones", value: entities.zones },
    { key: "parcels", value: entities.parcels },
    { key: "paths", value: entities.paths },
    { key: "nodes", value: entities.nodes },
  ];

  return (
    <section className="system-stats-section">
      <h3 className="system-stats-section-title">{t("systemStats.entities.title")}</h3>
      <table className="system-stats-table system-stats-entities">
        <thead>
          <tr>
            <th scope="col">{t("systemStats.entities.colMetric")}</th>
            <th scope="col" className="is-number">{t("systemStats.entities.colCount")}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.key}>
              <th scope="row">{t(`systemStats.entities.${row.key}`)}</th>
              <td className="is-number system-stats-entity-count">{fmt(row.value)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

/** バイト数を GB(1GB以上)か MB で表す。単位記号は言語によらず共通。 */
function formatBytes(bytes: number | null, formatters: Formatters): string {
  if (bytes == null) return "—";
  if (bytes >= 1e9) return `${formatters.seconds.format(bytes / 1e9)}GB`;
  return `${formatters.ms.format(bytes / 1e6)}MB`;
}

/** ドライバーやOSが返す利用可能容量を、製品の容量表記に近い整数GBで示す。 */
function formatCapacity(bytes: number, formatters: Formatters): string {
  return `${formatters.ms.format(bytes / 1024 ** 3)}GB`;
}

/**
 * 全体が搭載量の横棒。グレーがPC全体の使用量、緑が本ゲームの使用量(重ねて描く)。
 * 緑は常にグレーの内側に収める(計測の時刻のずれやプロセス間の共有ページで超えても、棒の意味を崩さない)。
 */
function UsageBar({ label, total, pcUsed, game, formatters }: {
  label: string;
  total: number;
  pcUsed: number | null;
  game: number;
  formatters: Formatters;
}) {
  const { t } = useTranslation();
  const used = pcUsed ?? game;
  const ratio = (bytes: number) => `${Math.min(1, Math.max(0, bytes / total)) * 100}%`;
  const tooltip = t("systemStats.status.usageTooltip", {
    pc: formatBytes(used, formatters),
    game: formatBytes(game, formatters),
    total: formatCapacity(total, formatters),
  });
  return (
    <div className="system-stats-usage" title={tooltip}>
      <div className="system-stats-usage-head">
        <span className="system-stats-core-label">{label}</span>
        <span>{formatBytes(used, formatters)} / {formatCapacity(total, formatters)}</span>
      </div>
      <div className="system-stats-usage-bar" role="img" aria-label={`${label} ${tooltip}`}>
        <div className="system-stats-usage-pc" style={{ width: ratio(used) }} />
        <div className="system-stats-usage-game" style={{ width: ratio(Math.min(game, used)) }} />
      </div>
    </div>
  );
}

function SystemStatusSection({ stats, formatters }: SectionProps) {
  const { t } = useTranslation();
  const { current_bytes: current, peak_bytes: peak, system_total_bytes: total, used_bytes: pcUsed, graphic } = stats.memory;
  const { vram } = stats;
  const env = stats.environment;
  const gameMemory = (current ?? 0) + (graphic?.current_bytes ?? 0);
  const ramBar = total && current != null ? { total, pcUsed: pcUsed ?? null, game: gameMemory } : null;
  const vramBar = vram && env.gpu_memory_bytes
    ? { total: env.gpu_memory_bytes, pcUsed: vram.used_bytes, game: vram.graphic_bytes ?? 0 }
    : null;

  return (
    <section className="system-stats-section">
      <h3 className="system-stats-section-title">{t("systemStats.status.title")}</h3>
      <div className="system-stats-status-text">
        <p>{env.cpu_name || "—"}</p>
        {env.gpu_name && <p>{env.gpu_name}{env.gpu_memory_bytes != null ? ` ${formatCapacity(env.gpu_memory_bytes, formatters)}` : ""}</p>}
        <p>{total != null ? formatCapacity(total, formatters) : "—"} RAM</p>
        <p>{t("systemStats.status.gpuDisabled")}</p>
      </div>
      <div className="system-stats-status-text system-stats-core-status">
        <p className="system-stats-core-label">[{t("systemStats.status.coreSimulator")}]</p>
        <p>{t("systemStats.status.currentMemory", { value: formatBytes(current, formatters) })}</p>
        <p>{t("systemStats.status.peakMemory", { value: formatBytes(peak, formatters) })}</p>
      </div>
      {graphic && <div className="system-stats-status-text system-stats-core-status">
        <p className="system-stats-core-label">[{t("systemStats.status.graphic")}]</p>
        <p>{t("systemStats.status.currentMemory", { value: formatBytes(graphic.current_bytes, formatters) })}</p>
        <p>{t("systemStats.status.peakMemory", { value: formatBytes(graphic.peak_bytes, formatters) })}</p>
        {vram?.graphic_bytes != null && <p>{t("systemStats.status.currentVram", { value: formatBytes(vram.graphic_bytes, formatters) })}</p>}
      </div>}
      {(ramBar || vramBar) && <div className="system-stats-usage-group">
        {ramBar && <UsageBar label="RAM" {...ramBar} formatters={formatters} />}
        {vramBar && <UsageBar label="VRAM" {...vramBar} formatters={formatters} />}
        <div className="system-stats-usage-legend">
          <span><i className="system-stats-usage-swatch is-pc" aria-hidden="true" />{t("systemStats.status.legendPc")}</span>
          <span><i className="system-stats-usage-swatch is-game" aria-hidden="true" />{t("systemStats.status.legendGame")}</span>
        </div>
      </div>}
    </section>
  );
}

/**
 * シミュレーションの近似(提供前の予告表示)。数値は未定のプレースホルダーで、値の保持・送信はしない。
 * 行は設定フォームの共通部品で描くので、この節だけ文字の寸法が px で固定になる(SettingsForm.css)。
 */
function ApproximationSection() {
  const { t } = useTranslation();
  return (
    <section className="system-stats-section system-stats-approximation">
      <h3 className="system-stats-section-title">{t("systemStats.approximation.title")}</h3>
      <div className="system-stats-warning" role="note">
        <strong>{t("systemStats.approximation.warning")}</strong>
      </div>
      <div className="settings-card">
        {APPROXIMATION_CANDIDATES.map((key) => (
          <SettingsRow
            key={key}
            label={t(`systemStats.approximation.${key}.label`)}
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
