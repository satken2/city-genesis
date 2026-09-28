/**
 * チュートリアルの一覧と表示情報
 *
 * ニューゲームの選択肢・開始時の説明ウィンドウ・チュートリアルウィンドウの進捗表示に使う。
 * 各ステップの案内・操作制限・完了判定は `tutorial/definitions/*.json`(進行基盤)が正で、
 * ここはそこからステップの並びと翻訳キーを導くだけにする。
 * ステップのタイトルは `tutorial.<シナリオID>.step<番号>.title`、本文は各段階の `textKey`。
 */

import { DEFINITIONS } from "../../tutorial/definitions";
import basicScenarioThumbnail from "../../assets/images/scenarios/basic.png";
import scenario2Thumbnail from "../../assets/images/scenarios/scenario2.png";
import scenario3Thumbnail from "../../assets/images/scenarios/scenario3.png";
import scenario4Thumbnail from "../../assets/images/scenarios/scenario4.png";
import tradeScenarioThumbnail from "../../assets/images/scenarios/trade.png";
import highwayScenarioThumbnail from "../../assets/images/scenarios/highway.png";
import miningScenarioThumbnail from "../../assets/images/scenarios/mining.png";
import tourismScenarioThumbnail from "../../assets/images/scenarios/tourism.png";
import roadStepImage from "../../assets/images/tutorial/step2.png";
import roadActiveStepImage from "../../assets/images/tutorial/step4.png";
import parcelStepImage from "../../assets/images/tutorial/step5.png";
import homeStepImage from "../../assets/images/tutorial/step6.png";
import homeActiveStepImage from "../../assets/images/tutorial/step7.png";

/** チュートリアルウィンドウに出す1ステップの表示情報 */
export interface TutorialStep {
  /** ステップ番号 (1-indexed。定義の legacyStep) */
  id: number;
  /** ステップ説明画像の URL。無いステップは空文字 */
  image: string;
  /** i18n 翻訳キー: ステップのタイトル */
  titleKey: string;
  /** i18n 翻訳キー: 最初の段階の案内文 */
  descriptionKey: string;
}

/** チュートリアルシナリオの表示情報 */
export interface TutorialScenario {
  id: string;
  titleKey: string;
  descriptionKey: string;
  /** 開始時の説明ウィンドウに表示する「このチュートリアルで何をするか」の翻訳キー */
  introKey: string;
  /** 開始する地形マップ。教材の座標はこの地形を前提にする */
  terrainMapId: string;
  /** シナリオ一覧や詳細パネル、説明ウィンドウで表示するサムネイル画像URL */
  image?: string;
  steps: TutorialStep[];
}

/** 一覧の並び順(学ぶ順) */
const SCENARIO_ORDER = ["basic", "scenario2", "scenario3", "scenario4", "trade", "highway", "mining", "tourism"];

const THUMBNAILS: Record<string, string> = {
  basic: basicScenarioThumbnail,
  scenario2: scenario2Thumbnail,
  scenario3: scenario3Thumbnail,
  scenario4: scenario4Thumbnail,
  trade: tradeScenarioThumbnail,
  highway: highwayScenarioThumbnail,
  mining: miningScenarioThumbnail,
  tourism: tourismScenarioThumbnail,
};

/** 基本編は操作するボタンと同じ絵柄の画像をステップに添える(定義のステップIDで引く) */
const STEP_IMAGES: Record<string, string> = {
  "basic.road": roadStepImage,
  "basic.road_active": roadActiveStepImage,
  "basic.parcels": parcelStepImage,
  "basic.home": homeStepImage,
  "basic.home_active": homeActiveStepImage,
};

/** 基本編の名前と説明はニューゲーム画面の既存の文言を使う */
const TITLE_KEYS: Record<string, { titleKey: string; descriptionKey: string }> = {
  basic: { titleKey: "newGame.tutorialName", descriptionKey: "newGame.tutorialDesc" },
};

/**
 * チュートリアルの全シナリオ定義
 */
export const TUTORIAL_SCENARIOS: TutorialScenario[] = SCENARIO_ORDER.map((id) => ({
  id,
  titleKey: TITLE_KEYS[id]?.titleKey ?? `tutorial.${id}.title`,
  descriptionKey: TITLE_KEYS[id]?.descriptionKey ?? `tutorial.${id}.description`,
  introKey: `tutorial.intro.${id}`,
  terrainMapId: DEFINITIONS[id].terrainMap,
  image: THUMBNAILS[id],
  steps: DEFINITIONS[id].steps.map((step) => ({
    id: step.legacyStep,
    image: STEP_IMAGES[step.id] ?? "",
    titleKey: `tutorial.${id}.step${step.legacyStep}.title`,
    descriptionKey: step.phases[0].textKey,
  })),
}));
