// 市民の声の文面(日本語原本)が、コアの返す話題と差し込み値に合っているかを確かめる。
// 話題ごとに必ず届く値だけで書いた文が、文を置いた各段階に1本以上あること(無いとその市民の文を作れない)と、
// 差し込み名が話題の値・身の回りの値のどれかであること(打ち間違いの検出)を見る。
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const voice = JSON.parse(readFileSync(new URL("../src/i18n/ja/citizenVoice.json", import.meta.url), "utf8")).citizenVoice;

const ALWAYS = ["name", "age", "city", "month", "members"];
const CONTEXT = ["zone", "employer", "employerKind", "school", "schoolKind", "home", "homeKind",
  "partner", "partnerAge", "partnerRelation", "child", "childAge", "childRelation"];
const WEATHER = ["temperature", "precipitation", "sunlight", "wind"];
const PLACE = ["place", "placeKind"];
// 話題: [必ず届く値, 届くことがある値]
const TOPICS = {
  "goods.any": [["product", "percent"], []],
  "home.housing": [["area", "perPerson"], []], "home.quiet": [[], []], "home.sunlight": [["percent"], []],
  "home.safety": [[], []], "home.evicted": [[], []],
  "life.health": [["percent"], []], "life.commute_car": [["minutes"], ["destination"]],
  "life.commute_transit": [["minutes"], ["destination"]], "life.work": [["workHours", "freeHours"], []],
  "life.unemployed": [[], []], "life.retired": [[], []], "life.school": [[], []], "life.car_owner": [[], []],
  "life.car_wanted": [[], []], "life.money": [[], ["savings", "months", "debt"]],
  "life.partner": [["partner", "partnerAge", "partnerRelation"], []], "life.child": [["child", "childAge", "childRelation"], []],
  "life.immigrant": [[], []], "life.shopping": [["shop", "product"], []],
  "news.fire": [["place"], []], "news.fire_home": [[], []], "news.crime": [["place"], []], "news.crime_home": [[], []],
  "news.accident": [[], ["road"]], "news.street_parking": [[], ["road"]],
  "news.opened": [PLACE, []], "news.price_up": [["product", "change"], []],
  "news.price_down": [["product", "change"], []],
  ...Object.fromEntries(["hot", "cold", "rainy", "windy", "sunny", "cloudy"].map(name => [`weather.${name}`, [WEATHER, []]])),
  ...Object.fromEntries(["births", "marriages", "deaths", "newcomers", "leavers", "tourists", "traffic_deaths", "fires", "crimes",
    "population"].map(name => [`city.${name}`, [["count"], []]])),
  "city.unemployment": [["percent"], []], "city.consumption_tax": [["percent"], []], "city.income_tax": [["percent"], []],
  "city.opened": [PLACE, []], "city.closed": [PLACE, []],
};

const placeholders = text => [...text.matchAll(/\{\{\s*(\w+)/g)].map(([, name]) => name);
const usableWith = (texts, names) => texts.filter(text => placeholders(text).every(name => names.has(name)));

function checkTexts(label, texts, required, optional) {
  const known = new Set([...ALWAYS, ...CONTEXT, ...required, ...optional]);
  for (const text of texts) {
    for (const name of placeholders(text)) assert.ok(known.has(name), `${label}: 未知の差し込み {{${name}}} 「${text}」`);
  }
  assert.ok(usableWith(texts, new Set([...ALWAYS, ...required])).length > 0, `${label}: 必ず届く値だけで書いた文がない`);
}

for (const [topic, [required, optional]] of Object.entries(TOPICS)) {
  const [group, name] = topic.split(".");
  const tiers = voice.lines[group]?.[name];
  assert.ok(tiers, `${topic}: 文がない`);
  for (const [tier, texts] of Object.entries(tiers)) {
    assert.ok(["0", "1", "2", "3", "4"].includes(tier), `${topic}: 段階 ${tier} は 0〜4 でない`);
    checkTexts(`${topic}.${tier}`, Object.values(texts), required, optional);
  }
}

// 品目ごとの文は goods.any と同じ値を使う。話題に挙げていない種類の文は、コアが返さない話題なので置かない。
for (const [group, topics] of Object.entries(voice.lines)) {
  for (const [name, tiers] of Object.entries(topics)) {
    if (group === "goods") {
      for (const [tier, texts] of Object.entries(tiers)) checkTexts(`goods.${name}.${tier}`, Object.values(texts), ["product", "percent"], []);
    } else {
      assert.ok(`${group}.${name}` in TOPICS, `${group}.${name}: コアが返さない話題`);
    }
  }
}

for (const part of ["openers", "closers"]) {
  for (const mood of ["bad", "neutral", "good"]) checkTexts(`${part}.${mood}`, Object.values(voice[part][mood]), [], []);
}

const lines = Object.values(voice.lines).flatMap(topics => Object.values(topics)).flatMap(tiers => Object.values(tiers))
  .flatMap(texts => Object.values(texts));
assert.equal(new Set(lines).size, lines.length, "同じ文が重複している");
console.log(`citizen voice lines: ${lines.length} lines OK`);
