import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";

export type TerrainPropInstance = {
  id: string; prop_type: number; x: number; y: number; z: number; rotation: number; scale: number;
};
export type TerrainPropScatter = {
  id: string; prop_type: number; polygon: number[][]; density: number; seed: number;
};
/** 地形の作業コピーと .props.json に保存する配置定義。y は地面からの相対高さ。 */
export type TerrainProps = { instances: TerrainPropInstance[]; scatters: TerrainPropScatter[] };
export const emptyTerrainProps = (): TerrainProps => ({ instances: [], scatters: [] });

export function translateTerrainProps(data: TerrainProps, dx: number, dz: number): TerrainProps {
  return {
    instances: data.instances.map(p => ({ ...p, x: p.x + dx, z: p.z + dz })),
    scatters: data.scatters.map(p => ({ ...p, polygon: p.polygon.map(([x, z]) => [x + dx, z + dz]) })),
  };
}

export function useTerrainPropEditor(beforeChange: () => void, redraw: () => void) {
  const data = useRef<TerrainProps>(emptyTerrainProps());
  const [revision, setRevision] = useState(0);
  const [mode, setMode] = useState<"instance" | "scatter">("instance");
  const [type, setType] = useState<number | null>(null);
  const [height, setHeight] = useState(0);
  const [rotation, setRotation] = useState(0);
  const [scale, setScale] = useState(1);
  const [density, setDensity] = useState(400);
  const [seed, setSeed] = useState(12345);
  const draft = useRef<{ instances: TerrainPropInstance[]; polygon: number[][]; closed: boolean }>({ instances: [], polygon: [], closed: false });
  function refresh() { setRevision(v => v + 1); redraw(); }
  function reset() { draft.current = { instances: [], polygon: [], closed: false }; refresh(); }
  function restore(next: TerrainProps) { data.current = next; reset(); }
  function click(x: number, z: number) {
    if (type === null) return;
    if (mode === "instance") {
      draft.current.instances.push({ id: crypto.randomUUID(), prop_type: type, x, y: height, z, rotation, scale });
    } else if (!draft.current.closed) {
      draft.current.polygon.push([x, z]);
    }
    refresh();
  }
  function closePolygon() {
    if (mode === "scatter" && draft.current.polygon.length >= 3) { draft.current.closed = true; refresh(); }
  }
  const polygon = draft.current.polygon;
  const area = Math.abs(polygon.reduce((sum, [x, z], i) => {
    const next = polygon[(i + 1) % polygon.length];
    return sum + x * next[1] - next[0] * z;
  }, 0)) / 2;
  const canConfirm = type !== null && (mode === "instance" ? draft.current.instances.length > 0 : draft.current.closed && area >= 1);
  function confirm() {
    if (!canConfirm || type === null) return;
    beforeChange();
    data.current = mode === "instance"
      ? { ...data.current, instances: [...data.current.instances, ...draft.current.instances] }
      : { ...data.current, scatters: [...data.current.scatters, { id: crypto.randomUUID(), prop_type: type, polygon: polygon.map(p => [...p]), density, seed }] };
    reset();
  }
  function remove(id: string) {
    beforeChange();
    data.current = { instances: data.current.instances.filter(p => p.id !== id), scatters: data.current.scatters.filter(p => p.id !== id) };
    refresh();
  }
  function shift(dx: number, dz: number) {
    data.current = translateTerrainProps(data.current, dx, dz);
    draft.current = {
      ...draft.current,
      instances: draft.current.instances.map(p => ({ ...p, x: p.x + dx, z: p.z + dz })),
      polygon: draft.current.polygon.map(([x, z]) => [x + dx, z + dz]),
    };
    refresh();
  }
  function draw(ctx: CanvasRenderingContext2D, zoom: number, active: boolean, cursor: { x: number; z: number } | null) {
    ctx.save();
    ctx.lineWidth = 1.5 / zoom;
    function region(points: number[][], closed: boolean, pending: boolean) {
      if (!points.length) return;
      ctx.strokeStyle = pending ? "#ffd280" : "#7fe0a0";
      ctx.fillStyle = "rgba(100,210,130,0.18)";
      ctx.beginPath();
      points.forEach(([x, z], i) => { if (i === 0) ctx.moveTo(x, z); else ctx.lineTo(x, z); });
      if (closed) { ctx.closePath(); ctx.fill(); }
      else if (cursor) ctx.lineTo(cursor.x, cursor.z);
      ctx.stroke();
    }
    function marker(p: TerrainPropInstance, pending: boolean) {
      ctx.strokeStyle = pending ? "#ffd280" : "#7fe0a0";
      const r = Math.max(4 / zoom, p.scale * 2);
      ctx.beginPath(); ctx.arc(p.x, p.z, r, 0, Math.PI * 2); ctx.stroke();
      const angle = p.rotation * Math.PI / 180;
      ctx.beginPath(); ctx.moveTo(p.x, p.z); ctx.lineTo(p.x + Math.sin(angle) * r * 2, p.z + Math.cos(angle) * r * 2); ctx.stroke();
    }
    data.current.scatters.forEach(p => region(p.polygon, true, false));
    data.current.instances.forEach(p => marker(p, false));
    if (active) {
      draft.current.instances.forEach(p => marker(p, true));
      if (mode === "scatter") region(draft.current.polygon, draft.current.closed, true);
      else if (cursor && type !== null) marker({ id: "", prop_type: type, ...cursor, y: height, rotation, scale }, true);
    }
    ctx.restore();
  }
  // キャンバスのイベントは再登録されないので、呼び出し口だけを最新レンダーへ差し替える。
  const actions = { data, draft, revision, mode, type, height, rotation, scale, density, seed, canConfirm, setHeight, setRotation, setScale, setDensity, setSeed,
    setMode: (v: "instance" | "scatter") => { setMode(v); reset(); },
    setType: (v: number | null) => { setType(v); reset(); },
    reset, restore, click, closePolygon, confirm, remove, shift, draw };
  const latest = useRef(actions);
  latest.current = actions;
  useEffect(() => { redraw(); }, [revision, mode, type, height, rotation, scale, density, seed]);
  return latest;
}

export function TerrainPropEditor({ editor }: { editor: ReturnType<typeof useTerrainPropEditor>["current"] }) {
  const { t } = useTranslation();
  const master = useMasterData();
  const entries = Object.entries(master?.prop_types ?? {});
  const name = (id: number) => master?.prop_types[id]?.display_name ?? `#${id}`;
  return <div className="terrain-editor-card">
    <div className="terrain-editor-card-title">{t("terrainEditor.tabProp")}</div>
    <p className="terrain-editor-hint">{t("terrainEditor.propHint")}</p>
    <select className="terrain-editor-select" aria-label={t("prop_placement.selectType")} value={editor.type ?? ""} onChange={e => editor.setType(e.target.value ? Number(e.target.value) : null)}>
      <option value="">{t("prop_placement.selectType")}</option>
      {entries.map(([id, entry]) => <option key={id} value={id}>{entry.display_name}</option>)}
    </select>
    {!entries.length && <p className="terrain-editor-hint">{t("prop_placement.noVariants")}</p>}
    <div className="terrain-editor-param-row">
      <span className="terrain-editor-param-label">{t("prop_placement.modeLabel")}</span>
      <select className="terrain-editor-select" value={editor.mode} onChange={e => editor.setMode(e.target.value as "instance" | "scatter")}>
        <option value="instance">{t("prop_placement.modeInstance")}</option><option value="scatter">{t("prop_placement.modeScatter")}</option>
      </select>
    </div>
    <div className="terrain-editor-param-group">
      {(editor.mode === "instance" ? [
        { key: "height", value: editor.height, onChange: editor.setHeight, step: 0.1 },
        { key: "rotation", value: editor.rotation, onChange: editor.setRotation, step: 15, min: 0, max: 360 },
        { key: "scale", value: editor.scale, onChange: editor.setScale, step: 0.1, min: 0.1, max: 5 },
      ] : [
        { key: "density", value: editor.density, onChange: editor.setDensity, step: 10, min: 1, max: 100000 },
        { key: "seed", value: editor.seed, onChange: editor.setSeed, step: 1, min: 0, max: 2147483647 },
      ]).map(field => <div className="terrain-editor-param-row" key={field.key}>
        <span className="terrain-editor-param-label">{t(`prop_placement.${field.key}`)}</span>
        <NumberStepper value={field.value} onChange={field.onChange} min={field.min} max={field.max} step={field.step} integerOnly={field.step >= 1} />
      </div>)}
    </div>
    <p className="terrain-editor-hint">{t("terrainEditor.propDraftCount", { count: editor.mode === "instance" ? editor.draft.current.instances.length : editor.draft.current.polygon.length })}</p>
    <div className="terrain-editor-action-buttons">
      <button className="terrain-editor-btn" onClick={() => editor.setType(null)}>{t("prop_placement.cancel")}</button>
      <button className="terrain-editor-btn" onClick={editor.reset}>{t("prop_placement.reset")}</button>
      {editor.mode === "scatter" && <button className="terrain-editor-btn" disabled={editor.draft.current.polygon.length < 3 || editor.draft.current.closed} onClick={editor.closePolygon}>{t("terrainEditor.resourceCloseShape")}</button>}
      <button className="terrain-editor-btn terrain-editor-btn-confirm" disabled={!editor.canConfirm} onClick={editor.confirm}>{t("prop_placement.confirm")}</button>
    </div>
    <div className="terrain-editor-section-title">{t("terrainEditor.propList")}</div>
    {[...editor.data.current.instances, ...editor.data.current.scatters].map(p => <div className="terrain-editor-param-row" key={p.id}>
      <span className="terrain-editor-param-label">{name(p.prop_type)} · {t("polygon" in p ? "prop_placement.modeScatter" : "prop_placement.modeInstance")}</span>
      <button className="terrain-editor-btn" onClick={() => editor.remove(p.id)}>{t("terrainEditor.resourceDeleteOne")}</button>
    </div>)}
  </div>;
}
