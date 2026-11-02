import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createInstance } from "i18next";
import { I18nextProvider } from "react-i18next";
import { PRODUCT_HIGH_WATER, PRODUCT_WATER_SUPPLY, RESOURCE_OIL, RESOURCE_ORE, RESOURCE_NONMETALLIC, RESOURCE_WATER } from "../src/constants";
import { presetMaxAmount, resourcePresetsFor, waterPopulationEstimate } from "../src/data/resourcePresets";
import { ResourcePresetHint } from "../src/components/resource/ResourcePresetHint";
import { SUPPORTED_LANGUAGES } from "../src/utils/locale";
import type { MasterData } from "../src/types";

const masterData = {
  products: { [PRODUCT_WATER_SUPPLY]: { demand_max: 150 } },
  agents: {
    1: { supply: PRODUCT_HIGH_WATER, materials: { [RESOURCE_WATER]: 1 } },
    2: { supply: PRODUCT_WATER_SUPPLY, materials: { [PRODUCT_HIGH_WATER]: 0.001 } },
    3: { supply: PRODUCT_HIGH_WATER, materials: {} },
  },
} as unknown as MasterData;

const river = resourcePresetsFor(RESOURCE_WATER)[0];
const riverAmount = presetMaxAmount(river, 0)!;
assert.ok(Math.abs(waterPopulationEstimate(riverAmount, river.recoveryRate, masterData)!.population - 666666.6667) < 0.001,
  "河川の10万m³/日をKL→Lの投入係数と150L/人/日で換算する");
assert.equal(waterPopulationEstimate(riverAmount, 0, masterData)!.population, 0, "回復しない貯水を持続供給に数えない");
assert.equal(waterPopulationEstimate(riverAmount, 1, masterData)!.population,
  waterPopulationEstimate(riverAmount, river.recoveryRate, masterData)!.population, "高速回復でも1エポックに埋蔵上限までしか採れない");

const groundwater = resourcePresetsFor(RESOURCE_WATER).find(preset => preset.key === "groundwater")!;
assert.ok(Math.abs(waterPopulationEstimate(presetMaxAmount(groundwater, 1e6)!, groundwater.recoveryRate, masterData)!.population - 6666.6667) < 0.001,
  "1km²の地下水は涵養量1000m³/日で換算する");

const modifiedMaster = structuredClone(masterData);
modifiedMaster.products[PRODUCT_WATER_SUPPLY].demand_max = 100;
assert.equal(waterPopulationEstimate(riverAmount, river.recoveryRate, modifiedMaster)!.population, 1e6,
  "シナリオのマスターで生活用水需要を変えた場合も反映する");
modifiedMaster.agents[1].materials![RESOURCE_WATER] = 2;
assert.equal(waterPopulationEstimate(riverAmount, river.recoveryRate, modifiedMaster)!.population, 500000,
  "取水の投入係数を固定の1対1と仮定しない");
modifiedMaster.agents[4] = { ...modifiedMaster.agents[1], materials: { [RESOURCE_WATER]: 1 } };
assert.equal(waterPopulationEstimate(riverAmount, river.recoveryRate, modifiedMaster)!.population, 500000,
  "複数の取水方式では原水を多く使う方を基準にする");
modifiedMaster.products[PRODUCT_WATER_SUPPLY].demand_max = 0;
assert.equal(waterPopulationEstimate(riverAmount, river.recoveryRate, modifiedMaster), null);
assert.equal(waterPopulationEstimate(riverAmount, river.recoveryRate, { ...masterData, agents: {} }), null,
  "海水淡水化のような原水を使わない施設だけでは水源の人数を見積もらない");
assert.equal(waterPopulationEstimate(Infinity, 1, masterData), null);

const i18n = createInstance();
const resources = Object.fromEntries(SUPPORTED_LANGUAGES.map(language => [language, {
  translation: JSON.parse(readFileSync(`src/i18n/${language}/construction.json`, "utf8")),
}]));
await i18n.init({ resources, lng: "ja", fallbackLng: "ja", interpolation: { escapeValue: false } });

function hint(productId: number, areaM2: number, maxAmount: number, recoveryRate: number, awaitingShape: boolean) {
  return renderToStaticMarkup(createElement(I18nextProvider, { i18n }, createElement(ResourcePresetHint, {
    preset: resourcePresetsFor(productId)[0], productId, masterData, areaM2, maxAmount, recoveryRate, awaitingShape,
    className: "pc-hint",
  })));
}

const oilBeforeShape = hint(RESOURCE_OIL, 0, 1000, 0, true);
assert.match(oilBeforeShape, /1 km²あたり約500万 バレル/, "形状がなくても油田の面積あたりの量を示す");
assert.doesNotMatch(oilBeforeShape, /埋蔵量 1000/, "未描画時の仮入力を実際の埋蔵量として表示しない");
for (const productId of [RESOURCE_ORE, RESOURCE_NONMETALLIC]) {
  assert.match(hint(productId, 0, 1000, 0, true), /1 km²あたり約2億 t/, "鉱床の大きな数量はトンで示す");
  assert.match(hint(productId, 100, 1500, 0, false), /埋蔵量 1.5 t/, "手入力した埋蔵量にも単位を付ける");
}
const riverHint = hint(RESOURCE_WATER, 0, riverAmount, river.recoveryRate, false);
assert.match(riverHint, /持続採取量 約10万 m³\/日/);
assert.match(riverHint, /約66.7万人分/);
assert.match(riverHint, /工業用水や農業用水を含みません/);
assert.doesNotMatch(hint(RESOURCE_WATER, 0, 1000, 0.01, true), /賄える生活用水/, "形状待ちの仮入力から人口を計算しない");
assert.match(hint(RESOURCE_WATER, 1000, riverAmount / 2, river.recoveryRate, false), /約33.3万人分/,
  "手入力した埋蔵量の変更を人口目安に反映する");
assert.match(hint(RESOURCE_WATER, 1000, riverAmount, 0, false), /約0人分/);

for (const language of SUPPORTED_LANGUAGES) {
  await i18n.changeLanguage(language);
  for (const productId of [RESOURCE_OIL, RESOURCE_ORE, RESOURCE_NONMETALLIC, RESOURCE_WATER]) {
    const html = hint(productId, 1e6, 3e6, 0.01, false);
    assert.doesNotMatch(html, /resourcePreset\.|\{\{|NaN|Infinity/, `${language}: 数量・人口の翻訳を解決する`);
  }
}
console.log("PASS: resource quantity guides, units, and sustainable domestic-water population estimates");
