/**
 * 店舗・企業タブ
 *
 * 地区・タイプ・バリアントをプルダウンで指定し、該当する経済主体の一覧を表示する。
 * バックエンドの GET /api/v1/stats/agents が single_agent スキーマ相当の詳細情報を
 * 最大100件返し、本コンポーネントはそれをタイプ別の列構成で表に描画する。
 * 列ヘッダのクリックでフロントエンド側ソートを行う（バックエンドはソートしない）。
 *
 * タイプごとに表示する列は COLUMN_BUILDERS で定義する。共通列（ID・ステータス・名前）に
 * 続けて、タイプ固有の列（生産・賃金・充足度など）を並べる。
 */

import { useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useTranslation } from "react-i18next";
import { buildApiUrl, fetchApi } from "../../../utils/api";
import { useMasterData } from "../../../contexts/MasterDataContext";
import type { MasterData } from "../../../types";
import { StatusBadge } from "../../common/StatusBadge";
import {
  TYPE_STANDARD,
  TYPE_BUILDING,
  TYPE_EDUCATION,
  TYPE_HOUSEHOLD,
  TYPE_IMPORT,
  FAMILY_SINGLE,
  FAMILY_COUPLE,
  FAMILY_FAMILY,
  FAMILY_SENIOR,
} from "../../../constants";
import "./BusinessTab.css";

// --- バックエンド GET /api/v1/stats/agents のレスポンス型 ---

/**
 * 経済主体の詳細情報。single_agent スキーマ相当 + タイプ別の補完フィールド。
 * queue型SoA（utilization 等）は新しい順のリストとして届くため、先頭要素が直近値。
 */
type AgentItem = {
  agent_id: number;
  name: string;
  status: number;
  variant: number;
  // TYPE_STANDARD
  supply_product_id?: number | null;
  supply_price?: number | null;
  supply_production?: number | null;
  supply_amount?: number | null;
  supply_sold_amount?: number[] | null;
  wage?: number | null;
  wage_uneducated?: number | null;
  wage_high?: number | null;
  quota?: number | null;
  total_labor?: number | null;
  utilization?: number[] | null;
  // TYPE_BUILDING
  bd_type?: number | null;
  durability?: number | null;
  rent?: number | null;
  floors?: number | null;
  floor_area?: number | null;
  // TYPE_HOUSEHOLD
  family_type?: number | null;
  discretionary_income?: number | null;
  satis_nutrition?: number | null;
  satis_healthcare?: number | null;
  satis_entertainment?: number | null;
  satis_residential?: number | null;
  has_automobile?: boolean | null;
  // TYPE_EDUCATION
  education_type?: Record<string, number> | null;
  enrolled_by_skill?: Record<string, number> | null;
};

type AgentListResponse = {
  epoch: number;
  type: number;
  variant: number | null;
  zone_id: number | null;
  total: number;
  count: number;
  agents: AgentItem[];
};

type ZoneOption = { zone_id: number; name: string };

// --- 列定義 ---

type CellAlign = "left" | "right" | "center";

/**
 * 1列の定義。
 * - render: セルの表示内容
 * - sortValue: ソートに使う比較値。未定義の列はソート不可（ヘッダがクリックできない）
 */
type ColumnDef = {
  key: string;
  label: string;
  align?: CellAlign;
  render: (item: AgentItem) => React.ReactNode;
  sortValue?: (item: AgentItem) => number | string;
};

/** 選択可能なタイプ（表示順）。ラベルはマスターの display_name で解決する */
const SELECTABLE_TYPES: number[] = [
  TYPE_STANDARD,
  TYPE_BUILDING,
  TYPE_EDUCATION,
  TYPE_HOUSEHOLD,
  TYPE_IMPORT,
];

const SKILL_ORDER = [0, 1, 2, 3, 4, 5];

/** queue型SoAリストの直近値（先頭要素）を取り出す */
function latest(list?: number[] | null): number | null {
  if (!list || list.length === 0) return null;
  return list[0];
}

/** 0.0〜1.0 の割合をパーセント表記にする */
function pct(v?: number | null): string {
  return v == null ? "—" : `${Math.round(v * 100)}%`;
}

/** 数値を桁区切りで表示する */
function num(v?: number | null, digits = 0): string {
  return v == null
    ? "—"
    : v.toLocaleString(undefined, {
        minimumFractionDigits: digits,
        maximumFractionDigits: digits,
      });
}

export function BusinessTab({ apiBaseUrl }: { apiBaseUrl: string }) {
  const { t } = useTranslation();
  const masterData = useMasterData();

  const [selectedType, setSelectedType] = useState<number>(TYPE_STANDARD);
  const [selectedVariant, setSelectedVariant] = useState<number | null>(null);
  const [selectedZone, setSelectedZone] = useState<number | null>(null);

  const [zones, setZones] = useState<ZoneOption[]>([]);
  const [data, setData] = useState<AgentListResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // ソート状態。null の間は ID 昇順（取得順）で表示する。
  const [sortKey, setSortKey] = useState<string | null>(null);
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");

  // --- 地区一覧の取得（マウント時に1回） ---
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await fetchApi<{ zones: ZoneOption[] }>(
          buildApiUrl(apiBaseUrl, "/api/v1/zones")
        );
        if (!cancelled) {
          setZones(
            (res.zones ?? []).filter((z) => z.name && z.name.trim() !== "")
          );
        }
      } catch (err) {
        console.error("[BusinessTab] zones fetch error:", err);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [apiBaseUrl]);

  // --- エージェント一覧の取得（フィルタ変更時） ---
  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    (async () => {
      setLoading(true);
      setError(null);
      try {
        const params = new URLSearchParams();
        params.append("type", String(selectedType));
        if (selectedVariant != null) params.append("variant", String(selectedVariant));
        if (selectedZone != null) params.append("zone_id", String(selectedZone));
        const url = `${buildApiUrl(apiBaseUrl, "/api/v1/stats/agents")}?${params.toString()}`;
        const res = await fetchApi<AgentListResponse>(url, {
          signal: controller.signal,
        });
        if (!cancelled) setData(res);
      } catch (err) {
        if (controller.signal.aborted || cancelled) return;
        console.error("[BusinessTab] agents fetch error:", err);
        setError(err instanceof Error ? err.message : "Unknown error");
        setData(null);
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [apiBaseUrl, selectedType, selectedVariant, selectedZone]);

  // タイプ変更時はバリアント選択とソートをリセットする。
  function handleTypeChange(typeStr: string) {
    setSelectedType(Number(typeStr));
    setSelectedVariant(null);
    setSortKey(null);
    setSortDir("asc");
  }

  function openAgentDetail(agentId: number) {
    void invoke("open_registered_window", {
      windowType: "agent_detail",
      entityId: String(agentId),
    });
  }

  // 選択中タイプに属するバリアント候補。
  const variantOptions = useMemo(() => {
    if (!masterData?.agents) return [] as { id: number; label: string }[];
    return Object.entries(masterData.agents)
      .filter(([, entry]) => entry.type === selectedType)
      .map(([id, entry]) => ({ id: Number(id), label: entry.display_name }));
  }, [masterData, selectedType]);

  // タイプ別の列定義。
  const columns = useMemo(
    () => buildColumns(selectedType, masterData, t),
    [selectedType, masterData, t]
  );

  // ソート適用後の表示行。
  const rows = useMemo(() => {
    const list = data?.agents ?? [];
    if (sortKey == null) return list;
    const col = columns.find((c) => c.key === sortKey);
    if (!col?.sortValue) return list;
    const accessor = col.sortValue;
    const sorted = [...list].sort((a, b) => {
      const va = accessor(a);
      const vb = accessor(b);
      if (typeof va === "string" || typeof vb === "string") {
        return String(va).localeCompare(String(vb));
      }
      return va - vb;
    });
    if (sortDir === "desc") sorted.reverse();
    return sorted;
  }, [data, columns, sortKey, sortDir]);

  function toggleSort(col: ColumnDef) {
    if (!col.sortValue) return;
    if (sortKey === col.key) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(col.key);
      setSortDir("asc");
    }
  }

  return (
    <div className="biz-tab">
      {/* フィルタバー */}
      <div className="biz-filter-bar">
        <div className="biz-filter">
          <label className="biz-filter__label">{t("agentList.filterType")}</label>
          <select
            className="biz-filter__select"
            value={selectedType}
            onChange={(e) => handleTypeChange(e.target.value)}
          >
            {SELECTABLE_TYPES.map((typeId) => (
              <option key={typeId} value={typeId}>
                {masterData?.agent_types?.[typeId]?.display_name ?? String(typeId)}
              </option>
            ))}
          </select>
        </div>

        <div className="biz-filter">
          <label className="biz-filter__label">{t("agentList.filterVariant")}</label>
          <select
            className="biz-filter__select"
            value={selectedVariant ?? ""}
            onChange={(e) =>
              setSelectedVariant(e.target.value === "" ? null : Number(e.target.value))
            }
          >
            <option value="">{t("agentList.allVariants")}</option>
            {variantOptions.map((opt) => (
              <option key={opt.id} value={opt.id}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>

        <div className="biz-filter">
          <label className="biz-filter__label">{t("agentList.filterZone")}</label>
          <select
            className="biz-filter__select"
            value={selectedZone ?? ""}
            onChange={(e) =>
              setSelectedZone(e.target.value === "" ? null : Number(e.target.value))
            }
          >
            <option value="">{t("agentList.allZones")}</option>
            {zones.map((z) => (
              <option key={z.zone_id} value={z.zone_id}>
                {z.name}
              </option>
            ))}
          </select>
        </div>

        <div className="biz-filter__count">
          {data && (
            <span>
              {t("agentList.resultCount", { count: data.count })}
              {data.total > data.count && (
                <span className="biz-filter__capped">
                  {" "}
                  {t("agentList.capped", { total: data.total })}
                </span>
              )}
            </span>
          )}
        </div>
      </div>

      {/* 結果テーブル */}
      <div className="biz-results">
        {loading && <p className="biz-status">{t("agentList.loading")}</p>}
        {!loading && error && <p className="biz-status biz-status--error">{error}</p>}
        {!loading && !error && rows.length === 0 && (
          <p className="biz-status">{t("agentList.noResults")}</p>
        )}
        {!loading && !error && rows.length > 0 && (
          <table className="biz-table">
            <thead>
              <tr>
                {columns.map((col) => {
                  const sortable = !!col.sortValue;
                  const active = sortKey === col.key;
                  return (
                    <th
                      key={col.key}
                      className={[
                        col.align ? `biz-th--${col.align}` : "",
                        sortable ? "biz-th--sortable" : "",
                        active ? "biz-th--active" : "",
                      ]
                        .filter(Boolean)
                        .join(" ")}
                      onClick={() => toggleSort(col)}
                    >
                      {col.label}
                      {sortable && (
                        <span className="biz-th__arrow">
                          {active ? (sortDir === "asc" ? "▲" : "▼") : "⇅"}
                        </span>
                      )}
                    </th>
                  );
                })}
              </tr>
            </thead>
            <tbody>
              {rows.map((item) => (
                <tr
                  key={item.agent_id}
                  className="biz-row"
                  onClick={() => openAgentDetail(item.agent_id)}
                >
                  {columns.map((col) => (
                    <td
                      key={col.key}
                      className={col.align ? `biz-td--${col.align}` : ""}
                    >
                      {col.render(item)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

// --- 列ビルダー ---

type TFn = ReturnType<typeof useTranslation>["t"];

/** 世帯タイプ番号を表示名へ変換する（HouseholdInfoPanel と同一マッピング） */
function familyTypeName(familyType: number | null | undefined, t: TFn): string {
  switch (familyType) {
    case FAMILY_SINGLE:
      return t("household.familySingle");
    case FAMILY_COUPLE:
      return t("household.familyCouple");
    case FAMILY_FAMILY:
      return t("household.familyFamily");
    case FAMILY_SENIOR:
      return t("household.familySenior");
    default:
      return familyType == null ? "—" : String(familyType);
  }
}

/** すべてのタイプ共通の先頭列（ID・ステータス・バリアント・名前） */
function baseColumns(masterData: MasterData | null, t: TFn): ColumnDef[] {
  return [
    {
      key: "agent_id",
      label: "ID",
      align: "right",
      render: (a) => <span className="biz-col-id">{a.agent_id}</span>,
      sortValue: (a) => a.agent_id,
    },
    {
      key: "status",
      label: t("agentList.colStatus"),
      align: "center",
      render: (a) => <StatusBadge status={a.status} />,
      sortValue: (a) => a.status,
    },
    {
      key: "variant",
      label: t("agentList.colVariant"),
      render: (a) =>
        a.variant != null
          ? masterData?.agents?.[a.variant]?.display_name ?? String(a.variant)
          : "—",
      sortValue: (a) =>
        a.variant != null
          ? masterData?.agents?.[a.variant]?.display_name ?? ""
          : "",
    },
    {
      key: "name",
      label: t("agentList.colName"),
      render: (a) => a.name,
      sortValue: (a) => a.name,
    },
  ];
}

/**
 * 指定タイプの列定義一覧を返す。
 * masterData が未取得の場合でも、名前解決をフォールバックして描画可能にする。
 */
function buildColumns(
  type: number,
  masterData: MasterData | null,
  t: TFn
): ColumnDef[] {
  const base = baseColumns(masterData, t);

  if (type === TYPE_STANDARD) {
    return [
      ...base,
      {
        key: "supply_price",
        label: t("agentList.colPrice"),
        align: "right",
        render: (a) => num(a.supply_price, 1),
        sortValue: (a) => a.supply_price ?? 0,
      },
      {
        key: "supply_production",
        label: t("agentList.colProduction"),
        align: "right",
        render: (a) => num(a.supply_production, 1),
        sortValue: (a) => a.supply_production ?? 0,
      },
      {
        key: "stock",
        label: t("agentList.colStock"),
        align: "right",
        render: (a) => num(a.supply_amount, 1),
        sortValue: (a) => a.supply_amount ?? 0,
      },
      {
        key: "sold",
        label: t("agentList.colSold"),
        align: "right",
        render: (a) => num(latest(a.supply_sold_amount), 1),
        sortValue: (a) => latest(a.supply_sold_amount) ?? 0,
      },
      {
        key: "wage",
        label: t("agentList.colWage"),
        align: "right",
        render: (a) =>
          `${num(a.wage_high, 0)} / ${num(a.wage, 0)} / ${num(a.wage_uneducated, 0)}`,
        sortValue: (a) => a.wage ?? 0,
      },
      {
        key: "employment",
        label: t("agentList.colEmployment"),
        align: "right",
        render: (a) => `${num(a.total_labor, 0)} / ${num(a.quota, 0)}`,
        sortValue: (a) => a.total_labor ?? 0,
      },
      {
        key: "utilization",
        label: t("agentList.colUtilization"),
        align: "right",
        render: (a) => pct(latest(a.utilization)),
        sortValue: (a) => latest(a.utilization) ?? 0,
      },
    ];
  }

  if (type === TYPE_BUILDING) {
    return [
      ...base,
      {
        key: "bd_type",
        label: t("agentList.colBdType"),
        render: (a) =>
          a.bd_type != null
            ? masterData?.bd_types?.[a.bd_type]?.display_name ?? String(a.bd_type)
            : "—",
        sortValue: (a) => a.bd_type ?? 0,
      },
      {
        key: "durability",
        label: t("agentList.colDurability"),
        align: "right",
        render: (a) => pct(a.durability),
        sortValue: (a) => a.durability ?? 0,
      },
      {
        key: "rent",
        label: t("agentList.colRent"),
        align: "right",
        render: (a) => num(a.rent, 0),
        sortValue: (a) => a.rent ?? 0,
      },
      {
        key: "floors",
        label: t("agentList.colFloors"),
        align: "right",
        render: (a) => num(a.floors, 0),
        sortValue: (a) => a.floors ?? 0,
      },
      {
        key: "floor_area",
        label: t("agentList.colFloorArea"),
        align: "right",
        render: (a) => num(a.floor_area, 0),
        sortValue: (a) => a.floor_area ?? 0,
      },
      {
        key: "occupancy",
        label: t("agentList.colOccupancy"),
        align: "right",
        render: (a) => pct(latest(a.utilization)),
        sortValue: (a) => latest(a.utilization) ?? 0,
      },
    ];
  }

  if (type === TYPE_HOUSEHOLD) {
    return [
      ...base,
      {
        key: "family_type",
        label: t("agentList.colFamilyType"),
        render: (a) => familyTypeName(a.family_type, t),
        sortValue: (a) => a.family_type ?? 0,
      },
      {
        key: "discretionary_income",
        label: t("agentList.colDiscretionaryIncome"),
        align: "right",
        render: (a) => num(a.discretionary_income, 0),
        sortValue: (a) => a.discretionary_income ?? 0,
      },
      {
        key: "satis_nutrition",
        label: t("agentList.colSatisNutrition"),
        align: "right",
        render: (a) => pct(a.satis_nutrition),
        sortValue: (a) => a.satis_nutrition ?? 0,
      },
      {
        key: "satis_healthcare",
        label: t("agentList.colSatisHealthcare"),
        align: "right",
        render: (a) => pct(a.satis_healthcare),
        sortValue: (a) => a.satis_healthcare ?? 0,
      },
      {
        key: "satis_entertainment",
        label: t("agentList.colSatisEntertainment"),
        align: "right",
        render: (a) => pct(a.satis_entertainment),
        sortValue: (a) => a.satis_entertainment ?? 0,
      },
      {
        key: "satis_residential",
        label: t("agentList.colSatisResidential"),
        align: "right",
        render: (a) => pct(a.satis_residential),
        sortValue: (a) => a.satis_residential ?? 0,
      },
      {
        key: "has_automobile",
        label: t("agentList.colAutomobile"),
        align: "center",
        render: (a) =>
          a.has_automobile == null ? "—" : a.has_automobile ? "○" : "×",
        sortValue: (a) => (a.has_automobile ? 1 : 0),
      },
    ];
  }

  if (type === TYPE_EDUCATION) {
    // スキル別の「在籍者数 / 枠数」を1セルにまとめて表示する。
    return [
      ...base,
      {
        key: "enrollment",
        label: t("agentList.colEnrollment"),
        render: (a) => {
          const capacity = a.education_type ?? {};
          const enrolled = a.enrolled_by_skill ?? {};
          // 枠数が設定されているスキルのみを、定義順で表示する。
          const skillKeys = SKILL_ORDER.filter((sk) => (capacity[String(sk)] ?? 0) > 0);
          if (skillKeys.length === 0) return "—";
          return (
            <div className="biz-enrollment">
              {skillKeys.map((sk) => {
                const cap = capacity[String(sk)] ?? 0;
                const enr = enrolled[String(sk)] ?? 0;
                const skillName =
                  masterData?.skills?.[sk]?.display_name ?? String(sk);
                return (
                  <span key={sk} className="biz-enrollment__item">
                    <span className="biz-enrollment__skill">{skillName}</span>
                    <span className="biz-enrollment__count">
                      {enr} / {cap}
                    </span>
                  </span>
                );
              })}
            </div>
          );
        },
      },
    ];
  }

  // TYPE_IMPORT（輸出入拠点）は共通列（ID・ステータス・名前）のみ。
  return base;
}
