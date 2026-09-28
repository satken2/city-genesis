/**
 * 建物建設パネル
 *
 * window_type="building_construction" のポップアップウィンドウで表示する専用パネル。
 * 分類(マスターの bd_category)と建物(バリアント)の選択、階数設定、形状プレビューを行う。
 *
 * 操作フロー:
 *   1. 分類タイルを選ぶ → その分類の建物タイルに切り替わる
 *   2. 建物タイルを選ぶ → タイルが消えて説明文と建設の設定欄が表示される
 *      (左上の戻るボタンで建物タイル、さらに分類タイルへ1段ずつ戻れる)
 *   3. IPC で graphic モジュールに STATE_EDIT_BUILDING を指示
 *   4. graphic 側で土地区画が選択されると edit_building.select_parcel が通知される
 *      Ctrlで自由/道路沿い配置を選んだ場合はplacement_stateで矩形と位置を受け取る
 *   5. 形状プレビュー・情報欄が表示される
 *   6. 階数を変更すると edit_building.update_floors が送信される
 *   7. [建設を確定] で UI が直接バックエンドに送信する(道路建設と同じ責務分担。
 *      区画モード=POST /buildings、直接配置=POST /buildings/direct、
 *      既存建物の編集=PATCH /agents/{id})。成功後は session:reload で Unity に再描画させる
 *
 * 市庁舎の詳細パネルの移転ボタンから開くと(relocateCityHall)、選択を市庁舎に固定した移転モードになる。
 * 操作と送信は新規の配置と同じで、配置済みの市庁舎を置き直すとバックエンドが無料・即時の移転として扱う。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import type { AgentMasterEntry, LandmarkMasterEntry, ParcelData, StructureMasterEntry } from "../../types";
import { resolveAgentIcon, resolveAgentVariantIcon } from "../../utils/agentIcon";
import { resolveBdCategoryIcon } from "../../utils/bdCategoryIcon";
import { resolveParcelIcon } from "../../utils/parcelIcon";

import { setHintbarText } from "../../utils/hintbar";
import {
  sendChangeState,
  sendChangeDisplay,
  sendEditBuildingUpdateFloors,
  sendEditBuildingUpdateStructure,
  sendEditBuildingUpdateLandmark,
  sendEditBuildingUpdateShape,
  sendEditBuildingUpdateEffectRange,
  sendBuildingPlacementCommand,
  IPC_EVENT_BUILDING_PLACEMENT_STATE,
  sendFocusGraphic,
  sendSessionReload,
  sendPickCopySource,
  sendHighlightObjects,
  sendClearHighlights,
  IPC_EVENT_EDIT_BUILDING_SELECT_PARCEL,
  IPC_EVENT_EDIT_BUILDING_INVALID_PARCEL,
  IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE,
} from "../../utils/ipc";
import type { BuildingPlacementState, EffectRangeRegion, PathInterferenceHighlight } from "../../utils/ipc";
import { fetchApi, ApiError } from "../../utils/api";
import { useMasterData } from "../../contexts/MasterDataContext";
import { NumberStepper } from "../common/NumberStepper";
import { TagPicker } from "../common/TagPicker";
import { TAGS_CHANGED_EVENT } from "../../utils/constructionTags";

import { ConstructionInfoCard } from "../common/ConstructionInfoCard";
import { isConstructionAffordable } from "../../utils/constructionFunding";
import { useVariantAffordability } from "../../hooks/useVariantAffordability";
import { BuildingFacilityInfoCard } from "./BuildingFacilityInfoCard";
import { BuildingFloorSpecs } from "./BuildingFloorSpecs";
import { showNotification } from "../../utils/notification";
import { BD_CATEGORY_OTHER, PARCEL_LAND, VARIANT_CITY } from "../../constants";
import { isGradingVariant } from "../grading/grading";
import "./BuildingConstructionPanel.css";

// 直接配置モードで default_width が未設定のバリアント用のフォールバック(m)。
// バックエンド crud_buildings.DEFAULT_BUILDING_WIDTH と揃える。
const DEFAULT_BUILDING_WIDTH = 20.0;

// 干渉ハイライトの発信元(Unity HighlightManager のソースレイヤー名)。
// このウィンドウの windowType と一致させる(クローズ時に window.rs がクリアを送るため)。
const HIGHLIGHT_SOURCE = "building_construction";

// 用地買収の確認ウィンドウ(BuildingDirectAcquisitionConfirmWindow)がOKで発火する
// クロスウィンドウイベント。パネル側が購読して承諾つき確定を実行する。
const EVENT_ACQUISITION_CONFIRMED = "building_direct:acquisition_confirmed";

// 回転角(度)を [0, 360) に正規化する。回転ステッパーはマイナス・360超を許容するため、
// 確定送信時にこの関数で mod 360 する(例: 450→90, -90→270)。
function normalizeAngle(angle: number): number {
  return ((angle % 360) + 360) % 360;
}

type BuildingPreviewData = {
  polygon: number[][];
  height: number;
  center_point: number[];
  area?: number;
};

type BuildingConstructionPanelProps = {
  apiBaseUrl: string;
  onClose: () => void;
  editAgentId?: number | null;
  /** 市庁舎の詳細パネルの移転ボタンから開かれたか。配置の操作は新規建設と同じで、確定すると移転になる。 */
  relocateCityHall?: boolean;
};

/* ---- メインコンポーネント ---- */

export function BuildingConstructionPanel({ apiBaseUrl, onClose, editAgentId, relocateCityHall = false }: BuildingConstructionPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  // バリアント単位の資金目安(最小規模すら建てられない施設をタイルで選ばせない)
  const { affordability: variantAffordability } = useVariantAffordability("buildings", apiBaseUrl);
  const isVariantUnaffordable = (variantId: number) =>
    variantAffordability != null
    && !variantAffordability.unlimited
    && variantAffordability.variants[variantId]?.affordable === false;
  const appWindow = useMemo(() => getCurrentWindow(), []);

  // 建物タイルを表示している分類(BD_CATEGORY_*)。null のときは分類タイルを表示する。
  const [selectedCategory, setSelectedCategory] = useState<number | null>(null);
  // 既存建物の編集で候補を絞る bd_type(編集対象の建物の値)。新規建設では null。
  const [editBdType, setEditBdType] = useState<number | null>(null);
  const [selectedVariant, setSelectedVariant] = useState<number | null>(null);
  const [floors, setFloors] = useState(1);
  // 構造種別(structures マスターのID)。構造を持つバリアントを選ぶと許可構造の最上位で初期化され、
  // 構造を持たないバリアント(農地など)では null のまま(送信しない=バックエンドが 0 を確定する)。
  const [structure, setStructure] = useState<number | null>(null);
  // 観光名所(landmarks マスターのID)。名所軸を持つバリアントを選ぶと未配置の最小IDで初期化され、
  // 名所軸を持たないバリアントでは null のまま(送信しない=バックエンドが 0 を確定する)。
  const [landmark, setLandmark] = useState<number | null>(null);
  // すでに都市に存在する名所のID一覧。同じ名所は都市に1基までなので選択肢を無効化する。
  // 取得に失敗したときは空のままにして選択を妨げない(最終的な拒否はバックエンドが行う)。
  const [placedLandmarkIds, setPlacedLandmarkIds] = useState<number[]>([]);
  // 設置する建物に付ける建設プロジェクトのタグ(空文字=付けない)。作成リクエストに載せて一緒に保存する。
  const [projectTag, setProjectTag] = useState("");
  // 建ぺい率(0.0〜1.0)。敷地面積に対する建築面積の上限割合。プレビュー形状・建設コストの算出に用いる。
  const [coverageRatio, setCoverageRatio] = useState(0.6);
  // 外観バリアント(visual_variant1/2/3)。0.0〜1.0の値で建物の外観を決定する。
  // 新規建設時はバリアント選択時に0.5で初期化され、スライダーで調整後、確定時にバックエンドへ送信される。
  // 編集モードでは既存建物の値を読み込む。GET /agents/{id}がvisual_variantを返さない場合はnullのままになり、
  // 確定時にnullのフィールドは送信されないため現在値が維持される（ユーザーがスライダーを動かさなければ外観は変わらない）。
  const [visualVariant, setVisualVariant] = useState<number | null>(null);
  const [visualVariant2, setVisualVariant2] = useState<number | null>(null);
  const [visualVariant3, setVisualVariant3] = useState<number | null>(null);
  const [parcelId, setParcelId] = useState<number | null>(null);
  const [parcelData, setParcelData] = useState<ParcelData | null>(null);
  const [isParcelLoading, setIsParcelLoading] = useState(false);
  // 直接配置の固定位置。配置方法はplacementMode、未固定の追従状態はnullで表す。
  const [pickedPosition, setPickedPosition] = useState<number[] | null>(null);
  // 直接配置の底面矩形サイズ(横幅・縦幅・回転角)。バリアント選択時に default_width で初期化する。
  const [rectWidth, setRectWidth] = useState(DEFAULT_BUILDING_WIDTH);
  const [rectDepth, setRectDepth] = useState(DEFAULT_BUILDING_WIDTH);
  const [rectAngle, setRectAngle] = useState(0);
  const [placementMode, setPlacementMode] = useState<BuildingPlacementState["mode"]>("parcel");
  const [placementRevision, setPlacementRevision] = useState(-1);
  const [validatedDirectKey, setValidatedDirectKey] = useState<string | null>(null);
  const [placementCommandPending, setPlacementCommandPending] = useState(false);
  const latestPlacementRevision = useRef(-1);
  const pendingPlacementRequest = useRef<string | null>(null);
  const placementRequestSequence = useRef(0);
  // 直接配置の設置可否(バックエンドの preview-direct が返す valid / invalid_reason)。
  const [directValid, setDirectValid] = useState(false);
  const [directInvalidReason, setDirectInvalidReason] = useState<string | null>(null);
  // 民間区画との重なりを自動用地買収で解消すれば確定できる状態(preview-direct の
  // requires_acquisition)。確定ボタンは押せるが、押下時に確認ウィンドウを挟む。
  const [directRequiresAcquisition, setDirectRequiresAcquisition] = useState(false);
  // 買収対象の件数と費用見積(requires_acquisition=True のときのみ非null)。
  const [directAcquisition, setDirectAcquisition] = useState<{
    count: number;
    estimatedCost: number;
  } | null>(null);
  const [buildingPreview, setBuildingPreview] = useState<BuildingPreviewData | null>(null);
  const [estimatedCost, setEstimatedCost] = useState<number | null>(null);
  // 完全に平坦な地形（傾斜ゼロ）で建てた場合の標準コスト。推定コストが標準の何倍かの表示に使う。
  const [estimatedStandardCost, setEstimatedStandardCost] = useState<number | null>(null);
  // 推定建設期間(日)。道路パネルと同じ枠組みで表示する。
  const [estimatedDurationDays, setEstimatedDurationDays] = useState<number | null>(null);
  // 建設サービス財の調達充足率(0〜1)と、それを織り込んだ見込み建設期間(日)。
  // 充足率が低い都市では建設が推定期間どおりに進まないため、警告表示に使う。
  const [supplyRatio, setSupplyRatio] = useState<number | null>(null);
  const [expectedDurationDays, setExpectedDurationDays] = useState<number | null>(null);
  // 発注に使える市の資金(現預金 − 貯蓄目標 − 既存案件の拘束額)。資金審査の先回り表示に使う。
  const [availableFunds, setAvailableFunds] = useState<number | null>(null);
  // 資金審査の必要総額 = 建設費 + 直接配置の用地買収見積(民間区画の自動買収も市の支出)。
  const requiredTotal =
    estimatedCost == null ? null : estimatedCost + (directAcquisition?.estimatedCost ?? 0);
  const isAffordable = isConstructionAffordable(requiredTotal, availableFunds);
  // TYPE_STANDARD(一般施設)のときのみ返る、最大稼働時の推定生産量・雇用人数・稼働コスト(いずれもsimulation_daysあたり)。
  const [estimatedMaxProduction, setEstimatedMaxProduction] = useState<number | null>(null);
  const [estimatedEmployment, setEstimatedEmployment] = useState<number | null>(null);
  const [estimatedOperatingCost, setEstimatedOperatingCost] = useState<number | null>(null);
  // 稼働コストの内訳(人件費・原料費)と支出元の予算カテゴリ(LEDGER_*)。
  // ランニングコスト表示の内訳行・費目行として使う。総額 = 人件費 + 原料費(専用建物のため家賃なし)。
  const [operatingWageCost, setOperatingWageCost] = useState<number | null>(null);
  const [operatingMaterialCost, setOperatingMaterialCost] = useState<number | null>(null);
  const [operatingCostCategory, setOperatingCostCategory] = useState<number | null>(null);
  // 1エポックの日数。ランニングコストの「1エポック(n日)あたり」注記に使う。
  const [costSimulationDays, setCostSimulationDays] = useState<number | null>(null);
  const [isCostLoading, setIsCostLoading] = useState(false);
  // 形状プレビュー・建設費見積の取得に失敗した理由(バックエンドの検証メッセージ)。
  // 失敗すると3Dプレビューも建設情報カードも出ないため、無言の故障に見えないよう画面に理由を出す。
  const [estimateError, setEstimateError] = useState<string | null>(null);
  // 選択中の施設がインフラ供給施設(効果範囲を持つ)で、効果範囲を3D表示中かどうか。
  // 効果範囲の凡例表示の出し分けに使う。
  const [effectRangeActive, setEffectRangeActive] = useState(false);
  // 新設プレビューの効果範囲領域(敷地/地点の選択後に preview-effect-range から取得)。
  const [previewEffectRegion, setPreviewEffectRegion] = useState<EffectRangeRegion | null>(null);
  // 選択中バリアントと同じサービス財を供給する既存施設の効果範囲一覧。
  // カバー済みの場所と新設候補地を見比べられるよう、敷地選択前から3D表示する。
  const [existingEffectRanges, setExistingEffectRanges] = useState<EffectRangeRegion[]>([]);

  // 既存建物コピー建設: コピー元の agent_id。非nullのときコピーモード。
  // このモードでは元建物の実寸フットプリントを固定コピーし、忠実コピー(バリアント・階数一致)なら
  // バックエンドが建設体積を割引く。フットプリントが対象区画に収まるかはバックエンドが判定し、
  // preview の valid / invalid_reason を copyValid / copyInvalidReason で受ける。
  const [copySourceAgentId, setCopySourceAgentId] = useState<number | null>(null);
  const [copyValid, setCopyValid] = useState(false);
  const [copyInvalidReason, setCopyInvalidReason] = useState<string | null>(null);

  const [cityHallPlaced, setCityHallPlaced] = useState<boolean>(true);
  const [templateMode, setTemplateMode] = useState<boolean>(false);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();

    const fetchStatus = async () => {
      try {
        const baseUrl = apiBaseUrl.replace(/\/+$/, "");
        
        // city と session を並行して取得
        const [cityRes, sessionRes] = await Promise.all([
          fetch(`${baseUrl}/api/v1/city`, { signal: controller.signal }),
          fetch(`${baseUrl}/api/v1/sessions`, { signal: controller.signal }),
        ]);

        if (cityRes.ok && sessionRes.ok) {
          const cityData = await cityRes.json();
          const sessionData = await sessionRes.json();
          
          setCityHallPlaced(cityData.city?.city_hall_placed ?? false);
          setTemplateMode(sessionData.template_mode ?? false);
        }
      } catch (err) {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch city or session status:", err);
        }
      }
    };

    void fetchStatus();

    return () => controller.abort();
  }, [apiBaseUrl]);

  const isCityHallPlacedGateActive = !cityHallPlaced && !templateMode;
  /**
   * 市庁舎だけを置くモード。都市創設前の配置と、詳細パネルからの移転がこれに当たり、選択を市庁舎に固定する。
   * 移転もバックエンドへの送信は配置と同じで、配置済みの市庁舎を置き直すとバックエンドが移転として扱う。
   */
  const cityHallOnly = isCityHallPlacedGateActive || relocateCityHall;

  /* ---- 配置済みの観光名所を取得 ---- */

  /**
   * すでに都市にある名所(稼働中・建設中・計画中・倒壊のいずれか)のID一覧を取得する。
   * 同じ名所を二重に建てられないよう、セレクタの該当項目を無効化するために使う。
   */
  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();

    fetchApi<{ landmark_ids: number[] }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/placed-landmarks`,
      { signal: controller.signal },
    )
      .then((data) => {
        if (!controller.signal.aborted) setPlacedLandmarkIds(data.landmark_ids ?? []);
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch placed landmarks:", err);
        }
      });

    return () => controller.abort();
  }, [apiBaseUrl]);

  /* ---- 既存建物編集モード: データ取得 ---- */
  useEffect(() => {
    if (editAgentId == null || !apiBaseUrl) return;
    const controller = new AbortController();

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/agents/${editAgentId}`, {
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{ economic_agent: { bd_type: number; variant: number; floors: number; parcel_id: number; structure?: number; landmark?: number; visual_variant?: number; visual_variant2?: number; visual_variant3?: number } }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        const agent = data.economic_agent;
        setEditBdType(agent.bd_type);
        setSelectedVariant(agent.variant);
        setFloors(agent.floors);
        setStructure(agent.structure != null && agent.structure > 0 ? agent.structure : null);
        setLandmark(agent.landmark != null && agent.landmark > 0 ? agent.landmark : null);
        setParcelId(agent.parcel_id);
        // 既存建物の外観バリアントを初期値として読み込む。
        // single_agentスキーマがvisual_variantを含まない場合はundefinedとなり、
        // その場合はnullのまま（確定時に送信されないので現在値が維持される）。
        if (agent.visual_variant != null) setVisualVariant(agent.visual_variant);
        if (agent.visual_variant2 != null) setVisualVariant2(agent.visual_variant2);
        if (agent.visual_variant3 != null) setVisualVariant3(agent.visual_variant3);
        // バリアント選択に伴い Unity 側に建物建設モードを通知する
        sendChangeState("STATE_EDIT_BUILDING", {
          building_variant: agent.variant,
          agent_id: editAgentId,
          structure: agent.structure ?? 0,
          landmark: agent.landmark ?? 0,
        }).catch((err) => {
          console.error("[BuildingConstructionPanel] sendChangeState failed:", err);
        });
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch existing agent:", err);
        }
      });

    return () => controller.abort();
  }, [editAgentId, apiBaseUrl]);

  // 市庁舎だけを置くモードでは、自動的に「市政府」を選択状態にする
  useEffect(() => {
    if (cityHallOnly && editAgentId == null) {
      // マスターデータ取得後にのみ市庁舎を自動選択する。未取得のまま選択すると
      // min_floors 等が読めず floors を誤った初期値で固定してしまい、
      // 一度 selectedVariant が確定すると下の再選択ガードで訂正されなくなるため。
      if (selectedVariant !== VARIANT_CITY && masterData) {
        setSelectedVariant(VARIANT_CITY);
        const entry = masterData.agents[VARIANT_CITY];
        // 市庁舎バリアントの最小階数を初期値にする。1階固定にすると min_floors>1 の
        // 市庁舎で「1階のまま確定→バックエンドが階数エラーで拒否」になるため。
        setFloors(entry?.min_floors ?? 1);
        setStructure(defaultStructureOf(entry));
        // 直接配置の底面サイズを default_width × default_height で初期化する
        // (default_height 未設定なら正方形)。
        const initialWidth = entry?.default_width ?? DEFAULT_BUILDING_WIDTH;
        const initialDepth = entry?.default_height ?? initialWidth;
        setRectWidth(initialWidth);
        setRectDepth(initialDepth);
        setRectAngle(0);
        setVisualVariant(Math.random());
        setVisualVariant2(Math.random());
        setVisualVariant3(Math.random());
        // 配置方法は自由配置から始める(Ctrlラジアルで区画配置へ戻せる)。都市創設時は道路も土地区画も
        // 無いのが通常で、移転ではカーソルを道路に乗せれば道路の脇へ吸着して置ける。
        sendChangeState("STATE_EDIT_BUILDING", {
          building_variant: VARIANT_CITY,
          agent_id: null,
          structure: defaultStructureOf(entry) ?? 0,
          placement_mode: "free",
        }).catch((err) => {
          console.error("[BuildingConstructionPanel] sendChangeState for city hall failed:", err);
        });
      }
    }
  }, [cityHallOnly, selectedVariant, masterData, editAgentId]);

  /* ---- ヒントバー表示 ---- */

  /**
   * パネルが開いたときにヒントバーに建物建設の操作説明を表示し、
   * パネルが閉じられたときにヒントをクリアする。
   */
  useEffect(() => {
    let cancelled = false;
    (async () => {
      await setHintbarText(editAgentId != null ? t("hint.buildingConstruction") :
        placementMode === "parcel" ? t("building_construction.placement.parcelHint") :
          `${t("building_construction.placement.placeHint")} · ${t("building_construction.placement.sizeHint")}`);
    })().catch((err) => {
      if (!cancelled) {
        console.error("[BuildingConstruction] setHintbarText failed:", err);
      }
    });
    return () => {
      cancelled = true;
      void setHintbarText("");
    };
  }, [t, placementMode, editAgentId]);

  /* ---- マスターデータからの派生 ---- */

  /**
   * 建物として建設可能なバリアント一覧 (building === true)
   * TYPE_BUILDING(共有建物)だけでなく、building=trueのTYPE_STANDARD(一般施設)、
   * TYPE_EDUCATION(教育機関)、TYPE_TRADE(輸出入拠点)など、
   * 全ての建設可能エージェントを含む。分類(bd_category)ごとのタイルにまとめて表示する。
   * template_only のバリアント(国境検問所・観光名所)はシナリオ制作専用なので、
   * テンプレートモードでないときは候補から外す。
   * 造成工事は範囲を描いて発注する造成ウィンドウ専用なので、常に外す。
   * 市庁舎は都市に1つだけなので、配置済みなら候補から外す(移転は市庁舎の詳細パネルから開く)。
   */
  const buildingVariants = useMemo(() => {
    if (!masterData) return [];
    return Object.entries(masterData.agents).filter(
      ([id, entry]) => entry.building && (templateMode || !entry.template_only) && !isGradingVariant(Number(id))
        && !(Number(id) === VARIANT_CITY && cityHallPlaced && !relocateCityHall)
    ) as [string, AgentMasterEntry][];
  }, [masterData, templateMode, cityHallPlaced, relocateCityHall]);

  /** 分類タイルに並べる分類(ID順=並び順)。建設できる建物を1つも持たない分類は出さない。 */
  const availableCategories = useMemo(
    () => [...new Set(buildingVariants.map(([, entry]) => categoryOf(entry)))].sort((a, b) => a - b),
    [buildingVariants],
  );

  /**
   * 選択中の分類に属するバリアント。既存建物の編集は敷地を選び直さず建築規制(allowed_bd_mask)の
   * 照合を通らないため、規制の単位である bd_type が編集対象と同じものに限る。
   */
  const variantsForCategory = useMemo(() => {
    if (selectedCategory === null) return [];
    return buildingVariants.filter(([, entry]) =>
      categoryOf(entry) === selectedCategory && (editBdType === null || entry.bd_type === editBdType));
  }, [buildingVariants, selectedCategory, editBdType]);

  /** 選択中のバリアントのマスターデータ */
  const selectedVariantData = useMemo((): AgentMasterEntry | null => {
    if (selectedVariant === null || !masterData) return null;
    return masterData.agents[selectedVariant] ?? null;
  }, [masterData, selectedVariant]);

  /**
   * 選択中バリアントが選べる構造種別の一覧(ID昇順)。構造を持たないバリアントは空。
   * 表示名は構造種別マスターから引く。
   */
  const structureOptions = useMemo((): { id: number; entry: StructureMasterEntry | undefined }[] => {
    if (!selectedVariantData || !masterData) return [];
    const ids = [...(selectedVariantData.structures ?? [])].sort((a, b) => a - b);
    return ids.map((id) => ({ id, entry: masterData.structures?.[id] }));
  }, [selectedVariantData, masterData]);

  /** 選択中の構造種別のマスター(構造なしは null) */
  const selectedStructureData = useMemo((): StructureMasterEntry | null => {
    if (structure == null || !masterData) return null;
    return masterData.structures?.[structure] ?? null;
  }, [structure, masterData]);

  /**
   * 選択中バリアントに属する観光名所の一覧(ID昇順)。名所軸を持たないバリアントは空。
   * placed=すでに都市に在る名所で、選択肢としては無効化する。
   */
  const landmarkOptions = useMemo((): { id: number; entry: LandmarkMasterEntry; placed: boolean }[] => {
    if (selectedVariant === null || !masterData?.landmarks) return [];
    return Object.entries(masterData.landmarks)
      .map(([idStr, entry]) => ({ id: Number(idStr), entry }))
      .filter(({ entry }) => entry.agent_variant === selectedVariant)
      .sort((a, b) => a.id - b.id)
      .map(({ id, entry }) => ({ id, entry, placed: placedLandmarkIds.includes(id) }));
  }, [selectedVariant, masterData, placedLandmarkIds]);

  /** 選択中の観光名所のマスター(名所ではない建物は null) */
  const selectedLandmarkData = useMemo((): LandmarkMasterEntry | null => {
    if (landmark == null || !masterData) return null;
    return masterData.landmarks?.[landmark] ?? null;
  }, [landmark, masterData]);

  /**
   * 階数の上限。バリアントの max_floors と、構造上の最大階数(木造は3階までなど。0=無制限)の
   * 小さい方。バックエンドの検証と同じ規則。
   */
  const maxFloorsAllowed = useMemo(() => {
    const variantMax = selectedVariantData?.max_floors ?? 50;
    const structureMax = selectedStructureData?.max_floors ?? 0;
    return structureMax > 0 ? Math.min(variantMax, structureMax) : variantMax;
  }, [selectedVariantData, selectedStructureData]);

  /** 直接配置は地点未固定でも有効。数値入力を位置選びと同時に操作できる。 */
  const isDirectMode = editAgentId == null && placementMode !== "parcel";
  const directRequestKey = JSON.stringify([selectedVariant, placementRevision, pickedPosition, rectWidth, rectDepth, rectAngle, floors, landmark]);
  const directPreviewReady = pickedPosition !== null && validatedDirectKey === directRequestKey && !placementCommandPending;
  /** 現在コピー建設モード(既存建物の実寸フットプリントを固定コピー)かどうか。 */
  const isCopyMode = copySourceAgentId !== null;
  /**
   * 選択中バリアントの底面が可変か。false のとき底面は固定寸法の矩形になる。
   * 観光名所は名所ごとに敷地寸法が決まっているため、常に固定扱いとする。
   */
  const isResizable = (selectedVariantData?.resizable ?? true) && selectedLandmarkData === null;
  /** 直接配置の初期底面サイズ。観光名所は名所の寸法が優先される。 */
  const defaultWidth = selectedLandmarkData?.width ?? selectedVariantData?.default_width ?? DEFAULT_BUILDING_WIDTH;
  const defaultHeight = selectedLandmarkData?.depth ?? selectedVariantData?.default_height ?? defaultWidth;

  /** 選択中の土地区画で建設可能な建物種別の表示名一覧（allowed_bd_mask から解決） */
  const allowedBuildingNames = useMemo(() => {
    if (!parcelData || !masterData) return [];
    const mask = parcelData.allowed_bd_mask ?? 0;
    if (mask === 0) return [];
    const names: string[] = [];
    for (const [bdTypeIdStr, bdTypeEntry] of Object.entries(masterData.bd_types)) {
      const bdTypeId = parseInt(bdTypeIdStr);
      if ((mask & bdTypeId) !== 0) {
        names.push(bdTypeEntry.display_name);
      }
    }
    return names;
  }, [parcelData, masterData]);

  /**
   * 選択中バリアントが要求する土地区画種別(未設定は陸地)。
   * 洋上風力発電所のような海上構造物は水面区画しか受け付けない。
   * 区画種別は完全一致でしか成立しない(バックエンド is_building_compatible_with_parcel_type と同じ規則)。
   */
  const requiredParcelType = selectedVariantData?.parcel_type ?? PARCEL_LAND;
  const requiredParcelTypeName =
    masterData?.parcel_types[requiredParcelType]?.display_name ?? String(requiredParcelType);
  /**
   * 選択済みの区画が要求種別と食い違っているか。
   * Unity は非互換な区画のクリックを無視するが、区画を選んだあとにバリアントを切り替えると
   * 選択が残ったままこの状態になるため、確定前にパネル側で弾く。
   */
  const parcelTypeMismatch = parcelData != null && parcelData.parcel_type !== requiredParcelType;

  /* ---- プレビュー取得 ---- */

  const fetchPreview = useCallback(
    async (variant: number, pId: number, f: number, cr: number, copySrc: number | null, lm: number | null, signal?: AbortSignal) => {
      const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview`;
      // コピーモードでは copy_source_agent_id を送り、元建物の実寸フットプリントを固定コピーする。
      // このとき建ぺい率(coverage_ratio)は使わず、収まり判定(valid/invalid_reason)が返る。
      // 編集モードでは編集対象自身を一点物(観光名所)の重複判定から外すため exclude_agent_id を送る。
      // これが無いと、既存の名所を開いただけで「既に存在する」と判定されプレビューが返らない。
      const body = copySrc != null
        ? { copy_source_agent_id: copySrc, parcel_id: pId, floors: f }
        : { variant, parcel_id: pId, floors: f, coverage_ratio: cr, ...(lm != null ? { landmark: lm } : {}) };
      try {
        const data = await fetchApi<BuildingPreviewData & { valid?: boolean; invalid_reason?: string | null }>(url, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ ...body, ...(editAgentId != null ? { exclude_agent_id: editAgentId } : {}) }),
          signal,
        });
        if (!signal?.aborted) {
          setEstimateError(null);
          setBuildingPreview(data);
          if (copySrc != null) {
            setCopyValid(data.valid ?? false);
            setCopyInvalidReason(data.invalid_reason ?? null);
          }
          // バックエンドから取得した形状データをUnity側に送信し、3Dプレビューを表示する
          sendEditBuildingUpdateShape(
            data.polygon,
            f,
            data.center_point,
            data.height,
          ).catch((err) => {
            console.error("[BuildingConstruction] sendEditBuildingUpdateShape failed:", err);
          });
        }
      } catch (err) {
        if (!signal?.aborted) {
          console.error("Failed to fetch building preview:", err);
          setBuildingPreview(null);
          setEstimateError(err instanceof Error ? err.message : String(err));
        }
      }
    },
    [apiBaseUrl, editAgentId]
  );

  /* ---- バリアント・土地区画・階数のいずれかが変化したらプレビューを再取得 ---- */

  useEffect(() => {
    if (selectedVariant === null || parcelId === null) return;
    const controller = new AbortController();
    void fetchPreview(selectedVariant, parcelId, floors, coverageRatio, copySourceAgentId, landmark, controller.signal);
    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, coverageRatio, copySourceAgentId, landmark, fetchPreview]);

  /* ---- 推定建設コストの取得 ---- */
  useEffect(() => {
    if (selectedVariant === null || parcelId === null) {
      setEstimatedCost(null);
      setEstimatedStandardCost(null);
      setEstimatedDurationDays(null);
      setSupplyRatio(null);
      setAvailableFunds(null);
      setExpectedDurationDays(null);
      setEstimatedMaxProduction(null);
      setEstimatedEmployment(null);
      setEstimatedOperatingCost(null);
      setOperatingWageCost(null);
      setOperatingMaterialCost(null);
      setOperatingCostCategory(null);
      setCostSimulationDays(null);
      setEstimateError(null);
      return;
    }
    const controller = new AbortController();
    setIsCostLoading(true);

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/construction-cost`;
    fetchApi<Record<string, number | null | undefined>>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        parcel_id: parcelId,
        floors,
        construction_ratio: coverageRatio,
        ...(structure != null ? { structure } : {}),
        ...(landmark != null ? { landmark } : {}),
        // コピーモードでは copy_source_agent_id を送り、忠実コピーなら割引後コストを受け取る。
        ...(copySourceAgentId != null ? { copy_source_agent_id: copySourceAgentId } : {}),
        // 編集モードでは編集対象自身を観光名所の重複判定から外す(プレビューと同じ理由)。
        ...(editAgentId != null ? { exclude_agent_id: editAgentId } : {}),
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (!controller.signal.aborted) {
          setEstimateError(null);
          setEstimatedCost(data.estimated_construction_cost ?? null);
          setEstimatedStandardCost(data.standard_construction_cost ?? null);
          setEstimatedDurationDays(data.estimated_duration_days ?? null);
          setSupplyRatio(data.construction_supply_ratio ?? null);
          setAvailableFunds(data.available_funds ?? null);
          setExpectedDurationDays(data.expected_duration_days ?? null);
          // 生産系の項目はTYPE_STANDARDのときのみ返るため、未定義なら null にリセットする。
          setEstimatedMaxProduction(data.estimated_max_production ?? null);
          setEstimatedEmployment(data.estimated_employment ?? null);
          setEstimatedOperatingCost(data.estimated_operating_cost ?? null);
          setOperatingWageCost(data.operating_wage_cost ?? null);
          setOperatingMaterialCost(data.operating_material_cost ?? null);
          setOperatingCostCategory(data.operating_cost_category ?? null);
          setCostSimulationDays(data.simulation_days ?? null);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstructionPanel] Failed to fetch construction cost:", err);
          setEstimateError(err instanceof Error ? err.message : String(err));
          setEstimatedCost(null);
          setEstimatedStandardCost(null);
          setEstimatedDurationDays(null);
          setSupplyRatio(null);
          setAvailableFunds(null);
          setExpectedDurationDays(null);
          setEstimatedMaxProduction(null);
          setEstimatedEmployment(null);
          setEstimatedOperatingCost(null);
          setOperatingWageCost(null);
          setOperatingMaterialCost(null);
          setOperatingCostCategory(null);
          setCostSimulationDays(null);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsCostLoading(false);
      });

    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, coverageRatio, copySourceAgentId, structure, landmark, apiBaseUrl, editAgentId]);

  /* ---- インフラ施設の効果範囲の取得と3D可視化 ---- */

  /**
   * インフラ供給施設(変電所・下水処理場・電波塔などサービス財を生産する施設)を選択して
   * 敷地を選んだとき、その施設を建てた場合の効果範囲をバックエンドから取得する。
   * 効果範囲は施設位置・地形・階数(電波塔の送信点高さ)で決まるため、
   * これらが変わるたびに再取得する。効果範囲を持たない variant(通常建物)には restricted:false が返る。
   * 3D への送信は既存施設の範囲と合わせて下の送信用 useEffect が一括で行う。
   */
  useEffect(() => {
    if (selectedVariant === null || parcelId === null) {
      setEffectRangeActive(false);
      setPreviewEffectRegion(null);
      return;
    }
    const controller = new AbortController();

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-effect-range`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ variant: selectedVariant, parcel_id: parcelId, floors }),
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<{
          restricted: boolean;
          region?: EffectRangeRegion | null;
        }>;
      })
      .then((data) => {
        if (controller.signal.aborted) return;
        if (data.restricted && data.region) {
          setEffectRangeActive(true);
          setPreviewEffectRegion(data.region);
        } else {
          setEffectRangeActive(false);
          setPreviewEffectRegion(null);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch effect range:", err);
          setEffectRangeActive(false);
          setPreviewEffectRegion(null);
        }
      });

    return () => controller.abort();
  }, [selectedVariant, parcelId, floors, apiBaseUrl]);

  /* ---- 既存施設の効果範囲の取得 ---- */

  /**
   * バリアントを選択したら、同じサービス財を供給する既存施設(建設中含む)の効果範囲一覧を
   * 取得する。敷地・階数には依存しないためバリアント選択時に一度だけ取得すればよい。
   * 効果範囲の制限を持たない variant には restricted:false と空の ranges が返る。
   */
  useEffect(() => {
    if (selectedVariant === null) {
      setExistingEffectRanges([]);
      return;
    }
    const controller = new AbortController();

    fetchApi<{
      restricted: boolean;
      ranges: { agent_id: number; variant: number; region: EffectRangeRegion }[];
    }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/effect-ranges?variant=${selectedVariant}`,
      { signal: controller.signal },
    )
      .then((data) => {
        if (controller.signal.aborted) return;
        setExistingEffectRanges(
          data.restricted ? data.ranges.map((r) => r.region) : [],
        );
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch existing effect ranges:", err);
          setExistingEffectRanges([]);
        }
      });

    return () => controller.abort();
  }, [selectedVariant, apiBaseUrl]);

  /* ---- 効果範囲(プレビュー+既存)の3D表示送信 ---- */

  /**
   * プレビュー領域か既存領域のいずれかが変わったら、両方をまとめて Unity へ送信する。
   * 個別に送るとプレビュー側の更新が既存側の表示を消してしまうため、送信はここに一元化する。
   * どちらも無ければクリア指示(null, null)になる。
   */
  useEffect(() => {
    void sendEditBuildingUpdateEffectRange(
      previewEffectRegion,
      existingEffectRanges.length > 0 ? existingEffectRanges : null,
    ).catch((err) => {
      console.error("[BuildingConstruction] sendEditBuildingUpdateEffectRange failed:", err);
    });
  }, [previewEffectRegion, existingEffectRanges]);

  /* ---- アンマウント時に効果範囲表示をクリア ---- */
  useEffect(() => {
    return () => {
      void sendEditBuildingUpdateEffectRange(null).catch(() => {});
    };
  }, []);

  /* ---- 直接配置モード: 底面プレビューと設置可否の取得 ---- */

  /**
   * 直接配置モード(pickedPosition が非null)で、バリアント・地点・サイズ・回転・階数の
   * いずれかが変わったら、バックエンドに矩形底面のプレビューを問い合わせる。
   * 返ってきた底面ポリゴンを Unity の3Dプレビュー(edit_building:update_shape)へ送り、
   * 2Dキャンバス用に buildingPreview へも反映する。設置可否(valid/invalid_reason)を保持する。
   * resizable=false のバリアントはバックエンドが width/depth を default_width に強制するため、
   * 返却値の実効サイズを入力欄へ反映する。
   */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-direct`;
    setValidatedDirectKey(null);
    setDirectValid(false);
    setDirectRequiresAcquisition(false);
    fetchApi<{
      polygon: number[][];
      height: number;
      center_point: number[];
      area: number;
      valid: boolean;
      invalid_reason: string | null;
      requires_acquisition?: boolean;
      acquisition?: {
        blocking_parcel_ids: number[];
        count: number;
        estimated_cost: number;
      } | null;
      interference?: PathInterferenceHighlight | null;
      width: number;
      depth: number;
      angle: number;
      resizable: boolean;
    }>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        position: pickedPosition,
        floors,
        width: rectWidth,
        depth: rectDepth,
        angle: rectAngle,
        ...(landmark != null ? { landmark } : {}),
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        setEstimateError(null);
        setBuildingPreview({
          polygon: data.polygon,
          height: data.height,
          center_point: data.center_point,
          area: data.area,
        });
        setDirectValid(data.valid);
        setValidatedDirectKey(directRequestKey);
        setDirectInvalidReason(data.invalid_reason ?? null);
        setDirectRequiresAcquisition(data.requires_acquisition === true);
        setDirectAcquisition(
          data.requires_acquisition === true && data.acquisition
            ? { count: data.acquisition.count, estimatedCost: data.acquisition.estimated_cost }
            : null,
        );
        // 重なり区画・建物を道路敷設と同じ配色(blocking=赤など)で3Dハイライトする。
        // 重なりがなければこの発信元のハイライトをクリアする。
        const interference = data.interference;
        const hasInterference =
          interference != null &&
          (interference.parcels.blocking.length > 0 ||
            interference.parcels.removable.length > 0 ||
            interference.parcels.shavable.length > 0 ||
            interference.building_ids.length > 0);
        if (hasInterference) {
          void sendHighlightObjects(interference, HIGHLIGHT_SOURCE).catch(() => {});
        } else {
          void sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
        }
        // resizable=false では要求サイズが default_width に強制されるため入力欄へ反映する
        // (無限ループを避けるため実値が異なるときだけ更新する)。
        if (Math.abs(data.width - rectWidth) > 1e-6) setRectWidth(data.width);
        if (Math.abs(data.depth - rectDepth) > 1e-6) setRectDepth(data.depth);
        // Unity の3Dプレビューへ底面形状を送る
        sendEditBuildingUpdateShape(
          data.polygon,
          floors,
          data.center_point,
          data.height,
          placementRevision,
        ).catch((err) => {
          console.error("[BuildingConstruction] sendEditBuildingUpdateShape (direct) failed:", err);
        });
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct preview:", err);
          setEstimateError(err instanceof Error ? err.message : String(err));
        }
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, landmark, apiBaseUrl, placementRevision, directRequestKey]);

  /* ---- 直接配置モード: 推定建設コストの取得 ---- */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();
    setIsCostLoading(true);

    const url = `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/construction-cost`;
    fetchApi<Record<string, number | null>>(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        variant: selectedVariant,
        position: pickedPosition,
        floors,
        width: rectWidth,
        depth: rectDepth,
        angle: rectAngle,
        ...(structure != null ? { structure } : {}),
        ...(landmark != null ? { landmark } : {}),
      }),
      signal: controller.signal,
    })
      .then((data) => {
        if (controller.signal.aborted) return;
        setEstimateError(null);
        setEstimatedCost(data.estimated_construction_cost ?? null);
        setEstimatedStandardCost(data.standard_construction_cost ?? null);
        setEstimatedDurationDays(data.estimated_duration_days ?? null);
        setSupplyRatio(data.construction_supply_ratio ?? null);
        setAvailableFunds(data.available_funds ?? null);
        setExpectedDurationDays(data.expected_duration_days ?? null);
        setEstimatedMaxProduction(data.estimated_max_production ?? null);
        setEstimatedEmployment(data.estimated_employment ?? null);
        setEstimatedOperatingCost(data.estimated_operating_cost ?? null);
        setOperatingWageCost(data.operating_wage_cost ?? null);
        setOperatingMaterialCost(data.operating_material_cost ?? null);
        setOperatingCostCategory(data.operating_cost_category ?? null);
        setCostSimulationDays(data.simulation_days ?? null);
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct construction cost:", err);
          setEstimateError(err instanceof Error ? err.message : String(err));
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsCostLoading(false);
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, structure, landmark, apiBaseUrl]);

  /* ---- 構造種別をUnityへ同期(プレビューの外観用) ---- */

  /**
   * 構造種別が変わったら Unity へ送り、キャッシュ済みの底面ポリゴンから構造に応じた
   * パーツでプレビューメッシュを再生成させる。構造を持たないバリアントは 0 を送る。
   */
  useEffect(() => {
    if (selectedVariant === null) return;
    void sendEditBuildingUpdateStructure(structure ?? 0).catch((err) => {
      console.error("[BuildingConstruction] sendEditBuildingUpdateStructure failed:", err);
    });
  }, [selectedVariant, structure]);

  /* ---- 観光名所をUnityへ同期(プレビューの外観用) ---- */

  /**
   * 観光名所が変わったら Unity へ送り、名所専用モデルでプレビューメッシュを差し替えさせる。
   * 名所軸を持たないバリアントは 0 を送る。
   */
  useEffect(() => {
    if (selectedVariant === null) return;
    void sendEditBuildingUpdateLandmark(landmark ?? 0).catch((err) => {
      console.error("[BuildingConstruction] sendEditBuildingUpdateLandmark failed:", err);
    });
  }, [selectedVariant, landmark]);

  /* ---- 直接配置モード: インフラ施設の効果範囲の取得(3D送信は共通の送信用useEffectが行う) ---- */
  useEffect(() => {
    if (selectedVariant === null || pickedPosition === null) return;
    const controller = new AbortController();

    fetchApi<{ restricted: boolean; region?: EffectRangeRegion | null }>(
      `${apiBaseUrl.replace(/\/+$/, "")}/api/v1/buildings/preview-effect-range`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ variant: selectedVariant, position: pickedPosition, floors }),
        signal: controller.signal,
      },
    )
      .then((data) => {
        if (controller.signal.aborted) return;
        if (data.restricted && data.region) {
          setEffectRangeActive(true);
          setPreviewEffectRegion(data.region);
        } else {
          setEffectRangeActive(false);
          setPreviewEffectRegion(null);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch direct effect range:", err);
          setEffectRangeActive(false);
          setPreviewEffectRegion(null);
        }
      });

    return () => controller.abort();
  }, [selectedVariant, pickedPosition, floors, rectWidth, rectDepth, rectAngle, apiBaseUrl]);

  /* ---- 配置方法と矩形はUnityの状態を購読し、パネルからは変更差分だけを送る ---- */
  useEffect(() => {
    if (selectedVariant === null || editAgentId != null) return;
    let disposed = false;
    let unlisten: (() => void) | undefined;
    const labelKeys = ["parcel", "free", "roadside", "frontage", "depth", "parcelHint", "committed", "placeHint", "snapped", "searching", "sizeHint", "fixed", "rotateHint"];
    void appWindow.listen<BuildingPlacementState>(IPC_EVENT_BUILDING_PLACEMENT_STATE, ({ payload }) => {
      if (disposed || payload.variant !== selectedVariant || payload.revision < latestPlacementRevision.current) return;
      latestPlacementRevision.current = payload.revision;
      setPlacementRevision(payload.revision);
      if (pendingPlacementRequest.current === null || payload.request_id === pendingPlacementRequest.current) {
        pendingPlacementRequest.current = null;
        setPlacementCommandPending(false);
      }
      setPlacementMode(payload.mode);
      setRectWidth(payload.width);
      setRectDepth(payload.depth);
      setRectAngle(payload.angle);
      // 同じ位置への応答ではプレビューAPIを呼び直さない。
      setPickedPosition(previous => JSON.stringify(previous) === JSON.stringify(payload.position) ? previous : payload.position);
      if (payload.mode !== "parcel" || payload.parcel_id < 0) {
        setParcelId(null);
        setParcelData(null);
      }
      if (payload.position === null && payload.parcel_id < 0) {
        setBuildingPreview(null);
        setValidatedDirectKey(null);
        setDirectValid(false);
        setDirectInvalidReason(null);
        setDirectRequiresAcquisition(false);
        setDirectAcquisition(null);
        setEstimatedCost(null);
        setEstimateError(null);
        setPreviewEffectRegion(null);
        setEffectRangeActive(false);
        void sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
      }
    }).then(fn => {
      if (disposed) { fn(); return; }
      unlisten = fn;
      // 購読完了後に状態を要求し、初期化通知の取りこぼしを防ぐ。
      return sendBuildingPlacementCommand(selectedVariant, {
        labels: Object.fromEntries(labelKeys.map(key => [key, t(`building_construction.placement.${key}`)])),
      });
    }).catch(console.error);
    return () => { disposed = true; unlisten?.(); };
  }, [appWindow, selectedVariant, editAgentId, t]);

  function changeDirectParameter(field: "width" | "depth" | "angle", value: number) {
    if (selectedVariant === null) return;
    const requestId = `${Date.now()}-${++placementRequestSequence.current}`;
    pendingPlacementRequest.current = requestId;
    setPlacementCommandPending(true);
    void sendBuildingPlacementCommand(selectedVariant, { [field]: value, request_id: requestId }).catch(err => {
      if (pendingPlacementRequest.current === requestId) {
        pendingPlacementRequest.current = null;
        setPlacementCommandPending(false);
      }
      console.error("[BuildingConstruction] Failed to change placement:", err);
    });
  }

  /* ---- バリアント選択解除時に関連状態をリセット ---- */

  useEffect(() => {
    if (selectedVariant === null) {
      setParcelId(null);
      setBuildingPreview(null);
      // 直接配置モードの状態もリセットする。
      setPickedPosition(null);
      setDirectValid(false);
      setDirectInvalidReason(null);
      setDirectRequiresAcquisition(false);
      setDirectAcquisition(null);
      setEstimateError(null);
      void sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
      setLandmark(null);
      // コピーモードの状態もリセットする。
      setCopySourceAgentId(null);
      setCopyValid(false);
      setCopyInvalidReason(null);
    }
  }, [selectedVariant]);

  /* ---- IPCイベントリスナー: 土地区画選択 (Unity → UI) ---- */

  useEffect(() => {
    if (selectedVariant === null) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{
        parcel_id: number;
        position?: number[];
        is_edit_mode: boolean;
        editing_building_id: number;
        polygon?: number[][];
      }>(IPC_EVENT_EDIT_BUILDING_SELECT_PARCEL, (event) => {
        console.log("[BuildingConstruction] edit_building.select_parcel", event.payload);
        // 区画モードに入る。直接配置モードとは排他なので pickedPosition をクリアする。
        setPickedPosition(null);
        setPlacementMode("parcel");
        setParcelId(event.payload.parcel_id);
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, selectedVariant]);

  /* ---- IPCイベントリスナー: 建設不可パーセル通知 (Unity → UI) ---- */

  /**
   * 建物種類と互換性のない土地区画がクリックされた場合、警告通知を表示する。
   * 例: 港湾建物選択中に陸地区画をクリック、通常建物選択中に沿岸区画をクリック等。
   */
  useEffect(() => {
    if (selectedVariant === null) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{ parcel_id: number }>(IPC_EVENT_EDIT_BUILDING_INVALID_PARCEL, () => {
        showNotification({
          message: t("building_construction.invalidParcel"),
          durationMs: 3000,
        }).catch((err) => {
          console.error("[BuildingConstruction] showNotification failed:", err);
        });
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, selectedVariant, t]);

  /* ---- IPCイベントリスナー: コピー元建物の選択 (Unity → UI) ---- */

  /**
   * 既存建物コピー建設のコピー元選択モードで建物が選ばれたら、その variant / floors /
   * visual_variant をパネルへシードし、コピーモードに入って区画選択フローへ遷移する。
   * バリアント未選択の状態(=コピーボタンが押せる状態)から入るため、selectedVariant で
   * ガードせず常時購読する。ただしテンプレート専用バリアント(国境検問所・観光名所)は
   * 通常プレイの建設候補ではないので、コピー元に選ばれても取り込まない。
   */
  useEffect(() => {
    if (!masterData) return;

    let unlisten: (() => void) | undefined;

    appWindow
      .listen<{
        source_agent_id: number;
        variant: number;
        floors: number;
        structure?: number;
        visual_variant?: number;
        visual_variant2?: number;
        visual_variant3?: number;
      }>(IPC_EVENT_BUILDING_CONSTRUCTION_COPY_SOURCE, (event) => {
        console.log("[BuildingConstruction] building_construction.copy_source", event.payload);
        const p = event.payload;
        const entry = masterData.agents[p.variant];
        // コピー元に選べない建物は状態を取り込まずに捨てる。取り込むとバリアント一覧の
        // 絞り込みを迂回して、確定もプレビューもできない行き止まりの選択状態になる。
        // 観光名所は都市に1基までの一点物なので、テンプレートモードでも複製できない。
        const isLandmarkVariant = Object.values(masterData.landmarks ?? {})
          .some((lm) => lm.agent_variant === p.variant);
        if (isLandmarkVariant || (entry?.template_only && !templateMode)) {
          void showNotification({
            message: t("building_construction.copySourceNotAllowed"),
            durationMs: 4000,
          });
          return;
        }
        setSelectedVariant(p.variant);
        setFloors(p.floors);
        // 構造もコピーする(元建物の構造が許可集合に無ければ既定=最上位)。
        const copiedStructure =
          p.structure != null && (entry?.structures ?? []).includes(p.structure)
            ? p.structure
            : defaultStructureOf(entry);
        setStructure(copiedStructure);
        // 観光名所は都市に1基までなのでコピーの対象外。名所軸は必ず外れた状態で始める。
        setLandmark(null);
        setCopySourceAgentId(p.source_agent_id);
        setCopyValid(false);
        setCopyInvalidReason(null);
        // 見た目を完全コピーする(visual_variant1/2/3)。以降 buildVisualVariantFields で送信される。
        if (p.visual_variant != null) setVisualVariant(p.visual_variant);
        if (p.visual_variant2 != null) setVisualVariant2(p.visual_variant2);
        if (p.visual_variant3 != null) setVisualVariant3(p.visual_variant3);
        // 区画選択フローへ遷移する(新規建設。agent_id は付けない)。
        sendChangeState("STATE_EDIT_BUILDING", {
          building_variant: p.variant,
          copy_source_agent_id: p.source_agent_id,
          agent_id: null,
          structure: copiedStructure ?? 0,
        }).catch((err) => {
          console.error("[BuildingConstruction] sendChangeState (copy) failed:", err);
        });
      })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);

    return () => { unlisten?.(); };
  }, [appWindow, masterData, templateMode, t]);

  /* ---- 土地区画データ取得 ---- */

  /**
   * 土地区画が選択されたら、バックエンドから区画の詳細情報（面積、規制値など）を取得する。
   * 取得したデータは敷地情報パネルの表示に使用する。
   */
  useEffect(() => {
    if (parcelId === null) {
      setParcelData(null);
      return;
    }
    const controller = new AbortController();
    setIsParcelLoading(true);

    fetch(`${apiBaseUrl.replace(/\/+$/, "")}/api/v1/parcels/${parcelId}`, {
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<ParcelData>;
      })
      .then((data) => {
        if (!controller.signal.aborted) {
          setParcelData(data);
        }
      })
      .catch((err) => {
        if (!controller.signal.aborted) {
          console.error("[BuildingConstruction] Failed to fetch parcel:", err);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setIsParcelLoading(false);
      });

    return () => controller.abort();
  }, [parcelId, apiBaseUrl]);

  /* ---- 建物面積計算 ---- */

  const buildingArea = useMemo(() => {
    if (!buildingPreview) return null;
    if (buildingPreview.area != null) return buildingPreview.area;
    if (!buildingPreview.polygon) return null;
    return polygonArea(buildingPreview.polygon);
  }, [buildingPreview]);

  /* ---- ハンドラ ---- */

  function handleVariantSelect(variantId: number) {
    // 建物バリアントを選択した際、そのバリアントのマスターデータに定義された最小階数を初期値として設定する。
    // これにより、1階では建てられない建物（例: 高層ビル）を選択した際にも、適切な階数でプレビューが表示される。
    const entry = masterData?.agents[variantId];
    const initialStructure = defaultStructureOf(entry);
    // 観光名所バリアントは未配置の最小IDを既定にする(全部配置済みなら最小ID。
    // 確定はバックエンドが拒否する)。名所は敷地寸法と階数を名所側が決める。
    const initialLandmark = defaultLandmarkOf(masterData?.landmarks, variantId, placedLandmarkIds);
    const landmarkEntry = initialLandmark != null ? masterData?.landmarks?.[initialLandmark] : undefined;
    const initialFloors = landmarkEntry?.floors ?? entry?.min_floors ?? 1;
    setPlacementMode("parcel");
    pendingPlacementRequest.current = null;
    setPlacementCommandPending(false);
    setSelectedVariant(variantId);
    setFloors(initialFloors);
    setStructure(initialStructure);
    setLandmark(initialLandmark);
    // 直接配置モードの底面サイズを default_width × default_height で初期化する
    // (default_height 未設定なら正方形)。
    const initialWidth = landmarkEntry?.width ?? entry?.default_width ?? DEFAULT_BUILDING_WIDTH;
    const initialDepth = landmarkEntry?.depth ?? entry?.default_height ?? initialWidth;
    setRectWidth(initialWidth);
    setRectDepth(initialDepth);
    setRectAngle(0);
    // 新規建設時の外観バリアント初期値。編集モードの場合は別途useEffectで既存値を読み込み上書きされる。
    setVisualVariant(Math.random());
    setVisualVariant2(Math.random());
    setVisualVariant3(Math.random());
    sendChangeState("STATE_EDIT_BUILDING", {
      building_variant: variantId,
      agent_id: editAgentId ?? null,
      structure: initialStructure ?? 0,
      landmark: initialLandmark ?? 0,
    }).catch((err) => {
      console.error("[BuildingConstruction] sendChangeState failed:", err);
    });
  }

  /**
   * 建設画面から建物タイルへ戻る。戻り先は選んでいた建物の分類で、既存建物の編集やコピー建設のように
   * 分類タイルを経ずに建物が決まった場合も同じ規則で決まる。
   */
  function handleVariantDeselect() {
    if (selectedVariantData) setSelectedCategory(categoryOf(selectedVariantData));
    setSelectedVariant(null);
    setStructure(null);
    setLandmark(null);
    // 外観バリアントをリセット
    setVisualVariant(null);
    setVisualVariant2(null);
    setVisualVariant3(null);
    sendChangeState("STATE_NORMAL").catch((err) => {
      console.error("[BuildingConstruction] sendChangeState failed:", err);
    });
  }

  /**
   * 構造種別の変更。構造上の最大階数(木造3階など)を超えている階数は上限へ丸める。
   */
  function handleStructureChange(value: number) {
    setStructure(value);
    const entry = masterData?.structures?.[value];
    const structureMax = entry?.max_floors ?? 0;
    if (structureMax > 0 && floors > structureMax) {
      handleFloorChange(structureMax);
    }
  }

  /**
   * 観光名所の変更。名所ごとに敷地寸法と階数が決まっているため、底面と階数も合わせて差し替える。
   */
  function handleLandmarkChange(value: number) {
    setLandmark(value);
    const entry = masterData?.landmarks?.[value];
    if (!entry) return;
    setRectWidth(entry.width);
    setRectDepth(entry.depth);
    handleFloorChange(entry.floors);
  }

  function handleFloorChange(value: number) {
    setFloors(value);
    sendEditBuildingUpdateFloors(value).catch((err) => {
      console.error("[BuildingConstruction] sendEditBuildingUpdateFloors failed:", err);
    });
  }



  /**
   * 確定失敗時のエラー通知。ApiError のエラーコードを i18n キーへマップし、
   * 対応する翻訳がなければバックエンドのメッセージをそのまま表示する。
   */
  function notifyConfirmError(err: unknown) {
    console.error("[BuildingConstruction] confirm failed:", err);
    let message = t("building_construction.confirmFailed");
    let code: string | undefined;
    if (err instanceof ApiError) {
      message = err.message;
      code = err.code;
    } else if (err instanceof Error) {
      message = err.message;
    }
    const i18nKey = code ? `errors.${code}` : null;
    const displayMessage =
      i18nKey && i18nKey !== "errors.UNKNOWN_ERROR"
        ? t(i18nKey, { defaultValue: message })
        : message;
    void showNotification({ message: displayMessage, durationMs: 5000 });
  }

  /**
   * 建設確定に成功した後の共通処理。
   * 効果範囲オーバーレイを明示的に消し(ウィンドウを閉じるとアンマウント時の
   * クリア送信が届かない可能性があるため)、Unity にセッションリロードを送って
   * 新しい建物を描画させる。
   * 先に STATE_NORMAL に戻してから表示モードを切り替える。
   * STATE_NORMAL でないと view:change_view の変更が _normalStateMemory に記憶されず、
   * 後から Rust 側が STATE_NORMAL を送った際に default に戻ってしまうため。
   * 最後に、ユーザーが建設結果を確認できるよう計画中表示をONにしてパネルを閉じる。
   */
  async function finishAfterConfirm() {
    await sendEditBuildingUpdateEffectRange(null).catch(() => {});
    await sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
    await sendSessionReload();
    await sendChangeState("STATE_NORMAL");
    await sendChangeDisplay("building", "default", null, ["plan"]);
    onClose();
  }

  /**
   * 外観バリアントのリクエストフィールドを組み立てる。
   * null(編集モードで現在値を取得できなかった場合)のフィールドは送信しない。
   * これにより、ユーザーがスライダーを動かさなければ現在の外観が維持される。
   */
  function buildVisualVariantFields(): Record<string, number> {
    return {
      ...(visualVariant != null ? { visual_variant: visualVariant } : {}),
      ...(visualVariant2 != null ? { visual_variant2: visualVariant2 } : {}),
      ...(visualVariant3 != null ? { visual_variant3: visualVariant3 } : {}),
    };
  }

  /** タグ付きで設置したら、開いている建設プロジェクトウィンドウとタグ選択に一覧を取り直させる。 */
  function notifyTagAssigned() {
    if (projectTag) void emit(TAGS_CHANGED_EVENT, { tag: projectTag }).catch(console.error);
  }

  /**
   * 区画モードの建設確定ハンドラ。UI が直接バックエンドに送信する(道路建設と同じ責務分担)。
   * 新規建設は POST /buildings、既存建物の編集は PATCH /agents/{id} を呼ぶ。
   */
  async function handleParcelConfirm() {
    if (selectedVariant === null || parcelId === null || selectedVariantData === null) return;
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");
    try {
      if (editAgentId != null) {
        await fetchApi(`${baseUrl}/api/v1/agents/${editAgentId}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            variant: selectedVariant,
            floors,
            ...(structure != null ? { structure } : {}),
            ...buildVisualVariantFields(),
          }),
        });
      } else {
        const created = await fetchApi<{ agent_id: number }>(`${baseUrl}/api/v1/buildings/`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            parcel_id: parcelId,
            type: selectedVariantData.type,
            variant: selectedVariant,
            construction_ratio: coverageRatio,
            floors,
            ...(structure != null ? { structure } : {}),
            ...(landmark != null ? { landmark } : {}),
            // コピーモードでは元建物の実寸フットプリントを固定コピーし、忠実コピーなら割引く。
            ...(copySourceAgentId != null ? { copy_source_agent_id: copySourceAgentId } : {}),
            ...(projectTag ? { tag: projectTag } : {}),
            ...buildVisualVariantFields(),
          }),
        });
        notifyTagAssigned();
        if (created && created.agent_id) {
          void invoke("open_registered_window", {
            windowType: "agent_detail",
            entityId: String(created.agent_id),
          });
        }
      }
      await finishAfterConfirm();
    } catch (err) {
      notifyConfirmError(err);
    }
  }

  /**
   * 直接配置モードの建設確定ハンドラ。UI が直接 POST /buildings/direct に送る。
   * バックエンドが矩形の土地区画を自動生成し、その全面に建物を作成する。
   *
   * acknowledgeAcquisition=true は、民間区画との重なりの自動用地買収をユーザーが
   * 確認ウィンドウで承諾した場合のみ渡す(バックエンドは承諾なしの blocking を拒否する)。
   */
  async function handleDirectConfirm(acknowledgeAcquisition = false) {
    if (selectedVariant === null || pickedPosition === null || selectedVariantData === null || !directPreviewReady) return;
    const baseUrl = apiBaseUrl.replace(/\/+$/, "");
    try {
      const created = await fetchApi<{ agent_id: number }>(`${baseUrl}/api/v1/buildings/direct`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          variant: selectedVariant,
          type: selectedVariantData.type,
          position: pickedPosition,
          floors,
          width: rectWidth,
          depth: rectDepth,
          // 回転角はマイナス・360超も入力可能なため、確定時に mod 360 で [0,360) に正規化する。
          angle: normalizeAngle(rectAngle),
          ...(structure != null ? { structure } : {}),
          ...(landmark != null ? { landmark } : {}),
          ...(acknowledgeAcquisition ? { acknowledge_acquisition: true } : {}),
          ...(projectTag ? { tag: projectTag } : {}),
          ...buildVisualVariantFields(),
        }),
      });
      notifyTagAssigned();
      if (created && created.agent_id) {
        void invoke("open_registered_window", {
          windowType: "agent_detail",
          entityId: String(created.agent_id),
        });
      }
      await finishAfterConfirm();
    } catch (err) {
      notifyConfirmError(err);
    }
  }

  // 確認ウィンドウのOKイベント購読から最新の確定処理を呼ぶための ref。
  // listen の useEffect を購読しっぱなしにしても、常に最新の state で確定できる。
  const directConfirmRef = useRef<(acknowledge: boolean) => Promise<void>>();
  directConfirmRef.current = handleDirectConfirm;

  /* ---- クロスウィンドウイベント: 用地買収の確認ウィンドウのOK ---- */

  /**
   * BuildingDirectAcquisitionConfirmWindow でユーザーが「買収して建設」を押すと
   * 発火する。直接配置モードが継続中(買収必要のプレビューが有効)の場合のみ、
   * 承諾つきで確定を実行する。
   */
  useEffect(() => {
    let unlisten: (() => void) | undefined;
    listen(EVENT_ACQUISITION_CONFIRMED, () => {
      void directConfirmRef.current?.(true);
    })
      .then((fn) => { unlisten = fn; })
      .catch(console.error);
    return () => { unlisten?.(); };
  }, []);

  /**
   * パネル全体のクリックイベントを捕捉する共通のハンドラ。
   * ボタン要素やバリアントタイルなど、ユーザーが明示的に対話する要素をクリックした際に、
   * 直後にUnity側での3Dビュー操作をシームレスに行えるよう、Unityにフォーカスを強制する。
   * 階数入力欄（NumberStepperのインプット）など、入力フォーカスを維持すべき要素をクリックした場合は
   * Unityへのフォーカス移動を行わないよう、対象を制限している。
   */
  const handlePanelClick = useCallback((e: React.MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    const button = target.closest("button, [role='button']");
    if (button) {
      sendFocusGraphic().catch(() => {});
    }
  }, []);

  /* ---- マスターデータ未取得時 ---- */

  if (!masterData) {
    return <p className="bc-muted">{t("common.loading")}</p>;
  }

  /* ---- 計算値 ---- */

  // 観光名所は名所ごとに階高が決まっている(大展望塔=75m/階など)ため、名所の値を優先する。
  const heightPerFloor =
    selectedLandmarkData?.height_per_floor ?? selectedVariantData?.height_per_floor ?? 3.5;
  const totalHeight = floors * heightPerFloor;
  const totalFloorArea = buildingArea != null ? buildingArea * floors : null;

  /* ---- レンダリング ---- */

  return (
    <div className="bc-layout" onClick={handlePanelClick}>
      {/* 分類タイル → 建物タイル。市庁舎だけを置くモードは選択が固定なので出さない。
          既存建物の編集は分類を変えられないため、分類タイルを出さず建物タイルから戻れない。 */}
      {selectedVariant === null && !cityHallOnly && (
        <div className="bc-section">
          {selectedCategory !== null && editAgentId == null && (
            <button className="bc-back-btn" type="button" onClick={() => setSelectedCategory(null)}>
              ← {t("building_construction.back")}
            </button>
          )}
          {selectedCategory === null && editAgentId != null ? (
            <p className="bc-muted">{t("common.loading")}</p>
          ) : (
            <div className="bc-tile-grid">
              {selectedCategory === null
                ? availableCategories.map((category) => (
                    <SelectionTile
                      key={category}
                      icon={resolveBdCategoryIcon(category)}
                      name={t(`building_construction.category.${category}`)}
                      onClick={() => setSelectedCategory(category)}
                    />
                  ))
                : variantsForCategory.map(([idStr, entry]) => {
                    const variantId = Number(idStr);
                    const unaffordable = isVariantUnaffordable(variantId);
                    return (
                      <SelectionTile
                        key={idStr}
                        icon={resolveAgentVariantIcon(variantId)}
                        name={entry.display_name + (unaffordable ? t("construction.variantUnaffordableSuffix") : "")}
                        disabled={unaffordable}
                        onClick={() => handleVariantSelect(variantId)}
                      />
                    );
                  })}
            </div>
          )}
          {/* 既存建物をコピー: 押すと Unity がコピー元建物の選択モードに入り、
              選ばれた建物の見た目・階数・フットプリントを取り込む。 */}
          {editAgentId == null && (
            <button
              className="bc-btn"
              type="button"
              style={{ marginTop: 10 }}
              onClick={() => {
                sendPickCopySource().catch((err) => {
                  console.error("[BuildingConstruction] sendPickCopySource failed:", err);
                });
              }}
            >
              {t("building_construction.copyExisting")}
            </button>
          )}
        </div>
      )}

      {/* 説明文 */}
      {selectedVariantData && (
        <div className="bc-section">
          {!cityHallOnly && (
            <button className="bc-back-btn" type="button" onClick={handleVariantDeselect}>
              ← {t("building_construction.back")}
            </button>
          )}
          <div className="bc-description-frame">
            <img
              src={resolveAgentIcon(
                selectedVariant!,
                selectedVariantData.type,
                selectedVariantData.building ?? false,
                selectedVariantData.bd_type ?? 0
              )}
              alt={selectedVariantData.display_name}
              className="bc-description-icon"
              draggable={false}
            />
            <div className="bc-description-body">
              <div className="bc-description-name">{selectedVariantData.display_name}</div>
              <div className="bc-description-text">
                {selectedVariantData.description || "—"}
              </div>
              <BuildingFloorSpecs
                entry={selectedVariantData}
                fixedFloors={selectedLandmarkData?.floors ?? null}
              />
            </div>
          </div>

          {/* 構造種別の選択(構造を持つバリアントのみ)。上位ほど建設費が高く、耐用年数・耐火性が上がる。 */}
          {structureOptions.length > 0 && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">{t("building_construction.structure")}</div>
                <select
                  className="bc-select"
                  value={structure ?? ""}
                  onChange={(e) => handleStructureChange(parseInt(e.target.value))}
                  disabled={editAgentId != null && parcelId === null}
                >
                  {structureOptions.map(({ id, entry }) => (
                    <option key={id} value={id}>
                      {entry?.display_name ?? `#${id}`}
                    </option>
                  ))}
                </select>
                <div className="bc-description-text" style={{ marginTop: 6 }}>
                  {selectedStructureData?.description || t("building_construction.structureNote")}
                </div>
              </div>
            </div>
          )}

          {/* 観光名所の選択(名所軸を持つバリアントのみ)。同じ名所は都市に1基までなので、
              すでに在るものは選べない。敷地寸法と階数は名所ごとに決まっている。
              既存建物の編集では表示のみ(名所の差し替えは敷地寸法ごと作り直す操作になるため)。 */}
          {landmarkOptions.length > 0 && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">{t("building_construction.landmark")}</div>
                <select
                  className="bc-select"
                  value={landmark ?? ""}
                  onChange={(e) => handleLandmarkChange(parseInt(e.target.value))}
                  disabled={editAgentId != null}
                >
                  {landmarkOptions.map(({ id, entry, placed }) => (
                    <option key={id} value={id} disabled={placed}>
                      {entry.display_name}
                      {placed ? t("building_construction.landmarkPlacedSuffix") : ""}
                    </option>
                  ))}
                </select>
                <div className="bc-description-text" style={{ marginTop: 6 }}>
                  {selectedLandmarkData?.description || t("building_construction.landmarkNote")}
                </div>
                {selectedLandmarkData && (
                  <div className="bc-description-text" style={{ marginTop: 4 }}>
                    {t("building_construction.landmarkDimensions", {
                      width: selectedLandmarkData.width,
                      depth: selectedLandmarkData.depth,
                      floors: selectedLandmarkData.floors,
                    })}
                  </div>
                )}
              </div>
            </div>
          )}

          {/* プレビュー・建設費見積の取得に失敗したときの理由表示。
              失敗するとプレビューも費用カードも出ないため、理由を画面に出して無言の故障を避ける。 */}
          {estimateError !== null && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">
                  {t("building_construction.estimateFailed")}
                </div>
                <div className="bc-description-text bc-direct-invalid">{estimateError}</div>
              </div>
            </div>
          )}

          {/* インフラ施設の効果範囲の凡例（プレビューか既存範囲のいずれかを3D表示中のみ） */}
          {(effectRangeActive || existingEffectRanges.length > 0) && (
            <div className="bc-effect-range-legend">
              <div className="bc-effect-range-legend-title">
                {t("building_construction.effectRangeTitle")}
              </div>
              {effectRangeActive && (
                <div className="bc-effect-range-legend-row">
                  <span className="bc-effect-range-swatch bc-effect-range-swatch-area" />
                  {t("building_construction.effectRangeArea")}
                </div>
              )}
              {existingEffectRanges.length > 0 && (
                <div className="bc-effect-range-legend-row">
                  <span className="bc-effect-range-swatch bc-effect-range-swatch-existing" />
                  {t("building_construction.effectRangeExisting")}
                </div>
              )}
            </div>
          )}



          {/* 直接配置モードの情報パネル(設置可否の理由表示) */}
          {isDirectMode && pickedPosition !== null && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">
                  {t("building_construction.directPlacement")}
                </div>
                <div className="bc-description-text">
                  {!directPreviewReady ? (
                    <span>{t("common.loading")}</span>
                  ) : directValid ? (
                    t("building_construction.directPlaceable")
                  ) : directRequiresAcquisition ? (
                    // 民間区画と重なるが、自動用地買収を承諾すれば確定できる状態。
                    // 確定ボタン押下で確認ウィンドウが開く。
                    <span className="bc-direct-acquisition">
                      {t("building_construction.directAcquisitionRequired", {
                        count: directAcquisition?.count ?? 0,
                      })}
                      {directAcquisition != null && (
                        <>
                          <br />
                          {t("building_construction.directAcquisitionCost", {
                            cost: Math.round(directAcquisition.estimatedCost).toLocaleString(),
                          })}
                        </>
                      )}
                    </span>
                  ) : (
                    <span className="bc-direct-invalid">
                      {directInvalidReason
                        ? t(`building_construction.directInvalid.${directInvalidReason}`, {
                            defaultValue: t("building_construction.directInvalid.generic"),
                          })
                        : t("building_construction.directInvalid.generic")}
                    </span>
                  )}
                </div>
              </div>
            </div>
          )}

          {/* コピー建設モードの情報パネル(割引・収まり可否の表示) */}
          {isCopyMode && (
            <div className="bc-description-frame" style={{ marginTop: 10 }}>
              <div className="bc-description-body">
                <div className="bc-description-name">
                  {t("building_construction.copyMode")}
                </div>
                <div className="bc-description-text">
                  {parcelId === null ? (
                    t("building_construction.copySelectParcelHint")
                  ) : copyValid ? (
                    t("building_construction.copyPlaceable")
                  ) : (
                    <span className="bc-direct-invalid">
                      {copyInvalidReason
                        ? t(`building_construction.copyInvalid.${copyInvalidReason}`, {
                            defaultValue: t("building_construction.copyInvalid.generic"),
                          })
                        : t("building_construction.copyInvalid.generic")}
                    </span>
                  )}
                </div>
              </div>
            </div>
          )}

          {/* 敷地情報パネル(区画モードのみ) */}
          {!isDirectMode && (
          <div className="bc-description-frame" style={{ marginTop: 10 }}>
            {parcelId === null ? (
              <div className="bc-description-body bc-parcel-prompt">
                <div className="bc-description-text">
                  {t("building_construction.selectParcelHint")}
                  {/* 陸地以外を要求するバリアント(海上構造物など)は、どの区画を探せばよいか事前に示す */}
                  {requiredParcelType !== PARCEL_LAND && (
                    <>
                      <br />
                      {t("building_construction.parcelTypeRequired", { type: requiredParcelTypeName })}
                    </>
                  )}
                </div>
              </div>
            ) : (
              <>
                <img
                  src={parcelData ? resolveParcelIcon(parcelData.parcel_type) : resolveParcelIcon(0)}
                  alt=""
                  className="bc-description-icon"
                  draggable={false}
                />
                <div className="bc-description-body">
                  <div className="bc-description-name">
                    {t("building_construction.parcelSelected", { id: parcelId })}
                  </div>
                  <div className="bc-description-text">
                    {isParcelLoading || !parcelData ? (
                      t("common.loading")
                    ) : (
                      <>
                        {t("building_construction.landArea")}: {parcelData.area.toFixed(1)}{t("building_construction.unitSqm")}
                        <br />
                        {parcelData.limit_height != null && (
                          <>{t("building_construction.heightLimit")}: {parcelData.limit_height.toFixed(1)}{t("building_construction.unitMeter")}<br /></>
                        )}
                        {parcelData.limit_coverage_ratio != null && (
                          <>{t("building_construction.coverageRatioLimit")}: {Math.round(parcelData.limit_coverage_ratio * 100)}%<br /></>
                        )}
                        {parcelData.limit_volume_ratio != null && (
                          <>{t("building_construction.volumeRatioLimit")}: {Math.round(parcelData.limit_volume_ratio * 100)}%<br /></>
                        )}
                        {allowedBuildingNames.length > 0 && (
                          <>{t("building_construction.allowedBuildings")}: {allowedBuildingNames.join(", ")}</>
                        )}
                        {parcelTypeMismatch && (
                          <>
                            <br />
                            <span className="bc-direct-invalid">
                              {t("building_construction.parcelTypeRequired", { type: requiredParcelTypeName })}
                            </span>
                          </>
                        )}
                      </>
                    )}
                  </div>
                </div>
              </>
            )}
          </div>
          )}
        </div>
      )}

      {/* 階数・建ぺい率設定 (左右2カラム) — 区画選択後に表示 */}
      {selectedVariantData && parcelId !== null && (
        <div className="bc-section">
          <div className="bc-split-row">
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.floorCount")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={floors}
                  onChange={handleFloorChange}
                  min={selectedVariantData.min_floors ?? 1}
                  max={maxFloorsAllowed}
                  step={1}
                  integerOnly={true}
                  disabled={selectedLandmarkData !== null}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitFloors")}</span>
              </div>
            </div>
            {/* 建ぺい率は底面可変(resizable)の建物のみ。固定底面の建物・コピーモードでは非表示。
                コピーモードでは元建物の実寸フットプリントを固定コピーするため建ぺい率は使わない。 */}
            {isResizable && !isCopyMode && (
              <div className="bc-split-col">
                <div className="bc-section-title">{t("building_construction.coverageRatio")}</div>
                <div className="bc-stepper-row">
                  <NumberStepper
                    value={Math.round(coverageRatio * 100)}
                    onChange={(v) => setCoverageRatio(v / 100)}
                    min={10}
                    max={100}
                    step={5}
                    integerOnly={true}
                  />
                  <span className="bc-stepper-unit">%</span>
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {/* 建設プロジェクトのタグ。新設する建物に付けて作成する(既存建物は詳細パネルで付け替える)。
          市庁舎は設置と同時に竣工してタグを付けられないので出さない。 */}
      {selectedVariantData && editAgentId == null && selectedVariant !== VARIANT_CITY && (
        <div className="bc-section">
          <div className="bc-section-title">{t("tag.label")}</div>
          <TagPicker apiBaseUrl={apiBaseUrl} value={projectTag} onChange={setProjectTag} />
        </div>
      )}

      {selectedVariantData && editAgentId == null && (
        <div className="bc-section">
          <div className="bc-section-title">{t(`building_construction.placement.${placementMode}`)}</div>
          <div className="bc-muted">
            {t("building_construction.placement.menuHint")}
            {isDirectMode && <><br />{t(isResizable ? "building_construction.placement.sizeHint" : "building_construction.placement.fixed")}</>}
            {isDirectMode && pickedPosition === null && <><br />{t("building_construction.placement.placeHint")}</>}
            {placementMode === "roadside" && <><br />{t("building_construction.placement.roadHint")}</>}
          </div>
        </div>
      )}

      {/* 直接配置モードの設定: 階数 + 間口/奥行き/回転角 — 地点選択前から表示。
          底面は矩形そのものなので建ぺい率は無い。resizable=false のバリアントは
          横幅・縦幅を固定(入力不可)とし、回転のみ編集できる。 */}
      {selectedVariantData && isDirectMode && (
        <div className="bc-section">
          <div className="bc-split-row">
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.floorCount")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={floors}
                  onChange={handleFloorChange}
                  min={selectedVariantData.min_floors ?? 1}
                  max={maxFloorsAllowed}
                  step={1}
                  integerOnly={true}
                  disabled={selectedLandmarkData !== null}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitFloors")}</span>
              </div>
            </div>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.rotation")}</div>
              <div className="bc-stepper-row">
                {/* 回転角はマイナス・360超も許容(クランプしない)。確定時に mod 360 で正規化する。 */}
                <NumberStepper
                  value={rectAngle}
                  onChange={value => changeDirectParameter("angle", value)}
                  disabled={placementMode === "roadside"}
                  step={5}
                  integerOnly={false}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitDegree")}</span>
              </div>
            </div>
          </div>
          <div className="bc-split-row" style={{ marginTop: 10 }}>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.placement.frontage")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={rectWidth}
                  onChange={value => changeDirectParameter("width", value)}
                  min={1}
                  max={500}
                  step={1}
                  integerOnly={false}
                  disabled={!isResizable}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitMeter")}</span>
              </div>
            </div>
            <div className="bc-split-col">
              <div className="bc-section-title">{t("building_construction.placement.depth")}</div>
              <div className="bc-stepper-row">
                <NumberStepper
                  value={rectDepth}
                  onChange={value => changeDirectParameter("depth", value)}
                  min={1}
                  max={500}
                  step={1}
                  integerOnly={false}
                  disabled={!isResizable}
                />
                <span className="bc-stepper-unit">{t("building_construction.unitMeter")}</span>
              </div>
            </div>
          </div>
          {!isResizable && (
            <div className="bc-muted" style={{ marginTop: 6 }}>
              {t("building_construction.fixedFootprintNote", { width: defaultWidth, height: defaultHeight })}
            </div>
          )}
        </div>
      )}

      {/* 建設情報カード(全 type 共通。詳細パネルと同じ共通カードを使う) */}
      {buildingPreview && (
        <div className="bc-section">
          <ConstructionInfoCard
            title={t("construction.info")}
            buildingArea={buildingArea}
            totalHeight={totalHeight}
            totalFloorArea={totalFloorArea}
            estimatedCost={estimatedCost}
            standardCost={estimatedStandardCost}
            costRatioHelp={t("building_construction.costRatioHelp")}
            estimatedDurationDays={estimatedDurationDays}
            isCostLoading={isCostLoading}
            supplyRatio={supplyRatio}
            expectedDurationDays={expectedDurationDays}
            availableFunds={availableFunds}
            requiredTotal={requiredTotal}
          />
        </div>
      )}

      {/* 施設情報カード(type ごとに内容が異なる。中身が無い type ではカード自体を出さない) */}
      {buildingPreview && (
        <BuildingFacilityInfoCard
          variantData={selectedVariantData}
          estimatedMaxProduction={estimatedMaxProduction}
          estimatedEmployment={estimatedEmployment}
          estimatedOperatingCost={estimatedOperatingCost}
          operatingWageCost={operatingWageCost}
          operatingMaterialCost={operatingMaterialCost}
          operatingCostCategory={operatingCostCategory}
          simulationDays={costSimulationDays}
          isCostLoading={isCostLoading}
        />
      )}



      {/* アクションボタン (パネル下部に固定) */}
      {selectedVariant && (
        <div className="bc-section bc-action-bar">
          <div className="bc-actions">
            <button
              className="bc-btn"
              type="button"
              onClick={() => {
                // 仮決定した設置位置(区画モードの選択区画/直接配置モードのクリック地点)だけを取り消す。
                // バリアント選択・外観バリアントは維持し、続けて別の位置を選び直せるようにする。
                setParcelId(null);
                setParcelData(null);
                setPickedPosition(null);
                setBuildingPreview(null);
                setDirectValid(false);
                setDirectInvalidReason(null);
                setDirectRequiresAcquisition(false);
                setDirectAcquisition(null);
                setEstimateError(null);
                void sendClearHighlights(HIGHLIGHT_SOURCE).catch(() => {});
                if (editAgentId == null) {
                  void sendBuildingPlacementCommand(selectedVariant, { reset: true }).catch(console.error);
                  return;
                }
                sendChangeState("STATE_EDIT_BUILDING", {
                  building_variant: selectedVariant,
                  agent_id: editAgentId ?? null,
                  structure: structure ?? 0,
                  landmark: landmark ?? 0,
                }).catch((err) => {
                  console.error("[BuildingConstruction] sendChangeState (cancel) failed:", err);
                });
              }}
            >
              {t("building_construction.cancel")}
            </button>
            <button
              className="bc-btn bc-btn-confirm"
              // 区画モードは区画選択済み、直接配置モードは設置可能(directValid)、または
              // 民間区画の自動買収で解消できる(directRequiresAcquisition)なら確定できる。
              // コピーモードは区画選択済みかつフットプリントが収まる(copyValid)なら確定できる。
              // 資金不足(建設費+用地買収見積 > 利用可能額)は確定させない。バックエンドも同じ基準で拒否する。
              // 区画種別の不一致(海上構造物を陸の区画に置くなど)もバックエンドが拒否するのでここで止める。
              disabled={
                !isAffordable
                || (isDirectMode
                  ? !directPreviewReady || !(directValid || directRequiresAcquisition)
                  : isCopyMode
                    ? parcelId === null || !copyValid || parcelTypeMismatch
                    : parcelId === null || parcelTypeMismatch)
              }
              type="button"
              onClick={async () => {
                if (isDirectMode) {
                  if (directRequiresAcquisition) {
                    // 民間区画の自動買収が必要な位置は、即確定せず確認ウィンドウを挟む。
                    // OK時に "building_direct:acquisition_confirmed" が飛んできて
                    // 承諾つき確定(handleDirectConfirm(true))が実行される。
                    void invoke("open_registered_window", {
                      windowType: "building_direct_acquisition",
                      params: {
                        extra: {
                          pendingAcquisition: {
                            blockingCount: directAcquisition?.count ?? 0,
                            estimatedCost: directAcquisition?.estimatedCost ?? 0,
                          },
                        },
                      },
                    });
                  } else {
                    await handleDirectConfirm();
                  }
                } else {
                  await handleParcelConfirm();
                }
              }}
            >
              {t(relocateCityHall ? "building_construction.relocateConfirm" : "building_construction.confirm")}
            </button>
          </div>
        </div>
      )}

    </div>
  );
}

/** 分類・建物の選択タイル。アイコン画像が無いものは枠だけ空けておく。 */
function SelectionTile({ icon, name, disabled = false, onClick }: {
  icon: string;
  name: string;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <button className="bc-tile" type="button" disabled={disabled} onClick={onClick}>
      <span className="bc-tile-icon">{icon && <img src={icon} alt="" draggable={false} />}</span>
      <span className="bc-tile-name">{name}</span>
    </button>
  );
}

/* ---- ユーティリティ ---- */

/** 建設パネルでの分類。マスターで bd_category が空欄の建物は「その他」に並べる。 */
function categoryOf(entry: AgentMasterEntry): number {
  return entry.bd_category ?? BD_CATEGORY_OTHER;
}

/**
 * バリアントの既定の構造種別(許可構造の最上位=最も耐火・長寿命)。構造を持たないバリアントは null。
 * バックエンドの resolve_structure(未指定時の既定)と同じ規則。
 */
function defaultStructureOf(entry: AgentMasterEntry | undefined | null): number | null {
  const ids = entry?.structures ?? [];
  if (ids.length === 0) return null;
  return Math.max(...ids);
}

/**
 * バリアントの既定の観光名所(そのバリアントに属する名所のうち、まだ都市に無い最小ID)。
 * 名所軸を持たないバリアントは null。全て配置済みなら最小IDを返し、確定はバックエンドが拒否する。
 */
function defaultLandmarkOf(
  landmarks: Record<number, LandmarkMasterEntry> | undefined,
  variantId: number,
  placedIds: number[],
): number | null {
  const ids = Object.entries(landmarks ?? {})
    .filter(([, entry]) => entry.agent_variant === variantId)
    .map(([idStr]) => Number(idStr))
    .sort((a, b) => a - b);
  if (ids.length === 0) return null;
  return ids.find((id) => !placedIds.includes(id)) ?? ids[0];
}

/** Shoelace formula で多角形の面積を計算 */
function polygonArea(polygon: number[][]): number {
  let area = 0;
  const n = polygon.length;
  for (let i = 0; i < n; i++) {
    const j = (i + 1) % n;
    area += polygon[i][0] * polygon[j][1];
    area -= polygon[j][0] * polygon[i][1];
  }
  return Math.abs(area) / 2;
}
