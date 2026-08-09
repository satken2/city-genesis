/**
 * 土地区画の環境セクション
 *
 * 区画固有の環境スコア(日照・大気汚染・騒音・犯罪)をメーターで表示する。
 * バックエンドの GET /api/v1/parcels/{id}/environment が返す値はいずれも
 * [0,1] の「悪さ度合い」(0=理想、1=最悪)で、住環境マルチプライヤーの合成前の生スコア。
 * 日照だけは「影になっている時間割合」なので、表示は反転させて日当たりの良さとして見せる。
 *
 * 値はエポック実行時にまとめて計算されるため、エポック未実行の都市では null が返る。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Meter } from "../common/Meter";
import { buildApiUrl, fetchApi } from "../../utils/api";

/** GET /api/v1/parcels/{id}/environment のレスポンス */
type ParcelEnvironment = {
  parcel_id: number;
  sunshine: number | null;
  air_pollution: number | null;
  noise: number | null;
  crime: number | null;
};

type ParcelEnvironmentSectionProps = {
  apiBaseUrl: string;
  parcelId: number;
};

export function ParcelEnvironmentSection({
  apiBaseUrl,
  parcelId,
}: ParcelEnvironmentSectionProps) {
  const { t } = useTranslation();
  const [environment, setEnvironment] = useState<ParcelEnvironment | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<ParcelEnvironment>(
      buildApiUrl(apiBaseUrl, `/api/v1/parcels/${parcelId}/environment`),
      { signal: controller.signal }
    )
      .then((res) => {
        if (controller.signal.aborted) return;
        setEnvironment(res);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        console.error("Failed to fetch parcel environment:", err);
        setEnvironment(null);
      });
    return () => controller.abort();
  }, [apiBaseUrl, parcelId]);

  // 4要素はすべて同じエポック処理で計算されるため、揃って未計算になる。
  // 個別に欠けるのは区画が生成直後でヒートマップに未収録のときで、扱いは同じ。
  const hasAnyValue =
    environment != null &&
    (environment.sunshine != null ||
      environment.air_pollution != null ||
      environment.noise != null ||
      environment.crime != null);

  return (
    <section className="agent-detail-section">
      <h3 className="agent-detail-section-title">
        {t("parcel.environment", "環境")}
      </h3>
      {hasAnyValue ? (
        <div className="parcel-environment-list">
          {environment.sunshine != null && (
            <Meter
              label={t("parcel.envSunshine")}
              value={1 - environment.sunshine}
            />
          )}
          {environment.air_pollution != null && (
            <Meter
              label={t("parcel.envAirPollution")}
              value={environment.air_pollution}
              polarity="negative"
            />
          )}
          {environment.noise != null && (
            <Meter
              label={t("parcel.envNoise")}
              value={environment.noise}
              polarity="negative"
            />
          )}
          {environment.crime != null && (
            <Meter
              label={t("parcel.envCrime")}
              value={environment.crime}
              polarity="negative"
            />
          )}
        </div>
      ) : (
        <p className="agent-detail-muted">{t("parcel.envNoData")}</p>
      )}
    </section>
  );
}
