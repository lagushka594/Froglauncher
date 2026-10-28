"""Проверка словарей: все ключи переведены, плейсхолдеры {0}, {1:0.#} совпадают с русскими, нет лишних ключей.

Запуск: python tools/Localize/check.py   (код выхода 1 — есть ошибки)
"""
import io, json, os, re, sys

LANG = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'RibbixLauncher', 'Lang')
HOLE = re.compile(r'(?<!\{)\{(\d+)(?:,[^}:]+)?(?::([^}]*))?\}(?!\})')

ru = json.load(io.open(os.path.join(LANG, 'ru.json'), encoding='utf-8'))
bad = 0
for code in ['en', 'es', 'de', 'pt-BR']:
    tr = json.load(io.open(os.path.join(LANG, code + '.json'), encoding='utf-8'))
    missing = [k for k in ru if not tr.get(k)]
    extra = [k for k in tr if k not in ru]
    holes = [k for k in ru if tr.get(k) and sorted(HOLE.findall(k)) != sorted(HOLE.findall(tr[k]))]
    for name, items in (('нет перевода', missing), ('лишний ключ', extra), ('плейсхолдеры', holes)):
        for k in items[:20]:
            print(f'{code}: {name}: {k!r}')
    bad += len(missing) + len(holes)
    print(f'{code}: {len(ru) - len(missing)}/{len(ru)} переведено, лишних {len(extra)}, ошибок плейсхолдеров {len(holes)}')
sys.exit(1 if bad else 0)
