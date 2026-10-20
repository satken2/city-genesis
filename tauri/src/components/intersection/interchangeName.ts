import type { TFunction } from "i18next";
import { INTERCHANGE_KIND_JCT } from "../../constants";
import type { InterchangeSummary } from "../../types";

/**
 * Interchange display name. If unnamed (an empty string), generate it from the type abbreviation and ID.
 * Match Unity's 3D labels (which have no multilingual resources) so the map and 3D view identify the same object.
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

/** Type name (interchange/junction). */
export function interchangeKindLabel(kind: number, t: TFunction): string {
  return kind === INTERCHANGE_KIND_JCT ? t("interchange.kind.jct") : t("interchange.kind.ic");
}
