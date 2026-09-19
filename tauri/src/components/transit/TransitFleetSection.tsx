/**
 * 車両セクション(路線詳細の基本情報タブ)
 *
 * 「何両編成の列車が何編成走っているか」を、列車の側面図を縦に並べた簡易イラストで示す。
 * 1編成が1行で、1両は「横長の四角い車体＋前後の車輪」の記号(先頭車と中間車の区別はしない)。
 * 車体は路線カラーで塗り、編成の全長に細い連結線を上から重ねて車両どうしのつながりを表す。
 *
 * 描く編成数は実際に走っている編成数(＝調達できた車両数 ÷ 1編成の車両数の切り捨て)で、
 * 見出しの合計両数はイラストと必ず一致させる(端数で編成を組めない車両は数えない)。
 * 目標運行間隔に必要な編成数(train_count)に足りない分は、破線の枠だけの編成として続けて描き、
 * 「どれだけ足りないか」が一目で分かるようにする。
 */

import { useTranslation } from "react-i18next";
import "./TransitFleetSection.css";

type TransitFleetSectionProps = {
  /** 1編成あたりの車両数(プレイヤー設定) */
  carsPerTrain: number;
  /** 当エポックに調達できた車両数(両) */
  vehicleCount: number;
  /** 目標運行間隔を満たすのに必要な編成数(バックエンドの派生値) */
  requiredTrains: number;
  /** 路線カラー("#rrggbb")。車体・車輪・連結線の色に使う */
  color: string;
};

/** イラストで描く編成数の上限。これを超えた分は行数ではなく文言で示す */
const MAX_TRAINS_DRAWN = 12;

/** 1両の基準幅(px)。編成が長いほど縮めて、パネル幅に収まるようにする */
const CAR_WIDTH_MAX = 44;
const CAR_WIDTH_MIN = 12;
const TRAIN_WIDTH_BUDGET = 290;

/** 車体の縦横比(高さ÷幅)。実物の鉄道車両に近い横長の箱にする */
const CAR_ASPECT = 0.32;

export function TransitFleetSection({
  carsPerTrain,
  vehicleCount,
  requiredTrains,
  color,
}: TransitFleetSectionProps) {
  const { t } = useTranslation();

  const cars = Math.max(1, Math.round(carsPerTrain));
  const procured = Math.max(0, Math.round(vehicleCount));
  // 編成を組めない端数の車両は運行に使えないため、走行編成数は切り捨てで数える。
  const runningTrains = Math.floor(procured / cars);
  const missingTrains = Math.max(0, Math.round(requiredTrains) - runningTrains);
  const totalCars = runningTrains * cars;

  const drawnRunning = Math.min(runningTrains, MAX_TRAINS_DRAWN);
  const drawnMissing = Math.min(missingTrains, MAX_TRAINS_DRAWN - drawnRunning);
  const hidden = runningTrains + missingTrains - drawnRunning - drawnMissing;

  const carWidth = Math.max(CAR_WIDTH_MIN, Math.min(CAR_WIDTH_MAX, Math.floor(TRAIN_WIDTH_BUDGET / cars)));

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">{t("transit.fleet")}</h3>

      <div className="transit-fleet__header">
        <span className="transit-fleet__total">{t("transit.fleetTotalCars", { count: totalCars })}</span>
        <span className="transit-fleet__formation">
          {t("transit.fleetFormation", { cars, trains: runningTrains })}
        </span>
      </div>

      {runningTrains === 0 && missingTrains === 0 ? (
        <p className="agent-detail-muted">{t("transit.fleetNone")}</p>
      ) : (
        <div className="transit-fleet__trains">
          {Array.from({ length: drawnRunning }, (_, i) => (
            <TrainGlyph key={`run-${i}`} cars={cars} carWidth={carWidth} color={color} />
          ))}
          {Array.from({ length: drawnMissing }, (_, i) => (
            <TrainGlyph key={`miss-${i}`} cars={cars} carWidth={carWidth} color={color} missing />
          ))}
        </div>
      )}

      {hidden > 0 && (
        <p className="transit-fleet__note">{t("transit.fleetMoreTrains", { count: hidden })}</p>
      )}
      {missingTrains > 0 && (
        <p className="transit-fleet__note transit-fleet__note--warn">
          {t("transit.fleetShortage", { count: missingTrains })}
        </p>
      )}
    </section>
  );
}

type TrainGlyphProps = {
  cars: number;
  carWidth: number;
  color: string;
  /** 車両不足で組めていない編成。車体を破線の枠だけにして薄く描く */
  missing?: boolean;
};

/**
 * 1編成の側面図。横長の車体の箱と前後の車輪だけの記号で、先頭車・中間車は区別しない。
 * 車体の中ほどに編成の全長を貫く細い連結線を重ね、車両間の隙間をつないで1本の列車に見せる。
 * 座標系は px と 1:1 にして、車両数に応じた実寸をそのまま viewBox に使う。
 */
function TrainGlyph({ cars, carWidth, color, missing = false }: TrainGlyphProps) {
  const gap = Math.max(2, Math.round(carWidth * 0.12));
  const bodyHeight = Math.max(6, Math.round(carWidth * CAR_ASPECT));
  const wheelR = Math.max(2, Math.round(carWidth * 0.08));
  const bodyY = 1;
  const wheelY = bodyY + bodyHeight + wheelR - 1;
  const width = cars * carWidth + (cars - 1) * gap;
  const height = wheelY + wheelR + 1;
  const couplerHeight = Math.max(2, Math.round(bodyHeight * 0.3));
  const couplerY = bodyY + (bodyHeight - couplerHeight) / 2;

  const bodyProps = missing
    ? { fill: "none", stroke: color, strokeWidth: 1, strokeDasharray: "3 3" }
    : { fill: color };

  return (
    <svg
      className={`transit-fleet__train${missing ? " transit-fleet__train--missing" : ""}`}
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="presentation"
    >
      {Array.from({ length: cars }, (_, i) => {
        const x = i * (carWidth + gap);
        return (
          <g key={i}>
            <rect x={x} y={bodyY} width={carWidth} height={bodyHeight} {...bodyProps} />
            <circle cx={x + carWidth * 0.22} cy={wheelY} r={wheelR} fill={color} />
            <circle cx={x + carWidth * 0.78} cy={wheelY} r={wheelR} fill={color} />
          </g>
        );
      })}
      {/* 連結線。車体より後に描いて上から重ね、車両間の隙間を埋める。
          組めていない編成は連結していないので線を引かない。 */}
      {cars > 1 && !missing && (
        <rect x={0} y={couplerY} width={width} height={couplerHeight} fill={color} />
      )}
    </svg>
  );
}
