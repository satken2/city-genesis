import { useTranslation } from "react-i18next";
import type { useRouteProposal } from "./useRouteProposal";

export function RouteProposalControls({ state }: { state: ReturnType<typeof useRouteProposal> }) {
  const { t } = useTranslation();
  if (!state.visible) return null;
  return <div className="pc-section">
    <div className="pc-section-title">{t("route_proposal.title")}</div>
    <div className="pc-settings-row">
      <select value={state.heightEditable ? state.heightMode : "fixed"} disabled={state.busy}
        onChange={(event) => state.setHeightMode(event.target.value as "fixed" | "adjust")} aria-label={t("route_proposal.mode")}>
        <option value="fixed">{t("route_proposal.fixed")}</option>
        <option value="adjust" disabled={!state.heightEditable}>{t("route_proposal.adjust")}</option>
      </select>
    </div>
    <p>{t("route_proposal.help")}</p>
    <div className="pc-settings-row">
      <button type="button" disabled={!state.available} onClick={() => void state.propose()}>{t(state.busy ? "route_proposal.searching" : "route_proposal.title")}</button>
      {state.busy && <button type="button" onClick={state.cancel}>{t("route_proposal.cancel")}</button>}
      <button type="button" disabled={!state.canUndo} onClick={() => void state.undo()}>{t("route_proposal.undo")}</button>
    </div>
    {state.message && <p role="alert">{state.message}</p>}
  </div>;
}
