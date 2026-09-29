/**
 * シナリオ管理ウィンドウ(トップメニューの「シナリオ管理」から開く。Unityは使わない)。
 *
 * - シナリオタブ: 自作・コミュニティ(Workshop)・DLC のシナリオを1つの一覧に並べる。
 *   自作は都市・地形・紹介情報を編集でき、アップロードもできる。
 *   コミュニティ・DLC は読み取り専用なので「コピーして編集」だけを出す(保存すると自作になる)。
 *   DLC 由来の暗号化された自作シナリオは地形を編集できない(作業コピーが平文でディスクに残るため、コアが
 *   SCENARIO_ENCRYPTED で断る)ので、「地形を編集」を無効にして理由をツールチップで示す。
 * - 地形を取り込むタブ: PC内のどこにある標高ファイル(グレースケールPNG・.npy)でも選び、新しい自作シナリオにする
 *   (ゲームは始まらない)。ゲームは地形を同梱しないので、新しい地形の入口はここだけ。タイトルは1つの文で、
 *   コアが英語とアプリ設定の言語の両方に入れる。作成直後に地形の平面図をシナリオ画像として登録し(失敗しても
 *   作成は成功のまま)、シナリオタブへ切り替えて新しいカードを選択状態にする。
 *
 * 都市の編集は create_session でポップアップをすべて閉じるので、確認文で地形エディタの未保存の編集に触れる。
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { errorCode, prepareSaveLoad, SaveEntry, saveError, scenarioDisplayTitle } from "../../utils/saves";
import { requestConfirm } from "../../utils/confirmDialog";
import { loadAppSettings } from "../../utils/settings";
import { resolveLocalizedText, resolveSupportedLanguage } from "../../utils/locale";
import { formatMapSize } from "../../utils/mapSize";
import { FlatTabs } from "../common/FlatTabs";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import { ScenarioOriginBadges } from "../common/ScenarioSource";
import {
  HEIGHTMAP_LIMITS, heightmapFormatOfName, importHeightmap, importedMapExtent, readHeightmapInfo, registerTerrainThumbnail,
  type HeightmapImportRequest, type HeightmapInfo,
} from "./heightmapImport";
import "./Scenarios.css";

type Catalog = { scenarios: Record<string, SaveEntry>; upload_available: boolean; upload_host: string | null };
type Tab = "scenarios" | "terrains";

export function ScenarioManagerWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const [tab, setTab] = useState<Tab>("scenarios");
  const [terrainOpened, setTerrainOpened] = useState(false);
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("");
  const grid = useRef<HTMLDivElement>(null);
  const reload = useCallback(async () => {
    setCatalog(await fetchApi<Catalog>(buildApiUrl(apiBaseUrl, "/api/v1/scenarios"), { cache: "reload" }));
  }, [apiBaseUrl]);
  useEffect(() => {
    void reload().catch(e => setError(saveError(e, t)));
    const listener = listen("scenarios:changed", () => { void reload().catch(e => setError(saveError(e, t))); });
    return () => { void listener.then(unlisten => unlisten()); };
  }, [reload, t]);
  // 作成直後のカードは一覧のどこに並ぶか分からないので、選択したカードを見える位置まで送る。
  useEffect(() => {
    if (tab !== "scenarios" || !selectedId) return;
    const card = [...grid.current?.querySelectorAll<HTMLElement>("[data-scenario-id]") ?? []]
      .find(element => element.dataset.scenarioId === selectedId);
    card?.scrollIntoView?.({ block: "nearest" });
  }, [tab, selectedId, catalog]);

  /** 操作を1つずつ実行し、失敗は理由コードで翻訳して出す。成功したかを返す */
  async function run(operation: () => Promise<unknown>): Promise<boolean> {
    setBusy(true); setError(""); setStatus("");
    try { await operation(); return true; } catch (e) { setError(saveError(e, t)); return false; } finally { setBusy(false); }
  }
  /** 都市を神モード(template_mode)で開く。自作は上書き、コミュニティ・DLC はコピーとして保存される。 */
  async function editCity(entry: SaveEntry, copy: boolean) {
    if (!await requestConfirm({ message: t(copy ? "scenarios.copyConfirm" : "scenarios.editConfirm") })) return;
    if (!await prepareSaveLoad(apiBaseUrl, entry.id, true, t)) return;
    await invoke("create_session", { templateId: entry.id, templateMode: true, acceptBalanceChange: true });
  }
  /**
   * シナリオの地形を作業フォルダへ書き出してから地形エディタを開く(セッションは使わない)。
   * コアの理由コードはそのまま翻訳し、コードの無い Rust 側の失敗(本文が日本語固定)は汎用の文言にして本文はログに残す。
   */
  async function editTerrain(entry: SaveEntry) {
    const editorId = await invoke<string>("prepare_scenario_terrain_editor", { scenarioId: entry.id }).catch((e: unknown) => {
      if (errorCode(e)) throw e;
      console.error("[ScenarioManager] failed to prepare the terrain editor", e);
      throw new Error(t("scenarios.terrainOpenFailed"));
    });
    await invoke("open_registered_window", { windowType: "terrain_editor", entityId: editorId });
  }
  async function createFromHeightmap(request: HeightmapImportRequest, format: HeightmapInfo["format"]) {
    const settings = await loadAppSettings().catch(() => null);
    const { id } = await importHeightmap(apiBaseUrl, request, resolveSupportedLanguage(settings?.language || i18n.language), format);
    // 画像は後から紹介情報で差し替えられるので、平面図を作れなくてもシナリオの作成は成功とする。
    await registerTerrainThumbnail(apiBaseUrl, id).catch((e: unknown) => console.error("[ScenarioManager] failed to register the terrain thumbnail", e));
    await reload();
    setSelectedId(id);
    setTab("scenarios");
    setStatus(t("scenarios.created"));
    void emit("scenarios:changed");
  }

  const entries = catalog ? Object.values(catalog.scenarios) : [];
  return <div className="scenario-manager">
    <FlatTabs<Tab>
      label={t("scenarios.manage")}
      active={tab}
      onChange={next => { setTab(next); setError(""); setStatus(""); if (next === "terrains") setTerrainOpened(true); }}
      tabs={[{ id: "scenarios", label: t("scenarios.tabScenarios") }, { id: "terrains", label: t("scenarios.tabTerrains") }]}
    />
    {tab === "scenarios" && <div className="scenario-manager-body">
      <div className="scenario-toolbar">
        {catalog && !catalog.upload_available ? <p className="scenario-hint">{t("scenarios.uploadUnconfigured")}</p> : <span />}
        <button type="button" disabled={busy} onClick={() => void run(reload)}>{t("scenarios.refresh")}</button>
      </div>
      {error && <p className="scenario-error" role="alert">{error}</p>}
      {status && <p className="scenario-status" role="status">{status}</p>}
      <div ref={grid} className="scenario-grid" aria-busy={busy || !catalog}>
        {catalog && entries.map(entry => {
          const own = entry.origin === "local" && !entry.read_only;
          const loadable = entry.compatibility.loadable;
          const description = resolveLocalizedText(entry.scenario_description, i18n.language);
          return <article key={entry.id} data-scenario-id={entry.id} aria-current={entry.id === selectedId ? "true" : undefined}
            className={`scenario-card${entry.id === selectedId ? " scenario-card--selected" : ""}`} onClick={() => setSelectedId(entry.id)}>
            <ScenarioThumbnail apiBaseUrl={apiBaseUrl} templateId={entry.id} revision={entry.content_revision} className="scenario-thumbnail" />
            <div className="scenario-card-content">
              <ScenarioOriginBadges entry={entry} />
              <h3>{scenarioDisplayTitle(entry, i18n.language)}</h3>
              {description && <p className="scenario-description">{description}</p>}
              {entry.author && <p>{t("scenarios.byAuthor", { author: entry.author })}</p>}
              <SaveVersionInfo entry={entry} />
              <div className="scenario-actions">
                {own ? <>
                  <button type="button" disabled={busy || !loadable} onClick={() => void run(() => editCity(entry, false))}>{t("scenarios.editCity")}</button>
                  <button type="button" disabled={busy || !loadable || entry.encrypted} title={entry.encrypted ? t("scenarios.terrainEncryptedHint") : undefined}
                    onClick={() => void run(() => editTerrain(entry))}>{t("scenarios.editTerrain")}</button>
                  <button type="button" disabled={busy || !loadable} onClick={() => void run(() => invoke("open_registered_window", {
                    windowType: "scenario_metadata", params: { extra: { scenarioId: entry.id } },
                  }))}>{t("scenarios.metadata")}</button>
                  <button type="button" disabled={busy || !catalog.upload_available || !loadable} title={!catalog.upload_available ? t("scenarios.uploadUnconfigured") : undefined}
                    onClick={() => void run(async () => {
                      if (!await requestConfirm({ message: t("scenarios.uploadConfirm", { host: catalog.upload_host }) })) return;
                      await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(entry.id)}/upload`), { method: "POST" });
                      setStatus(t("scenarios.uploaded"));
                    })}>{t("scenarios.upload")}</button>
                </> : <button type="button" disabled={busy || !loadable} title={t("scenarios.readOnlyHint")}
                  onClick={() => void run(() => editCity(entry, true))}>{t("scenarios.editCopy")}</button>}
              </div>
            </div>
          </article>;
        })}
        {catalog && entries.length === 0 && <p className="scenario-empty">{t("scenarios.empty")}</p>}
      </div>
    </div>}
    {terrainOpened && <TerrainImportPanel hidden={tab !== "terrains"} busy={busy} error={tab === "terrains" ? error : ""}
      onCreate={(request, format) => run(() => createFromHeightmap(request, format))} />}
  </div>;
}

/** 数値の入力欄の値。空欄は未入力として NaN にする(Number("") は0になるため) */
const numberOf = (value: string) => value.trim() === "" ? NaN : Number(value);
const withinHeightRange = (value: number) => Number.isFinite(value) && Math.abs(value) <= HEIGHTMAP_LIMITS.maxAbsHeight;

/**
 * 標高ファイルの取り込みフォーム。一度開いたら入力を保つため、タブを離れても隠すだけにする。
 * 画素数が分かればマップの大きさを見込みで出し、一辺の上限を超えるなら作成を止める(判定の正はコア)。
 * 作成に成功したらファイルとタイトルだけを空にする(変換の設定は続けて使うことが多いので残す)。
 */
function TerrainImportPanel({ hidden, busy, error, onCreate }: {
  hidden: boolean; busy: boolean; error: string;
  onCreate: (request: HeightmapImportRequest, format: HeightmapInfo["format"]) => Promise<boolean>;
}) {
  const { t, i18n } = useTranslation();
  const fileInput = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [info, setInfo] = useState<HeightmapInfo | null>(null);
  const [title, setTitle] = useState("");
  const [titleEdited, setTitleEdited] = useState(false);
  const [values, setValues] = useState({ spacing: "10", minHeight: "0", maxHeight: "1000", seaLevel: "0" });
  useEffect(() => {
    if (!file) return;
    let active = true;
    readHeightmapInfo(file).then(value => { if (active) setInfo(value); }).catch(() => { if (active) setInfo(null); });
    return () => { active = false; };
  }, [file]);

  const format = info?.format ?? heightmapFormatOfName(file?.name);
  const usesHeightRange = format !== "npy";
  const spacing = numberOf(values.spacing);
  const minHeight = numberOf(values.minHeight);
  const maxHeight = numberOf(values.maxHeight);
  const seaLevel = numberOf(values.seaLevel);
  const valid = Number.isFinite(spacing) && spacing > 0 && spacing <= HEIGHTMAP_LIMITS.maxSpacing && withinHeightRange(seaLevel)
    && (!usesHeightRange || (withinHeightRange(minHeight) && withinHeightRange(maxHeight) && maxHeight > minHeight));
  const extent = valid && info?.columns && info.rows
    ? { width: importedMapExtent(info.columns, spacing), height: importedMapExtent(info.rows, spacing) } : null;
  const tooLarge = !!extent && Math.max(extent.width, extent.height) > HEIGHTMAP_LIMITS.maxMapSize;
  const ready = !!file && valid && !tooLarge && title.trim() !== "";
  const field = (key: keyof typeof values, label: string, disabled = false) => <label className="scenario-import-row">
    <span>{label}</span>
    <input type="number" step="any" value={values[key]} disabled={busy || disabled} onChange={e => setValues({ ...values, [key]: e.target.value })} />
  </label>;

  function chooseFile(next: File | null) {
    setFile(next);
    setInfo(null);
    if (next && !titleEdited) setTitle(next.name.replace(/\.[^.]*$/, "").slice(0, HEIGHTMAP_LIMITS.titleMaxLength));
  }
  async function submit() {
    if (!file || !ready) return;
    if (await onCreate({ file, spacing, minHeight, maxHeight, seaLevel, title: title.trim() }, format)) {
      setFile(null); setInfo(null); setTitle(""); setTitleEdited(false);
      if (fileInput.current) fileInput.current.value = "";
    }
  }

  return <div className="scenario-manager-body" hidden={hidden}>
    <p className="scenario-hint">{t("scenarios.terrainHint")}</p>
    {error && <p className="scenario-error" role="alert">{error}</p>}
    <form className="scenario-import" onSubmit={e => { e.preventDefault(); void submit(); }}>
      <div className="scenario-import-row">
        <span>{t("scenarios.importFile")}</span>
        <div className="scenario-import-file">
          <button type="button" disabled={busy} onClick={() => fileInput.current?.click()}>{t("scenarios.chooseFile")}</button>
          <span className="scenario-import-file-name" title={file?.name}>{file?.name ?? t("scenarios.noFileChosen")}</span>
          <input ref={fileInput} type="file" accept=".png,.npy" hidden onChange={e => chooseFile(e.target.files?.[0] ?? null)} />
        </div>
      </div>
      <p className="scenario-hint">{t("scenarios.importFormats")}</p>
      {field("spacing", t("scenarios.importSpacing"))}
      {field("minHeight", t("scenarios.importMinHeight"), !usesHeightRange)}
      {field("maxHeight", t("scenarios.importMaxHeight"), !usesHeightRange)}
      {!usesHeightRange && <p className="scenario-hint">{t("scenarios.importNpyHeights")}</p>}
      {field("seaLevel", t("scenarios.importSeaLevel"))}
      <p className="scenario-hint">{t("scenarios.importSeaLevelHint")}</p>
      <label className="scenario-import-row">
        <span>{t("scenarios.importTitle")}</span>
        <input type="text" value={title} maxLength={HEIGHTMAP_LIMITS.titleMaxLength} disabled={busy}
          onChange={e => { setTitle(e.target.value); setTitleEdited(true); }} />
      </label>
      {extent && <div className="scenario-import-row">
        <span>{t("scenarios.importMapSize")}</span>
        <span className="scenario-import-size">{formatMapSize(extent.width, extent.height, i18n.language)}</span>
      </div>}
      {!valid && <p className="scenario-error">{t("scenarios.importInvalid")}</p>}
      {tooLarge && <p className="scenario-error">{t("scenarios.importTooLarge", { max: HEIGHTMAP_LIMITS.maxMapSize / 1000 })}</p>}
      <div className="scenario-import-actions">
        <button type="submit" className="scenario-primary" disabled={busy || !ready}>
          {busy ? t("scenarios.creating") : t("scenarios.createFromTerrain")}
        </button>
      </div>
    </form>
  </div>;
}
