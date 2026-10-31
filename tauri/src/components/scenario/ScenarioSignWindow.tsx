/** Development-only signing screen. Do not read key contents; pass only the file path to the backend. */
import { useEffect, useState } from "react";
import { open, save } from "@tauri-apps/plugin-dialog";
import { emit } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { saveError, scenarioDisplayTitle } from "../../utils/saves";
import "./Scenarios.css";

type ScenarioEntry = Parameters<typeof scenarioDisplayTitle>[0];
export type ScenarioCatalog = {
  scenarios: Record<string, ScenarioEntry & { scenario_id?: string }>;
  upload_available: boolean; upload_host: string | null; signing_available?: boolean;
};
type SignResult = { output_path: string; encrypted: boolean };
const SEGMENT = /^[A-Za-z0-9_-]{1,128}$/;

export function ScenarioSignWindow({ apiBaseUrl, scenarioId, onClose }: {
  apiBaseUrl: string; scenarioId: string; onClose: () => void;
}) {
  const { t, i18n } = useTranslation();
  const [entry, setEntry] = useState<ScenarioEntry | null>(null);
  const [available, setAvailable] = useState(false);
  const [encrypt, setEncrypt] = useState(false);
  const [values, setValues] = useState({ private_key_path: "", kid: "", scenario_id: "", content_revision: "1", output_path: "", pack_id: "", keyring_path: "" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [result, setResult] = useState<SignResult | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    void fetchApi<ScenarioCatalog>(buildApiUrl(apiBaseUrl, "/api/v1/scenarios"), { signal: controller.signal, cache: "reload" })
      .then(catalog => {
        const source = catalog.scenarios[scenarioId];
        if (!catalog.signing_available) { setError(t("scenarios.signing.unavailable")); return; }
        if (!source || source.origin !== "local" || source.read_only) { setError(t("scenarios.readOnlyHint")); return; }
        setAvailable(true); setEntry(source);
        setEncrypt(!!source.required_packs?.length);
        setValues(current => ({ ...current, scenario_id: source.scenario_id || source.id.replace(/^local~/, ""),
          content_revision: String(source.content_revision || 1), pack_id: source.required_packs?.[0] || "" }));
      }).catch(e => { if (!controller.signal.aborted) setError(saveError(e, t)); });
    return () => controller.abort();
  }, [apiBaseUrl, scenarioId, t]);

  const revision = Number(values.content_revision);
  const ready = available && !!entry && !!values.private_key_path.trim() && !!values.output_path.trim()
    && SEGMENT.test(values.kid) && SEGMENT.test(values.scenario_id) && Number.isSafeInteger(revision) && revision >= 1
    && (!encrypt || (SEGMENT.test(values.pack_id) && !!values.keyring_path.trim()));
  const update = (key: keyof typeof values, value: string) => {
    setValues(current => ({ ...current, [key]: value })); setResult(null); setError("");
  };

  async function choosePath(key: "private_key_path" | "keyring_path" | "output_path") {
    try {
      const path = key === "output_path"
        ? await save({ defaultPath: `${values.scenario_id || "scenario"}-signed.cg`, filters: [{ name: "City Genesis", extensions: ["cg"] }] })
        : await open({ multiple: false, directory: false,
          filters: [{ name: key === "private_key_path" ? t("scenarios.signing.privateKey") : t("scenarios.signing.keyring"),
            extensions: key === "private_key_path" ? ["key"] : ["json"] }] });
      if (typeof path === "string") update(key, path);
    } catch { setError(t("scenarios.signing.fileDialogFailed")); }
  }

  async function submit() {
    if (!ready || busy) return;
    setBusy(true); setError(""); setResult(null);
    try {
      const response = await fetchApi<SignResult>(buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(scenarioId)}/sign`), {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({
          private_key_path: values.private_key_path.trim(), kid: values.kid, scenario_id: values.scenario_id,
          content_revision: revision, output_path: values.output_path.trim(),
          pack_id: encrypt ? values.pack_id : null, keyring_path: encrypt ? values.keyring_path.trim() : null,
        }),
      });
      setResult(response);
      await emit("scenarios:changed");
    } catch (e) {
      setError(saveError(e, t));
    } finally { setBusy(false); }
  }

  const textField = (key: keyof typeof values, label: string, numeric = false) => <label className="scenario-sign-row">
    <span>{label}</span><input aria-label={label} type={numeric ? "number" : "text"} min={numeric ? 1 : undefined}
      step={numeric ? 1 : undefined} maxLength={numeric ? undefined : 128} value={values[key]} disabled={busy || !available}
      onChange={e => update(key, e.target.value)} />
  </label>;
  const pathField = (key: "private_key_path" | "keyring_path" | "output_path", label: string) => <div className="scenario-sign-row">
    <label htmlFor={`sign-${key}`}>{label}</label><div className="scenario-sign-file">
      <input id={`sign-${key}`} type="text" value={values[key]} maxLength={4096} disabled={busy || !available}
        onChange={e => update(key, e.target.value)} />
      <button type="button" disabled={busy || !available} aria-label={label} onClick={() => void choosePath(key)}>{t("scenarios.chooseFile")}</button>
    </div>
  </div>;

  return <form className="scenario-editor scenario-sign" onSubmit={e => { e.preventDefault(); void submit(); }} aria-busy={busy}>
    {entry && <h3>{scenarioDisplayTitle(entry, i18n.language)}</h3>}
    <p className="scenario-hint">{t("scenarios.signing.hint")}</p>
    {error && <p className="scenario-error" role="alert">{error}</p>}
    <fieldset className="scenario-sign-types" disabled={busy || !available || !!entry?.required_packs?.length}>
      <legend>{t("scenarios.signing.scenarioType")}</legend>
      {[false, true].map(encrypted => <label key={String(encrypted)}>
        <input type="radio" name="scenario-type" value={encrypted ? "dlc" : "official"} checked={encrypt === encrypted}
          onChange={() => { setEncrypt(encrypted); setResult(null); setError(""); }} />
        {t(encrypted ? "scenarios.signing.dlc" : "scenarios.signing.official")}
      </label>)}
    </fieldset>
    {!!entry?.required_packs?.length && <p className="scenario-hint">{t("scenarios.signing.packsHint", { packs: entry.required_packs.join(", ") })}</p>}
    {textField("scenario_id", t("scenarios.signing.scenarioId"))}
    {textField("content_revision", t("scenarios.signing.revision"), true)}
    <section className="scenario-group" aria-label={t("scenarios.signing.signature")}>
      <h4 className="scenario-group-title">{t("scenarios.signing.signature")}</h4>
      {pathField("private_key_path", t("scenarios.signing.privateKey"))}
      {textField("kid", t("scenarios.signing.keyId"))}
    </section>
    {encrypt && <section className="scenario-group" aria-label={t("scenarios.signing.encryption")}>
      <h4 className="scenario-group-title">{t("scenarios.signing.encryption")}</h4>
      <p className="scenario-hint">{t("scenarios.signing.encryptionHint")}</p>
      {textField("pack_id", t("scenarios.signing.packId"))}
      {pathField("keyring_path", t("scenarios.signing.keyring"))}
    </section>}
    {pathField("output_path", t("scenarios.signing.output"))}
    {result && <p className="scenario-status" role="status">{t(result.encrypted ? "scenarios.signing.completedEncrypted" : "scenarios.signing.completed", { path: result.output_path })}</p>}
    <div className="scenario-import-actions">
      <button type="button" disabled={busy} onClick={onClose}>{t("scenarios.close")}</button>
      <button type="submit" disabled={busy || !ready}>{t(busy ? "scenarios.signing.processing" : "scenarios.signing.execute")}</button>
    </div>
  </form>;
}
