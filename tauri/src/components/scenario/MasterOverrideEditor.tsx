import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";

type Entry = { table: string; row: string; column: string; value: string };
type StandardMaster = Record<string, Record<string, Record<string, unknown>>>;

/** Standard value representation, also used as the input's initial value. Ensure parsing it as YAML yields the same value. */
function formatValue(value: unknown): string {
  return typeof value === "string" ? value : JSON.stringify(value ?? null);
}

/**
 * Edit master overrides for the city open in the scenario editor (development only; shown in scenario tools when
 * the core has is_debug_enabled).
 *
 * Overrides belong to city data. Select tables, rows, and columns from standard master data, and enter values as
 * YAML. Save only replaces stored overrides; it does not change the open city's master data. Save and reopen the
 * scenario to apply them.
 */
export function MasterOverrideEditor({ apiBaseUrl, enabled, onError }: {
  apiBaseUrl: string;
  enabled: boolean;
  onError: (error: unknown) => void;
}) {
  const { t } = useTranslation();
  const [entries, setEntries] = useState<Entry[] | null>(null);
  const [standard, setStandard] = useState<StandardMaster | null>(null);
  const [draft, setDraft] = useState({ table: "", row: "", column: "" });
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    void fetchApi<{ entries: Entry[] }>(buildApiUrl(apiBaseUrl, "/api/v1/scenarios/master_override"), { signal: controller.signal })
      .then(value => { if (!controller.signal.aborted) setEntries(value.entries); })
      .catch(error => { if (!controller.signal.aborted) onError(error); });
    void fetchApi<StandardMaster>(buildApiUrl(apiBaseUrl, "/api/v1/scenarios/master_override/standard"), { signal: controller.signal })
      .then(value => { if (!controller.signal.aborted) setStandard(value); })
      .catch(error => { if (!controller.signal.aborted) onError(error); });
    return () => controller.abort();
  }, [apiBaseUrl, enabled, onError]);

  async function save(list: Entry[]) {
    setSaving(true);
    try {
      const value = await fetchApi<{ entries: Entry[] }>(buildApiUrl(apiBaseUrl, "/api/v1/scenarios/master_override"), {
        method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ entries: list }),
      });
      setEntries(value.entries);
    } catch (error) {
      onError(error);
    } finally {
      setSaving(false);
    }
  }

  const tables = useMemo(() => Object.keys(standard ?? {}).filter(table =>
    Object.values(standard?.[table] ?? {}).some(row => row && typeof row === "object")), [standard]);
  const rows = useMemo(() => Object.entries(standard?.[draft.table] ?? {}).filter(([, row]) => row && typeof row === "object"), [standard, draft.table]);
  const columns = useMemo(() => {
    const keys = new Set<string>();
    for (const [, row] of rows) for (const key of Object.keys(row)) keys.add(key);
    return [...keys].sort();
  }, [rows]);
  const standardValue = (entry: { table: string; row: string; column: string }) => {
    const row = standard?.[entry.table]?.[entry.row];
    return row && entry.column in row ? formatValue(row[entry.column]) : "—";
  };
  const rowLabel = (id: string, row: Record<string, unknown>) =>
    typeof row.display_name === "string" ? `${id} ${row.display_name}` : id;

  if (entries === null) return <p>{t("common.loading")}</p>;
  const busy = !enabled || saving;
  return <div className="master-override">
    <p>{t("scenarioTools.masterOverride.help")}</p>
    <table className="master-override__entries"><thead><tr>
      <th>{t("scenarioTools.masterOverride.table")}</th><th>{t("scenarioTools.masterOverride.row")}</th>
      <th>{t("scenarioTools.masterOverride.column")}</th><th>{t("scenarioTools.masterOverride.standard")}</th>
      <th>{t("scenarioTools.masterOverride.value")}</th><th />
    </tr></thead><tbody>{entries.map((entry, index) => <tr key={`${entry.table}:${entry.row}:${entry.column}`}>
      <td>{entry.table}</td><td>{entry.row}</td><td>{entry.column}</td><td>{standardValue(entry)}</td>
      <td><input value={entry.value} disabled={busy} aria-label={`${entry.table} ${entry.row} ${entry.column}`}
        onChange={event => { const value = event.currentTarget.value; setEntries(list => list && list.map((old, i) => i === index ? { ...old, value } : old)); }} /></td>
      <td><button type="button" disabled={busy} onClick={() => setEntries(list => list && list.filter((_, i) => i !== index))}>
        {t("scenarioTools.masterOverride.remove")}</button></td>
    </tr>)}</tbody></table>
    <div className="master-override__add">
      <select value={draft.table} disabled={busy} aria-label={t("scenarioTools.masterOverride.table")}
        onChange={event => setDraft({ table: event.currentTarget.value, row: "", column: "" })}>
        <option value="">{t("scenarioTools.masterOverride.table")}</option>
        {tables.map(table => <option key={table} value={table}>{table}</option>)}
      </select>
      <select value={draft.row} disabled={busy || !draft.table} aria-label={t("scenarioTools.masterOverride.row")}
        onChange={event => { const row = event.currentTarget.value; setDraft(value => ({ ...value, row })); }}>
        <option value="">{t("scenarioTools.masterOverride.row")}</option>
        {rows.map(([id, row]) => <option key={id} value={id}>{rowLabel(id, row)}</option>)}
      </select>
      <select value={draft.column} disabled={busy || !draft.row} aria-label={t("scenarioTools.masterOverride.column")}
        onChange={event => { const column = event.currentTarget.value; setDraft(value => ({ ...value, column })); }}>
        <option value="">{t("scenarioTools.masterOverride.column")}</option>
        {columns.map(column => <option key={column} value={column}>{column}</option>)}
      </select>
      <button type="button" disabled={busy || !draft.column || entries.some(entry =>
        entry.table === draft.table && entry.row === draft.row && entry.column === draft.column)}
        onClick={() => {
          const value = standardValue(draft);
          setEntries(list => list && [...list, { ...draft, value: value === "—" ? "" : value }]);
        }}>{t("scenarioTools.masterOverride.add")}</button>
    </div>
    <button type="button" className="master-override__save" disabled={busy} onClick={() => void save(entries)}>
      {t("scenarioTools.masterOverride.save")}</button>
  </div>;
}
