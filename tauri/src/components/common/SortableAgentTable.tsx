/**
 * 経済主体の一覧テーブル
 *
 * 列ヘッダのクリックでフロントエンド側の並べ替えを行い(同じ列をもう一度押すと昇順・降順が入れ替わる)、
 * 行クリックで経済主体の詳細ウィンドウを開く。
 * 並べ替えの状態はこの部品が持つ。列の構成が変わる切り替えでは、呼び出し側が key を変えて作り直す。
 */

import { useMemo, useState, type ReactNode } from "react";
import { invoke } from "@tauri-apps/api/core";
import "./SortableAgentTable.css";

export type SortableAgentColumn<T> = {
  key: string;
  label: string;
  align?: "left" | "right" | "center";
  render: (row: T) => ReactNode;
  /** 未指定の列は並べ替えできない */
  sortValue?: (row: T) => number | string;
};

function openAgentDetail(agentId: number) {
  void invoke("open_registered_window", {
    windowType: "agent_detail",
    entityId: String(agentId),
  });
}

export function SortableAgentTable<T extends { agent_id: number }>({
  columns,
  rows,
  emptyText,
}: {
  columns: SortableAgentColumn<T>[];
  rows: T[];
  emptyText: string;
}) {
  const [sortKey, setSortKey] = useState<string | null>(null);
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");

  const sortedRows = useMemo(() => {
    const accessor = columns.find((c) => c.key === sortKey)?.sortValue;
    if (!accessor) return rows;
    const sorted = [...rows].sort((a, b) => {
      const va = accessor(a);
      const vb = accessor(b);
      if (typeof va === "string" || typeof vb === "string") {
        return String(va).localeCompare(String(vb));
      }
      return va - vb;
    });
    if (sortDir === "desc") sorted.reverse();
    return sorted;
  }, [rows, columns, sortKey, sortDir]);

  function toggleSort(col: SortableAgentColumn<T>) {
    if (!col.sortValue) return;
    if (sortKey === col.key) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(col.key);
      setSortDir("asc");
    }
  }

  return (
    <div className="agent-table">
      {rows.length > 0 ? (
        <table className="agent-table__table">
          <thead>
            <tr>
              {columns.map((col) => {
                const sortable = !!col.sortValue;
                const active = sortKey === col.key;
                return (
                  <th
                    key={col.key}
                    className={[
                      col.align ? `agent-table__cell--${col.align}` : "",
                      sortable ? "agent-table__th--sortable" : "",
                      active ? "agent-table__th--active" : "",
                    ]
                      .filter(Boolean)
                      .join(" ")}
                    onClick={() => toggleSort(col)}
                  >
                    {col.label}
                    {sortable && (
                      <span className="agent-table__arrow">
                        {active ? (sortDir === "asc" ? "▲" : "▼") : "⇅"}
                      </span>
                    )}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {sortedRows.map((row) => (
              <tr
                key={row.agent_id}
                className="agent-table__row"
                onClick={() => openAgentDetail(row.agent_id)}
              >
                {columns.map((col) => (
                  <td
                    key={col.key}
                    className={col.align ? `agent-table__cell--${col.align}` : ""}
                  >
                    {col.render(row)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <div className="agent-table__empty">{emptyText}</div>
      )}
    </div>
  );
}
