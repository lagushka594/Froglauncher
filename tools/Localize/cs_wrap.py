"""Оборачивает русские строки C# в L.T(...) и собирает ключи словаря (как их построит LocHandler).

Запуск: python tools/Localize/cs_wrap.py [--dry]
Не трогает: логи (Log*), const, атрибуты, case/is-шаблоны, левые части switch-выражений,
строки с пометкой // noloc на той же строке и уже обёрнутые L.T(...).
"""
import glob, io, json, os, re, sys

ROOT = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'RibbixLauncher')
DIRS = ['ViewModels', 'Views', 'Services', 'Models', 'Core']
SKIP_FILES = {'Localization.cs'}
CYR = re.compile('[А-Яа-яЁё]')
DRY = '--dry' in sys.argv

def unescape(s):
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            n = s[i + 1]
            m = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', "'": "'", '\\': '\\', '0': '\0'}
            if n in m:
                out.append(m[n]); i += 2; continue
            if n == 'u':
                out.append(chr(int(s[i + 2:i + 6], 16))); i += 6; continue
        out.append(c); i += 1
    return ''.join(out)

def scan_string(src, i):
    """src[i] — начало литерала (", $", @", $@", @$"). Возвращает (конец, interpolated, verbatim)."""
    j = i
    interp = verb = False
    while src[j] in '$@':
        interp |= src[j] == '$'; verb |= src[j] == '@'; j += 1
    assert src[j] == '"'
    if src[j:j + 3] == '"""':
        raise ValueError('raw string literal')
    j += 1
    depth = 0
    while True:
        c = src[j]
        if interp and depth == 0 and c == '{':
            if src[j + 1] == '{': j += 2; continue
            depth = 1; j += 1; continue
        if depth > 0:
            if c == '{': depth += 1
            elif c == '}': depth -= 1
            elif c == '"' or (c in '$@' and src[j + 1] in '"$@'):
                j = scan_string(src, j)[0]; continue
            elif c == "'":
                j = skip_char(src, j); continue
            j += 1; continue
        if c == '\\' and not verb: j += 2; continue
        if c == '"':
            if verb and src[j + 1] == '"': j += 2; continue
            return j + 1, interp, verb
        if interp and c == '}' and src[j + 1] == '}': j += 2; continue
        j += 1

def skip_char(src, j):
    j += 1
    if src[j] == '\\': j += 2
    else: j += 1
    while src[j] != "'": j += 1
    return j + 1

def build_key(body, interp, verb):
    """Ключ, который соберёт LocHandler (или сама строка для обычного литерала)."""
    if not interp:
        return body.replace('""', '"') if verb else unescape(body)
    key, i, n = [], 0, 0
    lit = []
    def flush():
        t = ''.join(lit)
        t = t.replace('""', '"') if verb else unescape(t)
        key.append(t.replace('{', '{{').replace('}', '}}')); lit.clear()
    while i < len(body):
        c = body[i]
        if c == '{' and body[i + 1:i + 2] == '{': lit.append('{'); i += 2; continue
        if c == '}' and body[i + 1:i + 2] == '}': lit.append('}'); i += 2; continue
        if c == '{':
            flush()
            depth, j, colon, comma = 1, i + 1, None, None
            paren = 0
            while depth:
                ch = body[j]
                if ch in '([': paren += 1
                elif ch in ')]': paren -= 1
                elif ch == '"' or (ch in '$@' and body[j + 1] in '"$@'):
                    j = scan_string(body, j)[0]; continue
                elif ch == "'": j = skip_char(body, j); continue
                elif ch == '{': depth += 1
                elif ch == '}':
                    depth -= 1
                    if depth == 0: break
                elif ch == ':' and paren == 0 and depth == 1 and colon is None: colon = j
                elif ch == ',' and paren == 0 and depth == 1 and colon is None and comma is None: comma = j
                j += 1
            hole = '{' + str(n)
            if comma is not None:
                hole += ',' + body[comma + 1:(colon if colon is not None else j)].strip()
            if colon is not None:
                hole += ':' + body[colon + 1:j]
            key.append(hole + '}'); n += 1
            i = j + 1; continue
        lit.append(c); i += 1
    flush()
    return ''.join(key)

def statement_prefix(src, i):
    k = max(src.rfind(';', 0, i), src.rfind('{', 0, i), src.rfind('}', 0, i))
    return src[k + 1:i]

def excluded(src, start, end):
    line_start = src.rfind('\n', 0, start) + 1
    line_end = src.find('\n', end)
    line = src[line_start:line_end if line_end >= 0 else len(src)]
    if 'noloc' in line: return True
    stripped = line.lstrip()
    # Внутри атрибута ([Description("…")]) — нельзя; после него ([ObservableProperty] … = "…") — можно.
    if stripped.startswith('['):
        close = line.find(']')
        if close < 0 or line_start + close > start: return True
    before = src[line_start:start]
    prefix = statement_prefix(src, start)
    if re.search(r'\bconst\b', prefix): return True
    if re.search(r'\bcase\s*$', before) or re.search(r'\bis\s*(not\s+)?$', before) or re.search(r'\bor\s*$', before): return True
    if re.search(r'\b(Log(Information|Warning|Error|Debug|Critical|Trace)|Log\.(Information|Warning|Error|Debug|Fatal))\s*\(', prefix): return True
    if re.match(r'\s*=>', src[end:end + 4]): return True  # левая часть switch-выражения
    if re.search(r'L\.T\(\s*$', before): return True
    if re.search(r'nameof\(|GetManifestResourceStream\(|"pack://', before): return True
    return False

all_keys = set()
changed = 0
for d in DIRS:
    for f in glob.glob(os.path.join(ROOT, d, '**', '*.cs'), recursive=True):
        if os.path.basename(f) in SKIP_FILES: continue
        src = io.open(f, encoding='utf-8').read()
        out, i, edits = [], 0, 0
        while i < len(src):
            c = src[i]
            if src.startswith('//', i):
                j = src.find('\n', i); j = len(src) if j < 0 else j
                out.append(src[i:j]); i = j; continue
            if src.startswith('/*', i):
                j = src.find('*/', i) + 2
                out.append(src[i:j]); i = j; continue
            if c == "'":
                j = skip_char(src, i); out.append(src[i:j]); i = j; continue
            if c == '"' or (c in '$@' and i + 1 < len(src) and src[i + 1] in '"$@'):
                j, interp, verb = scan_string(src, i)
                lit = src[i:j]
                q = lit.index('"')
                body = lit[q + 1:-1]
                if CYR.search(body) and not excluded(src, i, j):
                    all_keys.add(build_key(body, interp, verb))
                    out.append(f'L.T({lit})'); edits += 1
                elif CYR.search(body):
                    out.append(lit)
                else:
                    out.append(lit)
                i = j; continue
            out.append(c); i += 1
        new = ''.join(out)
        if edits and new != src:
            if 'using RibbixLauncher.Core;' not in new and not re.search(r'namespace RibbixLauncher\.Core\b', new):
                new = 'using RibbixLauncher.Core;\n' + new
            changed += 1
            if not DRY: io.open(f, 'w', encoding='utf-8').write(new)

# Повторный запуск: ключи уже обёрнутых строк тоже собираем.
for d in DIRS:
    for f in glob.glob(os.path.join(ROOT, d, '**', '*.cs'), recursive=True):
        src = io.open(f, encoding='utf-8').read()
        for m in re.finditer(r'L\.T\(', src):
            k = m.end()
            if k < len(src) and (src[k] == '"' or src[k] in '$@'):
                try:
                    j, interp, verb = scan_string(src, k)
                except (ValueError, AssertionError, IndexError):
                    continue
                lit = src[k:j]; q = lit.index('"')
                all_keys.add(build_key(lit[q + 1:-1], interp, verb))

json.dump(sorted(all_keys), io.open(os.path.join(os.path.dirname(__file__), 'keys_cs.json'), 'w', encoding='utf-8'),
          ensure_ascii=False, indent=1)
print(changed, 'файлов,', len(all_keys), 'ключей C#')
