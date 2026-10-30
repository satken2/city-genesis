/** Show Tips only if execution is still running when tipsDue resolves, so quick executions do not flash a window. */
export async function runEpochWithPresentation<T>(
  execute: () => Promise<T>,
  tipsDue: Promise<void>,
  setTipsVisible: (visible: boolean) => Promise<void>,
): Promise<T> {
  let settled = false;
  const result = execute().then(
    (value) => { settled = true; return { ok: true as const, value }; },
    (error: unknown) => { settled = true; return { ok: false as const, error }; },
  );
  await Promise.race([result, tipsDue]);
  const needsTips = !settled;
  try {
    if (needsTips) await setTipsVisible(true);
    const outcome = await result;
    if (!outcome.ok) throw outcome.error;
    return outcome.value;
  } finally {
    if (needsTips) await setTipsVisible(false);
  }
}
