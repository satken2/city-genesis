/**
 * To compare Districts vertically, every card shares the same column widths, item order and bar scales.
 * Population (shared by resident and daytime), income and floor area scale to the maximum across all Districts;
 * ownership rate and living environment scale to 100%.
 * Floors overlay capacity and occupancy from the real estate statistics, kept distinct from the District API's
 * market supply and demand quantities.
 */

import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useEpochCachedStats } from "../useEpochCache";
import { resolveIntlLocale } from "../../../utils/locale";
import { formatRate } from "./CommuteTab";
import { spaceTypeLabelKey } from "./RealEstateTab";
import { Meter } from "../../common/Meter";
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

function ZoneMetric({ label, value, max, detail, capacity, color }: {
  label: string;
  value: number | null;
  max: number;
  detail: string;
  capacity?: number;
  color?: string;
}) {
  return (
    <div className="zone-list-card__row">
      <dt title={label}>{label}</dt>
      <dd>
        <div className="zone-list-card__graphic" role="img" aria-label={detail} tabIndex={0}>
          {value == null ? <span className="zone-list-card__missing" aria-hidden="true">—</span> : (
            <div className="zone-list-card__track" aria-hidden="true">
              {capacity != null && (
                <div className="zone-list-card__capacity">
                  <Meter value={max > 0 ? capacity / max : 0} color="var(--flat-accent)" />
                </div>
              )}
              <div className={capacity == null ? "zone-list-card__fill" : "zone-list-card__occupied"}>
                <Meter value={max > 0 ? value / max : 0} color={color} />
              </div>
            </div>
          )}
          <span className="zone-list-card__tooltip" aria-hidden="true">{detail}</span>
        </div>
      </dd>
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

  const populationMax = cards.reduce((max, card) => Math.max(max, card.population, card.daytimePopulation), 0);
  const incomeMax = cards.reduce((max, card) => Math.max(max, card.averageIncome ?? 0), 0);
  const floorMax = cards.reduce((max, card) => card.floors.reduce(
    (floorMax, floor) => Math.max(floorMax, floor.capacity, floor.occupied), max
  ), 0);
  // 欠測の用途も同じ行を確保し、地区によって棒の並びがずれないようにする。
  const floorTypes = Array.from(new Map(cards.flatMap((card) => card.floors.map(
    (floor) => [floor.space_type, floor.space_type_name] as const
  )))).sort(([a], [b]) => a - b);
  const detail = (label: string, value: string, max: string) =>
    `${label}: ${value}\n${t("statistics.zoneComparisonRange", { max })}`;

  return (
    <div className="zone-list-tab">
      <div className="zone-list-tab__legend">
        <div className="zone-list-tab__columns">
          <h4>{t("statistics.zoneComparisonOverview")}</h4>
          <div className="zone-list-tab__floor-legend">
            <h4>{t("statistics.tabRealEstate")}</h4>
            <span><i className="zone-list-tab__swatch zone-list-tab__swatch--capacity" />{t("statistics.floorCapacity")}</span>
            <span><i className="zone-list-tab__swatch" />{t("statistics.floorOccupied")}</span>
          </div>
          <h4>{t("wellbeing.comfortTitle")} <span className="zone-list-tab__scale">0–100</span></h4>
        </div>
      </div>
      {cards.map((card) => (
        <article key={card.zoneId} className="zone-list-card">
          <h3 className="zone-list-card__title" title={card.name}>{card.name}</h3>
          <div className="zone-list-card__body">
            <dl className="zone-list-card__list">
              <ZoneMetric
                label={t("zone.zonePopulation")} value={card.population} max={populationMax}
                detail={detail(t("zone.zonePopulation"), people(card.population), people(populationMax))}
                color="var(--flat-accent)"
              />
              <ZoneMetric
                label={t("zone.daytimePopulation")} value={card.daytimePopulation} max={populationMax}
                detail={detail(t("zone.daytimePopulation"), people(card.daytimePopulation), people(populationMax))}
                color="var(--flat-accent)"
              />
              <ZoneMetric
                label={t("statistics.carOwnershipTitle")} value={card.carOwnershipRate} max={1}
                detail={detail(t("statistics.carOwnershipTitle"), formatRate(card.carOwnershipRate), formatRate(1))}
                color="var(--flat-accent)"
              />
              <ZoneMetric
                label={t("statistics.incomeLegend")} value={card.averageIncome} max={incomeMax}
                detail={detail(t("statistics.incomeLegend"),
                  card.averageIncome == null ? "—" : t("common.currency", { value: Math.round(card.averageIncome) }),
                  t("common.currency", { value: Math.round(incomeMax) }))}
                color="var(--flat-accent)"
              />
            </dl>
            <dl className="zone-list-card__list">
              {floorTypes.map(([type, name]) => {
                const floor = card.floors.find((floor) => floor.space_type === type);
                const label = t(spaceTypeLabelKey(name));
                return (
                  <ZoneMetric key={type} label={label} value={floor?.occupied ?? null} max={floorMax}
                    capacity={floor?.capacity} color="var(--flat-accent)"
                    detail={`${label}\n${t("statistics.floorCapacity")}: ${floor ? sqm(floor.capacity) : "—"}\n${t("statistics.floorOccupied")}: ${floor ? sqm(floor.occupied) : "—"}\n${t("statistics.zoneComparisonRange", { max: sqm(floorMax) })}`}
                  />
                );
              })}
            </dl>
            <dl className="zone-list-card__list">
              {COMFORT_KEYS.map((key) => (
                <ZoneMetric key={key} label={t(`wellbeing.${key}`)} value={card.comfort[key] ?? null} max={1}
                  detail={detail(t(`wellbeing.${key}`), points(card.comfort[key]), points(1))}
                />
              ))}
            </dl>
          </div>
        </article>
      ))}
    </div>
  );
}
