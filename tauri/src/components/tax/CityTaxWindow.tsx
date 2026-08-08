/**
 * 税率設定ウィンドウ
 *
 * GET/PATCH /api/v1/city/taxes と通信し、都市の税率を設定する。
 *
 * 左右分割レイアウト:
 * - 左パネル: 税カテゴリ一覧（所得税、法人税、消費税、輸入関税、輸出関税）。
 *   各カテゴリはカード形式で、スライダーと数値入力欄を持つ。
 *   消費税・輸入関税・輸出関税は「一括 / 個別」トグルを持ち、
 *   個別モードでは左パネルの一括スライダーが無効化される。
 * - 右パネル: 選択したカテゴリの商品ごとの税率一覧。
 *   個別モード時は各商品のスライダーが編集可能。
 *
 * 消費税は小売財（GOODS_RETAIL）にのみ課税されるため、消費税の個別設定では
 * 小売財カテゴリーの商品のみを表示・編集対象とする。
 * 輸入関税・輸出関税は貿易対象商品（trade フラグが true）のみが対象。
 *
 * 注意: バックエンドの消費税(comsumption_tax)はスカラー値のみ対応のため、
 * 個別モードで設定した商品ごとの消費税率はUI上の表示のみ。
 * 保存時は一括税率が送信される。
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { GOODS_RETAIL } from "../../constants";
import {
  FiscalSankey,
  buildFiscalSankeyData,
  buildBusinessRevenueItems,
  buildLandAcquisitionSpendItem,
  TAX_REVENUE_META,
  spendCategoryColor,
  getActualSpendingValue,
  type FiscalFlowItem,
} from "../common/FiscalSankey";
import { TaxRevenueChart } from "./TaxRevenueChart";
import "./CityTaxWindow.css";

/**
 * GET /api/v1/city/taxes のレスポンス型
 * 税率は比率（0.05 = 5%）で格納されている
 */
type TaxData = {
  comsumption_tax: number;
  business_tax: number;
  income_tax: number;
  import_tax: Record<string, number>;
  export_tax: Record<string, number>;
};

/**
 * GET /api/v1/city/budgets のうち財政Sankeyで参照する部分。
 * 税率設定の参考として、前エポックの税収・支出実績を可視化する。
 */
type FiscalBudgetData = {
  total_expense: Record<string, number>;
  total_revenue: Record<string, number>;
  /** 過去エポックの税収実績時系列(昇順)。税収推移グラフで参照する。台帳キーは"31"〜"35"。 */
  history: { epoch: number; revenues: Record<string, number> }[];
};

/** 個別設定トグルを持つ税カテゴリのキー */
type PerProductTaxKey = "comsumption_tax" | "import_tax" | "export_tax";

/** 税カテゴリの定義 */
type TaxCategoryDef = {
  key: PerProductTaxKey | "income_tax" | "business_tax";
  labelKey: string;
  hasIndividual: boolean;
};

/**
 * 税カテゴリ一覧
 * 表示順序は左パネルの上から順
 */
const TAX_CATEGORIES: TaxCategoryDef[] = [
  { key: "income_tax", labelKey: "tax.incomeTax", hasIndividual: false },
  { key: "business_tax", labelKey: "tax.businessTax", hasIndividual: false },
  { key: "comsumption_tax", labelKey: "tax.consumptionTax", hasIndividual: true },
  { key: "import_tax", labelKey: "tax.importTax", hasIndividual: true },
  { key: "export_tax", labelKey: "tax.exportTax", hasIndividual: true },
];

/** 比率をパーセント表示に変換（0.05 → 5） */
function ratioToPercent(ratio: number): number {
  return ratio * 100;
}

/** パーセント表示を比率に変換（5 → 0.05） */
function percentToRatio(percent: number): number {
  return percent / 100;
}

/**
 * 税率スライダーと数値入力の共通コンポーネント
 *
 * スライダーは 0〜100%、数値入力は 0〜1000% まで入力可能。
 * step は 0.1% 単位。
 */
function TaxSliderInput({
  value,
  onChange,
  disabled,
}: {
  value: number;
  onChange: (v: number) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation();

  const handleInputChange = (raw: string) => {
    const v = parseFloat(raw);
    if (!isNaN(v)) {
      onChange(Math.max(0, Math.min(v, 1000)));
    }
  };

  return (
    <div className={`tax-slider-input ${disabled ? "tax-slider-input--disabled" : ""}`}>
      <input
        type="range"
        min={0}
        max={100}
        step={0.1}
        value={value}
        onChange={(e) => onChange(parseFloat(e.target.value))}
        disabled={disabled}
        className="tax-slider"
      />
      <div className="tax-input-group">
        <input
          type="number"
          min={0}
          max={1000}
          step={0.1}
          value={parseFloat(value.toFixed(1))}
          onChange={(e) => handleInputChange(e.target.value)}
          disabled={disabled}
          className="tax-input"
        />
        <span className="tax-unit">{t("tax.unit")}</span>
      </div>
    </div>
  );
}

export function CityTaxWindow({
  apiBaseUrl,
  onClose,
}: {
  apiBaseUrl: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  /* ---- APIデータ ---- */
  const [taxData, setTaxData] = useState<TaxData | null>(null);

  /* ---- 財政Sankey用データ(前エポック実績: 税収・支出) ---- */
  const [fiscalData, setFiscalData] = useState<FiscalBudgetData | null>(null);

  /* ---- スカラー税率（パーセント） ---- */
  const [incomeTax, setIncomeTax] = useState(0);
  const [businessTax, setBusinessTax] = useState(0);

  /* ---- 消費税率（パーセント）。バックエンドはスカラーのみ ---- */
  const [consumptionTax, setConsumptionTax] = useState(0);
  const [consumptionTaxByProduct, setConsumptionTaxByProduct] = useState<Record<number, number>>({});

  /* ---- 輸入関税（パーセント） ---- */
  const [importTaxBulk, setImportTaxBulk] = useState(0);
  const [importTaxByProduct, setImportTaxByProduct] = useState<Record<number, number>>({});

  /* ---- 輸出関税（パーセント） ---- */
  const [exportTaxBulk, setExportTaxBulk] = useState(0);
  const [exportTaxByProduct, setExportTaxByProduct] = useState<Record<number, number>>({});

  /* ---- 個別設定モードのトグル ---- */
  const [individualMode, setIndividualMode] = useState<Record<PerProductTaxKey, boolean>>({
    comsumption_tax: false,
    import_tax: false,
    export_tax: false,
  });

  /* ---- 右パネルに表示するカテゴリ ---- */
  const [selectedCategory, setSelectedCategory] = useState<PerProductTaxKey | null>(null);

  /* ---- UI状態 ---- */
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  /**
   * 商品一覧（ID順）
   * useMasterData で起動時に取得済みのマスターデータを参照
   */
  const products = useMemo(() => {
    if (!masterData?.products) return [];
    return Object.entries(masterData.products)
      .map(([id, entry]) => ({ id: Number(id), ...entry }))
      .sort((a, b) => a.id - b.id);
  }, [masterData?.products]);

  /**
   * 輸入・輸出関税の対象商品一覧
   * trade フラグが true の商品のみが貿易対象となる
   */
  const tradeableProducts = useMemo(
    () => products.filter((p) => p.trade),
    [products],
  );

  /**
   * 消費税の対象商品一覧
   * 消費税は小売財（GOODS_RETAIL）にのみ課税されるため、
   * 小売財カテゴリーの商品のみを個別設定の対象とする
   */
  const retailProducts = useMemo(
    () => products.filter((p) => p.type === GOODS_RETAIL),
    [products],
  );

  /* ==== 初期データ読み込み ==== */

  useEffect(() => {
    const controller = new AbortController();

    async function loadTaxes() {
      try {
        const data = await fetchApi<TaxData>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/taxes"),
          { signal: controller.signal },
        );

        setTaxData(data);
        setIncomeTax(ratioToPercent(data.income_tax));
        setBusinessTax(ratioToPercent(data.business_tax));
        setConsumptionTax(ratioToPercent(data.comsumption_tax));

        /* 消費税: 小売財のみにスカラー値を適用して商品別マップを初期化 */
        const consumptionMap = Object.fromEntries(
          retailProducts.map((p) => [p.id, ratioToPercent(data.comsumption_tax)]),
        );
        setConsumptionTaxByProduct(consumptionMap);

        /* 輸入関税: 全商品が同じ税率なら一括モード、異なれば個別モードで開始 */
        const importMap: Record<number, number> = {};
        for (const [k, v] of Object.entries(data.import_tax)) {
          importMap[Number(k)] = ratioToPercent(v);
        }
        setImportTaxByProduct(importMap);

        const importVals = Object.values(importMap);
        const importAllSame =
          importVals.length > 0 && importVals.every((v) => Math.abs(v - importVals[0]) < 0.01);
        setImportTaxBulk(importAllSame && importVals.length > 0 ? importVals[0] : 0);
        setIndividualMode((prev) => ({ ...prev, import_tax: !importAllSame }));

        /* 輸出関税: 同上 */
        const exportMap: Record<number, number> = {};
        for (const [k, v] of Object.entries(data.export_tax)) {
          exportMap[Number(k)] = ratioToPercent(v);
        }
        setExportTaxByProduct(exportMap);

        const exportVals = Object.values(exportMap);
        const exportAllSame =
          exportVals.length > 0 && exportVals.every((v) => Math.abs(v - exportVals[0]) < 0.01);
        setExportTaxBulk(exportAllSame && exportVals.length > 0 ? exportVals[0] : 0);
        setIndividualMode((prev) => ({ ...prev, export_tax: !exportAllSame }));
      } catch (err) {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : "Unknown error");
        }
      }
    }

    loadTaxes();
    return () => controller.abort();
  }, [apiBaseUrl, products, retailProducts]);

  /* ==== 財政Sankey用データ(前エポック実績)の読み込み ==== */
  useEffect(() => {
    const controller = new AbortController();

    async function loadFiscal() {
      try {
        const budgetsRes = await fetchApi<FiscalBudgetData>(
          buildApiUrl(apiBaseUrl, "/api/v1/city/budgets"),
          { signal: controller.signal },
        );
        setFiscalData(budgetsRes);
      } catch (err) {
        // Sankeyは補助表示のため、失敗しても税率設定本体はエラーにしない。
        if (!controller.signal.aborted) {
          // eslint-disable-next-line no-console
          console.warn("failed to load fiscal data for tax sankey", err);
        }
      }
    }

    loadFiscal();
    return () => controller.abort();
  }, [apiBaseUrl]);

  /**
   * 財政Sankey用データ(参照表示: 前エポック歳入 → 資金 → 前エポック支出実績)。
   * 歳入は税(31〜35)に加え、公営事業収入(コストカテゴリキー1〜7に計上される公共サービス
   * 売上・公営住宅家賃)も左側に描く。
   * 支出は予算カテゴリ実績に加え、収用補償(予算枠外の税外支出。前エポック実績)も右側に描く。
   * TODO: 将来的には税率スライダーの変更に応じて税収を近似予測して左側へ反映する。
   */
  const sankeyChart = useMemo(() => {
    if (!fiscalData) return null;
    const revenues: FiscalFlowItem[] = [
      ...TAX_REVENUE_META.map((m) => ({
        label: t(m.labelKey),
        value: fiscalData.total_revenue[m.key] ?? 0,
        color: m.color,
      })),
      ...buildBusinessRevenueItems(
        fiscalData.total_revenue,
        masterData?.budget_categories,
        t,
      ),
    ];
    const budgetCategories = masterData?.budget_categories ?? {};
    const spends: FiscalFlowItem[] = [
      ...Object.entries(budgetCategories)
        .map(([id, entry]) => ({ id: Number(id), entry }))
        .sort((a, b) => a.id - b.id)
        .map(({ id, entry }) => ({
          label: t(`budget.category.${id}`, {
            defaultValue: (entry as { display_name: string }).display_name,
          }),
          value: getActualSpendingValue(String(id), fiscalData.total_expense),
          color: spendCategoryColor(id),
        })),
      // 収用補償(市が民間から土地を収用した土地代)。予算カテゴリ外の税外支出だが、
      // 税率ウィンドウは前エポックの実支出を描くため右側に加える。
      buildLandAcquisitionSpendItem(fiscalData.total_expense, t),
    ];
    return buildFiscalSankeyData(
      revenues,
      { label: t("fiscalSankey.pool", { defaultValue: "資金" }) },
      spends,
      {
        surplus: t("fiscalSankey.surplus", { defaultValue: "繰越" }),
        deficit: t("fiscalSankey.deficit", { defaultValue: "貯蓄取り崩し(赤字)" }),
      },
    );
  }, [fiscalData, masterData, t]);

  /* ==== トグル切替 ==== */

  const toggleIndividualMode = useCallback(
    (key: PerProductTaxKey) => {
      setIndividualMode((prev) => {
        const next = !prev[key];

        /* 一括 → 個別に切り替えた時、現在の一括税率を全商品にコピーする */
        if (next) {
          if (key === "import_tax") {
            setImportTaxByProduct(
              Object.fromEntries(tradeableProducts.map((p) => [p.id, importTaxBulk])),
            );
          } else if (key === "export_tax") {
            setExportTaxByProduct(
              Object.fromEntries(tradeableProducts.map((p) => [p.id, exportTaxBulk])),
            );
          } else if (key === "comsumption_tax") {
            setConsumptionTaxByProduct(
              Object.fromEntries(retailProducts.map((p) => [p.id, consumptionTax])),
            );
          }
        } else {
          /* 個別 → 一括に切り替えた時、全商品の平均値を一括税率に設定する */
          if (key === "import_tax") {
            const vals = Object.values(importTaxByProduct);
            if (vals.length > 0) {
              setImportTaxBulk(vals.reduce((a, b) => a + b, 0) / vals.length);
            }
          } else if (key === "export_tax") {
            const vals = Object.values(exportTaxByProduct);
            if (vals.length > 0) {
              setExportTaxBulk(vals.reduce((a, b) => a + b, 0) / vals.length);
            }
          } else if (key === "comsumption_tax") {
            const vals = Object.values(consumptionTaxByProduct);
            if (vals.length > 0) {
              setConsumptionTax(vals.reduce((a, b) => a + b, 0) / vals.length);
            }
          }
        }

        return { ...prev, [key]: next };
      });
    },
    [importTaxBulk, exportTaxBulk, consumptionTax, importTaxByProduct, exportTaxByProduct, consumptionTaxByProduct, tradeableProducts, retailProducts],
  );

  /* ==== 商品別税率の更新 ==== */

  const updateProductRate = useCallback(
    (key: PerProductTaxKey, productId: number, rate: number) => {
      if (key === "import_tax") {
        setImportTaxByProduct((prev) => ({ ...prev, [productId]: rate }));
      } else if (key === "export_tax") {
        setExportTaxByProduct((prev) => ({ ...prev, [productId]: rate }));
      } else if (key === "comsumption_tax") {
        setConsumptionTaxByProduct((prev) => ({ ...prev, [productId]: rate }));
      }
    },
    [],
  );

  /* ==== 保存 ==== */

  const handleSave = useCallback(async () => {
    setIsSaving(true);
    setError(null);

    try {
      const payload: Record<string, unknown> = {
        income_tax: percentToRatio(incomeTax),
        business_tax: percentToRatio(businessTax),
        comsumption_tax: percentToRatio(consumptionTax),
      };

      /* 輸入関税: 個別モードなら各商品の税率を、一括モードなら全商品に一括税率を適用 */
      if (individualMode.import_tax) {
        payload.import_tax = Object.fromEntries(
          Object.entries(importTaxByProduct).map(([k, v]) => [k, percentToRatio(v)]),
        );
      } else {
        payload.import_tax = Object.fromEntries(
          tradeableProducts.map((p) => [String(p.id), percentToRatio(importTaxBulk)]),
        );
      }

      /* 輸出関税: 同上 */
      if (individualMode.export_tax) {
        payload.export_tax = Object.fromEntries(
          Object.entries(exportTaxByProduct).map(([k, v]) => [k, percentToRatio(v)]),
        );
      } else {
        payload.export_tax = Object.fromEntries(
          tradeableProducts.map((p) => [String(p.id), percentToRatio(exportTaxBulk)]),
        );
      }

      const res = await fetch(buildApiUrl(apiBaseUrl, "/api/v1/city/taxes"), {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });

      if (!res.ok) {
        throw new Error(`HTTP ${res.status}`);
      }

      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save");
    } finally {
      setIsSaving(false);
    }
  }, [
    apiBaseUrl,
    incomeTax,
    businessTax,
    consumptionTax,
    importTaxBulk,
    exportTaxBulk,
    importTaxByProduct,
    exportTaxByProduct,
    individualMode,
    tradeableProducts,
    onClose,
  ]);

  /* ==== 右パネル用の商品一覧 ==== */

  const rightPanelProducts = useMemo(() => {
    if (!selectedCategory) return [];
    /* 輸入・輸出関税は貿易対象商品のみ、消費税は小売財のみ */
    if (selectedCategory === "import_tax" || selectedCategory === "export_tax") {
      return tradeableProducts;
    }
    return retailProducts;
  }, [selectedCategory, retailProducts, tradeableProducts]);

  /**
   * 指定カテゴリ・商品の現在税率を取得する
   * 個別モードなら商品ごとの値、一括モードなら一括値を返す
   */
  const getProductRate = useCallback(
    (key: PerProductTaxKey, productId: number): number => {
      if (key === "import_tax") {
        return individualMode.import_tax
          ? (importTaxByProduct[productId] ?? importTaxBulk)
          : importTaxBulk;
      }
      if (key === "export_tax") {
        return individualMode.export_tax
          ? (exportTaxByProduct[productId] ?? exportTaxBulk)
          : exportTaxBulk;
      }
      /* comsumption_tax: 個別モードなら商品ごとの値、一括ならスカラー値 */
      if (key === "comsumption_tax") {
        return individualMode.comsumption_tax
          ? (consumptionTaxByProduct[productId] ?? consumptionTax)
          : consumptionTax;
      }
      return consumptionTax;
    },
    [
      importTaxByProduct,
      exportTaxByProduct,
      consumptionTaxByProduct,
      importTaxBulk,
      exportTaxBulk,
      consumptionTax,
      individualMode,
    ],
  );

  /* ==== 現在の税率値を取得するヘルパー ==== */

  const getScalarValue = useCallback(
    (key: TaxCategoryDef["key"]): number => {
      switch (key) {
        case "income_tax":
          return incomeTax;
        case "business_tax":
          return businessTax;
        case "comsumption_tax":
          return consumptionTax;
        case "import_tax":
          return importTaxBulk;
        case "export_tax":
          return exportTaxBulk;
        default:
          return 0;
      }
    },
    [incomeTax, businessTax, consumptionTax, importTaxBulk, exportTaxBulk],
  );

  const setScalarValue = useCallback(
    (key: TaxCategoryDef["key"], v: number) => {
      switch (key) {
        case "income_tax":
          setIncomeTax(v);
          break;
        case "business_tax":
          setBusinessTax(v);
          break;
        case "comsumption_tax":
          setConsumptionTax(v);
          break;
        case "import_tax":
          setImportTaxBulk(v);
          break;
        case "export_tax":
          setExportTaxBulk(v);
          break;
      }
    },
    [],
  );

  /* ==== レンダリング ==== */

  if (error && !taxData) {
    return <div className="tax-window__error">{error}</div>;
  }

  if (!taxData) {
    return <div className="tax-window__loading">{t("common.loading")}</div>;
  }

  return (
    <div className="tax-window">
      <div className="tax-window__body">
        {/* 左上: 税カテゴリ一覧 */}
        <div className="tax-window__categories-section">
          {TAX_CATEGORIES.map((cat) => {
            const ppKey = cat.key as PerProductTaxKey;
            const isPerProduct = cat.hasIndividual;
            const isIndividual = isPerProduct && individualMode[ppKey];
            const isSelected = selectedCategory === ppKey;
            const currentValue = getScalarValue(cat.key);

            return (
              <div
                key={cat.key}
                className={[
                  "tax-card",
                  isPerProduct && "tax-card--clickable",
                  isSelected && "tax-card--selected",
                ]
                  .filter(Boolean)
                  .join(" ")}
                onClick={() => {
                  if (isPerProduct) setSelectedCategory(ppKey);
                }}
              >
                <div className="tax-card__header">
                  <span className="tax-card__label">{t(cat.labelKey)}</span>
                  {isPerProduct && (
                    <div className="tax-card__toggle">
                      <button
                        className={[
                          "tax-toggle-btn",
                          !isIndividual && "tax-toggle-btn--active",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        onClick={(e) => {
                          e.stopPropagation();
                          setSelectedCategory(ppKey);
                          if (isIndividual) toggleIndividualMode(ppKey);
                        }}
                        type="button"
                      >
                        {t("tax.bulkMode")}
                      </button>
                      <button
                        className={[
                          "tax-toggle-btn",
                          isIndividual && "tax-toggle-btn--active",
                        ]
                          .filter(Boolean)
                          .join(" ")}
                        onClick={(e) => {
                          e.stopPropagation();
                          setSelectedCategory(ppKey);
                          if (!isIndividual) toggleIndividualMode(ppKey);
                        }}
                        type="button"
                      >
                        {t("tax.individualMode")}
                      </button>
                    </div>
                  )}
                </div>
                <TaxSliderInput
                  value={currentValue}
                  onChange={(v) => setScalarValue(cat.key, v)}
                  disabled={isPerProduct && isIndividual}
                />
              </div>
            );
          })}
        </div>

        {/* 右上: カテゴリ別税収の推移グラフ */}
        <div className="tax-window__chart-section">
          <h4 className="tax-window__chart-title">
            {t("tax.revenueHistoryTitle", { defaultValue: "税収推移(カテゴリ別)" })}
          </h4>
          <TaxRevenueChart data={fiscalData?.history ?? []} />
        </div>

        {/* 左下: 商品別個別税率 */}
        <div className="tax-window__product-section">
          {!selectedCategory ? (
            <div className="tax-window__placeholder">{t("tax.selectCategory")}</div>
          ) : (
            <div className="tax-product-list">
              <h4 className="tax-product-list__title">
                {t(TAX_CATEGORIES.find((c) => c.key === selectedCategory)!.labelKey)}
                {" — "}
                {t("tax.productTax")}
              </h4>
              {!individualMode[selectedCategory] ? (
                <div className="tax-window__placeholder">{t("tax.individualModeHint")}</div>
              ) : rightPanelProducts.length === 0 ? (
                <div className="tax-window__placeholder">{t("tax.noProducts")}</div>
              ) : (
                rightPanelProducts.map((product) => (
                  <div key={product.id} className="tax-product-row">
                    <span className="tax-product-row__name" title={product.display_name}>
                      {product.display_name}
                    </span>
                    <TaxSliderInput
                      value={getProductRate(selectedCategory, product.id)}
                      onChange={(v) => updateProductRate(selectedCategory, product.id, v)}
                    />
                  </div>
                ))
              )}
            </div>
          )}
        </div>

        {/* 右下: 前エポック実績の収支フロー。税率設定の参考として表示する。 */}
        <div className="tax-window__sankey-section">
          <h4 className="tax-window__sankey-title">
            {t("fiscalSankey.taxTitle", { defaultValue: "収支フロー(前エポック実績)" })}
          </h4>
          <FiscalSankey
            chart={sankeyChart}
            linkStyle="solid"
            noDataLabel={t("fiscalSankey.noData", { defaultValue: "表示できる収支データがありません" })}
          />
        </div>
      </div>

      {/* エラー表示 */}
      {error && <div className="tax-window__error-inline">{error}</div>}

      {/* 保存ボタン */}
      <div className="tax-window__footer">
        <button
          className="tax-window__save-btn"
          onClick={handleSave}
          disabled={isSaving}
          type="button"
        >
          {isSaving ? t("tax.saving") : t("tax.save")}
        </button>
      </div>
    </div>
  );
}
