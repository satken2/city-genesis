import { useCallback, useRef, useState, useEffect, type KeyboardEvent } from "react";
import "./NumberStepper.css";

type NumberStepperProps = {
  value: number;
  onChange: (value: number) => void;
  min?: number;
  max?: number;
  step?: number;
  integerOnly?: boolean;
  disabled?: boolean;
  label?: string;
  className?: string;
};

function clamp(value: number, min?: number, max?: number): number {
  let result = value;
  if (min !== undefined) result = Math.max(min, result);
  if (max !== undefined) result = Math.min(max, result);
  return result;
}

export function NumberStepper({
  value,
  onChange,
  min,
  max,
  step = 1,
  integerOnly = true,
  disabled = false,
  label,
  className,
}: NumberStepperProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [inputValue, setInputValue] = useState<string>("");
  const [isFocused, setIsFocused] = useState(false);

  // Sync the local string value when the parent value changes, but only if not focused
  useEffect(() => {
    if (!isFocused) {
      setInputValue(integerOnly ? String(Math.round(value)) : String(value));
    }
  }, [value, isFocused, integerOnly]);

  const adjust = useCallback(
    (delta: number) => {
      let next = value + delta;
      if (integerOnly) next = Math.round(next);
      const clamped = clamp(next, min, max);
      onChange(clamped);
      if (!isFocused) {
        setInputValue(String(clamped));
      }
    },
    [value, step, integerOnly, min, max, onChange, isFocused]
  );

  const commitInput = useCallback(() => {
    setIsFocused(false);
    const raw = inputRef.current?.value ?? inputValue;
    if (raw == null || raw === "") {
      setInputValue(integerOnly ? String(Math.round(value)) : String(value));
      return;
    }
    const parsed = parseFloat(raw);
    if (isNaN(parsed)) {
      setInputValue(integerOnly ? String(Math.round(value)) : String(value));
      return;
    }
    let next = parsed;
    if (integerOnly) next = Math.round(next);
    const clamped = clamp(next, min, max);
    onChange(clamped);
    setInputValue(String(clamped));
  }, [integerOnly, min, max, onChange, value, inputValue]);

  const handleKeyDown = useCallback(
    (e: KeyboardEvent<HTMLInputElement>) => {
      if (e.key === "Enter") {
        commitInput();
        inputRef.current?.blur();
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        adjust(step);
      } else if (e.key === "ArrowDown") {
        e.preventDefault();
        adjust(-step);
      } else if (e.key === "PageUp") {
        e.preventDefault();
        adjust(step);
      } else if (e.key === "PageDown") {
        e.preventDefault();
        adjust(-step);
      }
    },
    [adjust, commitInput, step]
  );

  const inputMode = integerOnly ? "numeric" : "decimal";

  return (
    <div className={`number-stepper ${className ?? ""}`}>
      {label && <span className="number-stepper__label">{label}</span>}
      <div className="number-stepper__control">
        <button
          className="number-stepper__btn"
          type="button"
          onClick={() => adjust(-step)}
          disabled={disabled || (min !== undefined && value <= min)}
          aria-label="Decrease"
        >
          -
        </button>
        <input
          ref={inputRef}
          className="number-stepper__input"
          type="text"
          inputMode={inputMode}
          value={inputValue}
          disabled={disabled}
          onFocus={() => setIsFocused(true)}
          onBlur={commitInput}
          onKeyDown={handleKeyDown}
          onChange={(e) => {
            const raw = e.target.value;
            setInputValue(raw);

            const parsed = parseFloat(raw);
            if (!isNaN(parsed) && !raw.endsWith(".")) {
              let next = parsed;
              if (integerOnly) next = Math.round(next);
              onChange(clamp(next, min, max));
            }
          }}
        />
        <button
          className="number-stepper__btn"
          type="button"
          onClick={() => adjust(step)}
          disabled={disabled || (max !== undefined && value >= max)}
          aria-label="Increase"
        >
          +
        </button>
      </div>
    </div>
  );
}
