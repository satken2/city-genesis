/**
 * 路線の収支見込みカード
 *
 * 必要車両数・1周の距離と時間・運営原価(車両リース費)・想定乗車率での運賃収入・収支・損益分岐の乗車率を、
 * バックエンドの試算(hooks/useTransitEstimate)どおりに並べるだけの表示部品。値の計算はしない。
 * 路線の新設パネル(bc-* の意匠)と路線詳細の設定タブ(agent-detail-* の意匠)の両方に置くため、
 * 見出しと外枠は置き場所のパネルが持ち、ここは地色を持たない中身だけを描く。
 */

import { useTranslation } from "react-i18next";
import type { TransitLineEstimate } from "../../types";
import { resolveIntlLocale } from "../../utils/locale";
import "./TransitEstimateCard.css";

// 試算できない理由コード → i18n キー。未知のコードは汎用文言にする。
const UNAVAILABLE_KEYS: Record<string, string> = {
  insufficient_stops: "transit.estimate.unavailable.insufficientStops",
  invalid_stop: "transit.estimate.unavailable.invalidStop",
  no_distance: "transit.estimate.unavailable.noDistance",
  route_disconnected: "transit.estimate.unavailable.routeDisconnected",
  no_underlay: "transit.estimate.unavailable.noUnderlay",
  train_too_long: "transit.estimate.unavailable.trainTooLong",
};

type TransitEstimateCardProps = {
  estimate: TransitLineEstimate | null;
  loading: boolean;
  error: string | null;
  /** 試算の入力が揃っていない間(停留所が2つ未満など)に出す案内 */
  placeholder?: string;
};

export function TransitEstimateCard({ estimate, loading, error, placeholder }: TransitEstimateCardProps) {
  const { t, i18n } = useTranslation();
  const locale = resolveIntlLocale(i18n.language);

  if (error) {
    return <p className="transit-estimate__error">{error}</p>;
  }
  if (!estimate) {
    const text = loading ? t("transit.estimate.loading") : placeholder;
    return text ? <p className="transit-estimate__muted">{text}</p> : null;
  }
  // 取得中は直前の結果を薄く残し、入力のたびに表示が空になって跳ねるのを防ぐ。
  const rootClass = `transit-estimate${loading ? " transit-estimate--stale" : ""}`;
  if (!estimate.ok) {
    return (
      <div className={rootClass}>
        <p className="transit-estimate__warning">
          {t((estimate.reason && UNAVAILABLE_KEYS[estimate.reason]) || "transit.estimate.unavailable.generic")}
        </p>
      </div>
    );
  }

  const number = (value: number, digits = 0) =>
    value.toLocaleString(locale, { maximumFractionDigits: digits, minimumFractionDigits: digits });
  const money = (value: number) => `${number(Math.round(value))}${t("transit.currencyUnit")}`;
  const loadPercent = formatPercent(estimate.load_factor, locale);
  const balance = estimate.balance;
  const breakEven = estimate.break_even_load_factor;
  // 超過線路が複数あっても、負荷率が最も高い1本だけを示せば「間隔を延ばす/線路を増やす」判断には足りる。
  // 軌道の超過(便数)と単線区間の超過(占有時間)は単位が違うので、容量に対する比で比べる。
  const worstOverload = estimate.track_overloads.reduce<TransitLineEstimate["track_overloads"][number] | null>(
    (worst, o) => (worst === null || o.load / o.capacity > worst.load / worst.capacity ? o : worst),
    null,
  );

  return (
    <div className={rootClass}>
      <div className="transit-estimate__rows">
        <EstimateRow
          label={t("transit.estimate.vehicles")}
          value={t("transit.estimate.vehiclesValue", { count: estimate.vehicles_required ?? 0 })}
          sub={t("transit.fleetFormation", { cars: estimate.cars_per_train, trains: estimate.train_count ?? 0 })}
        />
        <EstimateRow
          label={t("transit.estimate.roundTrip")}
          value={t("transit.estimate.roundTripValue", {
            km: number((estimate.round_trip_length_m ?? 0) / 1000, 1),
            minutes: number(estimate.round_trip_minutes ?? 0, 1),
          })}
          sub={t("transit.estimate.effectiveHeadway", { minutes: number(estimate.effective_headway ?? 0, 1) })}
        />
        <EstimateRow
          label={t("transit.estimate.operatingCost")}
          value={estimate.operating_cost != null ? money(estimate.operating_cost) : "—"}
          sub={
            estimate.lease_price != null
              ? t("transit.estimate.leasePrice", { price: money(estimate.lease_price) })
              : t("transit.estimate.leasePriceUnknown")
          }
        />
        <EstimateRow
          label={t("transit.estimate.revenue")}
          value={money(estimate.revenue ?? 0)}
          sub={t("transit.estimate.revenueBasis", {
            percent: loadPercent,
            riders: number(Math.round(estimate.riders_per_day ?? 0)),
          })}
        />
        <EstimateRow
          label={t("transit.estimate.balance")}
          value={balance != null ? `${balance > 0 ? "+" : ""}${money(balance)}` : "—"}
          total
          tone={balance == null || balance === 0 ? undefined : balance > 0 ? "positive" : "negative"}
        />
        <EstimateRow
          label={t("transit.estimate.breakEven")}
          value={
            breakEven == null
              ? "—"
              : breakEven > 1
                ? t("transit.estimate.breakEvenOverFull")
                : t("transit.estimate.breakEvenValue", { percent: formatPercent(breakEven, locale) })
          }
        />
      </div>

      {estimate.depot_connected === false && (
        <p className="transit-estimate__warning">{t("transit.estimate.noDepot")}</p>
      )}
      {worstOverload && (
        <p className="transit-estimate__warning">
          {!worstOverload.single_track
            ? t("transit.estimate.trackOverload", {
                load: number(Math.round(worstOverload.load)),
                capacity: number(Math.round(worstOverload.capacity)),
              })
            : worstOverload.min_headway != null
              ? t("transit.estimate.singleTrackOverload", {
                  minutes: number(Math.ceil(worstOverload.min_headway * 10) / 10, 1),
                })
              : t("transit.estimate.singleTrackFull")}
        </p>
      )}
      <p className="transit-estimate__note">
        {t("transit.estimate.note", { days: number(estimate.simulation_days), percent: loadPercent })}
        {estimate.lease_price_basis === "estimated" && ` ${t("transit.estimate.estimatedLeaseNote")}`}
      </p>
    </div>
  );
}

/**
 * 割合(0〜1)を百分率の数値部分に整える。損益分岐の乗車率は0.01%台になることがあるため、
 * 10%未満は有効2桁で出し、0%と丸めてしまわないようにする。
 */
function formatPercent(ratio: number, locale: string): string {
  const percent = ratio * 100;
  const options: Intl.NumberFormatOptions =
    percent >= 10 ? { maximumFractionDigits: 0 } : { maximumSignificantDigits: 2 };
  return new Intl.NumberFormat(locale, options).format(percent);
}

type EstimateRowProps = {
  label: string;
  value: string;
  /** 値の下に添える根拠(編成の内訳・単価・前提の乗車率など) */
  sub?: string;
  /** 結論の行(収支見込み)として面を持たせて目立たせる */
  total?: boolean;
  /** 収支の黒字/赤字の色分け */
  tone?: "positive" | "negative";
};

function EstimateRow({ label, value, sub, total, tone }: EstimateRowProps) {
  const className = [
    "transit-estimate__row",
    total && "transit-estimate__row--total",
    tone && `transit-estimate__row--${tone}`,
  ]
    .filter(Boolean)
    .join(" ");
  return (
    <div className={className}>
      <span className="transit-estimate__label">{label}</span>
      <span className="transit-estimate__value">
        <span className="transit-estimate__main">{value}</span>
        {sub && <span className="transit-estimate__sub">{sub}</span>}
      </span>
    </div>
  );
}
