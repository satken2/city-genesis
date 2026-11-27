/// <reference lib="es2022.intl" />
import { useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { useTranslation } from "react-i18next";
import { resolveIntlLocale } from "../../utils/locale";
import "./OpeningCaption.css";

const lines = ["opening.plain", "opening.village", "opening.city"];

export function OpeningCaption() {
  const [stage, setStage] = useState<number | null>(null);
  useEffect(() => {
    let disposed = false;
    let received = false;
    const listener = getCurrentWindow().listen<number>("opening-stage", ({ payload }) => {
      received = true;
      if (!disposed) setStage(payload);
    });
    // 初回の通知が WebView の準備より早く届いたときは、購読後に現在の場面を受け取る。
    void listener.then(() => invoke<number | null>("get_opening_stage")).then(current => {
      if (!disposed && !received) setStage(current);
    }).catch(console.error);
    return () => {
      disposed = true;
      void listener.then(stop => stop());
    };
  }, []);
  if (stage === null || !lines[stage]) return null;
  return <CaptionPage key={stage} stage={stage} />;
}

function CaptionPage({ stage }: { stage: number }) {
  const { t, i18n } = useTranslation();
  const line = t(lines[stage]);
  const ends = useMemo(() => {
    if (!Intl.Segmenter) return [line.length];
    const segmenter = new Intl.Segmenter(resolveIntlLocale(i18n.language), { granularity: "grapheme" });
    return Array.from(segmenter.segment(line), part => part.index + part.segment.length);
  }, [line, i18n.language]);
  const [count, setCount] = useState(0);
  const [revealed, setRevealed] = useState(false);
  const complete = revealed || count >= ends.length;
  useEffect(() => {
    if (revealed) return;
    const start = performance.now();
    let frame: number;
    function tick(now: number) {
      const visible = Math.min(ends.length, 1 + Math.floor((now - start) / 30));
      setCount(visible);
      if (visible < ends.length) frame = window.requestAnimationFrame(tick);
    }
    frame = window.requestAnimationFrame(tick);
    return () => window.cancelAnimationFrame(frame);
  }, [ends, revealed]);

  function advance(skip: boolean) {
    if (!skip && !complete) { setRevealed(true); return; }
    void invoke("advance_opening", { stage, skip }).catch(console.error);
  }

  return (
    <section className="opening-caption">
      <p className="opening-caption-text" aria-label={line}>
        <span aria-hidden="true">{complete ? line : line.slice(0, ends[count - 1] ?? 0)}</span>
      </p>
      <div className="opening-caption-actions">
        <button type="button" className="opening-skip" onClick={() => advance(true)}>{t("opening.skip")}</button>
        <button type="button" className="opening-next" onClick={() => advance(false)}>{t("common.next")}</button>
      </div>
    </section>
  );
}
