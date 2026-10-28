"""Собирает все ключи в src/RibbixLauncher/Lang/ru.json (исходник для Crowdin) и показывает, чего не хватает в переводах.

Запуск после xaml_to_t.py и cs_wrap.py: python tools/Localize/collect.py
"""
import glob, io, json, os, re

HERE = os.path.dirname(__file__)
REPO = os.path.join(HERE, '..', '..')
LANG = os.path.join(REPO, 'src', 'RibbixLauncher', 'Lang')
CYR = re.compile('[А-Яа-яЁё]')

keys = set(json.load(io.open(os.path.join(HERE, 'keys_xaml.json'), encoding='utf-8')))
keys |= set(json.load(io.open(os.path.join(HERE, 'keys_cs.json'), encoding='utf-8')))

# Сообщения API и общих библиотек: показываются в тостах как есть — переводим по точному совпадению.
for f in glob.glob(os.path.join(REPO, 'src', 'Ribbix.Api', '**', '*.cs'), recursive=True) + \
         glob.glob(os.path.join(REPO, 'src', 'Ribbix.Shared', '*.cs')):
    if os.sep + 'obj' + os.sep in f or '/obj/' in f: continue
    for line in io.open(f, encoding='utf-8'):
        if re.search(r'\bLog|//|///', line.split('"')[0]): continue
        if not re.search(r'Exception\(|=>\s*"|return\s+"', line): continue
        for m in re.finditer(r'(?<![$@])"((?:[^"\\]|\\.)*)"', line):
            if CYR.search(m.group(1)): keys.add(m.group(1).replace('\\"', '"'))

# Названия акций из promos.json.
promos = json.load(io.open(os.path.join(REPO, 'promos.json'), encoding='utf-8'))
for e in promos.get('events', []):
    keys.add(e['title'])

# Слова, которые приходят в переводимые строки аргументами (склонения) и категории палитры.
keys |= {'день', 'дня', 'дней', 'сессия', 'сессии', 'сессий', 'запуск', 'запуска', 'запусков', 'скриншот', 'скриншота', 'скриншотов', 'марафон', 'марафона', 'марафонов', 'Действие', 'Страница', 'Настройка', 'Мод', 'Поиск'}
# Подписи пунктов меню и кнопки «Играть» (раньше — ArtLabel поверх картинок атласа).
keys |= {'Главная', 'Каталог', 'Профиль', 'Настройки', 'Помощь', 'Выход', 'Играть'}
# Подписи скидок, которые передаются через Discount.Label.
keys |= {'Новогодний подарок'}

keys = sorted(k for k in keys if CYR.search(k))
os.makedirs(LANG, exist_ok=True)
json.dump({k: k for k in keys}, io.open(os.path.join(LANG, 'ru.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(len(keys), 'ключей → Lang/ru.json')

for code in ['en', 'es', 'de', 'pt-BR']:
    p = os.path.join(LANG, code + '.json')
    have = json.load(io.open(p, encoding='utf-8')) if os.path.exists(p) else {}
    missing = [k for k in keys if not have.get(k)]
    print(f'{code}: переведено {len(keys) - len(missing)}/{len(keys)}')
    json.dump(missing, io.open(os.path.join(HERE, f'missing_{code}.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
