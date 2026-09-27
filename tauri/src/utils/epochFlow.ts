/** HTTPは直ちに開始し、結果・失敗の表示だけを暗転完了まで保留する。 */
export async function runEpochWithPresentation<T>(
  execute: () => Promise<T>,
  darkened: Promise<void>,
  setTipsVisible: (visible: boolean) => Promise<void>,
): Promise<T> {
  let settled = false;
  const result = execute().then(
    (value) => { settled = true; return { ok: true as const, value }; },
    (error: unknown) => { settled = true; return { ok: false as const, error }; },
  );
  await darkened;
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
