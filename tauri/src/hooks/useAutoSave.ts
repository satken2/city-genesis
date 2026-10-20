import { useCallback, useEffect, useRef, useState } from "react";

/**
 * Delay in ms between an update() value change and autosave. Sending every slider drag step is expensive; commit
 * on release. This delay covers changes without a release action, such as keyboard input.
 */
const AUTO_SAVE_DEBOUNCE_MS = 800;

/**
 * Autosave for editors without a confirmation button. If update() differs from the server value, call save after
 * a short delay; commit() saves immediately. If changed during a request, resend the latest value after
 * completion to prevent out-of-order responses leaving stale server state. A thrown save marks the value
 * unsaved; retry on the next change or commit. Also send unsaved changes on unmount as a fallback, since closing
 * popups often only hides them.
 */
export function useAutoSave<T>(
  save: (value: T) => Promise<void>,
  equals: (a: T, b: T) => boolean,
) {
  const [isSaving, setIsSaving] = useState(false);
  // saved = server value (null before loading completes); latest = latest screen value.
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
    if (savingRef.current) return; // Called again with the latest value when the current save finishes.
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
    // Resend changes made during the request (except on failure, to avoid infinite retries).
    const latest = latestRef.current;
    if (ok && latest !== null && !equalsRef.current(value, latest)) {
      void flush();
    }
  }, []);

  /** Initialize from the server value immediately after loading; save only subsequent differences. */
  const reset = useCallback((value: T) => {
    savedRef.current = value;
    latestRef.current = value;
  }, []);

  /** Set the latest screen value. Save after debouncing if it differs from the server. */
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

  /** Save without waiting for debounce (slider release or editing finished). */
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
