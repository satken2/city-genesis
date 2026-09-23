/**
 * 駐車メーター(建物の駐車容量・道路の路上駐車量)の共通処理
 *
 * バックエンドの駐車台数・容量は float32 の実数で、容量を超えることもある(路上駐車はクリップしない設計)。
 * メーターの使用率は、画面に併記する台数と同じ桁数へ丸めてから求める。生値で割ると
 * 「20 / 20台」と表示しているのに端数のずれで100%超として光る、といった食い違いが起きるため。
 */

export type ParkingUsage = {
  /** 表示桁数へ丸めた使用台数 */
  used: number;
  /** 表示桁数へ丸めた容量 */
  capacity: number;
  /**
   * Meter に渡す使用率。容量0で使用があるときは全量が容量超過なので +Infinity
   * (Meter は100%で頭打ちにしたうえで超過表示にする)。
   */
  ratio: number;
};

export function parkingUsage(used: number, capacity: number, fractionDigits: number): ParkingUsage {
  const scale = 10 ** fractionDigits;
  const roundedUsed = Math.round(used * scale) / scale;
  const roundedCapacity = Math.round(capacity * scale) / scale;
  let ratio = 0;
  if (roundedCapacity > 0) {
    ratio = roundedUsed / roundedCapacity;
  } else if (roundedUsed > 0) {
    ratio = Number.POSITIVE_INFINITY;
  }
  return { used: roundedUsed, capacity: roundedCapacity, ratio };
}
