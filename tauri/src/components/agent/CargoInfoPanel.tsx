/**
 * 荷役情報コンポーネント
 *
 * 輸出入拠点(TYPE_IMPORT)の詳細パネルに表示される荷役情報セクション。
 * 拠点は単一の経済主体で輸入(都市への販売)と輸出(都市からの買取)の両方を担うため、
 * 取扱能力・稼働率のサマリーに続けて、輸入・輸出それぞれの品目別の
 * 割当量・取扱実績を横棒グラフで表示する。
 *
 * データは専用エンドポイント GET /api/v1/agents/{id}/cargo から取得する
 * (品目別データがSoAとrow-storeに分散しており、single_agentスキーマに含まれないため)。
 * 棒グラフは薄い帯=当エポックの割当量、濃い帯=取扱実績(輸入=都市への販売量、
 * 輸出=都市からの買取量)の二重表現で、FloorOccupancyBarと同じ読み方に揃えている。
 */

import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { AgentData, MasterData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { DataList } from "../common/DataList";
import "./CargoInfoPanel.css";

type CargoProduct = {
  product_id: number;
  allocation: number;
  handled: number;
  price: number;
  lost?: number;
};

type CargoInfoResponse = {
  capacity_per_epoch: number;
  imports: CargoProduct[];
  exports: CargoProduct[];
};

type CargoInfoPanelProps = {
  agent: AgentData;
  apiBaseUrl: string;
};

/** 取扱量の表示フォーマット(桁区切り+小数1桁まで) */
function formatAmount(value: number): string {
  return value.toLocaleString(undefined, { maximumFractionDigits: 1 });
}

type CargoDirectionSectionProps = {
  direction: "import" | "export";
  products: CargoProduct[];
  masterData: MasterData | null;
};

/**
 * 輸入または輸出の1方向ぶんの品目別グラフ。
 * 色系統(輸入=青、輸出=緑)はラッパーの cargo--import / cargo--export クラスで切り替わる。
 */
function CargoDirectionSection({ direction, products, masterData }: CargoDirectionSectionProps) {
  const { t } = useTranslation();
  const isImport = direction === "import";

  // 棒グラフのスケールは全品目の最大値(割当量と実績の大きい方)に合わせる。
  // 品目ごとに単位が異なるため厳密な量の比較ではないが、
  // 「どの品目がこの拠点の主力か」を直感的に掴むための相対表示とする。
  const maxValue = products.reduce(
    (max, p) => Math.max(max, p.allocation, p.handled),
    0,
  );

  // 主力品目が上に来るように実績降順(同値なら割当降順)で並べる
  const rows = [...products].sort(
    (a, b) => b.handled - a.handled || b.allocation - a.allocation,
  );

  const handledLabel = isImport
    ? t("cargo.handledImport", "販売量")
    : t("cargo.handledExport", "買取量");

  return (
    <div className={`cargo--${direction}`}>
      <h4 className="cargo-products-title">
        {isImport
          ? t("cargo.importSectionTitle", "輸入(都市への販売)")
          : t("cargo.exportSectionTitle", "輸出(都市からの買取)")}
      </h4>

      {rows.length === 0 ? (
        <p className="agent-detail-muted">{t("cargo.noProducts", "取扱品目がありません")}</p>
      ) : (
        <>
          <div className="cargo-legend">
            <span className="cargo-legend__item">
              <span className="cargo-legend__swatch cargo-legend__swatch--handled" />
              {handledLabel}
            </span>
            <span className="cargo-legend__item">
              <span className="cargo-legend__swatch cargo-legend__swatch--allocation" />
              {t("cargo.allocation", "割当量")}
            </span>
          </div>

          <div className="cargo-rows">
            {rows.map((row) => {
              const product = masterData?.products[row.product_id];
              const displayName = product?.display_name ?? String(row.product_id);
              const unit = product?.unit ?? "";
              const allocPct = maxValue > 0 ? (row.allocation / maxValue) * 100 : 0;
              const handledPct =
                row.allocation > 0 ? Math.min((row.handled / row.allocation) * 100, 100) : 0;

              return (
                <div key={row.product_id} className="cargo-row">
                  <div className="cargo-row__head">
                    <span className="cargo-row__name">{displayName}</span>
                    <span className="cargo-row__values">
                      {`${formatAmount(row.handled)} / ${formatAmount(row.allocation)} ${unit}`}
                    </span>
                  </div>
                  <div className="cargo-row__track">
                    <div className="cargo-row__allocation" style={{ width: `${allocPct}%` }}>
                      {row.handled > 0 && (
                        <div className="cargo-row__handled" style={{ width: `${handledPct}%` }} />
                      )}
                    </div>
                  </div>
                  <div className="cargo-row__sub">
                    <span>
                      {isImport
                        ? t("cargo.priceImport", "販売単価")
                        : t("cargo.priceExport", "買取単価")}
                      {": "}
                      {t("common.currency", { value: row.price.toFixed(2) })}
                    </span>
                    {(row.lost ?? 0) > 0 && (
                      <span className="cargo-row__lost">
                        {t("cargo.lost", "売り逃し")}
                        {": "}
                        {`${formatAmount(row.lost ?? 0)} ${unit}`}
                      </span>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}

export function CargoInfoPanel({ agent, apiBaseUrl }: CargoInfoPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [cargo, setCargo] = useState<CargoInfoResponse | null>(null);
  const [error, setError] = useState(false);

  // 詳細パネルが経済主体を再取得するたびにagentの参照が変わるため、
  // それに合わせて荷役情報も再取得する(エポック実行後の値の更新に追従する)
  useEffect(() => {
    const controller = new AbortController();
    setError(false);
    const url = buildApiUrl(apiBaseUrl, `/api/v1/agents/${agent.agent_id}/cargo`);
    fetchApi<CargoInfoResponse>(url, { signal: controller.signal })
      .then((res) => {
        if (!controller.signal.aborted) setCargo(res);
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("Failed to fetch cargo info:", err);
          setError(true);
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl, agent]);

  if (error) {
    return (
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("cargo.title", "荷役情報")}</h3>
        <p className="agent-detail-muted">{t("common.fetchError")}</p>
      </section>
    );
  }

  if (!cargo) {
    return (
      <section className="agent-detail-section">
        <h3 className="agent-detail-section-title">{t("cargo.title", "荷役情報")}</h3>
        <p className="agent-detail-muted">{t("common.loading")}</p>
      </section>
    );
  }

  const importHandled = cargo.imports.reduce((sum, p) => sum + p.handled, 0);
  const exportHandled = cargo.exports.reduce((sum, p) => sum + p.handled, 0);

  return (
    <section className="agent-detail-section cargo">
      <h3 className="agent-detail-section-title">{t("cargo.title", "荷役情報")}</h3>

      <p className="cargo-description">
        {t(
          "cargo.description",
          "この拠点は域外から商品を輸入して都市内の市場へ供給し、都市内の余剰生産物を買い取って域外へ輸出します。",
        )}
      </p>

      <div className="agent-detail-grid">
        <span className="agent-detail-label">{t("cargo.capacity", "取扱能力")}</span>
        <span className="agent-detail-value">
          {formatAmount(cargo.capacity_per_epoch)}
          <span className="cargo-unit-suffix">
            {t("cargo.perEpochPerDirection", "/エポック(輸出入それぞれ)")}
          </span>
        </span>

        <span className="agent-detail-label">{t("cargo.importHandled", "輸入取扱量")}</span>
        <span className="agent-detail-value">{formatAmount(importHandled)}</span>

        <span className="agent-detail-label">{t("cargo.exportHandled", "輸出取扱量")}</span>
        <span className="agent-detail-value">{formatAmount(exportHandled)}</span>

        {agent.utilization != null && (
          <>
            <span className="agent-detail-label">{t("cargo.utilization", "稼働率")}</span>
            <span className="agent-detail-value">
              <DataList
                values={agent.utilization}
                formatter={(v) => `${(v * 100).toFixed(1)}%`}
                clampRange={[0, 1]}
              />
            </span>
          </>
        )}
      </div>

      <CargoDirectionSection direction="import" products={cargo.imports} masterData={masterData} />
      <CargoDirectionSection direction="export" products={cargo.exports} masterData={masterData} />
    </section>
  );
}
