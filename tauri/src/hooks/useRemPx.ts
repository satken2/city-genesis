/**
 * rem を今の文字サイズ設定での px に直す関数を返す。
 *
 * recharts の軸幅(YAxis の width)のように px の数値しか受け付けない寸法を、
 * 文字(rem)と同じ比率で拡縮させるために使う。固定の px のままだと、設定を大きくしたときに
 * 目盛りの文字だけが大きくなって切れる。文字サイズの設定が変わると FontSizeContext 経由で
 * 呼び出し元が再描画され、新しい値が返る。
 */

import { useCallback } from "react";
import { useFontSize } from "../contexts/FontSizeContext";

export function useRemPx(): (rem: number) => number {
  const { fontSize } = useFontSize();
  return useCallback((rem: number) => Math.round(rem * fontSize), [fontSize]);
}
