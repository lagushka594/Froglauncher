"""Варианты кнопок без вшитой русской подписи (для других языков): подпись закрашивается цветом фона кнопки.

Запуск: python tools/Localize/blank_buttons.py — пишет *_blank.png рядом с исходниками в assets/out
(лаунчер берёт ассеты оттуда) и печатает средний цвет подписи (им рисуется переведённый текст поверх).
"""
import os
from PIL import Image

ROOT = os.path.join(os.path.dirname(__file__), '..', '..')
OUT = os.path.join(ROOT, 'assets', 'out')

# (файл, область поиска подписи в долях ширины/высоты)
BUTTONS = [
    ('ui/btn_home.png', (0.14, 0.58, 0.86, 0.86)),
    ('ui/btn_catalog.png', (0.12, 0.58, 0.88, 0.86)),
    ('ui/btn_profile.png', (0.12, 0.58, 0.88, 0.86)),
    ('ui/btn_settings.png', (0.12, 0.58, 0.88, 0.86)),
    ('ui/btn_help.png', (0.12, 0.60, 0.88, 0.86)),
    ('ui/btn_exit.png', (0.12, 0.60, 0.88, 0.86)),
    ('main/btn_play.png', (0.36, 0.28, 0.90, 0.72)),
]

def lum(p):
    r, g, b, a = p
    return (0.299 * r + 0.587 * g + 0.114 * b) * a / 255

for name, (fx0, fy0, fx1, fy1) in BUTTONS:
    src = os.path.join(OUT, name)
    if not os.path.exists(src):
        continue
    im = Image.open(src).convert('RGBA')
    w, h = im.size
    px = im.load()
    x0, y0, x1, y1 = int(w * fx0), int(h * fy0), int(w * fx1), int(h * fy1)
    # Фон внутри рамки — медиана яркости области; подпись — заметно ярче.
    vals = sorted(lum(px[x, y]) for x in range(x0, x1) for y in range(y0, y1))
    bg_l = vals[len(vals) // 3]
    text = [(x, y) for x in range(x0, x1) for y in range(y0, y1) if lum(px[x, y]) > bg_l + 60]
    if not text:
        print(name, 'подпись не найдена'); continue
    tx0, ty0 = min(p[0] for p in text) - 2, min(p[1] for p in text) - 2
    tx1, ty1 = max(p[0] for p in text) + 2, max(p[1] for p in text) + 2
    col = [px[x, y] for x, y in text]
    avg = tuple(sum(c[i] for c in col) // len(col) for i in range(3))
    for y in range(ty0, ty1 + 1):
        # Цвет фона в этой строке — медиана тёмных (не текстовых) пикселей слева и справа от подписи.
        def side(xs):
            c = sorted((px[x, y] for x in xs if lum(px[x, y]) < bg_l + 25), key=lum)
            return c[len(c) // 2] if c else None
        left = side(range(max(0, tx0 - 8), tx0)) or side(range(tx1 + 1, min(w, tx1 + 9))) or px[tx0, y]
        right = side(range(tx1 + 1, min(w, tx1 + 9))) or left
        for x in range(tx0, tx1 + 1):
            t = (x - tx0) / max(1, tx1 - tx0)
            px[x, y] = tuple(int(left[i] * (1 - t) + right[i] * t) for i in range(3)) + (255,)
    base = name[:-4] + '_blank.png'
    im.save(os.path.join(OUT, base))
    print(f'{base}: подпись {tx0},{ty0}–{tx1},{ty1} из {w}×{h}, цвет #{avg[0]:02X}{avg[1]:02X}{avg[2]:02X}, '
          f'отступ сверху {ty0 / h:.2f}, высота {(ty1 - ty0) / h:.2f}')
