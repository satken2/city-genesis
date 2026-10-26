/**
 * Mayor's actions on a citizen (buttons in the citizen detail panel)
 *
 * The core carries out each action at once (`/api/v1/citizens/{id}/<action>`).
 * - Reward and donation ask for the amount in the confirmation window, up to the payer's cash: the city's for a
 *   reward, the household's for a donation.
 * - Expel and relocate run without confirmation and report the result with only an OK button, one of several
 *   lines picked at random.
 */
import type { TFunction } from "i18next";
import { emit } from "@tauri-apps/api/event";

import type { AgentResponse } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { TRACKED_CITIZENS_CHANGED_EVENT } from "../../utils/citizenTracking";
import { requestAmount, showNotice } from "../../utils/confirmDialog";
import { resolveIntlLocale } from "../../utils/locale";
import { formatExactAmount } from "../../utils/money";
import rewardIcon from "../../assets/images/citizen-voice/reward.svg";
import expelIcon from "../../assets/images/citizen-voice/expel.svg";
import donationIcon from "../../assets/images/citizen-voice/donation.svg";
import relocateIcon from "../../assets/images/citizen-voice/relocate.svg";

export const CITIZEN_ACTIONS = [
  { id: "reward", icon: rewardIcon },
  { id: "expel", icon: expelIcon },
  { id: "donation", icon: donationIcon },
  { id: "relocate", icon: relocateIcon },
] as const;

export type CitizenActionId = (typeof CITIZEN_ACTIONS)[number]["id"];

export type CitizenActionTarget = {
  citizenId: number;
  name: string;
  householdAgentId: number;
};

async function payerCash(apiBaseUrl: string, action: "reward" | "donation", target: CitizenActionTarget) {
  if (action === "reward") {
    const budgets = await fetchApi<{ current_reserves?: number }>(buildApiUrl(apiBaseUrl, "/api/v1/city/budgets"));
    return budgets.current_reserves ?? 0;
  }
  const household = await fetchApi<AgentResponse>(buildApiUrl(apiBaseUrl, `/api/v1/agents/${target.householdAgentId}`));
  return household.economic_agent.reserves;
}

async function post(apiBaseUrl: string, target: CitizenActionTarget, action: CitizenActionId, body?: object) {
  await fetchApi(buildApiUrl(apiBaseUrl, `/api/v1/citizens/${target.citizenId}/${action}`), {
    method: "POST",
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
}

function randomResult(t: TFunction, action: CitizenActionId, name: string): string {
  const lines = t(`citizenAction.${action}.results`, { returnObjects: true });
  const count = typeof lines === "object" && lines !== null ? Object.keys(lines).length : 1;
  return t(`citizenAction.${action}.results.${Math.floor(Math.random() * count)}`, { name });
}

/** Run the action and resolve true once the core has carried it out (false if cancelled or failed). */
export async function runCitizenAction(
  apiBaseUrl: string,
  action: CitizenActionId,
  target: CitizenActionTarget,
  t: TFunction,
  language: string,
): Promise<boolean> {
  try {
    if (action === "reward" || action === "donation") {
      const available = Math.max(0, Math.floor(await payerCash(apiBaseUrl, action, target)));
      const amount = await requestAmount({
        titleKey: `citizenAction.${action}.label`,
        message: t(`citizenAction.${action}.message`, {
          name: target.name,
          available: formatExactAmount(available, resolveIntlLocale(language), true),
        }),
        confirmLabel: t(`citizenAction.${action}.confirm`),
        amountMax: available,
      });
      if (amount === null) return false;
      await post(apiBaseUrl, target, action, { amount });
      return true;
    }
    await post(apiBaseUrl, target, action);
    if (action === "expel") await emit(TRACKED_CITIZENS_CHANGED_EVENT, { citizen_id: target.citizenId });
    await showNotice({ titleKey: `citizenAction.${action}.label`, message: randomResult(t, action, target.name) });
    return true;
  } catch (err) {
    console.error(`citizen action ${action} failed`, err);
    return false;
  }
}
