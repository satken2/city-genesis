/**
 * 予算ウィンドウの「税率」タブ
 *
 * GET/PATCH /api/v1/city/taxes と通信し、都市の税率を設定する。予算タブと同じく確定ボタンを持たず、
 * 「編集」中だけカードにスライダーを出し、変更は自動保存する。
 *
 * 消費税・輸入関税・輸出関税は「一括 / 個別」を切り替えられる。個別のカードを押すと(個別に切り替えた
 * ときも)、商品ごとの税率をウィンドウ内のオーバーレイで設定する。消費税は小売財(GOODS_RETAIL)だけ、
 * 関税は貿易対象(trade)の商品だけが対象。
 *
 * 注意: バックエンドの消費税(comsumption_tax)はスカラー値のみ対応のため、
 * 個別モードで設定した商品ごとの消費税率はUI上の表示のみで、保存されるのは一括税率。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { showNotification } from "../../utils/notification";
import { GOODS_RETAIL } from "../../constants";
import { useAutoSave } from "../../hooks/useAutoSave";
import { BudgetOverlay } from "./BudgetOverlay";
import "./CityBudgetWindow.css";

/** GET /api/v1/city/taxes のレスポンス。税率は比率(0.05 = 5%)で、関税は商品ID→税率。 */
type TaxData = {
  comsumption_tax: number;
  business_tax: number;
  income_tax: number;
  import_tax: Record<string, number>;
  export_tax: Record<string, number>;
};

/** 商品ごとの税率を持てる税 */
type PerProductTaxKey = "comsumption_tax" | "import_tax" | "export_tax";
type TaxKey = PerProductTaxKey | "income_tax" | "business_tax";

/** 税のカード。この順に上から並ぶ */
const TAX_CATEGORIES: { key: TaxKey; labelKey: string; perProduct: boolean }[] = [
  { key: "income_tax", labelKey: "tax.incomeTax", perProduct: false },
  { key: "business_tax", labelKey: "tax.businessTax", perProduct: false },
  { key: "comsumption_tax", labelKey: "tax.consumptionTax", perProduct: true },
  { key: "import_tax", labelKey: "tax.importTax", perProduct: true },
  { key: "export_tax", labelKey: "tax.exportTax", perProduct: true },
];

/**
 * 画面上の税率(パーセント)。scalar の関税は一括税率で、個別モードの間は使わない
 * (個別から一括へ戻すときに商品ごとの税率の平均で上書きする)。
 */
type TaxRates = {
  scalar: Record<TaxKey, number>;
  byProduct: Record<PerProductTaxKey, Record<number, number>>;
  individual: Record<PerProductTaxKey, boolean>;
};

/** 数値入力で設定できる税率の上限(%)。スライダーは 0〜100% まで。 */
const RATE_INPUT_MAX = 1000;

/** 税率(%)の表示。入力の刻みと同じ0.1%単位で、末尾の0は付けない。 */
function formatRate(pct: number): string {
  return String(parseFloat(pct.toFixed(1)));
}

/** 全商品が同じ税率ならその値、異なるか商品が無ければ null */
function uniformRate(rates: Record<number, number>): number | null {
  const values = Object.values(rates);
  if (values.length === 0) return null;
  return values.every((v) => Math.abs(v - values[0]) < 0.01) ? values[0] : null;
}

function toPercentMap(ratios: Record<string, number>): Record<number, number> {
  return Object.fromEntries(Object.entries(ratios).map(([id, r]) => [Number(id), r * 100]));
}

/** サーバの税率から画面上の税率を作る。関税は全商品が同じ税率なら一括、異なれば個別で始める。 */
function ratesFromData(data: TaxData, retailIds: number[]): TaxRates {
  const importRates = toPercentMap(data.import_tax);
  const exportRates = toPercentMap(data.export_tax);
  const importUniform = uniformRate(importRates);
  const exportUniform = uniformRate(exportRates);
  const consumption = data.comsumption_tax * 100;
  return {
    scalar: {
      income_tax: data.income_tax * 100,
      business_tax: data.business_tax * 100,
      comsumption_tax: consumption,
      import_tax: importUniform ?? 0,
      export_tax: exportUniform ?? 0,
    },
    byProduct: {
      comsumption_tax: Object.fromEntries(retailIds.map((id) => [id, consumption])),
      import_tax: importRates,
      export_tax: exportRates,
    },
    individual: {
      comsumption_tax: false,
      import_tax: importUniform === null,
      export_tax: exportUniform === null,
    },
  };
}

/** PATCH の本文(JSON文字列)。関税は一括なら貿易対象の全商品へ一括税率を送る。 */
function taxPayload(rates: TaxRates, tradeableIds: number[]): string {
  const tariff = (key: "import_tax" | "export_tax") =>
    rates.individual[key]
      ? Object.fromEntries(Object.entries(rates.byProduct[key]).map(([id, pct]) => [id, pct / 100]))
      : Object.fromEntries(tradeableIds.map((id) => [String(id), rates.scalar[key] / 100]));
  return JSON.stringify({
    income_tax: rates.scalar.income_tax / 100,
    business_tax: rates.scalar.business_tax / 100,
    comsumption_tax: rates.scalar.comsumption_tax / 100,
    import_tax: tariff("import_tax"),
    export_tax: tariff("export_tax"),
  });
}

/**
 * 税率スライダー(0〜100%)と数値入力(0〜1000%)。どちらも0.1%単位。
 * つまみを離したとき・フォーカスが外れたときに onCommit を呼ぶ。
 */
function TaxSliderInput({
  value,
  onChange,
  onCommit,
  disabled,
}: {
  value: number;
  onChange: (v: number) => void;
  onCommit: () => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();
  return (
    <div className="tax-slider-input" onClick={(e) => e.stopPropagation()}>
      <input
        type="range"
        min={0}
        max={100}
        step={0.1}
        value={value}
        onChange={(e) => onChange(parseFloat(e.target.value))}
        onPointerUp={onCommit}
        onBlur={onCommit}
        disabled={disabled}
        className="tax-slider-input__slider"
      />
      <input
        type="number"
        min={0}
        max={RATE_INPUT_MAX}
        step={0.1}
        value={parseFloat(value.toFixed(1))}
        onChange={(e) => {
          const v = parseFloat(e.target.value);
          if (!isNaN(v)) onChange(Math.max(0, Math.min(v, RATE_INPUT_MAX)));
        }}
        onBlur={onCommit}
        disabled={disabled}
        className="tax-slider-input__number"
      />
      <span className="tax-slider-input__unit">{t("tax.unit")}</span>
    </div>
  );
}

export function TaxRatesPane({
  apiBaseUrl,
  isEditing,
  onSavingChange,
}: {
  apiBaseUrl: string;
  /** 予算タブと共通の「編集」トグル。OFF の間は税率をラベルで見せる。 */
  isEditing: boolean;
  /** 自動保存の送信中かどうかを、タブ列の横の「保存中...」表示へ伝える */
  onSavingChange: (saving: boolean) => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [rates, setRates] = useState<TaxRates | null>(null);
  // 連続した操作が再レンダリングを待たずに最新値へ積み上がるよう、画面上の最新値を ref にも持つ。
  const ratesRef = useRef<TaxRates | null>(null);
  const [error, setError] = useState<string | null>(null);
  /** 商品別税率のオーバーレイで開いている税(null は閉じている) */
  const [openTax, setOpenTax] = useState<PerProductTaxKey | null>(null);

  const products = useMemo(() => {
    if (!masterData?.products) return [];
    return Object.entries(masterData.products)
      .map(([id, entry]) => ({ id: Number(id), ...entry }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.products]);
  const tradeableProducts = useMemo(() => products.filter((p) => p.trade), [products]);
  const retailProducts = useMemo(() => products.filter((p) => p.type === GOODS_RETAIL), [products]);
  const tradeableIds = useMemo(() => tradeableProducts.map((p) => p.id), [tradeableProducts]);

  const saveTaxes = useCallback(
    async (body: string) => {
      try {
        await fetchApi<TaxData>(buildApiUrl(apiBaseUrl, "/api/v1/city/taxes"), {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body,
        });
      } catch (err) {
        showNotification({ message: t("tax.saveFailed"), durationMs: 4000 });
        console.error("Failed to save tax rates", err);
        throw err;
      }
    },
    [apiBaseUrl, t],
  );
  const { isSaving, reset, update, commit } = useAutoSave(saveTaxes, Object.is);

  useEffect(() => {
    onSavingChange(isSaving);
  }, [isSaving, onSavingChange]);

  useEffect(() => {
    const controller = new AbortController();
    fetchApi<TaxData>(buildApiUrl(apiBaseUrl, "/api/v1/city/taxes"), { signal: controller.signal })
      .then((data) => {
        const initial = ratesFromData(data, retailProducts.map((p) => p.id));
        ratesRef.current = initial;
        setRates(initial);
        reset(taxPayload(initial, tradeableIds));
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      });
    return () => controller.abort();
  }, [apiBaseUrl, retailProducts, tradeableIds, reset]);

  // 「編集を終える」でデバウンス待ちを打ち切って確定させる
  useEffect(() => {
    if (!isEditing) commit();
  }, [isEditing, commit]);

  const applyRates = useCallback(
    (change: (prev: TaxRates) => TaxRates) => {
      const prev = ratesRef.current;
      if (!prev) return;
      const next = change(prev);
      ratesRef.current = next;
      setRates(next);
      update(taxPayload(next, tradeableIds));
    },
    [tradeableIds, update],
  );

  const productsOf = (key: PerProductTaxKey) =>
    key === "comsumption_tax" ? retailProducts : tradeableProducts;

  const setScalar = (key: TaxKey, pct: number) =>
    applyRates((prev) => ({ ...prev, scalar: { ...prev.scalar, [key]: pct } }));

  const setProductRate = (key: PerProductTaxKey, productId: number, pct: number) =>
    applyRates((prev) => ({
      ...prev,
      byProduct: { ...prev.byProduct, [key]: { ...prev.byProduct[key], [productId]: pct } },
    }));

  /** 一括→個別は一括税率を全商品へ写し、個別→一括は商品ごとの税率の平均を一括税率にする */
  const setIndividual = (key: PerProductTaxKey, individual: boolean) => {
    applyRates((prev) => {
      if (prev.individual[key] === individual) return prev;
      if (individual) {
        return {
          ...prev,
          individual: { ...prev.individual, [key]: true },
          byProduct: {
            ...prev.byProduct,
            [key]: Object.fromEntries(productsOf(key).map((p) => [p.id, prev.scalar[key]])),
          },
        };
      }
      const values = Object.values(prev.byProduct[key]);
      const average = values.length > 0 ? values.reduce((a, b) => a + b, 0) / values.length : prev.scalar[key];
      return {
        ...prev,
        individual: { ...prev.individual, [key]: false },
        scalar: { ...prev.scalar, [key]: average },
      };
    });
    if (individual) setOpenTax(key);
  };

  if (error) {
    return <div className="budget-window__pane-status budget-window__pane-status--error">{error}</div>;
  }
  if (!rates) {
    return <div className="budget-window__pane-status">{t("common.loading")}</div>;
  }

  const openLabelKey = TAX_CATEGORIES.find((c) => c.key === openTax)?.labelKey;

  return (
    <>
      <div className="budget-tier-group__cards">
        {TAX_CATEGORIES.map((cat) => {
          const perProductKey = cat.perProduct ? (cat.key as PerProductTaxKey) : null;
          const isIndividual = perProductKey !== null && rates.individual[perProductKey];
          return (
            <div
              key={cat.key}
              className={[
                "budget-category-card",
                !isIndividual && "budget-category-card--static",
              ]
                .filter(Boolean)
                .join(" ")}
              onClick={isIndividual ? () => setOpenTax(perProductKey) : undefined}
            >
              {/* 右上のワッペン: 税率。個別の税は商品ごとに違うので「個別」と出す。 */}
              <span className="budget-category-card__badge">
                {isIndividual ? t("tax.individualMode") : `${formatRate(rates.scalar[cat.key])}${t("tax.unit")}`}
              </span>
              <div className="budget-category-card__body">
                <span className="budget-category-card__name">{t(cat.labelKey)}</span>
                {isEditing && perProductKey !== null && (
                  <div className="tax-mode-toggle" onClick={(e) => e.stopPropagation()}>
                    <button
                      type="button"
                      className={["tax-mode-toggle__btn", !isIndividual && "tax-mode-toggle__btn--active"]
                        .filter(Boolean)
                        .join(" ")}
                      aria-pressed={!isIndividual}
                      onClick={() => setIndividual(perProductKey, false)}
                    >
                      {t("tax.bulkMode")}
                    </button>
                    <button
                      type="button"
                      className={["tax-mode-toggle__btn", isIndividual && "tax-mode-toggle__btn--active"]
                        .filter(Boolean)
                        .join(" ")}
                      aria-pressed={isIndividual}
                      onClick={() => setIndividual(perProductKey, true)}
                    >
                      {t("tax.individualMode")}
                    </button>
                  </div>
                )}
                {isEditing && (
                  <TaxSliderInput
                    value={rates.scalar[cat.key]}
                    onChange={(v) => setScalar(cat.key, v)}
                    onCommit={commit}
                    disabled={isIndividual}
                  />
                )}
              </div>
            </div>
          );
        })}
      </div>

      {openTax && openLabelKey && (
        <BudgetOverlay
          title={`${t(openLabelKey)} — ${t("tax.productTax")}`}
          onClose={() => setOpenTax(null)}
        >
          {productsOf(openTax).length === 0 ? (
            <div className="budget-overlay__placeholder">{t("tax.noProducts")}</div>
          ) : (
            <div className="tax-product-list">
              {productsOf(openTax).map((product) => {
                const rate = rates.byProduct[openTax][product.id] ?? rates.scalar[openTax];
                return (
                  <div key={product.id} className="tax-product-row">
                    <span className="tax-product-row__name" title={product.display_name}>
                      {product.display_name}
                    </span>
                    {isEditing ? (
                      <TaxSliderInput
                        value={rate}
                        onChange={(v) => setProductRate(openTax, product.id, v)}
                        onCommit={commit}
                      />
                    ) : (
                      <span className="tax-product-row__rate">
                        {formatRate(rate)}
                        {t("tax.unit")}
                      </span>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </BudgetOverlay>
      )}
    </>
  );
}
