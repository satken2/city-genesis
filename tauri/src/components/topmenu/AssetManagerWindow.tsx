import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { ASSET_PACK_STANDARD, RAIL_VEHICLE_KEYS, ROAD_VEHICLE_KEYS } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { FlatTabs } from "../common/FlatTabs";
import { AssetPackEntry, fetchAssetPacks } from "../../utils/saves";
import { resolveLocalizedText } from "../../utils/locale";
import {
  AssetManagerCategory,
  AssetManagerModels,
  AssetManagerThumbnail,
  IPC_EVENT_ASSET_MANAGER_MODELS,
  IPC_EVENT_ASSET_MANAGER_THUMBNAIL,
  requestAssetManagerRender,
} from "../../utils/ipc";
import "./AssetManagerWindow.css";

/** Player-edited weights: {source (standard or pack ID): {unit: {model number: weight}}}. */
type UserWeights = Record<string, Record<string, Record<string, number>>>;
type Row = { id: number; name: string };

const CATEGORIES: AssetManagerCategory[] = ["buildings", "paths", "vehicles", "props"];
const MAX_WEIGHT = 2147483647; // Maximum fitting in a Unity int

/**
 * Asset manager, opened from the top menu, listing building/path/vehicle/prop models by asset pack.
 *
 * Rows follow master data order: building variants, path variants, road/rail vehicle types, or prop types.
 * Unity returns each row's models/thumbnails using its rendering rules for pack versus standard assets.
 * Building/vehicle model weights are editable; committed inputs write to the user folder's asset_weights.json,
 * omitting values equal to standard defaults.
 * Weights apply on the next game start/load. Additional Models on the right provides only a placeholder area for
 * adding individual models.
 */
export function AssetManagerWindow({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const master = useMasterData();
  const [packs, setPacks] = useState<AssetPackEntry[]>([]);
  const [packId, setPackId] = useState(ASSET_PACK_STANDARD);
  const [category, setCategory] = useState<AssetManagerCategory>("buildings");
  const [weights, setWeights] = useState<UserWeights>({});
  const weightsRef = useRef<UserWeights>({});
  const [models, setModels] = useState<AssetManagerModels["rows"] | null>(null);
  const [thumbnails, setThumbnails] = useState<Record<string, string | null>>({});
  const [error, setError] = useState<string | null>(null);
  const requestRef = useRef("");

  useEffect(() => {
    void fetchAssetPacks(apiBaseUrl).then(setPacks).catch(() => setPacks([]));
    void invoke<UserWeights>("load_asset_weights")
      .then(value => { weightsRef.current = value; setWeights(value); })
      .catch(reason => setError(String(reason)));
  }, [apiBaseUrl]);

  useEffect(() => {
    const subscriptions = [
      listen<AssetManagerModels>(IPC_EVENT_ASSET_MANAGER_MODELS, event => {
        if (event.payload.request_id === requestRef.current) setModels(event.payload.rows);
      }),
      listen<AssetManagerThumbnail>(IPC_EVENT_ASSET_MANAGER_THUMBNAIL, event => {
        const { request_id, row, model, file } = event.payload;
        if (request_id !== requestRef.current) return;
        const key = `${row}:${model}`;
        if (file === null) {
          setThumbnails(previous => ({ ...previous, [key]: null }));
          return;
        }
        void invoke<string>("read_asset_thumbnail", { file })
          .then(data => { if (request_id === requestRef.current) setThumbnails(previous => ({ ...previous, [key]: `data:image/png;base64,${data}` })); })
          .catch(() => { if (request_id === requestRef.current) setThumbnails(previous => ({ ...previous, [key]: null })); });
      }),
    ];
    return () => { for (const subscription of subscriptions) void subscription.then(unlisten => unlisten()); };
  }, []);

  const rows = useMemo<Row[]>(() => {
    if (!master) return [];
    const entries: [number, string][] = category === "buildings"
      ? Object.entries(master.agents).filter(([, agent]) => agent.building).map(([id, agent]) => [Number(id), agent.display_name])
      : category === "paths"
        ? Object.entries(master.paths).map(([id, path]) => [Number(id), path.display_name])
        : category === "vehicles"
          ? [...ROAD_VEHICLE_KEYS, ...RAIL_VEHICLE_KEYS].map(id => [id, master.vehicle_types[id]?.display_name ?? ""])
          : Object.entries(master.prop_types).map(([id, prop]) => [Number(id), prop.display_name]);
    return entries.map(([id, name]) => ({ id, name })).sort((a, b) => a.id - b.id);
  }, [master, category]);

  useEffect(() => {
    if (rows.length === 0) return;
    const requestId = crypto.randomUUID();
    requestRef.current = requestId;
    setModels(null);
    setThumbnails({});
    void requestAssetManagerRender(requestId, packId, category, rows.map(row => row.id)).catch(reason => setError(String(reason)));
  }, [packId, category, rows]);

  const modelsByRow = useMemo(() => new Map((models ?? []).map(entry => [entry.row, entry])), [models]);

  function commitWeight(source: string, unit: string, id: number, standard: number, text: string): boolean {
    const value = Number(text);
    if (text.trim() === "" || !Number.isInteger(value) || value < 0 || value > MAX_WEIGHT) return false;
    const next: UserWeights = structuredClone(weightsRef.current);
    const units = next[source] ?? {};
    const unitWeights = { ...(units[unit] ?? {}) };
    if (value === standard) delete unitWeights[String(id)];
    else unitWeights[String(id)] = value;
    if (Object.keys(unitWeights).length > 0) units[unit] = unitWeights;
    else delete units[unit];
    if (Object.keys(units).length > 0) next[source] = units;
    else delete next[source];
    weightsRef.current = next;
    setWeights(next);
    setError(null);
    void invoke("save_asset_weights", { weights: next }).catch(reason => setError(String(reason)));
    return true;
  }

  return <div className="asset-manager">
    <div className="asset-manager__toolbar">
      <label className="asset-manager__pack">{t("assetPack.label")}
        <select value={packId} onChange={event => setPackId(event.currentTarget.value)}>
          <option value={ASSET_PACK_STANDARD}>{t("assetPack.standard")}</option>
          {packs.map(pack => <option key={pack.pack_id} value={pack.pack_id}>{resolveLocalizedText(pack.display_name, i18n.language)}</option>)}
        </select>
      </label>
      <span className="asset-manager__note">{t("assetManager.applyNote")}</span>
    </div>
    <FlatTabs tabs={CATEGORIES.map(id => ({ id, label: t(`assetManager.tabs.${id}`) }))} active={category}
      onChange={setCategory} idPrefix="asset-manager" />
    {error && <p className="asset-manager__error" role="alert">{error}</p>}
    <div className="asset-manager__list" role="tabpanel" id={`asset-manager-panel-${category}`} aria-labelledby={`asset-manager-tab-${category}`}>
      <div className="asset-manager__head">
        <span>{models === null ? t("assetManager.loading") : ""}</span>
        <span className="asset-manager__extra-head">{t("assetManager.additional")}</span>
      </div>
      {rows.map(row => {
        const entry = modelsByRow.get(row.id);
        return <div className="asset-manager__row" key={row.id}>
          <div className="asset-manager__base">
            <div className="asset-manager__title">
              <span>{row.name}</span>
              <span className="asset-manager__id">{row.id}</span>
            </div>
            <div className="asset-manager__models">
              {entry && entry.models.length === 0 && <span className="asset-manager__empty">{t("assetManager.noModel")}</span>}
              {entry?.models.map(model => {
                const thumbnail = thumbnails[`${row.id}:${model.id}`];
                const standard = model.weight;
                const current = standard === null || entry.unit === null ? null
                  : weights[entry.source]?.[entry.unit]?.[String(model.id)] ?? standard;
                return <figure className="asset-manager__model" key={model.id}>
                  {thumbnail
                    ? <img src={thumbnail} alt={`${row.name} ${model.id}`} draggable={false} />
                    : <div className="asset-manager__placeholder">{thumbnail === null ? t("assetManager.noImage") : "…"}</div>}
                  {current !== null && standard !== null && entry.unit !== null && <label className="asset-manager__weight">w:
                    <input key={`${entry.source}:${entry.unit}:${model.id}:${current}`} type="number" min={0} step={1}
                      defaultValue={current} aria-label={`${row.name} ${model.id} ${t("assetManager.weight")}`}
                      onKeyDown={event => { if (event.key === "Enter") event.currentTarget.blur(); }}
                      onBlur={event => {
                        const input = event.currentTarget;
                        if (!commitWeight(entry.source, entry.unit!, model.id, standard, input.value)) input.value = String(current);
                      }} />
                  </label>}
                </figure>;
              })}
            </div>
          </div>
          <div className="asset-manager__extra" />
        </div>;
      })}
    </div>
  </div>;
}
