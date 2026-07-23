/**
 * 移動統計タブコンポーネント
 *
 * GET /api/v1/stats/commute から通勤・通学時間、昼夜人口、地区間流動を取得して表示する。
 *
 * レイアウト:
 * - 上下分割 (上1/3, 下2/3)
 *   - 上部 (1/3): 左右分割
 *     - 左上: 移動サマリカード (通勤人数、通学人数、合計移動人数)
 *     - 右上: 通勤・通学時間分布 (横向き積み上げ棒グラフ)
 *   - 下部 (2/3): 左右分割
 *     - 左下: 地区別昼夜間人口 (表形式、昼夜比率付き)
 *     - 右下: 地区間人口移動 (Recharts Sankey ダイアグラム)
 */

import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Sankey,
} from "recharts";
import { useEpochCachedStats } from "../useEpochCache";
import "./CommuteTab.css";

// --- 型定義 ---

type Summary = {
  worker_count: number;
  student_count: number;
  total_commuters: number;
};

type TimeDistPoint = {
  bucket: string;
  workers: number;
  students: number;
};

type ZonePopPoint = {
  zone_id: number;
  name: string;
  night_population: number;
  daytime_population: number;
};

type ODPoint = {
  from_zone_id: number;
  from_name: string;
  to_zone_id: number;
  to_name: string;
  count: number;
};

type CommuteStats = {
  epoch: number;
  summary: Summary;
  commute_time_distribution: TimeDistPoint[];
  zone_populations: ZonePopPoint[];
  od_traffic: ODPoint[];
};

// --- 配色 ---
const COLOR_WORKER = "#228be6";      // 通勤者: 濃い青
const COLOR_STUDENT = "#a5d8ff";     // 通学者: 淡い青 (ユーザー要望の薄い色)
const COLOR_LINK = "rgba(74, 158, 255, 0.4)"; // Sankey リンク色

export function CommuteTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const { data, loading, error } = useEpochCachedStats<CommuteStats>(
    apiBaseUrl,
    "/api/v1/stats/commute"
  );

  // --- Sankey 用データの構築 ---
  const sankeyData = useMemo(() => {
    if (!data || !data.od_traffic || data.od_traffic.length === 0) {
      return { nodes: [], links: [] };
    }

    const nodes: { name: string }[] = [];
    const nodeMap = new Map<string, number>();
    const links: { source: number; target: number; value: number }[] = [];

    // 移動先の重なりや循環を防ぐため、出発地と目的地で別のサフィックスをつける
    data.od_traffic.forEach((item) => {
      // 地区名が取得できていない場合の代替
      const fromName = item.from_name || `Zone #${item.from_zone_id}`;
      const toName = item.to_name || `Zone #${item.to_zone_id}`;

      const srcName = `${fromName} (居住)`;
      const dstName = `${toName} (目的地)`;

      if (!nodeMap.has(srcName)) {
        nodeMap.set(srcName, nodes.length);
        nodes.push({ name: srcName });
      }

      if (!nodeMap.has(dstName)) {
        nodeMap.set(dstName, nodes.length);
        nodes.push({ name: dstName });
      }

      links.push({
        source: nodeMap.get(srcName)!,
        target: nodeMap.get(dstName)!,
        value: item.count,
      });
    });

    return { nodes, links };
  }, [data]);

  if (loading) {
    return <div className="commute-tab__loading">{t("common.loading")}</div>;
  }
  if (error || !data) {
    return (
      <div className="commute-tab__error">
        {t("common.fetchError")}
        {error ? `: ${error}` : ""}
      </div>
    );
  }

  const { commute_time_distribution, zone_populations } = data;

  // --- Sankey のカスタムノード描画 ---
  const renderSankeyNode = ({ x, y, width, height, index, payload }: any) => {
    // 居住（左側）か目的地（右側）かでテキストの位置を切り替える
    const isSource = payload.name.endsWith("(居住)");
    const cleanName = payload.name.replace(" (居住)", "").replace(" (目的地)", "");

    return (
      <g key={`node-${index}`}>
        <rect
          x={x}
          y={y}
          width={width}
          height={height}
          fill={isSource ? "#1c7ed6" : "#74c0fc"}
          fillOpacity={0.8}
          rx={2}
        />
        <text
          x={isSource ? x - 6 : x + width + 6}
          y={y + height / 2}
          textAnchor={isSource ? "end" : "start"}
          dominantBaseline="middle"
          fontSize="10"
          fill="#c1c9d2"
        >
          {cleanName}
        </text>
      </g>
    );
  };

  // --- Sankey のカスタムツールチップ ---
  const CustomSankeyTooltip = ({ active, payload }: any) => {
    if (active && payload && payload.length) {
      const p = payload[0].payload;
      if (p.source && p.target) {
        // リンクホバー
        const srcName = p.source.name.replace(" (居住)", "");
        const dstName = p.target.name.replace(" (目的地)", "");
        return (
          <div className="commute-tooltip">
            <p className="commute-tooltip__route">{`${srcName} ➔ ${dstName}`}</p>
            <p className="commute-tooltip__val">{`移動者数: ${p.value} 人`}</p>
          </div>
        );
      } else if (p.name) {
        // ノードホバー
        const nodeName = p.name.replace(" (居住)", "").replace(" (目的地)", "");
        const role = p.name.endsWith("(居住)") ? "出発地" : "目的地";
        return (
          <div className="commute-tooltip">
            <p className="commute-tooltip__route">{`${nodeName} (${role})`}</p>
            <p className="commute-tooltip__val">{`総移動人数: ${p.value} 人`}</p>
          </div>
        );
      }
    }
    return null;
  };

  // --- 時間分布のカスタムツールチップ ---
  const CustomBarTooltip = ({ active, payload, label }: any) => {
    if (active && payload && payload.length) {
      const w = payload[0]?.value || 0;
      const s = payload[1]?.value || 0;
      return (
        <div className="commute-tooltip">
          <p className="commute-tooltip__time">{`${label} 分`}</p>
          <p className="commute-tooltip__item" style={{ color: COLOR_WORKER }}>
            {`通勤者: ${w} 人`}
          </p>
          <p className="commute-tooltip__item" style={{ color: COLOR_STUDENT }}>
            {`通学者: ${s} 人`}
          </p>
          <div className="commute-tooltip__divider" />
          <p className="commute-tooltip__total">
            {`合計: ${w + s} 人`}
          </p>
        </div>
      );
    }
    return null;
  };

  return (
    <div className="commute-tab">
      {/* 左ペイン: 2/5 幅 */}
      <div className="commute-tab__left">
        {/* 左上: 通勤通学時間分布 (1/3 高さ) */}
        <div className="commute-distribution">
          <div className="commute-panel-title">通勤・通学時間</div>
          <div className="commute-distribution__chart">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart
                data={commute_time_distribution}
                layout="vertical"
                margin={{ top: 5, right: 15, left: 5, bottom: 5 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="#253545" horizontal={true} vertical={false} />
                <XAxis type="number" stroke="#8ba3bb" fontSize={9} />
                <YAxis dataKey="bucket" type="category" stroke="#8ba3bb" fontSize={9} width={40} />
                <Tooltip content={<CustomBarTooltip />} cursor={{ fill: "rgba(255,255,255,0.05)" }} />
                <Bar dataKey="workers" stackId="a" fill={COLOR_WORKER} radius={[0, 0, 0, 0]} name="通勤者" />
                <Bar dataKey="students" stackId="a" fill={COLOR_STUDENT} radius={[0, 2, 2, 0]} name="通学者" />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>

        {/* 左下: 地区別昼夜間人口 (2/3 高さ) */}
        <div className="commute-zones">
          <div className="commute-panel-title">地区別人口</div>
          <div className="commute-zones__table-container">
            <table className="commute-table">
              <thead>
                <tr>
                  <th>地区名</th>
                  <th className="text-right">夜間</th>
                  <th className="text-right">昼間</th>
                  <th className="text-right">比率</th>
                </tr>
              </thead>
              <tbody>
                {zone_populations.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="text-center">地区データが存在しません。</td>
                  </tr>
                ) : (
                  zone_populations.map((z) => {
                    const ratio = z.night_population > 0
                      ? Math.round((z.daytime_population / z.night_population) * 100)
                      : 0;
                    
                    let ratioClass = "ratio-balanced";
                    if (ratio > 120) ratioClass = "ratio-day-heavy";
                    else if (ratio < 80) ratioClass = "ratio-night-heavy";

                    return (
                      <tr key={z.zone_id}>
                        <td className="font-semibold">{z.name}</td>
                        <td className="text-right">{z.night_population.toLocaleString()}</td>
                        <td className="text-right">{z.daytime_population.toLocaleString()}</td>
                        <td className="text-right">
                          <span className={`commute-ratio-badge ${ratioClass}`}>
                            {z.night_population > 0 ? `${ratio}%` : "0%"}
                          </span>
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        </div>
      </div>

      {/* 右ペイン: 3/5 幅 */}
      <div className="commute-tab__right">
        {/* 右側全体: 地区間人口移動 */}
        <div className="commute-flow">
          <div className="commute-panel-title">地区間の人口移動</div>
          <div className="commute-flow__chart">
            {sankeyData.nodes.length === 0 ? (
              <div className="commute-flow__empty">移動データが存在しません（通勤・通学ペアがありません）。</div>
            ) : (
              <ResponsiveContainer width="100%" height="100%">
                <Sankey
                  data={sankeyData}
                  node={renderSankeyNode}
                  link={{ stroke: COLOR_LINK, fill: "none" }}
                  nodePadding={12}
                  margin={{ top: 10, right: 80, bottom: 10, left: 80 }}
                >
                  <Tooltip content={<CustomSankeyTooltip />} />
                </Sankey>
              </ResponsiveContainer>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
