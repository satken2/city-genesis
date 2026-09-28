/**
 * シナリオ管理ウィンドウ(トップメニューの「シナリオ管理」から開く。Unityは使わない)。
 *
 * - シナリオタブ: 自作・コミュニティ(Workshop)・DLC のシナリオを1つの一覧に並べる。
 *   自作は都市・地形・紹介情報を編集でき、アップロードもできる。
 *   コミュニティ・DLC は読み取り専用なので「コピーして編集」だけを出す(保存すると自作になる)。
 *   DLC 由来の暗号化された自作シナリオは地形を編集できない(作業コピーが平文でディスクに残るため、コアが
 *   SCENARIO_ENCRYPTED で断る)ので、「地形を編集」を無効にして理由をツールチップで示す。
 * - 地形を取り込むタブ: オリジナル地形から新しい自作シナリオを作る(ゲームは始まらない)。
 *   作成後はシナリオタブへ切り替え、新しいカードを選択状態にする。
 *
 * 都市の編集は create_session でポップアップをすべて閉じるので、確認文で地形エディタの未保存の編集に触れる。
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { errorCode, prepareSaveLoad, SaveEntry, saveError } from "../../utils/saves";
import { requestConfirm } from "../../utils/confirmDialog";
import { loadAppSettings } from "../../utils/settings";
import { resolveSupportedLanguage } from "../../utils/locale";
import { formatMapSize, terrainMapDescription, terrainMapTitle, type TerrainMapEntry } from "../../utils/terrainMaps";
import { normalizeClimate, normalizeGroundCover } from "../terrain/terrainData";
import { FlatTabs } from "../common/FlatTabs";
import { ScenarioThumbnail } from "../common/ScenarioThumbnail";
import { TerrainMapThumbnail } from "../common/TerrainMapThumbnail";
import { SaveVersionInfo } from "../common/SaveVersionInfo";
import { ScenarioOriginBadges } from "../common/ScenarioSource";
import { TerrainFacts } from "../common/TerrainFacts";
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

  async function run(operation: () => Promise<unknown>) {
    setBusy(true); setError(""); setStatus("");
    try { await operation(); } catch (e) { setError(saveError(e, t)); } finally { setBusy(false); }
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
  async function createFromTerrain(map: TerrainMapEntry) {
    const settings = await loadAppSettings().catch(() => null);
    const { id } = await fetchApi<{ id: string }>(buildApiUrl(apiBaseUrl, "/api/v1/scenarios/from-terrain"), {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        terrain_map_id: map.id,
        title: terrainMapTitle(map, t),
        language: resolveSupportedLanguage(settings?.language || i18n.language),
      }),
    });
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
          return <article key={entry.id} data-scenario-id={entry.id} aria-current={entry.id === selectedId ? "true" : undefined}
            className={`scenario-card${entry.id === selectedId ? " scenario-card--selected" : ""}`} onClick={() => setSelectedId(entry.id)}>
            <ScenarioThumbnail apiBaseUrl={apiBaseUrl} templateId={entry.id} revision={entry.content_revision} terrainMapId={entry.terrain_map_id} className="scenario-thumbnail" />
            <div className="scenario-card-content">
              <ScenarioOriginBadges entry={entry} />
              <h3>{entry.scenario_title || entry.city_name}</h3>
              {entry.scenario_description && <p className="scenario-description">{entry.scenario_description}</p>}
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
    {terrainOpened && <TerrainImportPanel apiBaseUrl={apiBaseUrl} hidden={tab !== "terrains"} busy={busy} error={tab === "terrains" ? error : ""}
      onCreate={map => void run(() => createFromTerrain(map))} />}
  </div>;
}

/** オリジナル地形のタイルと詳細。一度開いたら選択を保つため、タブを離れても隠すだけにする。 */
function TerrainImportPanel({ apiBaseUrl, hidden, busy, error, onCreate }: {
  apiBaseUrl: string; hidden: boolean; busy: boolean; error: string; onCreate: (map: TerrainMapEntry) => void;
}) {
  const { t, i18n } = useTranslation();
  const [maps, setMaps] = useState<TerrainMapEntry[] | null>(null);
  const [loadError, setLoadError] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    fetchApi<{ maps: TerrainMapEntry[] }>(buildApiUrl(apiBaseUrl, "/api/v1/terrain/maps"), { signal: controller.signal })
      .then(data => setMaps(data.maps))
      .catch(e => { if (!controller.signal.aborted) { setMaps([]); setLoadError(saveError(e, t)); } });
    return () => controller.abort();
  }, [apiBaseUrl, t]);
  const selected = maps?.find(map => map.id === selectedId) ?? null;

  return <div className="scenario-manager-body scenario-terrain" hidden={hidden}>
    <div className="scenario-terrain-list">
      <p className="scenario-hint">{t("scenarios.terrainHint")}</p>
      {(loadError || error) && <p className="scenario-error" role="alert">{loadError || error}</p>}
      {maps === null ? <p className="scenario-empty">{t("newGame.loading")}</p>
        : maps.length === 0 ? <p className="scenario-empty">{t("scenarios.terrainEmpty")}</p>
        : <div className="scenario-terrain-tiles">
          {maps.map(map => <button key={map.id} type="button" className="scenario-terrain-tile" aria-pressed={map.id === selectedId}
            title={terrainMapTitle(map, t)} onClick={() => setSelectedId(map.id)}>
            <TerrainMapThumbnail apiBaseUrl={apiBaseUrl} mapId={map.id} className="scenario-terrain-tile-image" placeholderModifier="scenario-terrain-placeholder" />
            <span>{terrainMapTitle(map, t)}</span>
          </button>)}
        </div>}
    </div>
    <aside className="scenario-terrain-detail">
      {selected ? <>
        <TerrainMapThumbnail apiBaseUrl={apiBaseUrl} mapId={selected.id} className="scenario-terrain-detail-image" placeholderModifier="scenario-terrain-placeholder" />
        <div className="scenario-terrain-detail-content">
          <h3>{terrainMapTitle(selected, t)}</h3>
          <p className="scenario-description">{terrainMapDescription(selected.id, t)}</p>
          <TerrainFacts mapSize={formatMapSize(selected.settings, i18n.language)}
            groundCover={normalizeGroundCover(selected.ground_cover)} climate={normalizeClimate(selected.climate)} />
        </div>
        <button type="button" className="scenario-primary" disabled={busy} onClick={() => onCreate(selected)}>
          {busy ? t("scenarios.creating") : t("scenarios.createFromTerrain")}
        </button>
      </> : <p className="scenario-placeholder">{t("scenarios.terrainPlaceholder")}</p>}
    </aside>
  </div>;
}
