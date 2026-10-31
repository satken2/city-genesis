import i18n, { type TFunction } from "i18next";

/** コアと Rust が共有する、表示文を含まないエラー情報。 */
export interface ErrorInfo {
  code: string;
  params: Record<string, unknown>;
}

export function errorInfo(error: unknown): ErrorInfo {
  if (error && typeof error === "object" && "code" in error && typeof error.code === "string") {
    const params = "params" in error && error.params && typeof error.params === "object" && !Array.isArray(error.params)
      ? error.params as Record<string, unknown> : {};
    return { code: error.code, params };
  }
  // ネイティブ側の通信状態など、本文を持たない単一コードも同じ翻訳経路で扱う。
  const code = typeof error === "string" ? error : error instanceof Error ? error.message : "";
  return { code: /^[A-Z][A-Z0-9_]*$/.test(code) ? code : "UNKNOWN_ERROR", params: {} };
}

/** 診断文や応答本文をフォールバックに使わず、表示時点の言語で理由を組み立てる。 */
export function describeError(error: unknown, t: TFunction = i18n.t.bind(i18n)): string {
  const { code, params } = errorInfo(error);
  const values = { ...params };
  if (code === "TRANSIT_TRACK_CAPACITY_EXCEEDED" && Array.isArray(params.overloads)) {
    const overloads = params.overloads;
    if (!overloads.every(item => item && typeof item === "object" && Number.isFinite(item.path_id)
      && Number.isFinite(item.load) && Number.isFinite(item.capacity)
      && (item.min_headway == null || Number.isFinite(item.min_headway)))) return t("errors.UNKNOWN_ERROR");
    values.details = overloads.map((item: Record<string, unknown>) => {
      const key = !item.single_track ? "TRANSIT_TRACK_LOAD" : item.min_headway != null ? "TRANSIT_TRACK_HEADWAY" : "TRANSIT_TRACK_OCCUPANCY";
      return t(`errors.${key}`, { ...item,
        load: Math.round(Number(item.load)), capacity: Math.round(Number(item.capacity)),
        min_headway: Math.ceil(Number(item.min_headway) * 10) / 10,
      });
    }).join(t("errors.detailSeparator"));
    values.hint = overloads.some((item: Record<string, unknown>) => item.single_track)
      ? t("errors.TRANSIT_SINGLE_TRACK_HINT") : "";
  }
  const message = t(`errors.${code}`, { ...values, defaultValue: t("errors.UNKNOWN_ERROR") });
  return message.includes("{{") ? t("errors.UNKNOWN_ERROR") : message;
}
