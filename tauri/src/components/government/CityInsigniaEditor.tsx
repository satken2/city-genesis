/**
 * 紋章・市旗エディタ
 *
 * window_type="city_insignia" のポップアップ。市政府ウィンドウの紋章・市旗をクリックすると開く。
 * 左に紋章、右に市旗を並べ、それぞれ図柄の組み合わせを選ぶか、画像を読み込んで差し替える。
 * 市旗の地模様には現在の紋章を置く。「おまかせ」は配色をそろえた組み合わせを両方まとめて選び直す。
 *
 * 決定すると両方を PNG に焼き込んで PUT /api/v1/city/insignia/{emblem|flag} へ送り、
 * グローバルイベント `city:insignia-changed` を出す。Unity には `session:reload` を送り、
 * 都市データの取り直しと一緒に建物の旗・紋章の画像を読み直させる。図柄の組み合わせは保存しないので、
 * 開くたびに保存済みの画像を「読み込んだ画像」として持ち、未設定の側はおまかせの組み合わせから始める。
 */

import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { emit } from "@tauri-apps/api/event";

import { buildApiUrl, fetchApi } from "../../utils/api";
import { describeError } from "../../utils/errors";
import { sendSessionReload } from "../../utils/ipc";
import { ColorSwatchPicker } from "../common/ColorSwatchPicker";
import {
  INSIGNIA_COLOR_PRESETS, MOTIF_IDS, PATTERN_IDS, PLACEMENT_IDS,
  canvasToPng, randomDesigns, renderEmblem, renderFlag,
  type EmblemDesign, type FlagDesign, type Insignia,
} from "./insigniaDrawing";
import "../common/SettingsForm.css";
import "./CityInsigniaEditor.css";

const MOTIF_THUMB_SIZE = 64;
const PATTERN_THUMB_WIDTH = 72;
const PATTERN_THUMB_HEIGHT = 48;

type Kind = "emblem" | "flag";

async function loadSavedImage(apiBaseUrl: string, kind: Kind, signal: AbortSignal): Promise<ImageBitmap | null> {
  const blob = await fetchApi<Blob | undefined>(buildApiUrl(apiBaseUrl, `/api/v1/city/insignia/${kind}`), { responseType: "blob", signal });
  return blob ? await createImageBitmap(blob) : null;
}

/** 画像を選ぶボタン。同じファイルを選び直せるよう、選んだ後に入力を空に戻す。 */
function ImagePicker({ disabled, onPick }: { disabled: boolean; onPick: (file: File) => void }) {
  const { t } = useTranslation();
  const input = useRef<HTMLInputElement>(null);
  return <>
    <button type="button" className="settings-btn" disabled={disabled} onClick={() => input.current?.click()}>
      {t("cityInsignia.loadImage")}
    </button>
    <input ref={input} type="file" accept="image/*" hidden onChange={event => {
      const file = event.target.files?.[0];
      event.target.value = "";
      if (file) onPick(file);
    }} />
  </>;
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return <div className="insignia-editor__field">
    <h3>{label}</h3>
    {children}
  </div>;
}

export function CityInsigniaEditor({ apiBaseUrl, onClose }: { apiBaseUrl: string; onClose: () => void }) {
  const { t } = useTranslation();
  const [emblem, setEmblem] = useState<Insignia<EmblemDesign> | null>(null);
  const [flag, setFlag] = useState<Insignia<FlagDesign> | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    Promise.all([
      loadSavedImage(apiBaseUrl, "emblem", controller.signal),
      loadSavedImage(apiBaseUrl, "flag", controller.signal),
    ]).then(([savedEmblem, savedFlag]) => {
      if (controller.signal.aborted) return;
      const designs = randomDesigns();
      setEmblem({ design: designs.emblem, image: savedEmblem });
      setFlag({ design: designs.flag, image: savedFlag });
    }).catch(e => {
      if (!controller.signal.aborted) setError(describeError(e, t));
    });
    return () => controller.abort();
  }, [apiBaseUrl, t]);

  const emblemCanvas = useMemo(() => emblem && renderEmblem(emblem), [emblem]);
  const flagCanvas = useMemo(() => flag && renderFlag(flag, emblemCanvas), [flag, emblemCanvas]);
  const emblemUrl = useMemo(() => emblemCanvas?.toDataURL(), [emblemCanvas]);
  const flagUrl = useMemo(() => flagCanvas?.toDataURL(), [flagCanvas]);

  const emblemDesign = emblem?.design;
  const flagDesign = flag?.design;
  const motifThumbs = useMemo(() => emblemDesign && MOTIF_IDS.map(motif => ({
    motif,
    url: renderEmblem({ design: { ...emblemDesign, motif }, image: null }, MOTIF_THUMB_SIZE).toDataURL(),
  })), [emblemDesign?.field, emblemDesign?.color]);
  const patternThumbs = useMemo(() => flagDesign && PATTERN_IDS.map(pattern => ({
    pattern,
    url: renderFlag({ design: { ...flagDesign, pattern, placement: "none" }, image: null }, null,
      PATTERN_THUMB_WIDTH, PATTERN_THUMB_HEIGHT).toDataURL(),
  })), [flagDesign?.field, flagDesign?.accent]);

  if (!emblem || !flag) {
    return <div className="insignia-editor__message">{error || t("common.loading")}</div>;
  }

  const updateEmblem = (patch: Partial<EmblemDesign>) => setEmblem({ design: { ...emblem.design, ...patch }, image: null });
  const updateFlag = (patch: Partial<FlagDesign>) => setFlag({ design: { ...flag.design, ...patch }, image: null });

  async function pickImage(file: File, apply: (image: ImageBitmap) => void) {
    setError("");
    try {
      apply(await createImageBitmap(file));
    } catch {
      setError(t("cityInsignia.imageLoadFailed"));
    }
  }

  function randomize() {
    const designs = randomDesigns();
    setEmblem({ design: designs.emblem, image: null });
    setFlag({ design: designs.flag, image: null });
  }

  async function apply() {
    if (!emblemCanvas || !flagCanvas) return;
    setBusy(true);
    setError("");
    try {
      for (const [kind, canvas] of [["emblem", emblemCanvas], ["flag", flagCanvas]] as const) {
        await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/city/insignia/${kind}`), {
          method: "PUT",
          headers: { "Content-Type": "image/png" },
          body: await canvasToPng(canvas),
        });
      }
      await emit("city:insignia-changed");
      await sendSessionReload();
      onClose();
    } catch (e) {
      setError(describeError(e, t));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="insignia-editor">
      <div className="insignia-editor__columns">
        <section className="insignia-editor__column">
          <h2>{t("cityInsignia.emblem")}</h2>
          <div className="insignia-editor__preview insignia-editor__preview--emblem">
            {emblemUrl && <img src={emblemUrl} alt={t("cityInsignia.emblem")} />}
          </div>
          <Field label={t("cityInsignia.motif")}>
            <div className="insignia-editor__choices">
              {motifThumbs?.map(({ motif, url }) => {
                const selected = !emblem.image && emblem.design.motif === motif;
                return <button key={motif} type="button" aria-pressed={selected} disabled={busy}
                  className={`insignia-editor__choice${selected ? " is-selected" : ""}`}
                  title={t(`cityInsignia.motifs.${motif}`)} aria-label={t(`cityInsignia.motifs.${motif}`)}
                  onClick={() => updateEmblem({ motif })}>
                  <img src={url} alt="" />
                </button>;
              })}
            </div>
          </Field>
          <Field label={t("cityInsignia.fieldColor")}>
            <ColorSwatchPicker value={emblem.design.field} presets={INSIGNIA_COLOR_PRESETS} disabled={busy}
              onChange={field => updateEmblem({ field })} />
          </Field>
          <Field label={t("cityInsignia.motifColor")}>
            <ColorSwatchPicker value={emblem.design.color} presets={INSIGNIA_COLOR_PRESETS} disabled={busy}
              onChange={color => updateEmblem({ color })} />
          </Field>
          <ImagePicker disabled={busy} onPick={file => void pickImage(file, image => setEmblem({ ...emblem, image }))} />
        </section>

        <section className="insignia-editor__column">
          <h2>{t("cityInsignia.flag")}</h2>
          <div className="insignia-editor__preview insignia-editor__preview--flag">
            {flagUrl && <img src={flagUrl} alt={t("cityInsignia.flag")} />}
          </div>
          <Field label={t("cityInsignia.pattern")}>
            <div className="insignia-editor__choices">
              {patternThumbs?.map(({ pattern, url }) => {
                const selected = !flag.image && flag.design.pattern === pattern;
                return <button key={pattern} type="button" aria-pressed={selected} disabled={busy}
                  className={`insignia-editor__choice${selected ? " is-selected" : ""}`}
                  title={t(`cityInsignia.patterns.${pattern}`)} aria-label={t(`cityInsignia.patterns.${pattern}`)}
                  onClick={() => updateFlag({ pattern })}>
                  <img src={url} alt="" />
                </button>;
              })}
            </div>
          </Field>
          <Field label={t("cityInsignia.fieldColor")}>
            <ColorSwatchPicker value={flag.design.field} presets={INSIGNIA_COLOR_PRESETS} disabled={busy}
              onChange={field => updateFlag({ field })} />
          </Field>
          <Field label={t("cityInsignia.accentColor")}>
            <ColorSwatchPicker value={flag.design.accent} presets={INSIGNIA_COLOR_PRESETS} disabled={busy}
              onChange={accent => updateFlag({ accent })} />
          </Field>
          <Field label={t("cityInsignia.placement")}>
            <div className="insignia-editor__segments">
              {PLACEMENT_IDS.map(placement => {
                const selected = !flag.image && flag.design.placement === placement;
                return <button key={placement} type="button" aria-pressed={selected} disabled={busy}
                  className={`settings-btn${selected ? " is-selected" : ""}`} onClick={() => updateFlag({ placement })}>
                  {t(`cityInsignia.placements.${placement}`)}
                </button>;
              })}
            </div>
          </Field>
          <ImagePicker disabled={busy} onPick={file => void pickImage(file, image => setFlag({ ...flag, image }))} />
        </section>
      </div>

      <footer className="insignia-editor__footer">
        <button type="button" className="settings-btn" disabled={busy} onClick={randomize}>{t("cityInsignia.randomize")}</button>
        {error && <p role="alert" className="insignia-editor__error">{error}</p>}
        <button type="button" className="settings-btn" disabled={busy} onClick={onClose}>{t("common.cancel")}</button>
        <button type="button" className="settings-btn insignia-editor__apply" disabled={busy} onClick={() => void apply()}>
          {busy ? t("common.saving") : t("cityInsignia.apply")}
        </button>
      </footer>
    </div>
  );
}
