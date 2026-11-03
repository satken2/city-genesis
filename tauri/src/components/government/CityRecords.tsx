/**
 * 都市の現在の記録。対象・集計・単位の正は /city/records の応答で、ここでは表示だけを行う。
 * 最古の建物は作成日を仮想暦で表示する。記録保持者の名前は既存の詳細画面へのリンクにする。
 */
import { useCurrencyUnit } from "../../hooks/useCurrencyUnit";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { formatEpochDate } from "../../utils/date";
import { resolveIntlLocale } from "../../utils/locale";
import { formatExactAmount } from "../../utils/money";
import { AgentLink } from "../common/AgentLink";
import { CitizenLink } from "../common/CitizenLink";

type RecordHolder = { id: number; name: string; value: number };

export type CityRecordsData = {
  oldest_citizen: RecordHolder | null;
  wealthiest_household: RecordHolder | null;
  tallest_building: RecordHolder | null;
  oldest_building: RecordHolder | null;
  highest_rent_building: RecordHolder | null;
  mayor_days: number;
  road_length_m: number;
  railway_length_m: number;
  total_floor_area_m2: number;
  private_car_count: number;
};

function openAgent(entityId: string) {
  void invoke("open_registered_window", { windowType: "agent_detail", entityId }).catch(console.error);
}

export function CityRecords({ records }: { records: CityRecordsData }) {
  const { t, i18n } = useTranslation();
  useCurrencyUnit();
  const locale = resolveIntlLocale(i18n.language);
  const number = (value: number, decimals = 0) => value.toLocaleString(locale, { maximumFractionDigits: decimals });
  const money = (value: number) => formatExactAmount(value, locale, true);
  const entries: { key: string; holder?: RecordHolder | null; value: string; citizen?: boolean; hint?: string }[] = [
    { key: "oldestCitizen", holder: records.oldest_citizen, citizen: true,
      value: records.oldest_citizen ? t("cityGovernment.records.age", { value: number(records.oldest_citizen.value) }) : "—" },
    { key: "wealthiestHousehold", holder: records.wealthiest_household,
      value: records.wealthiest_household ? money(records.wealthiest_household.value) : "—" },
    { key: "tallestBuilding", holder: records.tallest_building,
      value: records.tallest_building ? `${number(records.tallest_building.value, 1)} m` : "—" },
    { key: "oldestBuilding", holder: records.oldest_building, hint: "createdDateHint",
      value: records.oldest_building ? formatEpochDate(records.oldest_building.value, locale) : "—" },
    { key: "highestRentBuilding", holder: records.highest_rent_building,
      value: records.highest_rent_building ? `${money(records.highest_rent_building.value)}${t("cityGovernment.unitPerAreaDay")}` : "—" },
    { key: "mayorDays", value: t("cityGovernment.records.days", { value: number(records.mayor_days) }) },
    { key: "roadLength", value: `${number(records.road_length_m / 1000, 2)} km` },
    { key: "railwayLength", value: `${number(records.railway_length_m / 1000, 2)} km`, hint: "railwayLengthHint" },
    { key: "totalFloorArea", value: `${number(records.total_floor_area_m2)} m²` },
    { key: "privateCars", value: t("cityGovernment.records.cars", { value: number(records.private_car_count) }) },
  ];

  return (
    <section className="city-gov__records" data-guide="cityGovernment.records" aria-labelledby="city-records-title">
      <h2 id="city-records-title">{t("cityGovernment.records.title")}</h2>
      <dl className="city-gov__stats">
        {entries.map(({ key, holder, value, citizen, hint }) => (
          <div key={key} className="city-gov__stat city-gov__record" title={hint ? t(`cityGovernment.records.${hint}`) : undefined}>
            <dt>{t(`cityGovernment.records.${key}`)}</dt>
            <dd>
              {holder && (citizen
                ? <CitizenLink citizenId={holder.id} citizenName={holder.name || `#${holder.id}`} />
                : <AgentLink agentId={holder.id} agentName={holder.name || `#${holder.id}`} onNavigate={openAgent} />)}
              <span>{value}</span>
            </dd>
          </div>
        ))}
      </dl>
    </section>
  );
}
