/**
 * 次のエポックまでの残り時間(分:秒)。数字が変わるたびに描き直すのはこの部品だけにし、コックピット全体を描き直さない。
 */
import { useEffect, useState } from "react";
import { remainingSecondsOf, type EpochClockSnapshot } from "./useEpochClock";

export function EpochCountdown({ clock }: { clock: EpochClockSnapshot | null }) {
  const [now, setNow] = useState(() => performance.now());

  useEffect(() => {
    setNow(performance.now());
    if (!clock?.running) return;
    const timer = setInterval(() => setNow(performance.now()), 250);
    return () => clearInterval(timer);
  }, [clock]);

  if (!clock) return <>--:--</>;
  const total = Math.ceil(remainingSecondsOf(clock, now));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return <>{`${minutes}:${String(seconds).padStart(2, "0")}`}</>;
}
