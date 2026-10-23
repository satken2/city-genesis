/**
 * District list tab component.
 *
 * Lists every District as a wide card, stacked vertically, so Districts can be compared at a glance.
 * Each card has three columns:
 * - Population, daytime population, car ownership rate, and average Household income.
 * - Floor capacity and occupancy by floor type.
 * - Living environment metrics on a 100-point scale.
 *
 * Combines the per-District data of existing statistics APIs (commute, income, real estate, and
 * wellbeing District snapshot). They share the per-Epoch cache with the corresponding tabs.
 * Floor amounts come from the real estate API: the District API's space supplies/demands are
 * market quantities and do not equal the floor area in use.
 */

import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useEpochCachedStats } from "../useEpochCache";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatRate } from "./CommuteTab";
import { spaceTypeLabelKey } from "./RealEstateTab";
import "./ZoneListTab.css";

// --- Fields of the backend responses used by this tab ---

type CommuteStats = {
  zone_populations: {
    zone_id: number;
    name: string;
    night_population: number;
    daytime_population: number;
  }[];
  car_ownership: { zones: { zone_id: number; rate: number | null }[] };
};

/** average_income is null where there are no Resident Households. */
type IncomeStats = {
  scopes: { zone_id: number | null; average_income: number | null }[];
};

type FloorPoint = {
  space_type: number;
  space_type_name: string;
  capacity: number;
  occupied: number;
};

type RealEstateStats = {
  scopes: { zone_id: number | null; current_floors: FloorPoint[] }[];
};

/** Values are null where the Household population is zero; Districts are absent before the first Epoch. */
type WellbeingZoneStats = {
  zones: { zone_id: number; values: Record<string, number | null> }[];
};

/** Living comfort first, then its four elements, matching the Living environment tab. */
const COMFORT_KEYS = ["satis_comfort", "satis_residential", "quiet", "sunlight", "safety"];

type ZoneCard = {
  zoneId: number;
  name: string;
  population: number;
  daytimePopulation: number;
  carOwnershipRate: number | null;
  averageIncome: number | null;
  floors: FloorPoint[];
  comfort: Record<string, number | null>;
};

function ZoneMetric({ label, value }: { label: string; value: string }) {
  return (
    <div className="zone-list-card__row">
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

export function ZoneListTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t, i18n } = useTranslation();
  const commute = useEpochCachedStats<CommuteStats>(apiBaseUrl, "/api/v1/stats/commute");
  const income = useEpochCachedStats<IncomeStats>(apiBaseUrl, "/api/v1/stats/income");
  const realestate = useEpochCachedStats<RealEstateStats>(apiBaseUrl, "/api/v1/stats/realestate");
  const wellbeing = useEpochCachedStats<WellbeingZoneStats>(apiBaseUrl, "/api/v1/stats/wellbeing/zones");

  const cards = useMemo<ZoneCard[] | null>(() => {
    if (!commute.data || !income.data || !realestate.data || !wellbeing.data) {
      return null;
    }
    const carRates = new Map(commute.data.car_ownership.zones.map((z) => [z.zone_id, z.rate]));
    const incomes = new Map(income.data.scopes.map((s) => [s.zone_id, s.average_income]));
    const floors = new Map(realestate.data.scopes.map((s) => [s.zone_id, s.current_floors]));
    const comforts = new Map(wellbeing.data.zones.map((z) => [z.zone_id, z.values]));
    return commute.data.zone_populations
      .map((z) => ({
        zoneId: z.zone_id,
        name: z.name,
        population: z.night_population,
        daytimePopulation: z.daytime_population,
        carOwnershipRate: carRates.get(z.zone_id) ?? null,
        averageIncome: incomes.get(z.zone_id) ?? null,
        floors: floors.get(z.zone_id) ?? [],
        comfort: comforts.get(z.zone_id) ?? {},
      }))
      .sort((a, b) => a.zoneId - b.zoneId);
  }, [commute.data, income.data, realestate.data, wellbeing.data]);

  const sources = [commute, income, realestate, wellbeing];
  if (sources.some((s) => s.loading)) {
    return <div className="zone-list-tab__message">{t("common.loading")}</div>;
  }
  const error = sources.find((s) => s.error)?.error;
  if (error || !cards) {
    return (
      <div className="zone-list-tab__message">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }
  if (cards.length === 0) {
    return <div className="zone-list-tab__message">{t("statistics.noZoneData")}</div>;
  }

  const locale = resolveIntlLocale(i18n.language);
  const people = (n: number) => `${n.toLocaleString(locale)} ${t("statistics.people")}`;
  const sqm = (n: number) => t("common.sqm", { value: Math.round(n).toLocaleString(locale) });
  const points = (v: number | null | undefined) =>
    v == null ? "—" : `${(v * 100).toFixed(1)} ${t("wellbeing.points")}`;

  return (
    <div className="zone-list-tab">
      {cards.map((card) => (
        <article key={card.zoneId} className="zone-list-card">
          <h3 className="zone-list-card__title">{card.name}</h3>
          <div className="zone-list-card__body">
            <dl className="zone-list-card__list">
              <ZoneMetric label={t("zone.zonePopulation")} value={people(card.population)} />
              <ZoneMetric label={t("zone.daytimePopulation")} value={people(card.daytimePopulation)} />
              <ZoneMetric label={t("statistics.carOwnershipTitle")} value={formatRate(card.carOwnershipRate)} />
              <ZoneMetric
                label={t("statistics.incomeLegend")}
                value={card.averageIncome == null ? "—" : t("common.currency", { value: Math.round(card.averageIncome) })}
              />
            </dl>
            <table className="zone-list-card__floors">
              <thead>
                <tr>
                  <th>{t("statistics.floorType")}</th>
                  <th>{t("statistics.floorCapacity")}</th>
                  <th>{t("statistics.floorOccupied")}</th>
                </tr>
              </thead>
              <tbody>
                {card.floors.map((floor) => (
                  <tr key={floor.space_type}>
                    <td>{t(spaceTypeLabelKey(floor.space_type_name))}</td>
                    <td>{sqm(floor.capacity)}</td>
                    <td>{sqm(floor.occupied)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <section>
              <div className="zone-list-card__heading">{t("wellbeing.comfortTitle")}</div>
              <dl className="zone-list-card__list">
                {COMFORT_KEYS.map((key) => (
                  <ZoneMetric key={key} label={t(`wellbeing.${key}`)} value={points(card.comfort[key])} />
                ))}
              </dl>
            </section>
          </div>
        </article>
      ))}
    </div>
  );
}
