/**
 * パラメータ設定ウィンドウのグラフ編集で使う式評価器。
 *
 * 式はシート general_params の graph_formula 列から届く。WebView の CSP が eval を許さないので、
 * 小さな再帰下降パーサで関数に組み立てる。解釈するのは数値・変数名・四則演算・べき乗(^)・括弧・
 * FORMULA_FUNCTIONS の呼び出しだけで、優先順位は Python と同じ(-x^2 は -(x^2)、^ は右結合)。
 * 使える構文と関数はコアの取り込みスクリプト(tools/create_parameters.py)が生成時に検査する集合と揃える。
 */

export type FormulaVars = Record<string, number>;
export type CompiledFormula = (vars: FormulaVars) => number;

const FORMULA_FUNCTIONS: Record<string, (...args: number[]) => number> = {
  exp: Math.exp,
  log: Math.log,
  sqrt: Math.sqrt,
  abs: Math.abs,
  min: Math.min,
  max: Math.max,
  pow: Math.pow,
};

type Token = { kind: "num"; value: number } | { kind: "name"; value: string } | { kind: "op"; value: string };

function tokenize(src: string): Token[] {
  const tokens: Token[] = [];
  const re = /\s*(?:(\d+\.?\d*(?:[eE][+-]?\d+)?|\.\d+(?:[eE][+-]?\d+)?)|([A-Za-z_][A-Za-z0-9_]*)|([-+*/^(),]))/y;
  let pos = 0;
  while (pos < src.length) {
    if (/^\s*$/.test(src.slice(pos))) break;
    re.lastIndex = pos;
    const m = re.exec(src);
    if (!m) throw new Error(`式を読めません: ${src.slice(pos)}`);
    if (m[1] !== undefined) tokens.push({ kind: "num", value: Number(m[1]) });
    else if (m[2] !== undefined) tokens.push({ kind: "name", value: m[2] });
    else tokens.push({ kind: "op", value: m[3] });
    pos = re.lastIndex;
  }
  return tokens;
}

export function compileFormula(src: string): CompiledFormula {
  const tokens = tokenize(src);
  let i = 0;
  const peekOp = (op: string) => tokens[i]?.kind === "op" && tokens[i].value === op;
  const expectOp = (op: string) => {
    if (!peekOp(op)) throw new Error(`「${op}」が必要です: ${src}`);
    i++;
  };

  function parseExpr(): CompiledFormula {
    let left = parseTerm();
    while (peekOp("+") || peekOp("-")) {
      const op = tokens[i++].value;
      const l = left, r = parseTerm();
      left = op === "+" ? (v) => l(v) + r(v) : (v) => l(v) - r(v);
    }
    return left;
  }

  function parseTerm(): CompiledFormula {
    let left = parseUnary();
    while (peekOp("*") || peekOp("/")) {
      const op = tokens[i++].value;
      const l = left, r = parseUnary();
      left = op === "*" ? (v) => l(v) * r(v) : (v) => l(v) / r(v);
    }
    return left;
  }

  function parseUnary(): CompiledFormula {
    if (peekOp("-")) {
      i++;
      const operand = parseUnary();
      return (v) => -operand(v);
    }
    if (peekOp("+")) {
      i++;
      return parseUnary();
    }
    return parsePower();
  }

  function parsePower(): CompiledFormula {
    const base = parsePrimary();
    if (!peekOp("^")) return base;
    i++;
    const exponent = parseUnary();
    return (v) => Math.pow(base(v), exponent(v));
  }

  function parsePrimary(): CompiledFormula {
    const tok = tokens[i++];
    if (!tok) throw new Error(`式が途中で終わっています: ${src}`);
    if (tok.kind === "num") {
      const value = tok.value;
      return () => value;
    }
    if (tok.kind === "name") {
      const name = tok.value;
      if (!peekOp("(")) return (v) => v[name] ?? NaN;
      const fn = FORMULA_FUNCTIONS[name];
      if (!fn) throw new Error(`使えない関数です: ${name}`);
      i++;
      const args: CompiledFormula[] = [];
      if (!peekOp(")")) {
        args.push(parseExpr());
        while (peekOp(",")) {
          i++;
          args.push(parseExpr());
        }
      }
      expectOp(")");
      return (v) => fn(...args.map((a) => a(v)));
    }
    if (tok.value === "(") {
      const inner = parseExpr();
      expectOp(")");
      return inner;
    }
    throw new Error(`「${tok.value}」の位置がおかしいです: ${src}`);
  }

  const compiled = parseExpr();
  if (i < tokens.length) throw new Error(`式の末尾に余分な記号があります: ${src}`);
  return compiled;
}
