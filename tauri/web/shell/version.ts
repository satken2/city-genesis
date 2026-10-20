/** 版情報と接続前の確認。`src-tauri/src/version.rs` の移植。 */
import versionJson from "../../../version.json";
import { DEVELOPMENT } from "./config";
import { sendUiAction } from "./ipc";
import { apiBase, fetchWithTimeout, sleep } from "./net";
import { graphicLoaded } from "./process";
import { state } from "./state";

export type VersionDefinition = { frontend_version: string; ipc_contract_version: number } & Record<string, unknown>;

export function definition(): VersionDefinition {
  return { ...(versionJson as VersionDefinition) };
}

let coreInfoValue: Record<string, unknown> | null = null;
let coreInfoRequest: Promise<Record<string, unknown>> | null = null;

/**
 * コアの版情報。コアは起動中に差し替わらないので、取得に成功した結果をずっと共有する。
 * 失敗は確定させず、次の呼び出しで取り直す。同時の呼び出しは1本の要求にまとめる。
 */
export async function coreInfo(): Promise<Record<string, unknown>> {
  if (coreInfoValue) return coreInfoValue;
  coreInfoRequest ??= (async () => {
    try {
      const response = await fetchWithTimeout(`${apiBase()}/api/v1/system/version`, 10000);
      if (!response.ok) throw `HTTP status ${response.status}`;
      coreInfoValue = (await response.json()) as Record<string, unknown>;
      return coreInfoValue;
    } finally {
      coreInfoRequest = null;
    }
  })();
  return coreInfoRequest;
}

export function coreInfoReady(): boolean {
  return coreInfoValue !== null;
}

/** フロントの MAJOR がコアの MAJOR 未満なら起動できない。コアの版を読めないときも互換と推測しない。 */
export function checkFrontendVersion(core: Record<string, unknown>): string | null {
  const frontend = String(definition().frontend_version ?? "");
  const coreVersion = typeof core.core_version === "string" ? core.core_version : "";
  const major = (version: string) => (/^\d+$/.test(version.split(".")[0]) ? Number(version.split(".")[0]) : null);
  const [frontMajor, coreMajor] = [major(frontend), major(coreVersion)];
  if (frontMajor !== null && coreMajor !== null && frontMajor >= coreMajor) return null;
  return `FRONTEND_VERSION_OUTDATED: core ${coreVersion} / UI ${frontend}`;
}

export async function verifyCore(): Promise<void> {
  if (!state.backendHealth.connected) throw "BACKEND_DISCONNECTED";
  if (state.graphicContractError !== null) throw state.graphicContractError;
  await coreInfo();
}

/**
 * 都市を開く前に、グラフィックに前の表示を片付けさせ、接続していなければ接続を待つ。
 * exe は Unity のプロセスが起動している前提で待つ。ブラウザ版はそれに当たる Unity の読み込みが終わってから待ち始める。
 */
export async function prepareGraphic(): Promise<void> {
  sendUiAction("", null, "session:reset", {});
  await graphicLoaded;
  for (let i = 0; i < 150; i++) {
    if (state.graphicContractError !== null) throw state.graphicContractError;
    if (state.graphicConnected) return;
    if (DEVELOPMENT) return;
    await sleep(100);
  }
  throw "IPC_CONTRACT_MISMATCH";
}

export async function validateLoad(id: string, scenario: boolean, accepted: boolean): Promise<void> {
  await verifyCore();
  const url = `${apiBase()}/api/v1/saves/${encodeURIComponent(id)}/validation?scenario=${scenario}`;
  const response = await fetchWithTimeout(url, 120000);
  const data = (await response.json()) as Record<string, unknown>;
  if (!response.ok) throw `${JSON.stringify(data.code ?? null)}: ${JSON.stringify(data.message ?? null)}`;
  const compatibility = data.compatibility as Record<string, unknown> | undefined;
  if (compatibility?.balance_changed === true && !accepted) throw "SAVE_BALANCE_CONFIRMATION_REQUIRED";
}

export function getFrontendStatus() {
  return { error: state.graphicContractError, graphic_version: state.graphicVersion };
}
