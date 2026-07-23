/**
 * 学校情報コンポーネント
 *
 * 経済主体がTYPE_EDUCATIONの場合に表示される詳細情報パネル。
 * スキルごとの受け入れ可能人数(education_type)を一覧表示する。
 * スキル名はマスターデータ(skills)から取得する。
 */

import { Fragment } from "react";
import { useTranslation } from "react-i18next";
import type { AgentData } from "../../types";
import { useMasterData } from "../../contexts/MasterDataContext";

type EducationInfoPanelProps = {
  agent: AgentData;
};

export function EducationInfoPanel({ agent }: EducationInfoPanelProps) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const educationType: Record<string, number> = agent.education_type ?? {};

  const entries = Object.entries(educationType);

  if (entries.length === 0) {
    return null;
  }

  return (
    <section className="agent-detail-section">
      <div className="agent-detail-grid">
        {entries.map(([skillId, capacity]) => {
          const skill = masterData?.skills[Number(skillId)];
          const skillName = skill?.display_name ?? String(skillId);
          return (
            <Fragment key={skillId}>
              <span className="agent-detail-label">{skillName}</span>
              <span className="agent-detail-value">
                {capacity} {t("education.people")}
              </span>
            </Fragment>
          );
        })}
      </div>
    </section>
  );
}
