import { useCallback, useEffect, useRef, useState } from "react";

/**
 * update() で値が変わってから自動保存するまでの待ち時間(ms)。
 * スライダーはドラッグ中の毎ステップを送ると重いので、つまみを離した時点で commit し、
 * この待ち時間はキーボード操作など「離す」操作を伴わない変更のための保険にする。
 */
const AUTO_SAVE_DEBOUNCE_MS = 800;

/**
 * 確定ボタンを持たない編集画面の自動保存。
 *
 * update() で伝えた値がサーバの値と違えば少し待ってから save を呼び、commit() なら待たずに呼ぶ。
 * 送信中にさらに値が変わった場合は、完了後に最新値で送り直す(応答の追い越しでサーバが古い値のまま
 * 終わるのを防ぐ)。save が例外を投げた分は未保存に戻し、次の変更か commit で送り直す。
 * アンマウント時にも未保存分を送るが、ポップアップは閉じても非表示になるだけのことが多いので保険にすぎない。
 */
export function useAutoSave<T>(
  save: (value: T) => Promise<void>,
  equals: (a: T, b: T) => boolean,
) {
  const [isSaving, setIsSaving] = useState(false);
  // saved=サーバが持っている値(nullはロード完了前)、latest=画面上の最新値。
  const savedRef = useRef<T | null>(null);
  const latestRef = useRef<T | null>(null);
  const timerRef = useRef<number | null>(null);
  const savingRef = useRef(false);
  const saveRef = useRef(save);
  const equalsRef = useRef(equals);
  useEffect(() => {
    saveRef.current = save;
    equalsRef.current = equals;
  });

  const flush = useCallback(async () => {
    if (savingRef.current) return; // 進行中の保存の完了時に最新値で呼び直される
    const value = latestRef.current;
    const saved = savedRef.current;
    if (saved === null || value === null || equalsRef.current(saved, value)) return;

    savingRef.current = true;
    savedRef.current = value;
    setIsSaving(true);
    let ok = false;
    try {
      await saveRef.current(value);
      ok = true;
    } catch {
      savedRef.current = saved;
    } finally {
      savingRef.current = false;
      setIsSaving(false);
    }
    // 送信中に動かされた分を送り直す(失敗時は無限リトライになるので送らない)。
    const latest = latestRef.current;
    if (ok && latest !== null && !equalsRef.current(value, latest)) {
      void flush();
    }
  }, []);

  /** サーバと同値の値を起点にする(ロード直後)。以後はこれとの差分だけを保存する。 */
  const reset = useCallback((value: T) => {
    savedRef.current = value;
    latestRef.current = value;
  }, []);

  /** 画面上の最新値を伝える。サーバの値と違えばデバウンス後に保存する。 */
  const update = useCallback(
    (value: T) => {
      latestRef.current = value;
      const saved = savedRef.current;
      if (saved === null || equalsRef.current(saved, value)) return;
      if (timerRef.current !== null) window.clearTimeout(timerRef.current);
      timerRef.current = window.setTimeout(() => {
        timerRef.current = null;
        void flush();
      }, AUTO_SAVE_DEBOUNCE_MS);
    },
    [flush],
  );

  /** デバウンスを待たずに保存する(スライダーを離した・編集を終えた) */
  const commit = useCallback(() => {
    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
      timerRef.current = null;
    }
    void flush();
  }, [flush]);

  useEffect(() => commit, [commit]);

  return { isSaving, reset, update, commit };
}
