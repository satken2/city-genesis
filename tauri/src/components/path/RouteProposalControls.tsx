import { useTranslation } from "react-i18next";
import type { useRouteProposal } from "./useRouteProposal";

export function RouteProposalControls({ state }: { state: ReturnType<typeof useRouteProposal> }) {
  const { t } = useTranslation();
  if (!state.visible) return null;
  return <div className="pse-route-controls" title={t("route_proposal.help")}>
    <label className="pse-route-mode">
      <span>{t("route_proposal.mode")}</span>
      <select className="pse-select" value={state.heightEditable ? state.heightMode : "fixed"} disabled={state.busy}
        onChange={(event) => state.setHeightMode(event.target.value as "fixed" | "adjust")} aria-label={t("route_proposal.mode")}>
        <option value="fixed">{t("route_proposal.fixed")}</option>
        <option value="adjust" disabled={!state.heightEditable}>{t("route_proposal.adjust")}</option>
      </select>
    </label>
    <button type="button" className="pse-btn pse-btn--primary" disabled={!state.available}
      onClick={() => void state.propose()}>{t(state.busy ? "route_proposal.searching" : "route_proposal.title")}</button>
    {state.busy && <button type="button" className="pse-btn" onClick={state.cancel}>{t("route_proposal.cancel")}</button>}
    <button type="button" className="pse-btn" disabled={!state.canUndo}
      onClick={() => void state.undo()}>{t("route_proposal.undo")}</button>
    {state.message && <span className="pse-route-message" role="alert">{state.message}</span>}
  </div>;
}
