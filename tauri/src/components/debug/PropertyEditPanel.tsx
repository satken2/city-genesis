import { requireApiSuccess } from "../../utils/api";
import { describeError } from "../../utils/errors";
/**
 * Property editor panel
 *
 * Dedicated panel for the window_type="property_edit" popup.
 * Directly edits literal properties of any entity (agent/path/parcel/zone/resource/citizen).
 *
 * Usage:
 * 1. Specify a data type and ID, then press Load to retrieve backend data.
 *    Opening from a property edit button prepopulates these values and loads automatically.
 * 2. Edit the displayed literal values in text fields.
 *    Edit dict and list values as JSON; syntax errors prevent saving.
 * 3. Save updates only changed values using PATCH.
 *    Dict values are replaced in full. Zero-valued keys omitted by GET remain zero if absent from the submitted
 *    data.
 * 4. Delete removes the entity.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import "./PropertyEditPanel.css";

type PropertyEditPanelProps = {
  apiBaseUrl: string;
  initialEntityType?: string;
  initialEntityId?: string;
};

/**
 * API paths and response wrappers by entity type
 *
 * Backend responses may be wrapped or returned directly at the top level, depending on entity type. Keep the
 * unwrapping key here.
 * When wrapperKey is null, treat the entire response as entity data.
 */
const ENTITY_CONFIG: Record<string, { label: string; apiPath: string; wrapperKey: string | null }> = {
  agent:    { label: "Agent",    apiPath: "/api/v1/agents",    wrapperKey: "economic_agent" },
  path:     { label: "Path",     apiPath: "/api/v1/paths",     wrapperKey: null },
  parcel:   { label: "Parcel",   apiPath: "/api/v1/parcels",   wrapperKey: null },
  zone:     { label: "Zone",     apiPath: "/api/v1/zones",     wrapperKey: "zone" },
  resource: { label: "Resource", apiPath: "/api/v1/resources", wrapperKey: "resource" },
  citizen:  { label: "Citizen",  apiPath: "/api/v1/citizens",  wrapperKey: "citizen" },
};

/** State for each editable field */
type FieldState = {
  key: string;
  originalValue: unknown;
  currentValue: string;
  valueType: "string" | "number" | "boolean" | "json";
  dirty: boolean;
  // Message for frontend JSON validation errors
  validationError?: string | null;
};

/**
 * Determine whether a value is a literal (primitive).
 * Dicts and lists are excluded from editing.
 */
function isLiteralValue(val: unknown): boolean {
  return val === null || typeof val !== "object";
}

/**
 * Determine a literal value's type.
 * Classify objects and arrays (except null) as json.
 */
function getLiteralType(val: unknown): "string" | "number" | "boolean" | "json" {
  if (typeof val === "boolean") return "boolean";
  if (typeof val === "number") return "number";
  if (val !== null && typeof val === "object") return "json";
  return "string";
}

/**
 * Parse an edited value according to its original type.
 * For json, parse the string into an object or array.
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
        // Validation errors prevent saving; return null as a fallback.
        return null;
      }
    default:
      return str;
  }
}

export function PropertyEditPanel({
  apiBaseUrl,
  initialEntityType,
  initialEntityId,
}: PropertyEditPanelProps) {
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

  /** Load automatically on mount when initial values are provided. */
  const didAutoLoadRef = useRef(false);
  useEffect(() => {
    if (didAutoLoadRef.current) return;
    if (!initialEntityType || !initialEntityId) return;
    didAutoLoadRef.current = true;
    void performFetch(initialEntityType, initialEntityId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /**
   * Retrieve data from the backend.
   * Wrap in useCallback to keep performFetch stable.
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
        await requireApiSuccess(res);
        const data = (await res.json()) as Record<string, unknown>;

        // Unwrap the response if it has a wrapper.
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
        setError(describeError(err));
        setLoading(false);
      }
    },
    [base],
  );

  /**
   * Load button handler.
   * Retrieve data using the current entityType and entityId.
   */
  const handleLoad = useCallback(() => {
    void performFetch(entityType, entityId);
  }, [performFetch, entityType, entityId]);

  /**
   * Load when Enter is pressed in the ID input.
   */
  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Enter") {
        handleLoad();
      }
    },
    [handleLoad],
  );

  /** Return only changed fields (dirty = true). */
  const changedFields = useMemo(
    () => fields.filter((f) => f.dirty),
    [fields]
  );

  /** Whether any field has a validation error */
  const hasValidationError = useMemo(
    () => fields.some((f) => f.validationError),
    [fields]
  );

  /**
   * Field edit handler.
   * For JSON data (json type), check input syntax and retain validation errors for invalid values.
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
            // Detect value changes independently of formatting (line breaks and spaces) by
            // serializing both values before comparison.
            dirty = JSON.stringify(f.originalValue) !== JSON.stringify(parsed);
          } catch (err) {
            validationError = t("property_edit.invalidJson", "Invalid JSON format.");
            // Set dirty to true to disable Save and prevent submission while invalid.
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

  /** Boolean field toggle handler */
  function handleBooleanToggle(key: string) {
    setFields((prev) =>
      prev.map((f) => {
        if (f.key !== key) return f;
        const toggled = f.currentValue === "true" ? "false" : "true";
        return { ...f, currentValue: toggled, dirty: String(f.originalValue) !== toggled };
      })
    );
  }

  /** Save: submit only changed values via PATCH. */
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
      await requireApiSuccess(res);
      // Refetch after a successful PATCH to reset dirty state.
      setSaveMessage(t("property_edit.saved", "Saved successfully."));
      setSaving(false);
      void performFetch(entityType, entityId);
    } catch (err: unknown) {
      setError(describeError(err));
      setSaving(false);
    }
  }

  /** Delete handler */
  async function handleDelete() {
    if (!config || !entityId) return;
    const confirmed = window.confirm(
      t("property_edit.deleteConfirm", "Really delete this entity? This cannot be undone.")
    );
    if (!confirmed) return;

    setDeleting(true);
    setError(null);
    setSaveMessage(null);

    const url = `${base}${config.apiPath}/${entityId}`;
    try {
      const res = await fetch(url, { method: "DELETE" });
      await requireApiSuccess(res);
      setFields([]);
      setSaveMessage(t("property_edit.deleted", "Deleted successfully."));
    } catch (err: unknown) {
      setError(describeError(err));
    } finally {
      setDeleting(false);
    }
  }

  return (
    <div className="property-edit">
      {/* Header: data type selector + ID input + Load button */}
      <div className="property-edit-header">
        <div className="property-edit-header-row">
          <select
            className="property-edit-select"
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
              {t("property_edit.selectEntityType", "Select entity type")}
            </option>
            {Object.entries(ENTITY_CONFIG).map(([key, cfg]) => (
              <option key={key} value={key}>
                {cfg.label}
              </option>
            ))}
          </select>

          <input
            className="property-edit-input"
            type="text"
            inputMode="numeric"
            placeholder={t("property_edit.idPlaceholder", "Entity ID")}
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
            className="property-edit-load-btn"
            type="button"
            disabled={!entityType || !entityId || loading}
            onClick={handleLoad}
          >
            {loading ? t("property_edit.loading", "Loading...") : t("property_edit.load", "Load")}
          </button>
        </div>
      </div>

      {/* Error display */}
      {error && <div className="property-edit-error">{error}</div>}

      {/* Save message */}
      {saveMessage && <div className="property-edit-message">{saveMessage}</div>}

      {/* Loading (hide the field list since the button also indicates loading) */}
      {loading && <p className="property-edit-loading">{t("common.loading")}</p>}

      {/* Field list */}
      {!loading && fields.length > 0 && (
        <div className="property-edit-fields">
          {fields.map((f) => {
            const isJson = f.valueType === "json";
            return (
              <div
                key={f.key}
                className={`property-edit-field ${f.dirty ? "property-edit-field--dirty" : ""} ${isJson ? "property-edit-field--textarea" : ""}`}
              >
                <label className="property-edit-field-label">{f.key}</label>
                <div className="property-edit-field-control">
                  {isJson ? (
                    <div className="property-edit-textarea-container">
                      <textarea
                        className={`property-edit-field-textarea ${f.validationError ? "property-edit-field-textarea--error" : ""}`}
                        value={f.currentValue}
                        onChange={(e) => handleFieldChange(f.key, e.target.value)}
                      />
                      {f.validationError && (
                        <div className="property-edit-field-error-text">
                          {f.validationError}
                        </div>
                      )}
                    </div>
                  ) : f.valueType === "boolean" ? (
                    <label className="property-edit-checkbox-label">
                      <input
                        type="checkbox"
                        className="property-edit-checkbox"
                        checked={f.currentValue === "true"}
                        onChange={() => handleBooleanToggle(f.key)}
                      />
                      <span className="property-edit-checkbox-text">{f.currentValue}</span>
                    </label>
                  ) : (
                    <input
                      className="property-edit-field-input"
                      type={f.valueType === "number" ? "number" : "text"}
                      value={f.currentValue}
                      onChange={(e) => handleFieldChange(f.key, e.target.value)}
                    />
                  )}
                  {f.dirty && <span className="property-edit-field-dirty-mark">*</span>}
                  {isJson && (
                    <span className="property-edit-field-type-badge">complex</span>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* Action buttons */}
      {!loading && fields.length > 0 && (
        <div className="property-edit-actions">
          <button
            className="property-edit-btn property-edit-btn--save"
            type="button"
            disabled={changedFields.length === 0 || saving || hasValidationError}
            onClick={handleSave}
          >
            {saving
              ? t("property_edit.saving", "Saving...")
              : t("property_edit.save", "Save") +
                (changedFields.length > 0 ? ` (${changedFields.length})` : "")}
          </button>
          <button
            className="property-edit-btn property-edit-btn--delete"
            type="button"
            disabled={deleting}
            onClick={handleDelete}
          >
            {deleting
              ? t("property_edit.deleting", "Deleting...")
              : t("property_edit.delete", "Delete")}
          </button>
        </div>
      )}
    </div>
  );
}
