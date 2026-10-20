/**
 * Return a function converting rem to px at the current font size. Use for dimensions accepting only numeric px,
 * such as recharts YAxis width, so they scale with text. Fixed px would clip larger tick labels when font size
 * increases. FontSizeContext rerenders callers on setting changes, returning updated values.
 */

import { useCallback } from "react";
import { useFontSize } from "../contexts/FontSizeContext";

export function useRemPx(): (rem: number) => number {
  const { fontSize } = useFontSize();
  return useCallback((rem: number) => Math.round(rem * fontSize), [fontSize]);
}
