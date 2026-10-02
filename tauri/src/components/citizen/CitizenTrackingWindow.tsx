/**
 * 追跡リストウィンドウ
 *
 * 市民パネルの「追跡」で加えた市民の一覧(GET /api/v1/citizens/tracked。追加した順で、セーブに残る)。
 * 入口はコックピットの左端のアイコン。各行から市民パネルを開き、カメラで追いかけ、一覧から外せる。
 * 亡くなった市民などはコアが一覧に出さない。
 *
 * 他のウィンドウでの追加・解除(`tracking:changed`)と、エポックの実行(年齢・勤務先が変わる)に追従して取り直す。
 */

import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";

import { CitizenLink } from "../common/CitizenLink";
import { sendCitizenFollow, sendCitizenUnfollow, sendFocusGraphic } from "../../utils/ipc";
import {
  TRACKED_CITIZENS_CHANGED_EVENT,
  fetchTrackedCitizens,
  untrackCitizen,
  useFollowedCitizen,
  type TrackedCitizen,
} from "../../utils/citizenTracking";
import { CameraIcon, RemoveIcon } from "./CitizenTrackingIcons";
import "../agent/AgentOperationPanel.css";
import "./CitizenTrackingWindow.css";

/** このウィンドウのウィンドウタイプ。Unityへ送るIPCの送信元ラベルにも使う。 */
const WINDOW_TYPE = "citizen_tracking";

type CitizenTrackingWindowProps = {
  apiBaseUrl: string;
};

export function CitizenTrackingWindow({ apiBaseUrl }: CitizenTrackingWindowProps) {
  const { t } = useTranslation();
  const [citizens, setCitizens] = useState<TrackedCitizen[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const followed = useFollowedCitizen();

  const reload = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = await fetchTrackedCitizens(apiBaseUrl, signal);
      if (signal?.aborted) return;
      setCitizens(list);
      setLoadFailed(false);
    } catch (err) {
      if (!signal?.aborted) {
        console.error("failed to load tracked citizens", err);
        setLoadFailed(true);
      }
    }
  }, [apiBaseUrl]);

  useEffect(() => {
    if (!apiBaseUrl) return;
    const controller = new AbortController();
    void reload(controller.signal);
    return () => controller.abort();
  }, [apiBaseUrl, reload]);

  useEffect(() => {
    const unlistenTracked = listen(TRACKED_CITIZENS_CHANGED_EVENT, () => void reload());
    const unlistenEpoch = listen<{ executing: boolean }>("epoch:executing", (event) => {
      if (!event.payload.executing) void reload();
    });
    return () => {
      void unlistenTracked.then((fn) => fn());
      void unlistenEpoch.then((fn) => fn());
    };
  }, [reload]);

  async function toggleFollow(citizenId: number) {
    try {
      if (followed === citizenId) {
        await sendCitizenUnfollow(WINDOW_TYPE, citizenId);
      } else {
        await sendCitizenFollow(WINDOW_TYPE, citizenId);
        await sendFocusGraphic();
      }
    } catch (err) {
      console.error("failed to toggle citizen follow", err);
    }
  }

  async function remove(citizenId: number) {
    try {
      await untrackCitizen(apiBaseUrl, citizenId);
      await emit(TRACKED_CITIZENS_CHANGED_EVENT, { citizen_id: citizenId });
      await reload();
    } catch (err) {
      console.error("failed to untrack citizen", err);
    }
  }

  function openAgent(agentId: number) {
    void invoke("open_registered_window", { windowType: "agent_detail", entityId: String(agentId) });
  }

  if (loadFailed) return <p className="agent-detail-muted">{t("common.fetchError")}</p>;
  if (!citizens) return <p className="agent-detail-muted">{t("common.loading")}</p>;
  if (citizens.length === 0) return <p className="agent-detail-muted">{t("citizenTracking.empty")}</p>;

  return (
    <ul className="citizen-tracking-list">
      {citizens.map((citizen) => {
        const workplaceId = citizen.employer_agent_id ?? citizen.school_agent_id;
        const workplaceName = citizen.employer_agent_name ?? citizen.school_agent_name;
        const following = followed === citizen.id;
        return (
          <li key={citizen.id} className={`citizen-tracking-row${following ? " citizen-tracking-row--following" : ""}`}>
            <div className="citizen-tracking-main">
              <CitizenLink citizenId={citizen.id} citizenName={citizen.name} />
              <span className="citizen-tracking-meta">
                {t("citizen.ageValue", { age: citizen.age })}
                {citizen.zone_name ? ` · ${citizen.zone_name}` : ""}
              </span>
              <span className="citizen-tracking-meta">
                {workplaceId && workplaceName ? (
                  <button className="entity-link" type="button" onClick={() => openAgent(workplaceId)}>
                    {workplaceName}
                  </button>
                ) : (
                  t("citizen.unemployed")
                )}
              </span>
            </div>
            <div className="operation-panel">
              <button
                className={`operation-btn${following ? " operation-btn--active" : ""}`}
                type="button"
                title={t(following ? "citizenTracking.unfollow" : "citizenTracking.follow")}
                aria-pressed={following}
                onClick={() => void toggleFollow(citizen.id)}
              >
                <CameraIcon />
              </button>
              <button
                className="operation-btn"
                type="button"
                title={t("citizenTracking.untrack")}
                onClick={() => void remove(citizen.id)}
              >
                <RemoveIcon />
              </button>
            </div>
          </li>
        );
      })}
    </ul>
  );
}
