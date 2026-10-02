/**
 * 市民詳細パネル
 *
 * window_type="citizen_detail" のポップアップウィンドウで表示する専用パネル。
 * 世帯詳細パネル(AgentDetailPanel の HouseholdInfoPanel)で世帯構成員(CitizenLink)をクリックしたとき、
 * 一時停止中の3Dビューで車・歩行者をクリックしたとき(Unity が市民を抽選して world:select_object で開く)、
 * 追跡リストから開いたときに、この市民詳細ウィンドウが開く。
 *
 * 見出しの下の操作ボタン:
 *   - カメラ: Unity でこの市民の車・歩行者を追いかける(もう一度押すとやめる)。状態は `citizen:follow_state` に追従する
 *   - 追跡: 追跡リスト(セーブに残る)に加える・外す
 *
 * 「基本」タブは API /api/v1/citizens/{id} (endpoints.yaml の single_citizen スキーマ)のフィールドで構成する。
 *   - 基本情報: 年齢・性別・配偶関係・所属世帯
 *   - 就労・教育: 勤務先・通学先・専攻スキル・高等教育修了状況
 *   - スキル: 6種のスキル値をレーダーチャートで可視化
 *   - 治安: 犯罪性向
 * 「行動」タブは訪問先と自宅からの経路(GET /api/v1/citizens/{id}/itinerary。`CitizenActivityPanel`)。
 *
 * 所属世帯・勤務先・通学先のリンクは、現在のパネル内ナビゲーションではなく、
 * 別の経済主体詳細ウィンドウを新規に開く(SearchWindow 等と同じ open_registered_window)。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import {
  Radar,
  RadarChart,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  ResponsiveContainer,
} from "recharts";
import type { CitizenData, CitizenResponse } from "../../types";
import { buildApiUrl, fetchApi } from "../../utils/api";
import { MEMBER_MARRIED } from "../../constants";
import { useMasterData } from "../../contexts/MasterDataContext";
import { InfoHeader } from "../common/InfoHeader";
import { sendCitizenFollow, sendCitizenUnfollow, sendFocusGraphic } from "../../utils/ipc";
import {
  TRACKED_CITIZENS_CHANGED_EVENT,
  fetchItinerary,
  fetchTrackedCitizens,
  trackCitizen,
  untrackCitizen,
  useFollowedCitizen,
  type CitizenItinerary,
} from "../../utils/citizenTracking";
import { CitizenActivityPanel } from "./CitizenActivityPanel";
import { CameraIcon, TrackIcon } from "./CitizenTrackingIcons";
import citizenIcon from "../../assets/images/citizen.png";
import "../agent/AgentDetailPanel.css";
import "../agent/AgentOperationPanel.css";
import "./CitizenDetailPanel.css";

/** このウィンドウのウィンドウタイプ。Unityへ送るIPCの送信元ラベルにも使う。 */
const WINDOW_TYPE = "citizen_detail";

/**
 * 高スキル境界。バックエンドの HIGH_SKILL_BOUNDARY と同じ値。
 * いずれかのスキルがこの値以上の市民は高等教育修了相当（高度人材）とみなす。
 */
const HIGH_SKILL_BOUNDARY = 1.5;

/**
 * スキル値の表示上限。
 * バックエンドのスキル値は基礎教育で MAX_BASE_SKILL(1.0)、
 * 高等教育で HIGH_SKILL_BOUNDARY(1.5) を上限として成長するため、
 * レーダーチャートの半径軸は最大値を 1.5 に固定して常に同じ尺度で比較できるようにする。
 */
const SKILL_DISPLAY_MAX = HIGH_SKILL_BOUNDARY;

type CitizenDetailPanelProps = {
  apiBaseUrl: string;
  pathTemplate: string;
  initialEntityId: string;
};

/**
 * 経済主体名のクリッカブルリンク。
 * クリックすると対象の経済主体詳細ウィンドウを新規に開く。
 * 名前が未設定(無職・未就学などで参照先が存在しない)場合はプレースホルダを表示する。
 */
function AgentWindowLink({
  agentName,
  agentId,
  placeholder,
}: {
  agentName: string | null | undefined;
  agentId: number;
  placeholder: string;
}) {
  if (!agentName) {
    return <span className="agent-detail-muted">{placeholder}</span>;
  }

  function handleClick() {
    void invoke("open_registered_window", {
      windowType: "agent_detail",
      entityId: String(agentId),
    });
  }

  return (
    <button className="entity-link" onClick={handleClick} type="button">
      {agentName}
    </button>
  );
}

export function CitizenDetailPanel({
  apiBaseUrl,
  pathTemplate,
  initialEntityId,
}: CitizenDetailPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();
  const [citizenData, setCitizenData] = useState<CitizenData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<"basic" | "activity">("basic");
  const [itinerary, setItinerary] = useState<CitizenItinerary | null>(null);
  const [itineraryFailed, setItineraryFailed] = useState(false);
  const [tracked, setTracked] = useState(false);
  const followed = useFollowedCitizen();
  const citizenId = Number(initialEntityId);

  const url = buildApiUrl(apiBaseUrl, pathTemplate, initialEntityId);

  const loadItinerary = useCallback(async (signal?: AbortSignal) => {
    try {
      const result = await fetchItinerary(apiBaseUrl, citizenId, signal);
      if (signal?.aborted) return;
      setItinerary(result);
      setItineraryFailed(false);
    } catch (err) {
      if (!signal?.aborted) {
        console.error("failed to load citizen itinerary", err);
        setItineraryFailed(true);
      }
    }
  }, [apiBaseUrl, citizenId]);

  const loadTracked = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = await fetchTrackedCitizens(apiBaseUrl, signal);
      if (!signal?.aborted) setTracked(list.some((row) => row.id === citizenId));
    } catch (err) {
      if (!signal?.aborted) console.error("failed to load tracked citizens", err);
    }
  }, [apiBaseUrl, citizenId]);

  useEffect(() => {
    if (!apiBaseUrl || !Number.isFinite(citizenId)) return;
    const controller = new AbortController();
    setItinerary(null);
    void loadItinerary(controller.signal);
    void loadTracked(controller.signal);
    return () => controller.abort();
  }, [apiBaseUrl, citizenId, loadItinerary, loadTracked]);

  // 他のウィンドウでの追跡の変更と、エポックの実行(経路と買い物先が変わる)に追従する。
  useEffect(() => {
    const unlistenTracked = listen(TRACKED_CITIZENS_CHANGED_EVENT, () => void loadTracked());
    const unlistenEpoch = listen<{ executing: boolean }>("epoch:executing", (event) => {
      if (!event.payload.executing) void loadItinerary();
    });
    return () => {
      void unlistenTracked.then((fn) => fn());
      void unlistenEpoch.then((fn) => fn());
    };
  }, [loadItinerary, loadTracked]);

  async function toggleFollow() {
    try {
      if (followed === citizenId) {
        await sendCitizenUnfollow(WINDOW_TYPE, citizenId);
      } else {
        await sendCitizenFollow(WINDOW_TYPE, citizenId);
        // Esc で終えたりホイールで寄ったりできるよう、3Dビューへキー入力を移す。
        await sendFocusGraphic();
      }
    } catch (err) {
      console.error("failed to toggle citizen follow", err);
    }
  }

  async function toggleTracked() {
    try {
      if (tracked) await untrackCitizen(apiBaseUrl, citizenId);
      else await trackCitizen(apiBaseUrl, citizenId);
      setTracked(!tracked);
      await emit(TRACKED_CITIZENS_CHANGED_EVENT, { citizen_id: citizenId });
    } catch (err) {
      console.error("failed to toggle tracked citizen", err);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchApi<CitizenResponse>(url, { signal: controller.signal })
      .then((response) => {
        if (!controller.signal.aborted) {
          setCitizenData(response.citizen);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          const message = err instanceof Error ? err.message : "Unknown error";
          setError(message);
          setLoading(false);
        }
      });

    return () => {
      controller.abort();
    };
  }, [url]);

  if (loading || !masterData) {
    return <p className="agent-detail-muted">{t("common.loading")}</p>;
  }

  if (error || !citizenData) {
    return (
      <div className="agent-detail-error">
        <strong>{t("common.fetchError")}</strong>
        <p>{error}</p>
      </div>
    );
  }

  // 性別・配偶関係の表示文字列。バックエンドの gender は 0=男性/1=女性、
  // citizen_type は MEMBER_SINGLE(独身)/MEMBER_MARRIED(既婚)。
  const genderLabel =
    citizenData.gender === 0 ? t("citizen.male") : t("citizen.female");
  const maritalLabel =
    citizenData.citizen_type === MEMBER_MARRIED
      ? t("citizen.married")
      : t("citizen.single");

  // 高等教育の専攻スキル。通学中かつ有効なスキルIDを持つ場合のみ表示する
  // (未就学・基礎教育中は education_skill が VALUE_NONE となり、マスターに該当しない)。
  const majorSkill = masterData.skills[citizenData.education_skill];

  // 高等教育修了はスキル基準で判定する（いずれかのスキルが高スキル境界以上）。
  const isHigherEducated = Object.values(citizenData.skills ?? {}).some(
    (value) => value >= HIGH_SKILL_BOUNDARY,
  );

  // スキルのレーダーチャート用データ。スキルIDの昇順で並べ、
  // 表示名はマスターデータから解決する(未定義IDはIDをそのまま表示)。
  const skillChartData = Object.entries(citizenData.skills ?? {})
    .map(([skillId, value]) => ({
      id: Number(skillId),
      subject: masterData.skills[Number(skillId)]?.display_name ?? skillId,
      value,
    }))
    .sort((a, b) => a.id - b.id);

  return (
    <div className="agent-detail">
      <InfoHeader
        iconSrc={citizenIcon}
        showStatus={false}
        variantName={maritalLabel}
        name={citizenData.name}
      />

      <div className="operation-panel">
        <button
          className={`operation-btn${followed === citizenId ? " operation-btn--active" : ""}`}
          type="button"
          title={t(followed === citizenId ? "citizenTracking.unfollow" : "citizenTracking.follow")}
          aria-pressed={followed === citizenId}
          onClick={() => void toggleFollow()}
        >
          <CameraIcon />
        </button>
        <button
          className={`operation-btn${tracked ? " operation-btn--active" : ""}`}
          type="button"
          title={t(tracked ? "citizenTracking.untrack" : "citizenTracking.track")}
          aria-pressed={tracked}
          onClick={() => void toggleTracked()}
        >
          <TrackIcon />
        </button>
      </div>

      <div className="agent-detail-tabs">
        {(["basic", "activity"] as const).map((tab) => (
          <button
            key={tab}
            className={`agent-detail-tab${tab === activeTab ? " agent-detail-tab--active" : ""}`}
            type="button"
            onClick={() => setActiveTab(tab)}
          >
            {t(tab === "basic" ? "citizenTracking.tabBasic" : "citizenTracking.tabActivity")}
          </button>
        ))}
      </div>

      <div className="agent-detail-tab-content">
        {activeTab === "activity" ? (
          <CitizenActivityPanel itinerary={itinerary} failed={itineraryFailed} />
        ) : (
          <>
            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">
                {t("citizen.basicInfo")}
              </h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("citizen.age")}</span>
                <span className="agent-detail-value">
                  {t("citizen.ageValue", { age: citizenData.age })}
                </span>

                <span className="agent-detail-label">{t("citizen.gender")}</span>
                <span className="agent-detail-value">{genderLabel}</span>

                <span className="agent-detail-label">
                  {t("citizen.maritalStatus")}
                </span>
                <span className="agent-detail-value">{maritalLabel}</span>

                <span className="agent-detail-label">{t("citizen.household")}</span>
                <span className="agent-detail-value">
                  <AgentWindowLink
                    agentName={citizenData.household_agent_name}
                    agentId={citizenData.household_agent_id}
                    placeholder="-"
                  />
                </span>
              </div>
            </section>

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">
                {t("citizen.workEducation")}
              </h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("citizen.employer")}</span>
                <span className="agent-detail-value">
                  <AgentWindowLink
                    agentName={citizenData.employer_agent_name}
                    agentId={citizenData.employer_agent_id}
                    placeholder={t("citizen.unemployed")}
                  />
                </span>

                {citizenData.employer_agent_id > 0 && (
                  <>
                    <span className="agent-detail-label">{t("citizen.commuteTime")}</span>
                    <span className="agent-detail-value citizen-commute" style={{ display: "flex", alignItems: "center", gap: "8px" }}>
                      <div 
                        className="citizen-commute-meter-container" 
                        style={{
                          width: "120px",
                          height: "12px",
                          backgroundColor: "rgba(255, 255, 255, 0.1)",
                          borderRadius: "6px",
                          overflow: "hidden",
                          border: "1px solid rgba(255, 255, 255, 0.2)",
                          position: "relative"
                        }}
                        title={`${citizenData.commute_time.toFixed(1)} mins`}
                      >
                        <div 
                          className="citizen-commute-meter-fill"
                          style={{
                            width: `${Math.min(100, (citizenData.commute_time / 120) * 100)}%`,
                            height: "100%",
                            backgroundColor: `hsl(${Math.max(0, 120 - (citizenData.commute_time / 120) * 120)}, 80%, 50%)`,
                            borderRadius: "inherit",
                            transition: "width 0.3s ease, background-color 0.3s ease"
                          }}
                        />
                      </div>
                      <span style={{ fontSize: "1.11rem", color: "#c5d4e3" }}>
                        {`${citizenData.commute_time.toFixed(1)} ${t("common.minutes")}`}
                      </span>
                    </span>
                  </>
                )}

                <span className="agent-detail-label">{t("citizen.school")}</span>
                <span className="agent-detail-value">
                  <AgentWindowLink
                    agentName={citizenData.school_agent_name}
                    agentId={citizenData.school_agent_id}
                    placeholder={t("citizen.notEnrolled")}
                  />
                </span>

                {majorSkill && (
                  <>
                    <span className="agent-detail-label">{t("citizen.major")}</span>
                    <span className="agent-detail-value">
                      {majorSkill.display_name}
                    </span>
                  </>
                )}

                <span className="agent-detail-label">
                  {t("citizen.higherEducation")}
                </span>
                <span className="agent-detail-value">
                  {isHigherEducated
                    ? t("citizen.graduated")
                    : t("citizen.notGraduated")}
                </span>
              </div>
            </section>

            {skillChartData.length > 0 && (
              <section className="agent-detail-section">
                <h3 className="agent-detail-section-title">{t("citizen.skills")}</h3>
                <div className="citizen-skill-chart">
                  <ResponsiveContainer width="100%" height={260}>
                    <RadarChart
                      cx="50%"
                      cy="50%"
                      outerRadius="75%"
                      data={skillChartData}
                    >
                      <PolarGrid />
                      <PolarAngleAxis
                        dataKey="subject"
                        tick={{ fontSize: "1.02rem", fill: "#c5d4e3" }}
                      />
                      <PolarRadiusAxis
                        angle={30}
                        domain={[0, SKILL_DISPLAY_MAX]}
                        tick={false}
                        axisLine={false}
                      />
                      <Radar
                        name={t("citizen.skills")}
                        dataKey="value"
                        stroke="#4ea1ff"
                        fill="#4ea1ff"
                        fillOpacity={0.6}
                      />
                    </RadarChart>
                  </ResponsiveContainer>
                </div>
              </section>
            )}

            <section className="agent-detail-section">
              <h3 className="agent-detail-section-title">
                {t("citizen.publicSafety")}
              </h3>
              <div className="agent-detail-grid">
                <span className="agent-detail-label">{t("citizen.criminality")}</span>
                <span className="agent-detail-value citizen-criminality">
                  <meter
                    className="citizen-criminality-meter"
                    value={citizenData.potential_criminality}
                    min={0}
                    max={1}
                  />
                  {`${(citizenData.potential_criminality * 100).toFixed(1)}%`}
                </span>
              </div>
            </section>
          </>
        )}
      </div>
    </div>
  );
}
