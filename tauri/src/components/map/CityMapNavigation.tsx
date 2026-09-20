import { useCallback, useEffect, useRef, useState, type RefObject } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { resolveIntlLocale } from "../../utils/locale";
import type { CityMapCanvasHandle, CityMapPointerEvent, CityMapViewState } from "./CityMapCanvas";
import "./CityMapNavigation.css";

type Place = {
  kind: "agent" | "zone" | "stop" | "station" | "point";
  id: number; name: string; x: number; z: number;
  zone_id: number; zone_name: string; parcel_id: number | null; polygon: number[][];
};
type Leg = {
  kind: string; points: number[][]; path_ids: number[]; line_id: number | null;
  name: string; color: string | null; minutes: number; distance_m: number;
  from_stop: string | null; to_stop: string | null;
};
type Route = {
  status: string; legs?: Leg[]; minutes?: number; distance_m?: number;
  fare?: number; epoch?: number; snapshot?: string;
};
type Status = { ready: boolean; busy: boolean; snapshot: string | null; session: string | null };
type Field = "origin" | "destination";

function SearchField({ apiBaseUrl, value, label, onSelect, onClear, refresh }: {
  apiBaseUrl: string; value: Place | null; label: string; onSelect: (p: Place) => void;
  onClear: () => void; refresh: string;
}) {
  const { t } = useTranslation();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<Place[]>([]);
  const [active, setActive] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const [open, setOpen] = useState(false);
  const editing = useRef(false);
  const listId = `map-search-${label.replace(/\s/g, "-")}`;
  const name = (p: Place) => p.name || t("mapNavigation.unnamed", { id: p.id, kind: t(`mapNavigation.kind.${p.kind}`) });
  useEffect(() => {
    if (!editing.current) {
      setQuery(value ? (value.kind === "point" ? `${value.x.toFixed(0)}, ${value.z.toFixed(0)}` : name(value)) : "");
      setOpen(false);
    }
    editing.current = false;
  }, [value]);
  useEffect(() => {
    if (!open || !query.trim()) { setResults([]); setBusy(false); return; }
    const controller = new AbortController();
    setBusy(true); setError(false); setResults([]); setActive(0);
    const timer = window.setTimeout(() => {
      fetchApi<{ places: Place[] }>(buildApiUrl(apiBaseUrl, `/api/v1/navigation/places?q=${encodeURIComponent(query.trim())}`),
        { signal: controller.signal }).then(data => {
          if (!controller.signal.aborted) setResults(data.places);
        }).catch(() => { if (!controller.signal.aborted) setError(true); })
        .finally(() => { if (!controller.signal.aborted) setBusy(false); });
    }, 220);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [apiBaseUrl, query, open, refresh]);
  const select = (p: Place) => { setOpen(false); setQuery(name(p)); onSelect(p); };
  return <div className="map-nav-search">
    <div className="map-nav-search__input">
      <span aria-hidden="true">⌕</span>
      <input aria-label={label} placeholder={label} value={query} role="combobox" aria-autocomplete="list"
        aria-expanded={open && !!query.trim()} aria-controls={listId}
        aria-activedescendant={open && results[active] ? `${listId}-${active}` : undefined}
        onFocus={() => { if (!value) setOpen(true); }}
        onChange={e => { setQuery(e.target.value); setOpen(true); if (value) { editing.current = true; onClear(); } }}
        onKeyDown={e => {
          if (e.key === "Escape") { setOpen(false); e.stopPropagation(); }
          if (e.key === "ArrowDown") { e.preventDefault(); setActive(a => Math.min(a + 1, results.length - 1)); }
          if (e.key === "ArrowUp") { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
          if (e.key === "Enter" && results[active] && open) { e.preventDefault(); select(results[active]); }
        }} />
      {query && <button type="button" aria-label={t("mapNavigation.clear")} onClick={() => {
        onClear(); setQuery(""); setOpen(true);
      }}>×</button>}
    </div>
    {open && query.trim() && <div className="map-nav-search__results" id={listId} role="listbox" aria-label={label}>
      {busy ? <p role="status">{t("mapNavigation.searching")}</p> : error ? <p role="alert">{t("mapNavigation.error")}</p>
        : !results.length ? <p>{t("mapNavigation.noResults")}</p> : results.map((p, i) =>
          <button type="button" key={`${p.kind}-${p.id}`} id={`${listId}-${i}`} role="option" aria-selected={active === i}
            className={active === i ? "is-active" : ""} onMouseEnter={() => setActive(i)} onClick={() => select(p)}>
            <span className="map-nav-search__pin" aria-hidden="true">⌖</span><span><strong>{name(p)}</strong>
              <small>{t(`mapNavigation.kind.${p.kind}`)}{p.zone_name ? ` · ${p.zone_name}` : ""}</small></span>
          </button>)}
    </div>}
  </div>;
}

export function useMapNavigation(apiBaseUrl: string, mapRef: RefObject<CityMapCanvasHandle>) {
  const { t, i18n } = useTranslation();
  const [destination, setDestination] = useState<Place | null>(null);
  const [origin, setOrigin] = useState<Place | null>(null);
  const [directions, setDirections] = useState(false);
  const [mode, setMode] = useState<"pedestrian" | "automobile">("pedestrian");
  const [picking, setPicking] = useState<Field | null>(null);
  const [route, setRoute] = useState<Route | null>(null);
  const [busy, setBusy] = useState(false);
  const [pickError, setPickError] = useState(false);
  const [status, setStatus] = useState<Status | null>(null);
  const [statusError, setStatusError] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [activeLeg, setActiveLeg] = useState<number | null>(null);
  const [overlayVersion, setOverlayVersion] = useState(0);
  const previous = useRef<Status | null>(null);
  const pickRequest = useRef<AbortController | null>(null);
  const url = useCallback((path: string) => buildApiUrl(apiBaseUrl, `/api/v1/navigation/${path}`), [apiBaseUrl]);
  const fit = useCallback((points: number[][]) => {
    if (!points.length) return;
    const bounds = { x1: Infinity, x2: -Infinity, z1: Infinity, z2: -Infinity };
    for (const [x, z] of points) {
      bounds.x1 = Math.min(bounds.x1, x); bounds.x2 = Math.max(bounds.x2, x);
      bounds.z1 = Math.min(bounds.z1, z); bounds.z2 = Math.max(bounds.z2, z);
    }
    const padding = Math.max(60, Math.max(bounds.x2 - bounds.x1, bounds.z2 - bounds.z1) * 0.1);
    mapRef.current?.fitBounds({ x1: bounds.x1 - padding, x2: bounds.x2 + padding, z1: bounds.z1 - padding, z2: bounds.z2 + padding });
  }, [mapRef]);
  const select = useCallback((field: Field, place: Place) => {
    pickRequest.current?.abort(); setPicking(null); setPickError(false); setRoute(null); setActiveLeg(null);
    if (field === "origin") setOrigin(place); else setDestination(place);
    fit(place.polygon.length ? place.polygon : [[place.x, place.z]]);
  }, [fit]);
  useEffect(() => {
    const controller = new AbortController();
    let timer = 0;
    const poll = async () => {
      try {
        const next = await fetchApi<Status>(url("status"), { signal: controller.signal });
        if (controller.signal.aborted) return;
        const prev = previous.current;
        if (prev && (prev.session !== next.session || prev.snapshot !== next.snapshot || prev.busy !== next.busy)) {
          pickRequest.current?.abort(); setRoute(null); setPicking(null); setActiveLeg(null);
          if (prev.session !== next.session) { setOrigin(null); setDestination(null); setDirections(false); }
          if (!next.busy) mapRef.current?.reload();
        }
        previous.current = next; setStatus(next); setStatusError(false);
      } catch { if (!controller.signal.aborted) { setStatusError(true); setRoute(null); } }
      if (!controller.signal.aborted) timer = window.setTimeout(poll, 3000);
    };
    void poll();
    return () => { controller.abort(); window.clearTimeout(timer); };
  }, [url, refresh, mapRef]);
  useEffect(() => {
    const controller = new AbortController();
    setRoute(null); setActiveLeg(null); setBusy(false);
    if (!directions || !origin || !destination || !status?.ready || statusError) return;
    setBusy(true);
    const endpoint = (p: Place) => ({ kind: p.kind, id: p.id, x: p.x, z: p.z });
    fetchApi<Route>(url("route"), { method: "POST", signal: controller.signal,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ origin: endpoint(origin), destination: endpoint(destination), mode })
    }).then(result => {
      if (controller.signal.aborted) return;
      setRoute(result);
      if (result.status === "ok") fit(result.legs?.flatMap(l => l.points) ?? [[origin.x, origin.z], [destination.x, destination.z]]);
    }).catch(() => { if (!controller.signal.aborted) setRoute({ status: "error" }); })
      .finally(() => { if (!controller.signal.aborted) setBusy(false); });
    return () => controller.abort();
  }, [url, origin, destination, mode, directions, status?.snapshot, status?.ready, statusError, refresh, fit]);
  useEffect(() => { setOverlayVersion(v => v + 1); }, [destination, origin, directions, route, activeLeg]);
  useEffect(() => () => pickRequest.current?.abort(), []);
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === "Escape") {
      pickRequest.current?.abort(); setPicking(null);
    } };
    window.addEventListener("keydown", escape);
    return () => window.removeEventListener("keydown", escape);
  }, []);
  const onMapClick = useCallback((event: CityMapPointerEvent) => {
    if (status?.busy) return;
    const field = picking ?? (!directions ? "destination" : null);
    if (!field) return;
    pickRequest.current?.abort();
    const controller = new AbortController(); pickRequest.current = controller;
    setPickError(false);
    const radius = Math.min(100, Math.max(1, 12 / event.view.zoom));
    fetchApi<{ place: Place | null }>(url(`pick?x=${event.world.x}&z=${event.world.z}&radius=${radius}`), { signal: controller.signal })
      .then(data => { if (!controller.signal.aborted && data.place) select(field, data.place); })
      .catch(() => { if (!controller.signal.aborted) setPickError(true); });
  }, [url, picking, directions, select, status?.busy]);
  const draw = useCallback((ctx: CanvasRenderingContext2D, view: CityMapViewState) => {
    const px = (n: number) => n / view.zoom;
    ctx.save(); ctx.lineJoin = "round"; ctx.lineCap = "round";
    route?.legs?.forEach((leg, index) => {
      if (!leg.points.length) return;
      const walk = leg.kind === "walk" || leg.kind === "access";
      ctx.beginPath(); leg.points.forEach((p, i) => i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1]));
      ctx.globalAlpha = activeLeg !== null && activeLeg !== index ? 0.35 : 1;
      ctx.setLineDash([]); ctx.strokeStyle = "#ffffff"; ctx.lineWidth = px(activeLeg === index ? 10 : 8); ctx.stroke();
      ctx.setLineDash(walk ? [px(2), px(8)] : []);
      ctx.strokeStyle = leg.color && /^#[0-9a-f]{6}$/i.test(leg.color) ? leg.color : walk ? "#5a7695" : "#4285f4";
      ctx.lineWidth = px(activeLeg === index ? 7 : 5); ctx.stroke();
    });
    ctx.globalAlpha = 1; ctx.setLineDash([]);
    const marker = (place: Place, color: string, label: string) => {
      if (place.polygon.length) {
        ctx.beginPath(); place.polygon.forEach((p, i) => i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1]));
        ctx.closePath(); ctx.fillStyle = `${color}30`; ctx.fill(); ctx.strokeStyle = color; ctx.lineWidth = px(2); ctx.stroke();
      }
      ctx.beginPath(); ctx.arc(place.x, place.z, px(11), 0, Math.PI * 2);
      ctx.fillStyle = color; ctx.fill(); ctx.strokeStyle = "white"; ctx.lineWidth = px(3); ctx.stroke();
      ctx.save(); ctx.translate(place.x, place.z); ctx.scale(1, -1); ctx.fillStyle = "white";
      ctx.font = `bold ${px(12)}px sans-serif`; ctx.textAlign = "center"; ctx.textBaseline = "middle"; ctx.fillText(label, 0, 0); ctx.restore();
    };
    if (directions && origin) marker(origin, "#188038", "A");
    if (destination) marker(destination, "#ea4335", directions ? "B" : "•");
    ctx.restore();
  }, [route, activeLeg, origin, destination, directions]);
  const displayName = (p: Place) => p.name || (p.kind === "point" ? t("mapNavigation.mapPoint") :
    t("mapNavigation.unnamed", { kind: t(`mapNavigation.kind.${p.kind}`), id: p.id }));
  const duration = (minutes: number) => t("mapNavigation.minutes", { count: Math.max(1, Math.round(minutes)) });
  const distance = (meters: number) => meters >= 1000 ? `${(meters / 1000).toLocaleString(resolveIntlLocale(i18n.language), { maximumFractionDigits: 1 })} km` : `${Math.round(meters)} m`;
  const field = (which: Field) => <SearchField key={`${which}-${status?.session}`} apiBaseUrl={apiBaseUrl} value={which === "origin" ? origin : destination}
    label={t(`mapNavigation.${directions ? which : "search"}`)} refresh={`${status?.snapshot}-${refresh}`}
    onSelect={p => select(which, p)} onClear={() => {
      pickRequest.current?.abort(); setRoute(null); if (which === "origin") setOrigin(null); else setDestination(null);
    }} />;
  const sidebar = <aside className="map-nav" aria-label={t("mapNavigation.title")}>
    <div className="map-nav__header"><span className="map-nav__brand">⌖</span><strong>{t("mapNavigation.title")}</strong>
      {directions && <button type="button" className="map-nav__back" onClick={() => {
        pickRequest.current?.abort(); setDirections(false); setPicking(null); setRoute(null);
      }}>{t("mapNavigation.back")}</button>}</div>
    <div className="map-nav__fields">
      {directions && <><div className="map-nav__modes" role="group" aria-label={t("mapNavigation.mode")}>
        {(["pedestrian", "automobile"] as const).map(m => <button type="button" key={m} aria-pressed={mode === m}
          className={mode === m ? "is-active" : ""} onClick={() => { if (mode !== m) { setRoute(null); setMode(m); } }}>{t(`mapNavigation.${m}`)}</button>)}
      </div>{field("origin")}<button type="button" className="map-nav__pick" onClick={() => {
        pickRequest.current?.abort(); setPicking("origin");
      }}>{t("mapNavigation.pickOrigin")}</button></>}
      {field("destination")}
      {directions && <div className="map-nav__field-actions"><button type="button" onClick={() => {
        pickRequest.current?.abort(); setPicking("destination");
      }}>{t("mapNavigation.pickDestination")}</button><button type="button" onClick={() => {
        pickRequest.current?.abort(); setPicking(null); setRoute(null); setOrigin(destination); setDestination(origin);
      }}>{t("mapNavigation.swap")}</button></div>}
    </div>
    <div className="map-nav__content">
      {picking && <div className="map-nav__hint" role="status">{t(`mapNavigation.picking.${picking}`)}
        <button type="button" onClick={() => { pickRequest.current?.abort(); setPicking(null); }}>{t("mapNavigation.cancel")}</button></div>}
      {pickError && <p role="alert">{t("mapNavigation.error")}</p>}
      {!directions && (destination ? <div className="map-nav__place">
        <div className="map-nav__place-icon" aria-hidden="true">⌖</div><h2>{displayName(destination)}</h2>
        <p>{t(`mapNavigation.kind.${destination.kind}`)}{destination.zone_name ? ` · ${destination.zone_name}` : ""}</p>
        <button type="button" className="map-nav__primary" onClick={() => { setDirections(true); setPicking(null); }}>
          ↱ {t("mapNavigation.directions")}</button>
      </div> : <div className="map-nav__empty"><span aria-hidden="true">⌕</span><h2>{t("mapNavigation.explore")}</h2><p>{t("mapNavigation.intro")}</p></div>)}
      {directions && <>
        {(statusError || status?.busy || (status && !status.ready)) && <p className="map-nav__hint" role="status">
          {t(`mapNavigation.${statusError ? "error" : status?.busy ? "busy" : "not_ready"}`)}</p>}
        {busy && <p role="status">{t("mapNavigation.routing")}</p>}
        {route && route.status !== "ok" && <p className="map-nav__hint" role="status">{t(`mapNavigation.${route.status}`)}</p>}
        {route?.status === "ok" && <>
          <div className="map-nav__summary"><strong>{duration(route.minutes ?? 0)}</strong><span>{distance(route.distance_m ?? 0)}</span>
            <button type="button" onClick={() => { setActiveLeg(null); fit(route.legs?.flatMap(l => l.points) ?? []); }}>{t("mapNavigation.showRoute")}</button></div>
          <p className="map-nav__snapshot">{t("mapNavigation.snapshot", { epoch: route.epoch })}</p>
          <ol className="map-nav__legs">{route.legs?.map((leg, index) => <li key={index}>
            <button type="button" className={activeLeg === index ? "is-active" : ""} onClick={() => { setActiveLeg(index); fit(leg.points); }}>
              <span className={`map-nav__leg-icon map-nav__leg-icon--${leg.kind}`} aria-hidden="true">{leg.kind === "transit" ? "▣" : leg.kind === "drive" ? "➜" : "⋮"}</span>
              <span><strong>{leg.kind === "transit" ? leg.name : t(`mapNavigation.leg.${leg.kind}`)}</strong>
                {leg.from_stop && <small>{leg.from_stop} → {leg.to_stop}</small>}
                <small>{duration(leg.minutes)} · {distance(leg.distance_m)}</small></span>
            </button></li>)}</ol>
        </>}
        {!origin && !picking && <p className="map-nav__intro">{t("mapNavigation.chooseOrigin")}</p>}
      </>}
    </div>
  </aside>;
  return { sidebar, draw, overlayVersion, onMapClick, picking, reload: () => {
    pickRequest.current?.abort(); setRoute(null); setRefresh(v => v + 1);
  } };
}
