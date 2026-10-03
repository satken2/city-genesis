/**
 * シナリオ管理ウィンドウ(トップメニューの「シナリオ管理」から開く。Unityは使わない)。
 *
 * - シナリオタブ: 自作・コミュニティ(Workshop)・DLC のシナリオを1つの一覧に並べる。
 *   自作は都市・地形・紹介情報を編集でき、アップロードと削除もできる。操作はアイコンのボタンで、説明はマウスオーバーで出す。
 *   削除は読み込めない(壊れた・新しすぎる)シナリオを片付ける用途が主なので、読み込めるかに関わらず押せる。
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
import { PLAY_MODE_SCEARIO } from "../../constants";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { errorCode, prepareSaveLoad, ScenarioEntry, saveError, scenarioDisplayTitle } from "../../utils/saves";
import { requestConfirm } from "../../utils/confirmDialog";
import { loadAppSettings } from "../../utils/settings";
import { resolveLocalizedText, resolveSupportedLanguage } from "../../utils/locale";
import { formatMapSize } from "../../utils/mapSize";
import { FlatTabs } from "../common/FlatTabs";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import { ScenarioOriginBadges } from "../common/ScenarioSource";
import {
  HEIGHTMAP_LIMITS, heightmapFormatOfName, importHeightmap, importedBackgroundExtent, importedMapExtent, readHeightmapInfo,
  registerTerrainThumbnail, type HeightmapImportRequest, type HeightmapInfo,
} from "./heightmapImport";
import { backgroundPreviewLayer, detailPreviewLayer } from "./heightmapPreview";
import { TerrainImportPreview, useHeightmapPreview } from "./TerrainImportPreview";
import infoIcon from "../../assets/images/ui/info.png";
import terrainIcon from "../../assets/images/ui/terrain.png";
import repairIcon from "../../assets/images/ui/repair.png";
import rocketIcon from "../../assets/images/ui/rocket.png";
import trashIcon from "../../assets/images/ui/trash.png";
import approvedIcon from "../../assets/images/ui/approved.png";
import type { ScenarioCatalog } from "./ScenarioSignWindow";
import "./Scenarios.css";

type Catalog = ScenarioCatalog;
type Tab = "scenarios" | "terrains";

/** アイコンだけの操作ボタン。説明は title(マウスオーバー)に出し、無効の理由があれば次の行に添える。 */
function ScenarioActionButton({ icon, label, reason, disabled, className, onClick }: {
  icon: string; label: string; reason?: string; disabled: boolean; className?: string; onClick: () => void;
}) {
  return <button type="button" className={`scenario-icon-button${className ? ` ${className}` : ""}`} disabled={disabled}
    aria-label={label} title={reason ? `${label}\n${reason}` : label} onClick={onClick}>
    <img src={icon} alt="" />
  </button>;
}

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
  /** 都市を神モード(play_mode=PLAY_MODE_SCEARIO)で開く。自作は上書き、コミュニティ・DLC はコピーとして保存される。 */
  async function editCity(entry: ScenarioEntry, copy: boolean) {
    if (!await requestConfirm({ message: t(copy ? "scenarios.copyConfirm" : "scenarios.editConfirm") })) return;
    if (!await prepareSaveLoad(apiBaseUrl, entry.id, true, t)) return;
    await invoke("create_session", { templateId: entry.id, playMode: PLAY_MODE_SCEARIO, acceptBalanceChange: true });
  }
  /**
   * シナリオの地形を作業フォルダへ書き出してから地形エディタを開く(セッションは使わない)。
   * コアの理由コードはそのまま翻訳し、コードの無い Rust 側の失敗(本文が日本語固定)は汎用の文言にして本文はログに残す。
   */
  async function editTerrain(entry: ScenarioEntry) {
    const editorId = await invoke<string>("prepare_scenario_terrain_editor", { scenarioId: entry.id }).catch((e: unknown) => {
      if (errorCode(e)) throw e;
      console.error("[ScenarioManager] failed to prepare the terrain editor", e);
      throw new Error(t("scenarios.terrainOpenFailed"));
    });
    await invoke("open_registered_window", { windowType: "terrain_editor", entityId: editorId });
  }
  /** 自作シナリオのファイルを削除する(元に戻せない)。失敗しても一覧が古いままのことがあるので取り直す。 */
  async function deleteScenario(entry: ScenarioEntry) {
    const confirmed = await requestConfirm({
      message: t("scenarios.deleteConfirm", { title: scenarioDisplayTitle(entry, i18n.language) }), confirmLabel: t("common.delete"), tone: "danger",
    });
    if (!confirmed) return;
    try {
      await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/saves/${encodeURIComponent(entry.id)}?template=true`), { method: "DELETE" });
    } finally {
      await reload().catch(() => undefined);
    }
    setSelectedId(current => current === entry.id ? null : current);
    setStatus(t("scenarios.deleted"));
    void emit("scenarios:changed");
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
      <p className="scenario-hint">{t("scenarios.createHint")}</p>
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
                  <ScenarioActionButton icon={infoIcon} label={t("scenarios.editMetadata")} disabled={busy || !loadable}
                    onClick={() => void run(() => invoke("open_registered_window", {
                      windowType: "scenario_metadata", params: { extra: { scenarioId: entry.id } },
                    }))} />
                  <ScenarioActionButton icon={terrainIcon} label={t("scenarios.editTerrain")} disabled={busy || !loadable || !!entry.encrypted}
                    reason={entry.encrypted ? t("scenarios.terrainEncryptedHint") : undefined} onClick={() => void run(() => editTerrain(entry))} />
                  <ScenarioActionButton icon={repairIcon} label={t("scenarios.editCity")} disabled={busy || !loadable}
                    onClick={() => void run(() => editCity(entry, false))} />
                  <ScenarioActionButton icon={rocketIcon} label={t("scenarios.upload")} disabled={busy || !catalog.upload_available || !loadable}
                    reason={!catalog.upload_available ? t("scenarios.uploadUnconfigured") : undefined}
                    onClick={() => void run(async () => {
                      if (!await requestConfirm({ message: t("scenarios.uploadConfirm", { host: catalog.upload_host }) })) return;
                      await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/scenarios/${encodeURIComponent(entry.id)}/upload`), { method: "POST" });
                      setStatus(t("scenarios.uploaded"));
                    })} />
                  {catalog.signing_available && <ScenarioActionButton icon={approvedIcon} label={t("scenarios.signing.button")} disabled={busy || !loadable}
                    onClick={() => void run(() => invoke("open_registered_window", {
                      windowType: "scenario_sign", params: { extra: { scenarioId: entry.id, openedAt: Date.now() } },
                    }))} />}
                  <ScenarioActionButton icon={trashIcon} label={t("common.delete")} className="scenario-icon-button--delete" disabled={busy}
                    onClick={() => void run(() => deleteScenario(entry))} />
                </> : <button type="button" disabled={busy || !loadable} title={t("scenarios.readOnlyHint")}
                  onClick={() => void run(() => editCity(entry, true))}>{t("scenarios.editCopy")}</button>}
              </div>
            </div>
          </article>;
        })}
        {catalog && entries.length === 0 && <p className="scenario-empty">{t("scenarios.empty")}</p>}
      </div>
    </div>}
    {terrainOpened && <TerrainImportPanel apiBaseUrl={apiBaseUrl} hidden={tab !== "terrains"} busy={busy} error={tab === "terrains" ? error : ""}
      onCreate={(request, format) => run(() => createFromHeightmap(request, format))} />}
  </div>;
}

/** 数値の入力欄の値。空欄は未入力として NaN にする(Number("") は0になるため) */
const numberOf = (value: string) => value.trim() === "" ? NaN : Number(value);
const withinHeightRange = (value: number) => Number.isFinite(value) && Math.abs(value) <= HEIGHTMAP_LIMITS.maxAbsHeight;

/** 項目名の横のインフォメーションアイコン。説明は常に出さず、マウスオーバー(title)のツールチップにする */
function InfoTip({ text }: { text: string }) {
  return <span className="scenario-import-info" role="img" aria-label={text} title={text}><img src={infoIcon} alt="" /></span>;
}

/** 標高ファイルの画素数と形式を先頭のバイトから読む。ファイルを替えたら読み直し、読み終わるまでは null */
function useHeightmapInfo(file: File | null): HeightmapInfo | null {
  const [info, setInfo] = useState<HeightmapInfo | null>(null);
  useEffect(() => {
    setInfo(null);
    if (!file) return;
    let active = true;
    readHeightmapInfo(file).then(value => { if (active) setInfo(value); }).catch(() => { if (active) setInfo(null); });
    return () => { active = false; };
  }, [file]);
  return info;
}

/**
 * 標高ファイルの取り込みフォームと、右側のプレビュー。一度開いたら入力を保つため、タブを離れても隠すだけにする。
 * 画素数が分かればマップ・背景の大きさを見込みで出し、一辺の上限を超えるなら作成を止める(判定の正はコア)。
 * 背景地形(.npy・任意)は1画素あたりの距離を詳細地形とは別に入力し、海面の高さだけ共通にする。
 * プレビューは選んだファイルをコアが取り込みと同じ読み方で返した標高で描く(読めない理由は入力欄の下に出す)。
 * 作成に成功したらファイルとタイトルだけを空にする(変換の設定は続けて使うことが多いので残す)。
 */
function TerrainImportPanel({ apiBaseUrl, hidden, busy, error, onCreate }: {
  apiBaseUrl: string; hidden: boolean; busy: boolean; error: string;
  onCreate: (request: HeightmapImportRequest, format: HeightmapInfo["format"]) => Promise<boolean>;
}) {
  const { t, i18n } = useTranslation();
  const fileInput = useRef<HTMLInputElement>(null);
  const backgroundInput = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [backgroundFile, setBackgroundFile] = useState<File | null>(null);
  const [title, setTitle] = useState("");
  const [titleEdited, setTitleEdited] = useState(false);
  const [values, setValues] = useState({ spacing: "10", minHeight: "0", maxHeight: "1000", seaLevel: "0", backgroundSpacing: "50" });
  const info = useHeightmapInfo(file);
  const backgroundInfo = useHeightmapInfo(backgroundFile);

  const format = info?.format ?? heightmapFormatOfName(file?.name);
  const usesHeightRange = format !== "npy";
  const spacing = numberOf(values.spacing);
  const minHeight = numberOf(values.minHeight);
  const maxHeight = numberOf(values.maxHeight);
  const seaLevel = numberOf(values.seaLevel);
  const backgroundSpacing = numberOf(values.backgroundSpacing);
  const spacingValid = (value: number) => Number.isFinite(value) && value > 0 && value <= HEIGHTMAP_LIMITS.maxSpacing;
  const heightRangeValid = !usesHeightRange || (withinHeightRange(minHeight) && withinHeightRange(maxHeight) && maxHeight > minHeight);
  const valid = spacingValid(spacing) && withinHeightRange(seaLevel) && heightRangeValid;
  const extent = valid && info?.columns && info.rows
    ? { width: importedMapExtent(info.columns, spacing), height: importedMapExtent(info.rows, spacing) } : null;
  const tooLarge = !!extent && Math.max(extent.width, extent.height) > HEIGHTMAP_LIMITS.maxMapSize;
  const backgroundValid = !backgroundFile || spacingValid(backgroundSpacing);
  const backgroundExtent = backgroundFile && spacingValid(backgroundSpacing) && backgroundInfo?.columns && backgroundInfo.rows
    ? { width: importedBackgroundExtent(backgroundInfo.columns, backgroundSpacing), height: importedBackgroundExtent(backgroundInfo.rows, backgroundSpacing) } : null;
  const backgroundTooLarge = !!backgroundExtent && Math.max(backgroundExtent.width, backgroundExtent.height) > HEIGHTMAP_LIMITS.maxBackgroundSize;
  const ready = !!file && valid && !tooLarge && title.trim() !== "" && backgroundValid && !backgroundTooLarge;

  // 値が不正な間は取り直さず、前のプレビューを残す。min/max は PNG だけが使うので、.npy では0に固定して取り直しを避ける。
  const seaValid = withinHeightRange(seaLevel);
  const detailPreview = useHeightmapPreview(apiBaseUrl, file,
    seaValid && heightRangeValid ? { minHeight: usesHeightRange ? minHeight : 0, maxHeight: usesHeightRange ? maxHeight : 0, seaLevel } : null,
    { format });
  const backgroundPreview = useHeightmapPreview(apiBaseUrl, backgroundFile,
    seaValid ? { minHeight: 0, maxHeight: 0, seaLevel } : null, { format: "npy", background: true });
  const detailGrid = detailPreview.grid;
  const backgroundGrid = backgroundPreview.grid;
  const detailLayer = useMemo(() => detailGrid && spacingValid(spacing) ? detailPreviewLayer(detailGrid, spacing) : null, [detailGrid, spacing]);
  const backgroundLayer = useMemo(() => backgroundGrid && spacingValid(backgroundSpacing)
    ? backgroundPreviewLayer(backgroundGrid, backgroundSpacing) : null, [backgroundGrid, backgroundSpacing]);

  // .npy は値そのものが標高で最低・最高標高を使わないので、その欄にはファイルの実際の値を見せる(小数1桁まで)。
  const fileHeight = (value: number | undefined) => value === undefined ? "" : String(Math.round(value * 10) / 10);
  const numberInput = (key: keyof typeof values, disabled = false, label?: string, shown?: string) =>
    <input type="number" step="any" aria-label={label} value={shown ?? values[key]} disabled={busy || disabled}
      onChange={e => setValues({ ...values, [key]: e.target.value })} />;
  const field = (key: keyof typeof values, label: string, disabled = false) => <label className="scenario-import-row">
    <span>{label}</span>
    {numberInput(key, disabled)}
  </label>;

  function chooseFile(next: File | null) {
    setFile(next);
    if (next && !titleEdited) setTitle(next.name.replace(/\.[^.]*$/, "").slice(0, HEIGHTMAP_LIMITS.titleMaxLength));
  }
  function clearBackground() {
    setBackgroundFile(null);
    if (backgroundInput.current) backgroundInput.current.value = "";
  }
  async function submit() {
    if (!file || !ready) return;
    const background = backgroundFile ? { file: backgroundFile, spacing: backgroundSpacing } : null;
    if (await onCreate({ file, spacing, minHeight, maxHeight, seaLevel, title: title.trim(), background }, format)) {
      setFile(null); setTitle(""); setTitleEdited(false);
      if (fileInput.current) fileInput.current.value = "";
      clearBackground();
    }
  }

  return <div className="scenario-manager-body" hidden={hidden}>
    {error && <p className="scenario-error" role="alert">{error}</p>}
    <div className="scenario-import-layout">
      <form className="scenario-import" onSubmit={e => { e.preventDefault(); void submit(); }}>
        <label className="scenario-import-row">
          <span>{t("scenarios.importTitle")}</span>
          <input type="text" value={title} maxLength={HEIGHTMAP_LIMITS.titleMaxLength} disabled={busy}
            onChange={e => { setTitle(e.target.value); setTitleEdited(true); }} />
        </label>
        <section className="scenario-group">
          <h3 className="scenario-group-title">{t("scenarios.importDetail")}</h3>
          <p className="scenario-hint">{t("scenarios.importDetailHint")}</p>
          <div className="scenario-import-row">
            <span>{t("scenarios.importFile")}<InfoTip text={t("scenarios.importFormats")} /></span>
            <div className="scenario-import-file">
              <button type="button" disabled={busy} onClick={() => fileInput.current?.click()}>{t("scenarios.chooseFile")}</button>
              <span className="scenario-import-file-name" title={file?.name}>{file?.name ?? t("scenarios.noFileChosen")}</span>
              <input ref={fileInput} type="file" accept=".png,.npy" hidden onChange={e => chooseFile(e.target.files?.[0] ?? null)} />
            </div>
          </div>
          {detailPreview.error && <p className="scenario-error" role="alert">{detailPreview.error}</p>}
          {field("spacing", t("scenarios.importSpacing"))}
          <div className="scenario-import-row">
            <span>{t("scenarios.importHeightRange")}</span>
            <div className="scenario-import-range">
              {numberInput("minHeight", !usesHeightRange, t("scenarios.importMinHeight"), usesHeightRange ? undefined : fileHeight(detailGrid?.minHeight))}
              <span>〜</span>
              {numberInput("maxHeight", !usesHeightRange, t("scenarios.importMaxHeight"), usesHeightRange ? undefined : fileHeight(detailGrid?.maxHeight))}
            </div>
          </div>
          {field("seaLevel", t("scenarios.importSeaLevel"))}
          {extent && <div className="scenario-import-row">
            <span>{t("scenarios.importMapSize")}</span>
            <span className="scenario-import-size">{formatMapSize(extent.width, extent.height, i18n.language)}</span>
          </div>}
          {!valid && <p className="scenario-error">{t("scenarios.importInvalid")}</p>}
          {tooLarge && <p className="scenario-error">{t("scenarios.importTooLarge", { max: HEIGHTMAP_LIMITS.maxMapSize / 1000 })}</p>}
        </section>
        <section className="scenario-group">
          <h3 className="scenario-group-title">{t("scenarios.importBackground")}<small>{t("scenarios.importOptional")}</small></h3>
          <p className="scenario-hint">{t("scenarios.importBackgroundHint")}</p>
          <div className="scenario-import-row">
            <span>{t("scenarios.importFile")}<InfoTip text={t("scenarios.importBackgroundFormats")} /></span>
            <div className="scenario-import-file">
              <button type="button" disabled={busy} onClick={() => backgroundInput.current?.click()}>{t("scenarios.chooseFile")}</button>
              <span className="scenario-import-file-name" title={backgroundFile?.name}>{backgroundFile?.name ?? t("scenarios.noFileChosen")}</span>
              {backgroundFile && <button type="button" disabled={busy} onClick={clearBackground}>{t("scenarios.clearBackground")}</button>}
              <input ref={backgroundInput} type="file" accept=".npy" hidden onChange={e => setBackgroundFile(e.target.files?.[0] ?? null)} />
            </div>
          </div>
          {backgroundPreview.error && <p className="scenario-error" role="alert">{backgroundPreview.error}</p>}
          {field("backgroundSpacing", t("scenarios.importBackgroundSpacing"), !backgroundFile)}
          {backgroundExtent && <div className="scenario-import-row">
            <span>{t("scenarios.importBackgroundSize")}</span>
            <span className="scenario-import-size">{formatMapSize(backgroundExtent.width, backgroundExtent.height, i18n.language)}</span>
          </div>}
          {!backgroundValid && <p className="scenario-error">{t("scenarios.importBackgroundInvalid")}</p>}
          {backgroundTooLarge && <p className="scenario-error">{t("scenarios.importBackgroundTooLarge", { max: HEIGHTMAP_LIMITS.maxBackgroundSize / 1000 })}</p>}
        </section>
        <div className="scenario-import-actions">
          <button type="submit" className="scenario-primary" disabled={busy || !ready}>
            {busy ? t("scenarios.creating") : t("scenarios.createFromTerrain")}
          </button>
        </div>
      </form>
      <TerrainImportPreview detail={detailLayer} background={backgroundLayer} loading={detailPreview.loading || backgroundPreview.loading} />
    </div>
  </div>;
}
