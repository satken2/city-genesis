/**
 * 市民パネルの「行動」タブ
 *
 * コアの推定(GET /api/v1/citizens/{id}/itinerary)を表示する。訪問先は勤務先・学校と、世帯が前エポックに
 * 買い物をした店(病院の医療サービスなども小売の買い物)で、どれも自宅との往復。各行に自宅からの片道の
 * 所要時間と乗り継ぎ(徒歩・車・路線)を添える。リストの上の地図に自宅・訪問先と片道経路を重ね、番号で対応させる。
 * 経路はエポックの経路計算の結果なので、計算前・計算中は理由を出す。
 */

import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";

import { useMasterData } from "../../contexts/MasterDataContext";
import type { CitizenItinerary, CitizenTrip, CitizenVisit } from "../../utils/citizenTracking";
import { CitizenActivityMap } from "./CitizenActivityMap";

type CitizenActivityPanelProps = {
  apiBaseUrl: string;
  itinerary: CitizenItinerary | null;
  failed: boolean;
};

export function CitizenActivityPanel({ apiBaseUrl, itinerary, failed }: CitizenActivityPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  if (failed) return <p className="agent-detail-muted">{t("citizenTracking.status.error")}</p>;
  if (!itinerary) return <p className="agent-detail-muted">{t("common.loading")}</p>;
  if (itinerary.status !== "ok") {
    return <p className="agent-detail-muted">{t(`citizenTracking.status.${itinerary.status}`, { defaultValue: t("citizenTracking.status.error") })}</p>;
  }

  const visits = itinerary.visits ?? [];
  const trips = itinerary.trips ?? [];
  const destinations = visits.map((visit, index) => ({ visit, index })).filter(({ index }) => index > 0);

  /** 店で買っている品目の表示名(品目ごとに1軒を選ぶので、同じ店に複数の品目が並ぶことがある)。 */
  function productNames(visit: CitizenVisit): string {
    return visit.product_ids
      .map((id) => masterData?.products?.[id]?.display_name ?? String(id))
      .join("・");
  }

  /** 乗り継ぎの並び。停留所への接続や右左折は省き、同じ手段が続くときは1つにまとめる。 */
  function routeSummary(trip: CitizenTrip): string {
    const parts: string[] = [];
    for (const leg of trip.legs ?? []) {
      if (leg.kind === "access" || leg.kind === "turn") continue;
      const label = leg.kind === "transit" ? leg.name || t("citizenTracking.legs.transit") : t(`citizenTracking.legs.${leg.kind}`);
      if (parts[parts.length - 1] !== label) parts.push(label);
    }
    return parts.join(" → ");
  }

  function openAgent(agentId: number) {
    void invoke("open_registered_window", { windowType: "agent_detail", entityId: String(agentId) });
  }

  return (
    <>
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("citizenTracking.home")}</h3>
        <div className="agent-detail-grid">
          <span className="agent-detail-label">{t("citizenTracking.visitKind.home")}</span>
          <span className="agent-detail-value">
            {visits[0] ? (
              <button className="entity-link" type="button" onClick={() => openAgent(visits[0].agent_id)}>
                {visits[0].name || `#${visits[0].agent_id}`}
              </button>
            ) : "-"}
          </span>
          <span className="agent-detail-label">{t("citizenTracking.mobility")}</span>
          <span className="agent-detail-value">{t(`citizenTracking.mode.${itinerary.mode ?? "pedestrian"}`)}</span>
        </div>
      </section>

      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("citizenTracking.visits")}</h3>
        <CitizenActivityMap apiBaseUrl={apiBaseUrl} itinerary={itinerary} />
        {destinations.length === 0 ? (
          <p className="agent-detail-muted">{t("citizenTracking.noVisits")}</p>
        ) : (
          <ul className="citizen-visits">
            {destinations.map(({ visit, index }) => {
              const trip = trips.find((item) => item.from_visit === 0 && item.to_visit === index);
              return (
                <li key={`${visit.kind}-${visit.agent_id}`} className="citizen-visit">
                  <span className="citizen-visit-number">{index}</span>
                  <div className="citizen-visit-content">
                    <div className="citizen-visit-head">
                      <span className={`citizen-visit-kind citizen-visit-kind--${visit.kind}`}>
                        {t(`citizenTracking.visitKind.${visit.kind}`)}
                      </span>
                      <button className="entity-link" type="button" onClick={() => openAgent(visit.agent_id)}>
                        {visit.name || `#${visit.agent_id}`}
                      </button>
                    </div>
                    {visit.kind === "shop" && visit.product_ids.length > 0 && (
                      <div className="citizen-visit-detail">{productNames(visit)}</div>
                    )}
                    <div className="citizen-visit-detail">
                      {trip && trip.status === "ok"
                        ? t("citizenTracking.trip", { minutes: Math.max(1, Math.round(trip.minutes ?? 0)), route: routeSummary(trip) })
                        : t("citizenTracking.noRoute")}
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </section>
    </>
  );
}
