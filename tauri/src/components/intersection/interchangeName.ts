import type { TFunction } from "i18next";
import { INTERCHANGE_KIND_JCT } from "../../constants";
import type { InterchangeSummary } from "../../types";

/**
 * インターチェンジの表示名。未命名(空文字)なら種別の略号とIDで作る。
 * 略号はUnityの3Dラベル(多言語リソースを持たない)と揃え、地図と3Dビューで同じ対象だと分かるようにする。
 */
export function interchangeDisplayName(
  ic: Pick<InterchangeSummary, "interchange_id" | "name" | "kind">,
  t: TFunction
): string {
  if (ic.name) return ic.name;
  return ic.kind === INTERCHANGE_KIND_JCT
    ? t("interchange.fallbackName.jct", { id: ic.interchange_id })
    : t("interchange.fallbackName.ic", { id: ic.interchange_id });
}

/** 種別名(インターチェンジ/ジャンクション) */
export function interchangeKindLabel(kind: number, t: TFunction): string {
  return kind === INTERCHANGE_KIND_JCT ? t("interchange.kind.jct") : t("interchange.kind.ic");
}
