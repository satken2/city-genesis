"""全言語の翻訳キーが日本語(原本)と一致しているか検査する。"""
import json, os, glob, sys

I18N = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "i18n")
LANGS = ["ja", "en", "es-419", "zh-Hans", "zh-Hant", "fr", "ko"]


def leaves(d, prefix, out):
    for k, v in d.items():
        p = "%s.%s" % (prefix, k) if prefix else k
        if isinstance(v, dict):
            leaves(v, p, out)
        else:
            out[p] = v


def load(lang):
    out = {}
    files = sorted(glob.glob(os.path.join(I18N, lang, "*.json")))
    for f in files:
        try:
            leaves(json.load(open(f, encoding="utf-8")), "", out)
        except Exception as e:
            print("PARSE ERROR", lang, os.path.basename(f), e)
            raise
    return out, [os.path.basename(f) for f in files]


base, base_files = load("ja")
print("ja: %d keys / %d files" % (len(base), len(base_files)))

ok = True
for lang in LANGS[1:]:
    cur, files = load(lang)
    missing = sorted(set(base) - set(cur))
    extra = sorted(set(cur) - set(base))
    missing_files = sorted(set(base_files) - set(files))
    extra_files = sorted(set(files) - set(base_files))
    status = "OK" if not (missing or extra or missing_files or extra_files) else "NG"
    if status == "NG":
        ok = False
    print("%-8s %d keys / %d files  %s" % (lang, len(cur), len(files), status))
    for f in missing_files:
        print("    MISSING FILE:", f)
    for f in extra_files:
        print("    EXTRA FILE:", f)
    for k in missing:
        print("    MISSING KEY:", k)
    for k in extra:
        print("    EXTRA KEY:", k)

# 補間プレースホルダの整合（{{name}} の集合が原本と一致するか）
import re

PH = re.compile(r"\{\{\s*([A-Za-z0-9_]+)")
for lang in LANGS[1:]:
    cur, _ = load(lang)
    for k, v in base.items():
        if k not in cur:
            continue
        want = set(PH.findall(v)) if isinstance(v, str) else set()
        got = set(PH.findall(cur[k])) if isinstance(cur[k], str) else set()
        if want != got:
            ok = False
            print("    PLACEHOLDER MISMATCH [%s] %s: ja=%s %s=%s" % (lang, k, sorted(want), lang, sorted(got)))

print("RESULT:", "OK" if ok else "NG")
sys.exit(0 if ok else 1)
