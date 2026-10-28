"""Переводит русские строки XAML в {l:T '...'} и собирает ключи словаря.

Запуск (из корня репозитория): python tools/Localize/xaml_to_t.py
Повторный запуск безопасен: уже обёрнутые значения не трогаются. Ключи — в tools/Localize/keys_xaml.json.
"""
import glob, html, io, json, os, re

ROOT = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'RibbixLauncher')
ATTR = re.compile(r'\b(Text|Content|Header|ToolTip|Title)="([^"{]*[А-Яа-яЁё][^"]*)"')
XMLNS = 'xmlns:l="clr-namespace:RibbixLauncher.Core"'

keys = set()
for f in glob.glob(os.path.join(ROOT, 'Views', '*.xaml')) + glob.glob(os.path.join(ROOT, 'Themes', '*.xaml')):
    s = io.open(f, encoding='utf-8').read()

    def repl(m):
        raw = m.group(2)
        keys.add(html.unescape(raw))
        arg = raw.replace('\\', '\\\\').replace("'", "\\'")
        return f'{m.group(1)}="{{l:T \'{arg}\'}}"'

    s2 = ATTR.sub(repl, s)
    # Особые случаи внутри привязок.
    s2 = s2.replace("TargetNullValue='не выбран'", "TargetNullValue={l:T 'не выбран'}")
    if "{l:T" in s2 and XMLNS not in s2:
        s2 = re.sub(r'(xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml")', r'\1\n             ' + XMLNS, s2, count=1)
    for m in re.finditer(r"\{l:T '((?:[^'\\]|\\.)*)'\}", s2):
        keys.add(html.unescape(m.group(1).replace("\\'", "'").replace('\\\\', '\\')))
    if s2 != s:
        io.open(f, 'w', encoding='utf-8').write(s2)

out = os.path.join(os.path.dirname(__file__), 'keys_xaml.json')
json.dump(sorted(keys), io.open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(len(keys), 'ключей XAML')
