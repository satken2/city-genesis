/**
 * 神ウィンドウ (God Edit) パネル
 *
 * window_type="god_edit" のポップアップウィンドウで表示する専用パネル。
 * 任意のエンティティ(agent/path/parcel/zone/resource/citizen)の
 * リテラルプロパティを直接編集できる。
 *
 * 使い方:
 * 1. データタイプとIDを指定し、「読み込み」ボタンでバックエンドからデータを取得する
 *    (God edit ボタンから遷移した場合は最初から指定済みで自動ロードされる)
 * 2. 表示されたリテラル値のテキストボックスを編集する
 *    (dict や list 型は JSON テキストとして編集し、構文エラー中は保存できない)
 * 3. 「保存」ボタンで変更した値だけ PATCH メソッドで更新する
 *    (dict 型は値全体の差し替え。GET が省略する値0のキーは、送り返しに無ければ0のまま)
 * 4. 「削除」ボタンでエンティティを削除する
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import "./GodEditPanel.css";

type GodEditPanelProps = {
  apiBaseUrl: string;
  initialEntityType?: string;
  initialEntityId?: string;
};

/**
 * エンティティ種別ごとの API パスとレスポンスラッパーの対応表
 *
 * バックエンドのレスポンスはエンティティ種別によってラップされている場合と
 * トップレベルで直接返される場合があるため、アンラップ用のキーを保持する。
 * wrapperKey が null の場合はレスポンス全体をそのままエンティティデータとして扱う。
 */
const ENTITY_CONFIG: Record<string, { label: string; apiPath: string; wrapperKey: string | null }> = {
  agent:    { label: "Agent",    apiPath: "/api/v1/agents",    wrapperKey: "economic_agent" },
  path:     { label: "Path",     apiPath: "/api/v1/paths",     wrapperKey: null },
  parcel:   { label: "Parcel",   apiPath: "/api/v1/parcels",   wrapperKey: null },
  zone:     { label: "Zone",     apiPath: "/api/v1/zones",     wrapperKey: "zone" },
  resource: { label: "Resource", apiPath: "/api/v1/resources", wrapperKey: "resource" },
  citizen:  { label: "Citizen",  apiPath: "/api/v1/citizens",  wrapperKey: "citizen" },
};

/** 編集対象の各フィールドの状態 */
type FieldState = {
  key: string;
  originalValue: unknown;
  currentValue: string;
  valueType: "string" | "number" | "boolean" | "json";
  dirty: boolean;
  // フロントエンド側でのJSONバリデーションエラー時のメッセージ
  validationError?: string | null;
};

/**
 * 値がリテラル（プリミティブ）かどうかを判定する。
 * dict や list 型は編集対象外。
 */
function isLiteralValue(val: unknown): boolean {
  return val === null || typeof val !== "object";
}

/**
 * リテラル値の型を判定する。
 * オブジェクトや配列（nullを除く）の場合は json と判定する。
 */
function getLiteralType(val: unknown): "string" | "number" | "boolean" | "json" {
  if (typeof val === "boolean") return "boolean";
  if (typeof val === "number") return "number";
  if (val !== null && typeof val === "object") return "json";
  return "string";
}

/**
 * 編集中の値を元の型に合わせてパースする。
 * json型の場合は文字列からオブジェクトまたは配列にパースする。
 */
function parseValue(str: string, valueType: "string" | "number" | "boolean" | "json"): unknown {
  switch (valueType) {
    case "number":
      return str === "" ? null : parseFloat(str);
    case "boolean":
      return str === "true";
    case "json":
      try {
        return JSON.parse(str);
      } catch {
        // バリデーションエラー時は保存できないが、フォールバックとしてnullを返す
        return null;
      }
    default:
      return str;
  }
}

export function GodEditPanel({
  apiBaseUrl,
  initialEntityType,
  initialEntityId,
}: GodEditPanelProps) {
  const { t } = useTranslation();

  const [entityType, setEntityType] = useState(initialEntityType ?? "");
  const [entityId, setEntityId] = useState(initialEntityId ?? "");

  const [fields, setFields] = useState<FieldState[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [saveMessage, setSaveMessage] = useState<string | null>(null);

  const base = apiBaseUrl.replace(/\/+$/, "");
  const config = ENTITY_CONFIG[entityType];

  /** 初期値が指定されている場合、マウント時に自動ロードする */
  const didAutoLoadRef = useRef(false);
  useEffect(() => {
    if (didAutoLoadRef.current) return;
    if (!initialEntityType || !initialEntityId) return;
    didAutoLoadRef.current = true;
    void performFetch(initialEntityType, initialEntityId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /**
   * データをバックエンドから取得する。
   * useCallback で包んで performFetch として stable な参照を保つ。
   */
  const performFetch = useCallback(
    async (type: string, id: string) => {
      const cfg = ENTITY_CONFIG[type];
      if (!cfg || !id) return;

      const controller = new AbortController();
      setLoading(true);
      setError(null);
      setSaveMessage(null);

      const url = `${base}${cfg.apiPath}/${id}`;
      try {
        const res = await fetch(url, { signal: controller.signal });
        if (!res.ok) {
          const body = await res.text().catch(() => "");
          throw new Error(`HTTP ${res.status}: ${body}`);
        }
        const data = (await res.json()) as Record<string, unknown>;

        // レスポンスラッパーがあればアンラップする
        const entityData = cfg.wrapperKey
          ? (data[cfg.wrapperKey] as Record<string, unknown> | undefined) ?? data
          : data;

        const fieldList: FieldState[] = [];
        for (const key of Object.keys(entityData).sort()) {
          const val = entityData[key];
          const lit = isLiteralValue(val);
          const valueType = lit ? getLiteralType(val) : "json";
          fieldList.push({
            key,
            originalValue: val,
            currentValue: lit ? String(val) : JSON.stringify(val, null, 2),
            valueType,
            dirty: false,
            validationError: null,
          });
        }
        setFields(fieldList);
        setLoading(false);
      } catch (err: unknown) {
        if (controller.signal.aborted) return;
        setError(err instanceof Error ? err.message : "Unknown error");
        setLoading(false);
      }
    },
    [base],
  );

  /**
   * ロードボタン押下時のハンドラ。
   * 現在の entityType と entityId を使ってデータを取得する。
   */
  const handleLoad = useCallback(() => {
    void performFetch(entityType, entityId);
  }, [performFetch, entityType, entityId]);

  /**
   * ID 入力欄で Enter キーが押されたらロードを実行する。
   */
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Enter") {
        handleLoad();
      }
    },
    [handleLoad],
  );

  /** 変更のあるフィールド（dirty = true）だけを返す */
  const changedFields = useMemo(
    () => fields.filter((f) => f.dirty),
    [fields]
  );

  /** いずれかのフィールドでバリデーションエラーが発生しているかどうか */
  const hasValidationError = useMemo(
    () => fields.some((f) => f.validationError),
    [fields]
  );

  /**
   * フィールド編集ハンドラ。
   * JSONデータ（json型）の場合は、入力値の構文チェックを行い、
   * 不正な値のときはバリデーションエラーを保持する。
   */
  function handleFieldChange(key: string, newValue: string) {
    setFields((prev) =>
      prev.map((f) => {
        if (f.key !== key) return f;

        let dirty = false;
        let validationError: string | null = null;

        if (f.valueType === "json") {
          try {
            const parsed = JSON.parse(newValue);
            validationError = null;
            // フォーマット（改行・空白）に関係なく値自体の変更を検知するため、
            // 双方をシリアライズして比較する。
            dirty = JSON.stringify(f.originalValue) !== JSON.stringify(parsed);
          } catch (err) {
            validationError = err instanceof Error ? err.message : t("god_edit.invalidJson", "Invalid JSON format.");
            // エラー状態のままでの送信を防ぐため、保存ボタンを無効化する目的でdirtyをtrueにする。
            dirty = true;
          }
        } else {
          dirty = String(f.originalValue) !== newValue;
        }

        return {
          ...f,
          currentValue: newValue,
          dirty,
          validationError,
        };
      })
    );
  }

  /** 真偽値フィールドのトグルハンドラ */
  function handleBooleanToggle(key: string) {
    setFields((prev) =>
      prev.map((f) => {
        if (f.key !== key) return f;
        const toggled = f.currentValue === "true" ? "false" : "true";
        return { ...f, currentValue: toggled, dirty: String(f.originalValue) !== toggled };
      })
    );
  }

  /** 保存処理: 変更のある値だけ PATCH で送信する */
  async function handleSave() {
    if (!config || !entityId || changedFields.length === 0) return;
    setSaving(true);
    setSaveMessage(null);
    setError(null);

    const body: Record<string, unknown> = {};
    for (const f of changedFields) {
      body[f.key] = parseValue(f.currentValue, f.valueType);
    }

    const url = `${base}${config.apiPath}/${entityId}`;
    try {
      const res = await fetch(url, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!res.ok) {
        const bodyText = await res.text().catch(() => "");
        throw new Error(`HTTP ${res.status}: ${bodyText}`);
      }
      // PATCH 成功時は再取得して dirty 状態をリセットする
      setSaveMessage(t("god_edit.saved", "Saved successfully."));
      setSaving(false);
      void performFetch(entityType, entityId);
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Unknown error");
      setSaving(false);
    }
  }

  /** 削除処理 */
  async function handleDelete() {
    if (!config || !entityId) return;
    const confirmed = window.confirm(
      t("god_edit.deleteConfirm", "Really delete this entity? This cannot be undone.")
    );
    if (!confirmed) return;

    setDeleting(true);
    setError(null);
    setSaveMessage(null);

    const url = `${base}${config.apiPath}/${entityId}`;
    try {
      const res = await fetch(url, { method: "DELETE" });
      if (!res.ok) {
        const bodyText = await res.text().catch(() => "");
        throw new Error(`HTTP ${res.status}: ${bodyText}`);
      }
      setFields([]);
      setSaveMessage(t("god_edit.deleted", "Deleted successfully."));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Unknown error");
    } finally {
      setDeleting(false);
    }
  }

  return (
    <div className="god-edit">
      {/* ヘッダー: データタイプ選択 + ID 入力 + ロードボタン */}
      <div className="god-edit-header">
        <div className="god-edit-header-row">
          <select
            className="god-edit-select"
            value={entityType}
            onChange={(e) => {
              setEntityType(e.target.value);
              setEntityId("");
              setFields([]);
              setError(null);
              setSaveMessage(null);
            }}
          >
            <option value="" disabled>
              {t("god_edit.selectEntityType", "Select entity type")}
            </option>
            {Object.entries(ENTITY_CONFIG).map(([key, cfg]) => (
              <option key={key} value={key}>
                {cfg.label}
              </option>
            ))}
          </select>

          <input
            className="god-edit-input"
            type="text"
            inputMode="numeric"
            placeholder={t("god_edit.idPlaceholder", "Entity ID")}
            value={entityId}
            disabled={!entityType}
            onChange={(e) => {
              setEntityId(e.target.value);
              setError(null);
              setSaveMessage(null);
            }}
            onKeyDown={handleKeyDown}
          />

          <button
            className="god-edit-load-btn"
            type="button"
            disabled={!entityType || !entityId || loading}
            onClick={handleLoad}
          >
            {loading ? t("god_edit.loading", "Loading...") : t("god_edit.load", "Load")}
          </button>
        </div>
      </div>

      {/* エラー表示 */}
      {error && <div className="god-edit-error">{error}</div>}

      {/* 保存メッセージ */}
      {saveMessage && <div className="god-edit-message">{saveMessage}</div>}

      {/* ローディング中 （ボタンがローディング状態も兼ねるため、フィールド一覧を隠す） */}
      {loading && <p className="god-edit-loading">{t("common.loading")}</p>}

      {/* フィールド一覧 */}
      {!loading && fields.length > 0 && (
        <div className="god-edit-fields">
          {fields.map((f) => {
            const isJson = f.valueType === "json";
            return (
              <div
                key={f.key}
                className={`god-edit-field ${f.dirty ? "god-edit-field--dirty" : ""} ${isJson ? "god-edit-field--textarea" : ""}`}
              >
                <label className="god-edit-field-label">{f.key}</label>
                <div className="god-edit-field-control">
                  {isJson ? (
                    <div className="god-edit-textarea-container">
                      <textarea
                        className={`god-edit-field-textarea ${f.validationError ? "god-edit-field-textarea--error" : ""}`}
                        value={f.currentValue}
                        onChange={(e) => handleFieldChange(f.key, e.target.value)}
                      />
                      {f.validationError && (
                        <div className="god-edit-field-error-text">
                          {f.validationError}
                        </div>
                      )}
                    </div>
                  ) : f.valueType === "boolean" ? (
                    <label className="god-edit-checkbox-label">
                      <input
                        type="checkbox"
                        className="god-edit-checkbox"
                        checked={f.currentValue === "true"}
                        onChange={() => handleBooleanToggle(f.key)}
                      />
                      <span className="god-edit-checkbox-text">{f.currentValue}</span>
                    </label>
                  ) : (
                    <input
                      className="god-edit-field-input"
                      type={f.valueType === "number" ? "number" : "text"}
                      value={f.currentValue}
                      onChange={(e) => handleFieldChange(f.key, e.target.value)}
                    />
                  )}
                  {f.dirty && <span className="god-edit-field-dirty-mark">*</span>}
                  {isJson && (
                    <span className="god-edit-field-type-badge">complex</span>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* 操作ボタン */}
      {!loading && fields.length > 0 && (
        <div className="god-edit-actions">
          <button
            className="god-edit-btn god-edit-btn--save"
            type="button"
            disabled={changedFields.length === 0 || saving || hasValidationError}
            onClick={handleSave}
          >
            {saving
              ? t("god_edit.saving", "Saving...")
              : t("god_edit.save", "Save") +
                (changedFields.length > 0 ? ` (${changedFields.length})` : "")}
          </button>
          <button
            className="god-edit-btn god-edit-btn--delete"
            type="button"
            disabled={deleting}
            onClick={handleDelete}
          >
            {deleting
              ? t("god_edit.deleting", "Deleting...")
              : t("god_edit.delete", "Delete")}
          </button>
        </div>
      )}
    </div>
  );
}
