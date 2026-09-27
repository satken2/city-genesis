import { DEFINITIONS } from "../../tutorial/definitions";
/**
 * チュートリアルの各ステップ定義とバリデーションロジック
 *
 * 各ステップは「ユーザーに操作を促す説明」と「完了ボタン押下時の検証関数」で構成される。
 * 検証関数はバックエンド API を叩いて条件を満たしているか確認し、
 * 条件を満たしていれば true を返す。
 * ステップの追加・変更があった場合はこのファイルを編集する。
 */

import { fetchLessonApi as fetchApi } from "../../tutorial/legacyApi";
import { sendSessionReload } from "../../utils/ipc";
import {
  PATH_STREET,
  PATH_TRACK,
  PATH_STATION_HOUSE,
  TRACK_STATION,
  STATUS_ACTIVE,
  STATUS_SCHEDULED_CONSTRUCTION,
  STATUS_CONSTRUCTING,
  BUILDING_MINE,
  VARIANT_SOLAR_PLANT,
  VARIANT_SUBSTATION,
  PATH_VARIANT_GRID,
  PRODUCT_HIGH_ELECTRICITY,
  PRODUCT_METRO_VEHICLE,
} from "../../constants";
import { isInfraPathType } from "../../local-constants";
import type {
  PathListResponse,
  ParcelListResponse,
  AgentListResponse,
  ZoneResponse,
} from "../../types/generated";
import {
  TUTORIAL2_TOPRIGHT_PATH_KEY,
  TUTORIAL2_MAIN_ZONE_KEY,
  TUTORIAL_FLAG_RESOURCE_VISIBLE,
  TUTORIAL_FLAG_ZONE_PANEL_OPEN,
  TUTORIAL_FLAG_PARCEL_PANEL_OPEN,
} from "./tutorialSetup";
import basicScenarioThumbnail from "../../assets/images/scenarios/basic.png";
import scenario2Thumbnail from "../../assets/images/scenarios/scenario2.png";
import scenario3Thumbnail from "../../assets/images/scenarios/scenario3.png";
import scenario4Thumbnail from "../../assets/images/scenarios/scenario4.png";
import tradeScenarioThumbnail from "../../assets/images/scenarios/trade.png";
import highwayScenarioThumbnail from "../../assets/images/scenarios/highway.png";
import miningScenarioThumbnail from "../../assets/images/scenarios/mining.png";
import tourismScenarioThumbnail from "../../assets/images/scenarios/tourism.png";

/**
 * シナリオ2で「右上の道路」の現在の所属地区 ID を取得するヘルパー。
 * セットアップ時に localStorage へ記録した path_id を用いて GET /paths から引き当てる。
 * 見つからない場合は null を返す。
 */
async function getTopRightPathZoneId(base: string): Promise<number | null> {
  const stored = localStorage.getItem(TUTORIAL2_TOPRIGHT_PATH_KEY);
  if (!stored) return null;
  const topRightId = parseInt(stored, 10);
  const data = await fetchApi<PathListResponse>(`${base}/api/v1/paths`);
  const path = data.paths.find((p) => p.path_id === topRightId);
  return path ? path.zone_id : null;
}

/** localStorage の真偽フラグを判定する(セットアップ・各パネルが書き込む) */
function isFlagOn(key: string): boolean {
  return localStorage.getItem(key) === "1";
}

/**
 * ステップ画像を PNG 優先で解決する。
 * 各ステップ番号に対して step{N}.png を探し、存在しなければ step{N}.svg にフォールバックする。
 * Vite の import.meta.glob を用いてビルド時に利用可能なファイルを収集する。
 */
const pngModules = import.meta.glob("../../assets/images/tutorial/step*.png", {
  eager: true,
  query: "?url",
  import: "default",
}) as Record<string, string>;

const svgModules = import.meta.glob("../../assets/images/tutorial/step*.svg", {
  eager: true,
  query: "?url",
  import: "default",
}) as Record<string, string>;

function getStepImage(stepId: number): string {
  const pngKey = `../../assets/images/tutorial/step${stepId}.png`;
  const svgKey = `../../assets/images/tutorial/step${stepId}.svg`;
  if (pngKey in pngModules) return pngModules[pngKey];
  if (svgKey in svgModules) return svgModules[svgKey];
  return "";
}

/** チュートリアルの1ステップを表す型 */
export interface TutorialStep {
  /** ステップ番号 (1-indexed) */
  id: number;
  /** ステップ説明画像の URL */
  image: string;
  /** i18n 翻訳キー: ステップのタイトル */
  titleKey: string;
  /** i18n 翻訳キー: ステップの説明文 */
  descriptionKey: string;
  /** i18n 翻訳キー: 完了ボタンのテキスト。未指定時は "tutorial.completeButton" */
  buttonTextKey?: string;
  /**
   * 検証の直前に呼ばれる前提整備フック(任意)。
   * 「完成したパスにしか現れない状態」を検証条件にするステップで、対象を先に完成させるために使う。
   * 端点ノードの接続はパスが稼働中(ACTIVE)になった瞬間にしか確定しないため、
   * 工期スキップをここで行わないと、正しく敷設しても検証が永久に通らない。
   * ここでの失敗は検証を止めない(呼び出し側で握りつぶす)。
   */
  prepare?: (apiBaseUrl: string) => Promise<void>;
  /**
   * 完了ボタン押下時に呼ばれる検証関数。
   * バックエンド API にアクセスして条件を満たしているか確認する。
   * @param apiBaseUrl バックエンド API のベース URL
   * @returns 条件を満たしていれば true、そうでなければ false
   */
  validate: (apiBaseUrl: string) => Promise<boolean>;
  /**
   * 検証に成功して次ステップへ進む直前に呼ばれる副作用フック(任意)。
   * バックエンドへの書き込みや Unity への再描画指示など、
   * 「完了ボタンを押したときにチュートリアル側が代行する処理」を記述する。
   * 例: 建設をスキップさせるステップで、対象施設を強制的に稼働状態へ変更する。
   * ここでの失敗はチュートリアルの進行を止めない(呼び出し側で握りつぶす)。
   */
  onComplete?: (apiBaseUrl: string) => Promise<void>;
  /** チュートリアル中に有効化・ハイライトする要素のID */
  targetElementId?: string;
}

/** API URL の末尾スラッシュを正規化するユーティリティ */
function normalizeBaseUrl(url: string): string {
  return url.replace(/\/+$/, "");
}

/** シナリオ3のステップ判定で参照する建物の最小限のプロパティ。 */
type InfraBuilding = {
  agent_id?: number;
  variant?: number;
  status?: number;
  center_point?: number[];
  /**
   * インフラ施設の「接続端子ノード」のID(未接続・非インフラ施設は -1)。
   * 端子ノードは施設の設置時にバックエンドが自動生成する。プレイヤーが
   * インフラパスの端点をこのノードへ接続すると施設がインフラ網に参加する。
   */
  infra_node_id?: number;
  /**
   * 車両基地の「走行網への接続端子ノード」のID(車両基地でなければ -1)。
   * 鉄道車両基地は電力も受け取るため、バックエンドは infra_node_id と同一の
   * 「共用接続ノード」(線路ビット + 電力ビット)を生成する。したがって引き込み線路の
   * 端点も高圧送電線の端点も、この1つのノードへ溶接できる。
   */
  depot_node_id?: number;
};

/** シナリオ3のステップ判定で参照するパスの最小限のプロパティ。 */
type InfraPath = {
  path_type?: number;
  path_variant?: number;
  start_node_id?: number;
  end_node_id?: number;
};

/**
 * 指定バリアントの建物を1つ取得する。存在しなければ null を返す。
 * GET /api/v1/buildings(multi_agent スキーマ)は variant/status/center_point を含む。
 */
async function findBuildingByVariant(
  base: string,
  variant: number
): Promise<InfraBuilding | null> {
  const data = await fetchApi<{ buildings: InfraBuilding[] }>(
    `${base}/api/v1/buildings`
  );
  return data.buildings.find((b) => b.variant === variant) ?? null;
}

/** 建物が「建設予定・建設中・稼働中」のいずれかであるかを判定する。 */
function isPlacedOrActive(status: number | undefined): boolean {
  return (
    status === STATUS_SCHEDULED_CONSTRUCTION ||
    status === STATUS_CONSTRUCTING ||
    status === STATUS_ACTIVE
  );
}

/* ==========================================================================
 * シナリオ4「鉄道を建設しよう」の共通ヘルパー
 *
 * 鉄道は「電化線路 / プラットフォーム / 駅舎」がいずれも path_master のバリアントで
 * 表現されるため、判定でバリアント ID を直接書かず、マスターの属性
 * (type / track_type / infra_products)から毎回引き当てる。
 * ========================================================================== */

/** GET /api/v1/master/paths のバリアント定義のうち、判定に使う項目だけを持つ型。 */
type PathMasterEntryLite = {
  type?: number;
  track_type?: number;
  infra_products?: number[];
};

/** GET /api/v1/master/transit のエントリのうち、判定に使う項目だけを持つ型。 */
type TransitMasterEntryLite = {
  transit_path_id?: number;
  route_path_variants?: number[];
};

/** パスバリアントのマスターを取得する。 */
async function fetchPathMaster(base: string): Promise<Record<string, PathMasterEntryLite>> {
  const master = await fetchApi<{ paths: Record<string, PathMasterEntryLite> }>(
    `${base}/api/v1/master/paths`
  );
  return master.paths ?? {};
}

/** 条件に合致する path_variant の集合をマスターから解決する。 */
function pathVariantsWhere(
  master: Record<string, PathMasterEntryLite>,
  predicate: (entry: PathMasterEntryLite) => boolean
): Set<number> {
  const result = new Set<number>();
  for (const [variantId, entry] of Object.entries(master)) {
    if (predicate(entry)) result.add(Number(variantId));
  }
  return result;
}

/**
 * 電化された走行用の軌道(高圧電力を流すバリアント)かどうか。
 * 構造(通常/橋梁/高架)は問わないが、駅の軌道であるプラットフォームは除く。
 */
function isElectrifiedTrack(entry: PathMasterEntryLite): boolean {
  return (
    entry.type === PATH_TRACK &&
    entry.track_type !== TRACK_STATION &&
    (entry.infra_products ?? []).includes(PRODUCT_HIGH_ELECTRICITY)
  );
}

/** 電化されたプラットフォーム(駅の軌道)バリアントかどうか。 */
function isElectrifiedPlatform(entry: PathMasterEntryLite): boolean {
  return (
    entry.type === PATH_TRACK &&
    entry.track_type === TRACK_STATION &&
    (entry.infra_products ?? []).includes(PRODUCT_HIGH_ELECTRICITY)
  );
}

/**
 * 鉄道車両基地の agent variant をマスターから解決する。
 * 「メトロ車両(PRODUCT_METRO_VEHICLE)を supply する施設」が鉄道車両基地であり、
 * バス車庫(バス車両を supply)とはここで区別される。見つからなければ null。
 */
async function resolveRailDepotVariant(base: string): Promise<number | null> {
  const master = await fetchApi<{ agents: Record<string, { supply?: number }> }>(
    `${base}/api/v1/master/agents`
  );
  const found = Object.entries(master.agents ?? {}).find(
    ([, entry]) => entry.supply === PRODUCT_METRO_VEHICLE
  );
  return found ? Number(found[0]) : null;
}

/** 鉄道車両基地の建物を取得する。マスター未整備・未建設なら null。 */
async function findRailDepot(base: string): Promise<InfraBuilding | null> {
  const variant = await resolveRailDepotVariant(base);
  if (variant == null) return null;
  return findBuildingByVariant(base, variant);
}

/**
 * 条件に合致するパスをまとめて稼働中(ACTIVE)にする。
 *
 * チュートリアルでは工期を待たせずに次のステップへ進ませたいため、建設ステップの
 * onComplete から呼んで「建設計画中」のパスを即完成扱いにする(template_mode で
 * ステータス制限をバイパスする)。1本でも変更したら Unity に再ロードを指示する。
 * 解体予定など建設以外のステータスのパスを巻き込まないよう、対象は
 * 「建設予定・建設中」のパスに限る。
 */
async function activatePathsWhere(
  base: string,
  predicate: (path: PathListResponse["paths"][number]) => boolean
): Promise<void> {
  const data = await fetchApi<PathListResponse>(`${base}/api/v1/paths`);
  const targets = data.paths.filter(
    (p) => predicate(p) && p.status !== STATUS_ACTIVE && isPlacedOrActive(p.status)
  );
  for (const path of targets) {
    await fetchApi(`${base}/api/v1/paths/${path.path_id}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ status: STATUS_ACTIVE }),
    });
  }
  if (targets.length > 0) {
    await sendSessionReload().catch(() => {});
  }
}

/** 軌道(線路・プラットフォーム)をまとめて稼働中にする。 */
async function activateAllTracks(base: string): Promise<void> {
  await activatePathsWhere(base, (p) => p.path_type === PATH_TRACK);
}

/**
 * 鉄道の路線種別(transit_master)の transit_path_id 集合を返す。
 * 「走行網に軌道(PATH_TRACK)を含む種別」を鉄道路線とみなす。
 */
async function resolveRailTransitVariants(base: string): Promise<Set<number>> {
  const [pathMaster, transitMaster] = await Promise.all([
    fetchPathMaster(base),
    fetchApi<{ transit: Record<string, TransitMasterEntryLite> }>(
      `${base}/api/v1/master/transit`
    ),
  ]);
  const trackVariants = pathVariantsWhere(pathMaster, (e) => e.type === PATH_TRACK);
  const result = new Set<number>();
  for (const entry of Object.values(transitMaster.transit ?? {})) {
    const runsOnTrack = (entry.route_path_variants ?? []).some((v) =>
      trackVariants.has(Number(v))
    );
    if (runsOnTrack && entry.transit_path_id != null) {
      result.add(Number(entry.transit_path_id));
    }
  }
  return result;
}

/**
 * チュートリアルの全ステップ定義
 *
 * ステップ1はウェルカムメッセージ（検証なし）で、常に true を返す。
 * ステップ2〜6は実際のゲーム内操作を検証する。
 * ステップを追加・変更する場合はこの配列を編集し、対応する翻訳キーと画像を用意する。
 */
/** チュートリアルシナリオを表す型 */
export interface TutorialScenario {
  id: string;
  titleKey: string;
  descriptionKey: string;
  /** シナリオ一覧や詳細パネルで表示するサムネイル画像URL */
  image?: string;
  steps: TutorialStep[];
}

/**
 * チュートリアルの全シナリオ定義
 */
export const TUTORIAL_SCENARIOS: TutorialScenario[] = [
  {
    id: "basic",
    titleKey: "newGame.tutorialName",
    descriptionKey: "newGame.tutorialDesc",
    image: basicScenarioThumbnail,
    steps: [
      {
        id: 1,
        image: getStepImage(1),
        titleKey: "tutorial.step1.title",
        descriptionKey: "tutorial.step1.description",
        buttonTextKey: "tutorial.startButton",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
      {
        id: 2,
        image: getStepImage(2),
        titleKey: "tutorial.step2.title",
        descriptionKey: "tutorial.step2.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const data = await fetchApi<PathListResponse>(
              `${base}/api/v1/paths`
            );
            return data.paths.some(
              (p) =>
                p.path_type === PATH_STREET &&
                (p.status === STATUS_SCHEDULED_CONSTRUCTION ||
                  p.status === STATUS_CONSTRUCTING ||
                  p.status === STATUS_ACTIVE)
            );
          } catch {
            return false;
          }
        },
        targetElementId: "road",
      },
      {
        id: 3,
        image: getStepImage(3),
        titleKey: "tutorial.step3.title",
        descriptionKey: "tutorial.step3.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const data = await fetchApi<{ budgets: Record<string, number> }>(
              `${base}/api/v1/city/budgets`
            );
            const budgets = data.budgets;
            const total = Object.values(budgets).reduce((sum, val) => sum + val, 0);
            if (total <= 0) return false;
            // Construction budget (ID 8) should be maximized (>= 95% of total budget)
            return (budgets["8"] || 0) / total >= 0.95;
          } catch {
            return false;
          }
        },
        targetElementId: "budget",
      },
      {
        id: 4,
        image: getStepImage(4),
        titleKey: "tutorial.step4.title",
        descriptionKey: "tutorial.step4.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const data = await fetchApi<PathListResponse>(
              `${base}/api/v1/paths`
            );
            return data.paths.some(
              (p) => p.path_type === PATH_STREET && p.status === STATUS_ACTIVE
            );
          } catch {
            return false;
          }
        },
        targetElementId: "execute",
      },
      {
        id: 5,
        image: getStepImage(5),
        titleKey: "tutorial.step5.title",
        descriptionKey: "tutorial.step5.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const data = await fetchApi<ParcelListResponse>(
              `${base}/api/v1/parcels`
            );
            return data.parcels.length > 0;
          } catch {
            return false;
          }
        },
        targetElementId: "land",
      },
      {
        id: 6,
        image: getStepImage(6),
        titleKey: "tutorial.step6.title",
        descriptionKey: "tutorial.step6.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const data = await fetchApi<{ buildings: AgentListResponse["agents"] }>(
              `${base}/api/v1/buildings`
            );
            return data.buildings.some(
              (b) =>
                b.variant === 2001 &&
                (b.status === STATUS_SCHEDULED_CONSTRUCTION ||
                  b.status === STATUS_CONSTRUCTING ||
                  b.status === STATUS_ACTIVE)
            );
          } catch {
            return false;
          }
        },
        targetElementId: "construction",
      },
      {
        id: 7,
        image: getStepImage(7),
        titleKey: "tutorial.step7.title",
        descriptionKey: "tutorial.step7.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const data = await fetchApi<{ buildings: AgentListResponse["agents"] }>(
              `${base}/api/v1/buildings`
            );
            return data.buildings.some(
              (b) => b.variant === 2001 && b.status === STATUS_ACTIVE
            );
          } catch {
            return false;
          }
        },
        targetElementId: "execute",
      },
    ],
  },
  {
    id: "scenario2",
    titleKey: "tutorial.scenario2.title",
    descriptionKey: "tutorial.scenario2.description",
    image: scenario2Thumbnail,
    steps: [
      {
        // イントロ(検証なし)
        id: 1,
        image: getStepImage(1),
        titleKey: "tutorial.scenario2.step1.title",
        descriptionKey: "tutorial.scenario2.step1.description",
        buttonTextKey: "tutorial.startButton",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
      {
        // 右上の道路を新しい地区に分割する。
        // 完了条件: 右上の道路が初期地区とは異なる地区に所属していること。
        id: 2,
        image: getStepImage(2),
        titleKey: "tutorial.scenario2.step2.title",
        descriptionKey: "tutorial.scenario2.step2.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const zoneId = await getTopRightPathZoneId(base);
            if (zoneId == null || zoneId <= 0) return false;
            const mainZoneRaw = localStorage.getItem(TUTORIAL2_MAIN_ZONE_KEY);
            const mainZoneId = mainZoneRaw ? parseInt(mainZoneRaw, 10) : null;
            // 初期地区から分離され、別の地区に所属していれば完了
            return mainZoneId == null ? true : zoneId !== mainZoneId;
          } catch {
            return false;
          }
        },
        targetElementId: "district",
      },
      {
        // 地区分割の意味を説明する(検証なし)
        id: 3,
        image: getStepImage(3),
        titleKey: "tutorial.scenario2.step3.title",
        descriptionKey: "tutorial.scenario2.step3.description",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
      {
        // 天然資源を表示状態に切り替える。
        // 完了条件: 表示パネルで天然資源が表示状態になっていること(UI状態フラグ)。
        id: 4,
        image: getStepImage(4),
        titleKey: "tutorial.scenario2.step4.title",
        descriptionKey: "tutorial.scenario2.step4.description",
        validate: async (): Promise<boolean> =>
          isFlagOn(TUTORIAL_FLAG_RESOURCE_VISIBLE),
        targetElementId: "visibility",
      },
      {
        // 新しい地区の詳細パネルを開く。
        // 完了条件: 地区詳細パネルが開かれていること(UI状態フラグ)。
        id: 5,
        image: getStepImage(5),
        titleKey: "tutorial.scenario2.step5.title",
        descriptionKey: "tutorial.scenario2.step5.description",
        validate: async (): Promise<boolean> =>
          isFlagOn(TUTORIAL_FLAG_ZONE_PANEL_OPEN),
        targetElementId: undefined,
      },
      {
        // 地区の建築規制で採石場のみを許可する。
        // 完了条件: 右上の道路が属する地区の allowed_bd_mask が採石場のみであること。
        id: 6,
        image: getStepImage(6),
        titleKey: "tutorial.scenario2.step6.title",
        descriptionKey: "tutorial.scenario2.step6.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const zoneId = await getTopRightPathZoneId(base);
            if (zoneId == null || zoneId <= 0) return false;
            const data = await fetchApi<ZoneResponse>(
              `${base}/api/v1/zones/${zoneId}`
            );
            return data.zone.allowed_bd_mask === BUILDING_MINE;
          } catch {
            return false;
          }
        },
        targetElementId: undefined,
      },
      {
        // 土地区画の詳細パネルを開いて規制の反映を確認する。
        // 完了条件: 土地区画詳細パネルが開かれていること(UI状態フラグ)。
        id: 7,
        image: getStepImage(7),
        titleKey: "tutorial.scenario2.step7.title",
        descriptionKey: "tutorial.scenario2.step7.description",
        validate: async (): Promise<boolean> =>
          isFlagOn(TUTORIAL_FLAG_PARCEL_PANEL_OPEN),
        targetElementId: undefined,
      },
      {
        // まとめ(検証なし)
        id: 8,
        image: getStepImage(8),
        titleKey: "tutorial.scenario2.step8.title",
        descriptionKey: "tutorial.scenario2.step8.description",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
    ],
  },
  {
    id: "scenario3",
    titleKey: "tutorial.scenario3.title",
    descriptionKey: "tutorial.scenario3.description",
    image: scenario3Thumbnail,
    steps: [
      {
        // イントロ(検証なし)
        id: 1,
        image: getStepImage(1),
        titleKey: "tutorial.scenario3.step1.title",
        descriptionKey: "tutorial.scenario3.step1.description",
        buttonTextKey: "tutorial.startButton",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
      {
        // 民家(集合住宅)の詳細を確認する(検証なし)
        id: 2,
        image: getStepImage(2),
        titleKey: "tutorial.scenario3.step2.title",
        descriptionKey: "tutorial.scenario3.step2.description",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
      {
        // 都市左側の広い区画に太陽光発電所を建設する。
        // 完了条件: 太陽光発電所が建設予定/建設中/稼働中のいずれかで存在すること。
        id: 3,
        image: getStepImage(3),
        titleKey: "tutorial.scenario3.step3.title",
        descriptionKey: "tutorial.scenario3.step3.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const solar = await findBuildingByVariant(base, VARIANT_SOLAR_PLANT);
            return solar != null && isPlacedOrActive(solar.status);
          } catch {
            return false;
          }
        },
        targetElementId: "construction",
      },
      {
        // これはチュートリアルなので建設をスキップする(検証なし)。
        // 完了ボタン押下時に onComplete で太陽光発電所を強制的に稼働状態へ変更し、
        // Unity に再ロードを指示して3D描画を更新する。
        id: 4,
        image: getStepImage(4),
        titleKey: "tutorial.scenario3.step4.title",
        descriptionKey: "tutorial.scenario3.step4.description",
        validate: async (): Promise<boolean> => true,
        onComplete: async (apiBaseUrl: string): Promise<void> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          const solar = await findBuildingByVariant(base, VARIANT_SOLAR_PLANT);
          if (solar?.agent_id != null && solar.status !== STATUS_ACTIVE) {
            await fetchApi(`${base}/api/v1/agents/${solar.agent_id}`, {
              method: "PATCH",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ status: STATUS_ACTIVE }),
            });
            // 稼働状態への変更を3D描画へ反映させる
            await sendSessionReload().catch(() => {});
          }
        },
        targetElementId: "execute",
      },
      {
        // 民家の隣の空き区画に変電所を建設する。
        // 完了条件: 変電所が建設予定/建設中/稼働中のいずれかで存在すること。
        id: 5,
        image: getStepImage(5),
        titleKey: "tutorial.scenario3.step5.title",
        descriptionKey: "tutorial.scenario3.step5.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const substation = await findBuildingByVariant(base, VARIANT_SUBSTATION);
            return substation != null && isPlacedOrActive(substation.status);
          } catch {
            return false;
          }
        },
        targetElementId: "construction",
      },
      {
        // 太陽光発電所から変電所まで高圧送電線を敷設する。
        // 完了条件: 両施設の「接続端子ノード」(施設設置時に自動生成され infra_node_id で
        // 観測できる)のそれぞれに、高圧送電線パスが1本以上接続されていること。
        // 端点をノードに置くとバックエンドの端点溶接で接続される(=網に参加する)。
        // 溶接は稼働中になった瞬間にしか起こらないため、判定前に工期をスキップする。
        id: 6,
        image: getStepImage(6),
        titleKey: "tutorial.scenario3.step6.title",
        descriptionKey: "tutorial.scenario3.step6.description",
        prepare: async (apiBaseUrl: string): Promise<void> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          await activatePathsWhere(base, (p) => isInfraPathType(p.path_type));
        },
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const [solar, substation] = await Promise.all([
              findBuildingByVariant(base, VARIANT_SOLAR_PLANT),
              findBuildingByVariant(base, VARIANT_SUBSTATION),
            ]);
            const solarNode = solar?.infra_node_id ?? -1;
            const subNode = substation?.infra_node_id ?? -1;
            if (solarNode <= 0 || subNode <= 0) return false;

            const pathData = await fetchApi<{ paths: InfraPath[] }>(
              `${base}/api/v1/paths`
            );
            // 高圧送電線(インフラパス)のうち、指定ノードを端点に持つものがあるか
            const touchesNode = (nodeId: number): boolean =>
              pathData.paths.some(
                (p) =>
                  isInfraPathType(p.path_type) &&
                  p.path_variant === PATH_VARIANT_GRID &&
                  (p.start_node_id === nodeId || p.end_node_id === nodeId)
              );
            return touchesNode(solarNode) && touchesNode(subNode);
          } catch {
            return false;
          }
        },
        targetElementId: undefined,
      },
      {
        // 時間を進めて、変電所が高圧電流を受電できているかを確認する。
        // 完了条件: 変電所の高圧電流の当エポック獲得量(received_amount)が正であること。
        // remain_amount は生産の原材料消費で減算されて0に戻り得るため判定に使えない。
        // ※ 実際の配送(InfrastructureMarketPhase)が動くにはマスターデータ側の整備が必要。
        id: 7,
        image: getStepImage(7),
        titleKey: "tutorial.scenario3.step7.title",
        descriptionKey: "tutorial.scenario3.step7.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const substation = await findBuildingByVariant(base, VARIANT_SUBSTATION);
            if (substation?.agent_id == null) return false;
            // 建物一覧(multi_agent)には received_amount が含まれないため、
            // 個別の single_agent を取得して高圧電流の受電量を確認する。
            const detail = await fetchApi<{
              economic_agent: { received_amount?: Record<string, number> };
            }>(`${base}/api/v1/agents/${substation.agent_id}`);
            const received = detail.economic_agent.received_amount ?? {};
            return (received[String(PRODUCT_HIGH_ELECTRICITY)] ?? 0) > 0;
          } catch {
            return false;
          }
        },
        targetElementId: "execute",
      },
      {
        // まとめ(検証なし)
        id: 8,
        image: getStepImage(8),
        titleKey: "tutorial.scenario3.step8.title",
        descriptionKey: "tutorial.scenario3.step8.description",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
    ],
  },
  {
    // シナリオ4「鉄道を建設しよう」。
    // 初期状態は「東西に貫く道路 + 太陽光発電所だけがある更地の都市」(tutorialSetup)。
    // 線路 → 車両基地 → 引き込み線 → 送電線 → プラットフォーム → 駅舎 → 路線 の順に
    // 鉄道一式を建設させ、鉄道が複数の要素の組み合わせで成り立つことを学ばせる。
    // 各建設ステップの onComplete で対象を即完成(ACTIVE)にする。プラットフォームの
    // 停留所判定(POST /transit/lines)が稼働中のパスしか受け付けないため、
    // 工期を待たずに次のステップへ進めるにはこの代行が必須となる。
    id: "scenario4",
    titleKey: "tutorial.scenario4.title",
    descriptionKey: "tutorial.scenario4.description",
    image: scenario4Thumbnail,
    steps: [
      {
        // イントロ(検証なし)
        id: 1,
        image: getStepImage(1),
        titleKey: "tutorial.scenario4.step1.title",
        descriptionKey: "tutorial.scenario4.step1.description",
        buttonTextKey: "tutorial.startButton",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
      {
        // 電化された地上線路を敷設する。
        // 完了条件: 電化軌道バリアントのパスが1本以上存在すること。
        id: 2,
        image: getStepImage(2),
        titleKey: "tutorial.scenario4.step2.title",
        descriptionKey: "tutorial.scenario4.step2.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const master = await fetchPathMaster(base);
            const electrified = pathVariantsWhere(master, isElectrifiedTrack);
            if (electrified.size === 0) return false;
            const data = await fetchApi<PathListResponse>(`${base}/api/v1/paths`);
            return data.paths.some(
              (p) =>
                electrified.has(p.path_variant) && isPlacedOrActive(p.status)
            );
          } catch {
            return false;
          }
        },
        onComplete: async (apiBaseUrl: string): Promise<void> => {
          await activateAllTracks(normalizeBaseUrl(apiBaseUrl));
        },
        targetElementId: "track",
      },
      {
        // 鉄道車両基地を空き区画に建設する。
        // 完了条件: 鉄道車両基地が建設予定/建設中/稼働中のいずれかで存在すること。
        id: 3,
        image: getStepImage(3),
        titleKey: "tutorial.scenario4.step3.title",
        descriptionKey: "tutorial.scenario4.step3.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const depot = await findRailDepot(base);
            return depot != null && isPlacedOrActive(depot.status);
          } catch {
            return false;
          }
        },
        onComplete: async (apiBaseUrl: string): Promise<void> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          const depot = await findRailDepot(base);
          if (depot?.agent_id != null && depot.status !== STATUS_ACTIVE) {
            await fetchApi(`${base}/api/v1/agents/${depot.agent_id}`, {
              method: "PATCH",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ status: STATUS_ACTIVE }),
            });
            await sendSessionReload().catch(() => {});
          }
        },
        targetElementId: "construction",
      },
      {
        // 車両基地の接続端子ノードから本線まで引き込み線を敷設する。
        // 完了条件: 車両基地の接続端子ノードを端点に持つ軌道パスが1本以上あること。
        // 端点をノードに置くとバックエンドの端点溶接で接続される(=走行網に参加する)。
        // 溶接は稼働中になった瞬間にしか起こらないため、判定前に工期をスキップする。
        id: 4,
        image: getStepImage(4),
        titleKey: "tutorial.scenario4.step4.title",
        descriptionKey: "tutorial.scenario4.step4.description",
        prepare: async (apiBaseUrl: string): Promise<void> => {
          await activateAllTracks(normalizeBaseUrl(apiBaseUrl));
        },
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const depot = await findRailDepot(base);
            const nodeId = depot?.depot_node_id ?? -1;
            if (nodeId <= 0) return false;
            const data = await fetchApi<PathListResponse>(`${base}/api/v1/paths`);
            return data.paths.some(
              (p) =>
                p.path_type === PATH_TRACK &&
                (p.start_node_id === nodeId || p.end_node_id === nodeId)
            );
          } catch {
            return false;
          }
        },
        targetElementId: "track",
      },
      {
        // 太陽光発電所から車両基地の共用接続ノードまで高圧送電線を敷設する。
        // 完了条件: 発電所のインフラ端子と車両基地の共用端子のそれぞれに、
        // 高圧送電線が1本以上接続されていること(途中で分岐・中継しても成立する)。
        // 車両基地の共用端子には引き込み線も溶接済みなので、ここが繋がると
        // 発電所 → 送電線 → 車両基地 → 引き込み線 → 電化線路 と電気が回る。
        // 溶接は稼働中になった瞬間にしか起こらないため、判定前に工期をスキップする。
        id: 5,
        image: getStepImage(5),
        titleKey: "tutorial.scenario4.step5.title",
        descriptionKey: "tutorial.scenario4.step5.description",
        prepare: async (apiBaseUrl: string): Promise<void> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          await activatePathsWhere(base, (p) => isInfraPathType(p.path_type));
        },
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const [solar, depot] = await Promise.all([
              findBuildingByVariant(base, VARIANT_SOLAR_PLANT),
              findRailDepot(base),
            ]);
            const solarNode = solar?.infra_node_id ?? -1;
            const depotNode = depot?.infra_node_id ?? -1;
            if (solarNode <= 0 || depotNode <= 0) return false;

            const pathData = await fetchApi<{ paths: InfraPath[] }>(
              `${base}/api/v1/paths`
            );
            const touchesNode = (nodeId: number): boolean =>
              pathData.paths.some(
                (p) =>
                  isInfraPathType(p.path_type) &&
                  p.path_variant === PATH_VARIANT_GRID &&
                  (p.start_node_id === nodeId || p.end_node_id === nodeId)
              );
            return touchesNode(solarNode) && touchesNode(depotNode);
          } catch {
            return false;
          }
        },
        targetElementId: "road",
      },
      {
        // 電化プラットフォーム(駅の軌道)を敷設する。
        // 完了条件: 電化プラットフォームのバリアントのパスが1本以上存在すること。
        id: 6,
        image: getStepImage(6),
        titleKey: "tutorial.scenario4.step6.title",
        descriptionKey: "tutorial.scenario4.step6.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const master = await fetchPathMaster(base);
            const platforms = pathVariantsWhere(master, isElectrifiedPlatform);
            if (platforms.size === 0) return false;
            const data = await fetchApi<PathListResponse>(`${base}/api/v1/paths`);
            return data.paths.some(
              (p) => platforms.has(p.path_variant) && isPlacedOrActive(p.status)
            );
          } catch {
            return false;
          }
        },
        onComplete: async (apiBaseUrl: string): Promise<void> => {
          await activateAllTracks(normalizeBaseUrl(apiBaseUrl));
        },
        targetElementId: "track",
      },
      {
        // 駅舎パスをプラットフォームと交差するように敷設する。
        // 完了条件: 駅舎パス(PATH_STATION_HOUSE)が1本以上存在すること。
        id: 7,
        image: getStepImage(7),
        titleKey: "tutorial.scenario4.step7.title",
        descriptionKey: "tutorial.scenario4.step7.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const data = await fetchApi<PathListResponse>(`${base}/api/v1/paths`);
            return data.paths.some(
              (p) =>
                p.path_type === PATH_STATION_HOUSE && isPlacedOrActive(p.status)
            );
          } catch {
            return false;
          }
        },
        onComplete: async (apiBaseUrl: string): Promise<void> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          // 路線の停留所は「稼働中の」プラットフォーム上にしか置けないため、
          // 駅舎とプラットフォームの双方をここで完成させておく。
          await activatePathsWhere(
            base,
            (p) => p.path_type === PATH_STATION_HOUSE || p.path_type === PATH_TRACK
          );
        },
        targetElementId: "track",
      },
      {
        // 鉄道路線を新設する。
        // 完了条件: 走行網に軌道を含む路線種別(=鉄道路線)の路線が1つ以上存在すること。
        id: 8,
        image: getStepImage(8),
        titleKey: "tutorial.scenario4.step8.title",
        descriptionKey: "tutorial.scenario4.step8.description",
        validate: async (apiBaseUrl: string): Promise<boolean> => {
          const base = normalizeBaseUrl(apiBaseUrl);
          try {
            const railVariants = await resolveRailTransitVariants(base);
            const data = await fetchApi<{ lines: { path_variant?: number }[] }>(
              `${base}/api/v1/transit/lines`
            );
            return (data.lines ?? []).some(
              (line) => line.path_variant != null && railVariants.has(line.path_variant)
            );
          } catch {
            return false;
          }
        },
        targetElementId: "transport",
      },
      {
        // まとめ(検証なし)
        id: 9,
        image: getStepImage(9),
        titleKey: "tutorial.scenario4.step9.title",
        descriptionKey: "tutorial.scenario4.step9.description",
        validate: async (): Promise<boolean> => true,
        targetElementId: undefined,
      },
    ],
  },
];

const scenarioThumbnails: Record<string, string> = {
  trade: tradeScenarioThumbnail,
  highway: highwayScenarioThumbnail,
  mining: miningScenarioThumbnail,
  tourism: tourismScenarioThumbnail,
};

for (const id of ["trade", "highway", "mining", "tourism"]) {
  TUTORIAL_SCENARIOS.push({
    id,
    titleKey: `tutorial.${id}.title`,
    descriptionKey: `tutorial.${id}.description`,
    image: scenarioThumbnails[id],
    steps: DEFINITIONS[id].steps.map((step) => ({
      id: step.legacyStep,
      image: "",
      titleKey: `tutorial.${id}.step${step.legacyStep}.title`,
      descriptionKey: step.phases[0].textKey,
      validate: async () => false,
    })),
  });
}

/** 互換性のための定義 */
export const TUTORIAL_STEPS = TUTORIAL_SCENARIOS[0].steps;
export const TUTORIAL_TOTAL_STEPS = TUTORIAL_STEPS.length;
