/**
 * グラフィック(Unity)の起動とアプリの終了。exe の `src-tauri/src/process.rs` に当たる。
 * exe は Unity を子プロセスとして起動するが、ブラウザ版は同じページの canvas に Unity の Web ビルドを読み込む。
 */
import { GRAPHIC_BUILD_URL } from "./config";
import * as host from "./host";
import { sendUiAction } from "./ipc";
import { state } from "./state";

/** Unity の Web ビルドが書き出す読み込み用のファイル名(`unity/Assets/Editor/WebBuild.cs`)。 */
interface GraphicBuildManifest {
  loader: string;
  data: string;
  framework: string;
  code: string;
  companyName: string;
  productName: string;
  productVersion: string;
}

interface UnityInstance {
  Quit(): Promise<void>;
}

declare global {
  interface Window {
    createUnityInstance?: (canvas: HTMLCanvasElement, config: Record<string, unknown>) => Promise<UnityInstance>;
  }
}

let unityInstance: UnityInstance | null = null;
let resolveGraphicLoaded: () => void = () => {};

/** グラフィックの読み込みが終わった(失敗・ビルドが無い場合も含む)。exe で Unity のプロセスが起動し終えたことに当たる。 */
export const graphicLoaded = new Promise<void>((resolve) => {
  resolveGraphicLoaded = resolve;
});

/** Unity の代わりのグラフィック(開発用の地図ページの地図)が、読み込みの終わりを知らせる。 */
export function markGraphicLoaded(): void {
  resolveGraphicLoaded();
}

export async function startGraphic(canvas: HTMLCanvasElement): Promise<void> {
  try {
    const response = await fetch(`${GRAPHIC_BUILD_URL}build.json`, { cache: "no-store" });
    if (!response.ok) {
      console.warn(`[graphic] Unity の Web ビルドがありません(${GRAPHIC_BUILD_URL}build.json)。描画なしで続けます`);
      return;
    }
    const manifest = (await response.json()) as GraphicBuildManifest;
    await loadScript(GRAPHIC_BUILD_URL + manifest.loader);
    if (!window.createUnityInstance) throw new Error("createUnityInstance が見つかりません");
    unityInstance = await window.createUnityInstance(canvas, {
      arguments: ["--api-base-url", state.apiBaseUrl],
      dataUrl: GRAPHIC_BUILD_URL + manifest.data,
      frameworkUrl: GRAPHIC_BUILD_URL + manifest.framework,
      codeUrl: GRAPHIC_BUILD_URL + manifest.code,
      streamingAssetsUrl: `${GRAPHIC_BUILD_URL}StreamingAssets`,
      companyName: manifest.companyName,
      productName: manifest.productName,
      productVersion: manifest.productVersion,
      showBanner: (message: string, type: string) => (type === "error" ? console.error : console.warn)(`[graphic] ${message}`),
    });
  } catch (error) {
    console.error("[graphic] Unity を起動できません", error);
  } finally {
    resolveGraphicLoaded();
  }
}

function loadScript(src: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = src;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(`${src} を読み込めません`));
    document.body.appendChild(script);
  });
}

/**
 * アプリ全体の終了(`request_app_exit`)。発生源によらずここに合流する。
 * ブラウザはページを閉じられないので、画面を消して Unity を止めたところで終える。コアはページの外にあるので止めない。
 */
export function requestAppExit(): void {
  if (state.exiting) return;
  state.exiting = true;
  state.shutdown = true;
  host.hideAll();
  try {
    sendUiAction("", null, "system:shutdown", {});
  } catch {
    // Unity がいなくても終了は続ける。
  }
  const instance = unityInstance;
  unityInstance = null;
  // exe が Unity の終了を待ってから止めるのと同じく、system:shutdown を処理する猶予を置いてから止める。
  if (instance) setTimeout(() => void instance.Quit().catch(() => {}), 2000);
}
