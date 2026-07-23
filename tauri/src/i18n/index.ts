/**
 * i18next の初期化設定
 *
 * react-i18next を使用して多言語対応を行う。
 * 翻訳リソースはドメインごとに ja/*.json・en/*.json へ分割して定義し、
 * デフォルト言語は日本語とする。
 *
 * 言語設定は Rust 側の設定ファイル（settings.json）に永続化され、
 * 各ウィンドウのマウント時に loadAppSettings から復元される。
 * そのため i18n の初期化時にはデフォルト言語を使用し、
 * 非同期で読み込んだ設定値を後から適用する。
 */

import i18n from "i18next";
import { initReactI18next } from "react-i18next";

/**
 * 翻訳リソースはドメインごとに ja/*.json・en/*.json へ分割して管理する。
 * 各ファイルはトップレベル名前空間(agent, budget など)をそのまま含み、
 * ここで全ファイルを1つの translation 名前空間へ集約する。
 * これにより i18next へ渡す形は従来と同一で、コンポーネント側の
 * t("agent.foo") のようなキー参照は変更不要。
 */
const jaModules = import.meta.glob("./ja/*.json", { eager: true });
const enModules = import.meta.glob("./en/*.json", { eager: true });

function assembleResources(
  modules: Record<string, unknown>,
): Record<string, unknown> {
  const merged: Record<string, unknown> = {};
  for (const mod of Object.values(modules)) {
    Object.assign(merged, (mod as { default: Record<string, unknown> }).default);
  }
  return merged;
}

const ja = assembleResources(jaModules);
const en = assembleResources(enModules);

i18n.use(initReactI18next).init({
  resources: {
    ja: { translation: ja },
    en: { translation: en },
  },
  lng: "ja",
  fallbackLng: "ja",
  interpolation: {
    escapeValue: false,
  },
});

export default i18n;
