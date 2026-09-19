/**
 * 路線図(縦型ストリップ)
 *
 * 地下鉄の車内ドア上に掲示される路線図を縦向きにしたもの。路線カラーの1本の直線に
 * 停留所を順に丸で打ち、丸の横に停留所名を並べる。地理的な位置関係は表さず、
 * 「どの順で停まるか」だけを示す(実地図上の路線は TransitLineMap が担う)。
 *
 * 構造は HTML/CSS で組む(SVG ではなく)。停留所名の折り返し・クリックリンク・ホバーを
 * ブラウザのテキスト描画に任せられるため。各行が「上半分の線 + 丸 + 下半分の線」を持ち、
 * 隣接行の半分どうしが1つの区間パスを表す。区間が運休(status≠ACTIVE)なら、その区間に
 * あたる上下の半分を破線・灰色で描く。
 *
 * 停留所名は GET /api/v1/transit/lines/{id} が下地パスから解決した name を使い、
 * 無名(バス停など)は「停留所 N」で補う。駅舎に所属する停留所(house_path_id あり)は
 * 駅名をクリックで駅舎パスの詳細ウィンドウ(path_detail)を開く。
 *
 * 環状路線(末尾の停留所から先頭へ戻る区間パスがある)は末尾の下に先頭停留所名を
 * 薄く再掲して「戻る」ことを示す。
 */

import { useMemo, type CSSProperties } from "react";
import { useTranslation } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import { STATUS_ACTIVE } from "../../constants";
import type { TransitStop, TransitEdge } from "./TransitDetailPanel";
import "./TransitLineDiagram.css";

type TransitLineDiagramProps = {
  stops: TransitStop[];
  edges: TransitEdge[];
  /** 路線カラー("#rrggbb") */
  color: string;
  /** 路線全体が運行中か。運休中は線全体を灰色にする */
  running: boolean;
};

/** 2停留所を結ぶ区間パスを(向きを問わず)引くためのキー */
function pairKey(a: number, b: number): string {
  return a < b ? `${a}:${b}` : `${b}:${a}`;
}

export function TransitLineDiagram({ stops, edges, color, running }: TransitLineDiagramProps) {
  const { t } = useTranslation();

  // 停留所ペア → 区間パス。運休区間の判定と環状判定に使う。
  const edgeByPair = useMemo(() => {
    const m = new Map<string, TransitEdge>();
    for (const e of edges) m.set(pairKey(e.from_node_id, e.to_node_id), e);
    return m;
  }, [edges]);

  const ordered = useMemo(() => [...stops].sort((a, b) => a.order - b.order), [stops]);
  const last = ordered.length - 1;
  // 3停留所以上で末尾→先頭の区間パスがあれば環状路線(2停留所の往復は環状ではない)。
  const loopEdge =
    ordered.length >= 3 ? edgeByPair.get(pairKey(ordered[0].node_id, ordered[last].node_id)) : undefined;
  const loopActive = loopEdge !== undefined && running && loopEdge.status === STATUS_ACTIVE;

  const stopLabel = (s: TransitStop, index: number) =>
    s.name && s.name !== "" ? s.name : t("transit.stopFallbackName", { n: index + 1 });

  /** 区間(停留所 i と i+1 の間)が運行中か。路線が運休なら全区間を運休扱いにする */
  const segmentActive = (i: number): boolean => {
    if (!running) return false;
    const e = edgeByPair.get(pairKey(ordered[i].node_id, ordered[i + 1].node_id));
    return e ? e.status === STATUS_ACTIVE : true;
  };

  const openStation = (housePathId: number) => {
    void invoke("open_registered_window", {
      windowType: "path_detail",
      entityId: String(housePathId),
    });
  };

  const segClass = (active: boolean) => `tld__seg${active ? "" : " tld__seg--suspended"}`;

  return (
    <div className="tld" style={{ "--tld-color": color } as CSSProperties}>
      <div className="tld__summary">
        <span className="tld__summary-count">{t("transit.stopsCountValue", { count: ordered.length })}</span>
        {ordered.length >= 2 && (
          <span className="tld__summary-ends">
            {stopLabel(ordered[0], 0)}
            <span className="tld__summary-arrow">⇄</span>
            {stopLabel(ordered[last], last)}
          </span>
        )}
      </div>

      <ol className="tld__list">
        {ordered.map((s, i) => {
          const isTerminal = i === 0 || i === last;
          const upActive = i > 0 ? segmentActive(i - 1) : true;
          const downActive = i < last ? segmentActive(i) : loopActive;
          const label = stopLabel(s, i);
          return (
            <li key={s.node_id} className="tld__row">
              <span className="tld__index">{String(i + 1).padStart(2, "0")}</span>
              <span className="tld__track" aria-hidden="true">
                {i > 0 && <span className={`${segClass(upActive)} tld__seg--up`} />}
                {(i < last || loopEdge) && <span className={`${segClass(downActive)} tld__seg--down`} />}
                <span className={`tld__dot${isTerminal ? " tld__dot--terminal" : ""}`} />
              </span>
              {s.house_path_id ? (
                <button
                  type="button"
                  className="tld__name tld__name--link"
                  title={t("transit.openStation")}
                  onClick={() => openStation(s.house_path_id as number)}
                >
                  {label}
                </button>
              ) : (
                <span className="tld__name">{label}</span>
              )}
            </li>
          );
        })}
        {loopEdge && (
          <li className="tld__row tld__row--loop">
            <span className="tld__index" />
            <span className="tld__track" aria-hidden="true">
              <span className={`${segClass(loopActive)} tld__seg--up`} />
              <span className="tld__loop-mark">↺</span>
            </span>
            <span className="tld__name tld__name--loop">
              {t("transit.loopLine", { name: stopLabel(ordered[0], 0) })}
            </span>
          </li>
        )}
      </ol>
    </div>
  );
}
