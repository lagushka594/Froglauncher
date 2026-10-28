"""Сценарий 4 «Сезонные темы» — покадровый рендер 1080x1920 @30fps, 22.6 с.

Темы и эффекты — как в лаунчере: Services/ThemeService.cs (цвета) и Views/SeasonEffects.cs
(лепестки, блики+стрекоза, туман+листья, иней+снег, снег+гирлянда).
Запуск:  python render.py [out.mp4] [--only 5.0,9.0]
"""
import math
import random
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

sys.path.insert(0, str(Path(__file__).parent.parent / "scenario3"))
from render import (asset, clamp, emo, font, lerp, mix, out_back, out_cubic,  # noqa: E402
                    prog, smooth, ui, uib)

HERE = Path(__file__).parent
W, H, FPS, DUR = 1080, 1920, 30, 22.6

# Кадры сценария (сдвинуты под длину озвучки: реплики ~25 с при плане в 20 с)
T_SPRING, T_SUMMER, T_AUTUMN, T_WINTER, T_NY, T_CTA = 4.15, 8.8, 12.95, 16.55, 17.95, 20.4
XF = 0.45  # полуширина плавного перехода между темами


def hx(c):
    return tuple(int(c[i:i + 2], 16) for i in (1, 3, 5))


# ThemeService.cs: ключ, подпись, Accent, Accent2, Bg
THEMES = {
    "spring": ("🌸 Цветение", hx("#4ADE80"), hx("#F472B6"), hx("#0F1412")),
    "summer": ("☀️ Пруд", hx("#10B981"), hx("#FACC15"), hx("#06140F")),
    "autumn": ("🍂 Листопад", hx("#F59E0B"), hx("#DC2626"), hx("#140C06")),
    "winter": ("❄️ Спящий пруд", hx("#7DD3FC"), hx("#E0F2FE"), hx("#07101A")),
    "newyear": ("🎄 Ква-Новый год", hx("#DC2626"), hx("#16A34A"), hx("#120808")),
}
TEXT = (231, 248, 236)
MUTED = (150, 165, 180)

# ====================================================================
# Пиксельная сцена «пруд» для баннера (рисуем в 250x225 и увеличиваем x4)
# ====================================================================
LW, LH, PX = 250, 225, 4
HERO_W, HERO_H = LW * PX, LH * PX

SKY = {
    "spring": ((176, 226, 255), (255, 228, 241)),
    "summer": ((110, 200, 250), (209, 250, 229)),
    "autumn": ((246, 170, 110), (252, 222, 170)),
    "winter": ((190, 204, 222), (240, 249, 255)),
    "newyear": ((12, 18, 52), (62, 26, 74)),
}
HILLS = {
    "spring": ((170, 220, 190), (110, 200, 120)),
    "summer": ((60, 190, 150), (22, 160, 100)),
    "autumn": ((205, 120, 60), (170, 80, 30)),
    "winter": ((226, 232, 240), (245, 248, 252)),
    "newyear": ((70, 80, 130), (150, 165, 205)),
}
WATER = {
    "spring": ((120, 190, 220), (60, 130, 170)),
    "summer": ((40, 190, 180), (8, 110, 120)),
    "autumn": ((170, 120, 80), (90, 60, 40)),
    "winter": ((200, 232, 250), (170, 210, 240)),
    "newyear": ((40, 55, 110), (20, 25, 60)),
}
BANK = {
    "spring": (74, 200, 110), "summer": (30, 150, 80), "autumn": (150, 90, 20),
    "winter": (240, 246, 252), "newyear": (200, 210, 235),
}
POND_Y = 142


def hero_base(season):
    rnd = random.Random(4)
    im = Image.new("RGB", (LW, LH))
    d = ImageDraw.Draw(im)
    top, bot = SKY[season]
    for y in range(POND_Y):
        d.line([(0, y), (LW, y)], fill=mix(top, bot, y / POND_Y))
    # светила
    if season == "newyear":
        for _ in range(60):
            x, y = rnd.randrange(LW), rnd.randrange(POND_Y - 30)
            d.point((x, y), fill=(255, 255, 230) if rnd.random() < 0.6 else (180, 200, 255))
        d.ellipse([186, 18, 210, 42], fill=(250, 245, 220))
        d.ellipse([194, 14, 218, 38], fill=SKY[season][0])
    else:
        sun = {"spring": ((255, 245, 200), 196, 34, 14), "summer": ((255, 236, 120), 200, 30, 17),
               "autumn": ((255, 190, 110), 60, 70, 20), "winter": ((250, 252, 255), 190, 40, 13)}[season]
        c, sx, sy, r = sun
        d.ellipse([sx - r - 4, sy - r - 4, sx + r + 4, sy + r + 4], fill=mix(c, SKY[season][1], 0.5))
        d.ellipse([sx - r, sy - r, sx + r, sy + r], fill=c)
    # холмы: дальний и ближний
    for k, (base, amp, fr, col) in enumerate([(98, 18, 2.2, HILLS[season][0]), (118, 12, 3.4, HILLS[season][1])]):
        ph = rnd.uniform(0, 6)
        pts = [(x, base - amp * (0.6 * math.sin(x / LW * fr * math.pi + ph) + 0.4 * math.sin(x / LW * fr * 2.7 * math.pi)))
               for x in range(0, LW + 1, 2)]
        d.polygon(pts + [(LW, POND_Y), (0, POND_Y)], fill=col)
    if season in ("winter", "newyear"):  # снежные шапки
        pass
    # берег
    d.rectangle([0, POND_Y - 6, LW, POND_Y + 2], fill=BANK[season])
    # пруд
    wt, wb = WATER[season]
    for y in range(POND_Y + 2, LH):
        d.line([(0, y), (LW, y)], fill=mix(wt, wb, (y - POND_Y) / (LH - POND_Y)))
    if season == "winter":
        for _ in range(9):  # трещины льда
            x, y = rnd.randrange(20, LW - 20), rnd.randrange(POND_Y + 10, LH - 10)
            pts = [(x, y)]
            for _ in range(4):
                x += rnd.randint(-9, 9)
                y += rnd.randint(-4, 4)
                pts.append((x, y))
            d.line(pts, fill=(255, 255, 255))
        for y in range(POND_Y + 2, LH, 9):
            d.line([(rnd.randrange(LW), y), (rnd.randrange(LW), y)], fill=(235, 246, 255))
    else:
        for _ in range(40):  # рябь
            x, y = rnd.randrange(LW), rnd.randrange(POND_Y + 6, LH)
            d.line([(x, y), (x + rnd.randint(4, 12), y)], fill=mix(wt, (255, 255, 255), 0.25))
    # дерево слева
    tx = 46
    if season == "newyear":
        # ёлка
        for i, (wd, y0) in enumerate([(44, 56), (56, 76), (68, 98), (80, 120)]):
            d.polygon([(tx, y0 - 30), (tx - wd // 2, y0), (tx + wd // 2, y0)], fill=(22, 101, 52))
            d.polygon([(tx, y0 - 30), (tx - wd // 2, y0), (tx - wd // 2 + 6, y0)], fill=(240, 246, 255))
        d.rectangle([tx - 4, 120, tx + 4, POND_Y - 4], fill=(90, 50, 20))
        d.polygon([(tx, 18), (tx + 4, 27), (tx + 10, 27), (tx + 5, 32), (tx + 7, 39), (tx, 35), (tx - 7, 39),
                   (tx - 5, 32), (tx - 10, 27), (tx - 4, 27)], fill=(250, 204, 21))
    else:
        trunk = (90, 60, 40) if season != "winter" else (70, 60, 60)
        d.rectangle([tx - 4, 70, tx + 4, POND_Y - 4], fill=trunk)
        for ang, ln in [(-0.7, 30), (0.6, 28), (-0.2, 24), (1.1, 22), (-1.2, 20)]:
            ex, ey = tx + math.sin(ang) * ln, 80 - math.cos(ang) * ln
            d.line([(tx, 84), (ex, ey)], fill=trunk, width=3)
            if season == "winter":
                d.line([(tx, 82), (ex, ey - 2)], fill=(255, 255, 255), width=1)
        crown = {"spring": [(251, 207, 232), (249, 168, 212), (244, 114, 182), (255, 228, 241)],
                 "summer": [(34, 140, 80), (22, 120, 60), (52, 170, 90)],
                 "autumn": [(245, 158, 11), (220, 38, 38), (234, 88, 12), (251, 191, 36)]}.get(season)
        if crown:
            for _ in range(70):
                a = rnd.uniform(0, math.tau)
                r = rnd.uniform(0, 34)
                cx, cy = tx + math.cos(a) * r * 1.2, 58 + math.sin(a) * r * 0.75
                s = rnd.randint(3, 7)
                d.ellipse([cx - s, cy - s, cx + s, cy + s], fill=rnd.choice(crown))
            if season == "spring":  # опавшие лепестки на воде
                for _ in range(30):
                    x, y = rnd.randrange(LW), rnd.randrange(POND_Y + 4, LH)
                    d.point((x, y), fill=(251, 207, 232))
            if season == "autumn":
                for _ in range(40):
                    x, y = rnd.randrange(LW), rnd.randrange(POND_Y + 4, LH)
                    d.rectangle([x, y, x + 1, y], fill=rnd.choice(crown))
    # второе дерево справа поменьше
    if season in ("spring", "summer", "autumn"):
        t2 = 214
        d.rectangle([t2 - 2, 96, t2 + 2, POND_Y - 4], fill=(90, 60, 40))
        crown = {"spring": [(249, 168, 212), (251, 207, 232)], "summer": [(34, 140, 80), (52, 170, 90)],
                 "autumn": [(220, 38, 38), (245, 158, 11)]}[season]
        for _ in range(30):
            a, r = rnd.uniform(0, math.tau), rnd.uniform(0, 18)
            cx, cy = t2 + math.cos(a) * r, 90 + math.sin(a) * r * 0.7
            s = rnd.randint(2, 5)
            d.ellipse([cx - s, cy - s, cx + s, cy + s], fill=rnd.choice(crown))
    # камыши
    reed = {"spring": (40, 130, 60), "summer": (20, 110, 50), "autumn": (140, 100, 40),
            "winter": (120, 110, 100), "newyear": (60, 70, 90)}[season]
    for x in list(range(4, 30, 4)) + list(range(226, 248, 4)):
        hgt = rnd.randint(20, 40)
        d.line([(x, POND_Y + 6), (x + rnd.randint(-2, 2), POND_Y + 6 - hgt)], fill=reed, width=1)
        if rnd.random() < 0.5:
            d.rectangle([x - 1, POND_Y + 6 - hgt - 6, x + 1, POND_Y + 6 - hgt], fill=(110, 70, 40))
            if season in ("winter", "newyear"):
                d.point((x, POND_Y + 6 - hgt - 7), fill=(255, 255, 255))
    # кувшинки
    if season in ("spring", "summer"):
        for (x, y, r) in [(90, 170, 9), (130, 195, 7), (70, 205, 8), (160, 165, 6)]:
            d.ellipse([x - r, y - r // 3, x + r, y + r // 3], fill=(40, 150, 70))
            d.polygon([(x, y), (x + r, y - 1), (x + r, y + 1)], fill=wt)
            if season == "summer" and r > 7:
                d.ellipse([x - 2, y - 4, x + 2, y], fill=(249, 168, 212))
    # кувшинка лягушки
    pad = (40, 150, 70) if season in ("spring", "summer") else (
        (120, 110, 40) if season == "autumn" else (235, 242, 250))
    d.ellipse([170, 196, 236, 214], fill=pad)
    d.polygon([(203, 205), (236, 202), (236, 208)], fill=wb)
    im = im.resize((HERO_W, HERO_H), Image.NEAREST)
    return im.convert("RGBA")


RIBBIX_POS = (HERO_W - 300, HERO_H - 170)  # где сидит лягушка (верх-лево спрайта)
TREE_X = 46 * PX


def santa_hat(im, x, y, s=1.0):
    d = ImageDraw.Draw(im)
    d.polygon([(x, y + 34 * s), (x + 100 * s, y + 34 * s), (x + 118 * s, y - 40 * s)], fill=(220, 38, 38))
    d.rounded_rectangle([x - 8 * s, y + 24 * s, x + 108 * s, y + 46 * s], 10, fill=(250, 250, 250))
    d.ellipse([x + 106 * s, y - 54 * s, x + 132 * s, y - 28 * s], fill=(250, 250, 250))


# ====================================================================
# Окно лаунчера: шапка, баннер-пруд, «Играть», карточки
# ====================================================================
CW, CH = 1000, 1440
HERO_Y = 110
_cache = {}


def window_static(season):
    if season in _cache:
        return _cache[season]
    name, acc, acc2, bg = THEMES[season]
    img = Image.new("RGBA", (CW, CH), bg + (255,))
    d = ImageDraw.Draw(img)
    img.alpha_composite(asset("ui/icon_frog.png", 0.58), (24, 18))
    d.text((100, 30), "RibbixLauncher", font=uib(38), fill=TEXT)
    # чип темы
    f = uib(28)
    e, rest = name.split(" ", 1)
    tw = d.textlength(rest, font=f) + 90
    d.rounded_rectangle([CW - 28 - tw, 26, CW - 28, 84], 29, fill=mix(bg, acc, 0.22), outline=acc, width=2)
    d.text((CW - 28 - tw + 20, 34), e, font=emo(28), embedded_color=True)
    d.text((CW - 28 - tw + 64, 36), rest, font=f, fill=TEXT)
    # баннер
    hero = hero_base(season).crop(((HERO_W - (CW - 56)) // 2, 0, (HERO_W + CW - 56) // 2, HERO_H))
    mask = Image.new("L", hero.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, hero.width - 1, hero.height - 1], 28, fill=255)
    img.paste(hero, (28, HERO_Y), mask)
    # подпись на баннере
    sh = Image.new("RGBA", (CW, 200), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([60, 40, 560, 170], 24, fill=(0, 0, 0, 120))
    img.alpha_composite(sh, (0, HERO_Y + 20))
    d.text((90, HERO_Y + 74), "СЕЗОННАЯ ТЕМА", font=uib(24), fill=acc2)
    d.text((90, HERO_Y + 106), rest, font=uib(48), fill=(255, 255, 255))
    y = HERO_Y + HERO_H + 30
    # «Играть» + версия
    pb = asset("main/btn_play.png", 1.35)
    img.alpha_composite(pb, (28, y))
    d.rounded_rectangle([420, y + 26, CW - 28, y + 160], 22, fill=mix(bg, (255, 255, 255), 0.06),
                        outline=mix(bg, acc, 0.6), width=2)
    d.text((450, y + 48), "Версия", font=ui(24), fill=MUTED)
    d.text((450, y + 82), "1.21.1 · Fabric", font=uib(36), fill=TEXT)
    y += 210
    # карточки
    cw2 = (CW - 56 - 20) / 2
    for i, (ic, t1, t2) in enumerate([("📦", "Мои версии", "3 сборки"), ("🧩", "Каталог модов", "Modrinth")]):
        x0 = 28 + i * (cw2 + 20)
        d.rounded_rectangle([x0, y, x0 + cw2, y + 150], 24, fill=mix(bg, (255, 255, 255), 0.05),
                            outline=mix(bg, acc, 0.45), width=2)
        d.text((x0 + 26, y + 30), ic, font=emo(46), embedded_color=True)
        d.text((x0 + 100, y + 30), t1, font=uib(32), fill=TEXT)
        d.text((x0 + 100, y + 78), t2, font=ui(26), fill=MUTED)
    _cache[season] = img
    return img


def window(season, t):
    """Окно + анимированные детали (лягушка, блики, гирлянда)."""
    img = window_static(season).copy()
    d = ImageDraw.Draw(img)
    hx0 = 28 - (HERO_W - (CW - 56)) // 2
    # лягушка на кувшинке, лёгкое покачивание
    frog = asset("ui/frog_idle.png" if season == "winter" else "ui/frog_happy.png", 2.4)
    fx = hx0 + RIBBIX_POS[0]
    fy = HERO_Y + RIBBIX_POS[1] - 30 + round(math.sin(t * 2.2) * 4)
    img.alpha_composite(frog, (fx, fy))
    if season == "newyear":
        santa_hat(img, fx + 38, fy + 6, 0.95)
    if season == "winter":  # «спит»: Zzz
        for k in range(3):
            p = (t * 0.6 + k / 3) % 1
            d.text((fx + 190 + p * 40, fy - 10 - p * 90), "z", font=uib(26 + k * 6),
                   fill=(224, 242, 254, round(255 * math.sin(p * math.pi))))
    # блики на воде
    rnd = random.Random(9)
    n = 26 if season == "summer" else 10
    for i in range(n):
        x = hx0 + rnd.uniform(60, HERO_W - 60)
        y = HERO_Y + rnd.uniform(POND_Y * PX + 20, HERO_H - 20)
        a = max(0.0, math.sin(t * rnd.uniform(1.5, 3.5) + rnd.uniform(0, 6)))
        if season in ("winter", "autumn"):
            a *= 0.5
        if a > 0.05 and 40 < x < CW - 40:
            s = rnd.uniform(6, 14) * a
            col = (255, 255, 230, round(230 * a))
            d.polygon([(x - s * 1.8, y), (x, y - s * 0.5), (x + s * 1.8, y), (x, y + s * 0.5)], fill=col)
    # гирлянда на ёлке и по верху окна (мигает)
    if season == "newyear":
        bulbs = [(220, 38, 38), (250, 204, 21), (34, 197, 94), (59, 130, 246)]
        for k in range(22):
            x = 30 + k * (CW - 60) / 21
            y = 98 + math.sin(k / 21 * math.pi * 3) * 8 + 8
            on = (int(t * 3) + k) % 3 != 0
            c = bulbs[k % 4]
            if on:
                d.ellipse([x - 14, y - 14, x + 14, y + 14], fill=c + (70,))
            d.ellipse([x - 7, y - 8, x + 7, y + 8], fill=c if on else mix(c, (40, 40, 40), 0.6))
        rnd = random.Random(2)
        for k in range(18):
            tier = k % 4
            y = HERO_Y + (60 + tier * 22 + rnd.uniform(-6, 14)) * PX
            half = (22 + tier * 6) * PX * ((y - HERO_Y) / PX - 26) / ((60 + tier * 22) - 26 + 1) * 0.9
            x = hx0 + TREE_X + rnd.uniform(-1, 1) * min(half, (18 + tier * 8) * PX)
            on = (int(t * 4) + k) % 2 == 0
            c = bulbs[k % 4]
            if on:
                d.ellipse([x - 13, y - 13, x + 13, y + 13], fill=c + (80,))
            d.ellipse([x - 6, y - 6, x + 6, y + 6], fill=c if on else mix(c, (30, 30, 30), 0.6))
    return img


# ====================================================================
# Частицы на весь кадр (как SeasonEffects)
# ====================================================================
PETALS = [hx(c) for c in ("#FBCFE8", "#F9A8D4", "#FFE4F1", "#F472B6")]
LEAVES = [hx(c) for c in ("#F59E0B", "#DC2626", "#EA580C", "#B45309", "#FBBF24")]


def rot(pts, ang, cx, cy, s):
    c, si = math.cos(ang), math.sin(ang)
    return [(cx + (x * c - y * si) * s, cy + (x * si + y * c) * s) for x, y in pts]


PETAL = [(0, -6), (3.5, -3.5), (4, 1), (0, 6), (-4, 1), (-3.5, -3.5)]
MAPLE = [(0, -9), (2, -4), (7, -6), (5, -1), (9, 1), (4, 3), (3, 8), (0, 5), (-3, 8), (-4, 3), (-9, 1),
         (-5, -1), (-7, -6), (-2, -4)]


def particles(frame, season, t, alpha=1.0):
    ov = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)
    rnd = random.Random(17)
    if season in ("spring", "autumn", "winter", "newyear"):
        n = {"spring": 34, "autumn": 26, "winter": 70, "newyear": 60}[season]
        for i in range(n):
            x0, y0 = rnd.uniform(-50, W + 50), rnd.uniform(0, H + 200)
            v = rnd.uniform(70, 150) if season in ("spring", "autumn") else rnd.uniform(50, 130)
            sway, fr, ph = rnd.uniform(20, 60), rnd.uniform(0.6, 1.4), rnd.uniform(0, 6.28)
            size = rnd.uniform(2.2, 4.2)
            y = (y0 + v * t) % (H + 200) - 100
            x = x0 + math.sin(t * fr + ph) * sway + (t * 20 if season != "winter" else 0)
            if season == "spring":
                ang = t * rnd.uniform(1, 2.5) + ph
                d.polygon(rot(PETAL, ang, x, y, size), fill=rnd.choice(PETALS) + (round(235 * alpha),))
            elif season == "autumn":
                ang = t * rnd.uniform(-2, 2) + ph
                d.polygon(rot(MAPLE, ang, x, y, size * 1.3), fill=rnd.choice(LEAVES) + (round(240 * alpha),))
            else:
                r = rnd.uniform(3, 9)
                d.ellipse([x - r, y - r, x + r, y + r], fill=(240, 249, 255, round(rnd.uniform(150, 240) * alpha)))
    if season == "summer":  # стрекоза пролетает
        p = ((t - T_SUMMER + 0.3) / 3.4)
        if 0 < p < 1:
            x = lerp(-80, W + 80, p)
            y = 980 + math.sin(p * 9) * 110 - p * 160
            flap = abs(math.sin(t * 40))
            s = 5.0
            body = hx("#0EA5E9") + (round(255 * alpha),)
            wing = hx("#BAE6FD") + (round(200 * alpha),)
            for dx in (-6, 6):
                for dirn in (-1, 1):
                    cx, cy = x + dx * s, y + dirn * (5 + 3 * flap) * s
                    d.ellipse([cx - 6 * s, cy - (2.5 + flap) * s, cx + 6 * s, cy + (2.5 + flap) * s], fill=wing)
            d.rounded_rectangle([x - 20 * s, y - 1.6 * s, x + 6 * s, y + 1.6 * s], 4, fill=body)
            d.ellipse([x + 6 * s, y - 3 * s, x + 12 * s, y + 3 * s], fill=body)
        # солнечные лучи-боке
        for i in range(10):
            x0, y0 = rnd.uniform(0, W), rnd.uniform(0, H)
            r = rnd.uniform(20, 60)
            a = 0.5 + 0.5 * math.sin(t * rnd.uniform(0.5, 1.2) + i)
            d.ellipse([x0 - r, y0 - r, x0 + r, y0 + r], fill=(255, 240, 160, round(40 * a * alpha)))
    frame.alpha_composite(ov)
    if season == "autumn":  # туман снизу
        fog = Image.new("L", (1, H))
        for y in range(H):
            fog.putpixel((0, y), round(150 * alpha * clamp((y / H - 0.68) / 0.32) ** 1.2))
        frame.paste(Image.new("RGBA", (W, H), (200, 190, 180, 255)), (0, 0), fog.resize((W, H)))
    if season == "winter":  # иней по краям
        if "frost" not in _cache:
            m = Image.new("L", (W, H), 0)
            ImageDraw.Draw(m).rounded_rectangle([60, 60, W - 60, H - 60], 160, fill=255)
            m = ImageChops.invert(m.filter(ImageFilter.GaussianBlur(70)))
            _cache["frost"] = m.point(lambda v: round(v * 0.75))
        m = _cache["frost"].point(lambda v: round(v * alpha)) if alpha < 1 else _cache["frost"]
        frame.paste(Image.new("RGBA", (W, H), (224, 242, 254, 255)), (0, 0), m)


# ====================================================================
def backdrop(season):
    key = ("bd", season)
    if key not in _cache:
        name, acc, acc2, bg = THEMES[season]
        im = Image.new("RGB", (W, H), bg)
        glow = Image.new("RGB", (W, H), (0, 0, 0))
        gd = ImageDraw.Draw(glow)
        gd.ellipse([-300, -200, 800, 900], fill=mix((0, 0, 0), acc2, 0.45))
        gd.ellipse([300, 1100, 1500, 2300], fill=mix((0, 0, 0), acc, 0.4))
        im = ImageChops.add(im, glow.filter(ImageFilter.GaussianBlur(200)))
        _cache[key] = im.convert("RGBA")
    return _cache[key]


def soft_title(lines, size=74, color=(255, 255, 255)):
    key = ("title", lines, size, color)
    if key in _cache:
        return _cache[key]
    f = uib(size)
    ef = emo(round(size * 0.9))
    tmp = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
    rows = []
    for ln in lines.split("\n"):
        e, rest = "", ln
        if ord(ln[0]) > 0x2000:
            e, rest = ln.split(" ", 1)
        ew = tmp.textlength(e, font=ef) + size * 0.3 if e else 0
        rows.append((e, rest, ew, ew + tmp.textlength(rest, font=f)))
    lh = round(size * 1.3)
    wd = round(max(r[3] for r in rows)) + 120
    hd = lh * len(rows) + 120
    txt = Image.new("RGBA", (wd, hd), (0, 0, 0, 0))
    td = ImageDraw.Draw(txt)
    for i, (e, rest, ew, tw) in enumerate(rows):
        x, y = (wd - tw) / 2, 60 + i * lh
        if e:
            td.text((x, y + size * 0.12), e, font=ef, embedded_color=True)
        td.text((x + ew, y), rest, font=f, fill=color)
    shadow = Image.new("RGBA", txt.size, (0, 0, 0, 0))
    shadow.putalpha(txt.getchannel("A").point(lambda v: min(255, v * 2)).filter(ImageFilter.GaussianBlur(14)))
    out = Image.new("RGBA", txt.size, (0, 0, 0, 0))
    out.alpha_composite(shadow, (0, 4))
    out.alpha_composite(shadow, (0, 4))
    out.alpha_composite(txt)
    _cache[key] = out
    return out


def put_title(frame, text, t, t_in, cy, size=74, color=(255, 255, 255), t_out=None, dur=0.6):
    if t < t_in:
        return
    a = smooth(prog(t, t_in, t_in + dur))
    if t_out is not None:
        a *= 1 - smooth(prog(t, t_out, t_out + 0.35))
    if a <= 0:
        return
    im = soft_title(text, size, color)
    if a < 1:
        im = im.copy()
        im.putalpha(im.getchannel("A").point(lambda v: round(v * a)))
    rise = (1 - a) * 24
    frame.alpha_composite(im, (round(W / 2 - im.width / 2), round(cy - im.height / 2 + rise)))


def season_frame(season, t):
    """Полный кадр одной темы: фон, окно с медленным зумом, частицы."""
    f = backdrop(season).copy()
    win = window(season, t)
    # зум — общий для всех тем, чтобы на наплыве окна совпадали: подлёт на крючке, дальше «дыхание»
    s = lerp(0.80, 1.02, smooth(prog(t, 0, T_SPRING))) + 0.025 * math.sin(max(0.0, t - T_SPRING) * 0.8)
    sw, sh = round(CW * s), round(CH * s)
    im = win.resize((sw, sh), Image.BILINEAR)
    mask = Image.new("L", (sw, sh), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, sw - 1, sh - 1], round(36 * s), fill=255)
    top = 420 - (s - 1) * 520
    sd = Image.new("RGBA", (sw + 120, sh + 120), (0, 0, 0, 0))
    ImageDraw.Draw(sd).rounded_rectangle([60, 70, sw + 60, sh + 70], 40, fill=(0, 0, 0, 140))
    f.alpha_composite(sd.filter(ImageFilter.GaussianBlur(28)), (round(W / 2 - sw / 2) - 60, round(top) - 60))
    f.paste(im, (round(W / 2 - sw / 2), round(top)), mask)
    particles(f, season, t)
    return f


SEGMENTS = [  # (тема, начало, конец)
    ("spring", 0.0, T_SUMMER),
    ("summer", T_SUMMER, T_AUTUMN),
    ("autumn", T_AUTUMN, T_WINTER),
    ("winter", T_WINTER, T_NY),
    ("newyear", T_NY, DUR),
]


def seg_frame(i, t):
    return season_frame(SEGMENTS[i][0], t)


def cta(t, base):
    lt = t - T_CTA
    f = base.filter(ImageFilter.GaussianBlur(lerp(0, 18, smooth(prog(lt, 0, 0.5)))))
    dim = Image.new("RGBA", (W, H), (18, 8, 8, round(150 * smooth(prog(lt, 0, 0.5)))))
    f.alpha_composite(dim)
    particles(f, "newyear", t)
    lg = asset("main/logo.png", 2.4)
    k = out_back(prog(lt, 0.05, 0.55), 1.6)
    s = lerp(0.5, 1, k)
    if k > 0:
        lgs = lg.resize((max(1, round(lg.width * s)), max(1, round(lg.height * s))), Image.LANCZOS)
        lgs.putalpha(lgs.getchannel("A").point(lambda v: round(v * clamp(k))))
        f.alpha_composite(lgs, (round(W / 2 - lgs.width / 2), round(700 - lgs.height / 2)))
    put_title(f, "🐸 RibbixLauncher", t, T_CTA + 0.2, 1110, 84, dur=0.4)
    put_title(f, "4 СЕЗОНА + НГ", t, T_CTA + 0.4, 1230, 96, (253, 224, 71), dur=0.4)
    put_title(f, "👇 Ссылка в профиле", t, T_CTA + 0.6, 1360, 64, dur=0.4)
    if lt > 0.5:
        d = ImageDraw.Draw(f)
        d.text((W / 2, 1460), "@ribbix", font=uib(50), fill=(255, 255, 255), anchor="ma")
        bob = abs(math.sin(lt * 5)) * 30
        d.text((W / 2 - 55, 1560 + bob), "👇", font=emo(100), embedded_color=True)
    return f


def frame_at(t):
    bounds = [s[1] for s in SEGMENTS[1:]]
    i = sum(1 for b in bounds if t >= b)
    f = seg_frame(i, t)
    # плавный переход: смешиваем с соседней темой
    for j, b in enumerate(bounds):
        if abs(t - b) < XF:
            k = smooth((t - b + XF) / (2 * XF))
            other = seg_frame(j if t >= b else j + 1, t)
            a, bimg = (other, f) if t >= b else (f, other)
            f = Image.blend(a, bimg, k)
            # мягкий «блик» перехода
            glow = round(60 * (1 - abs(t - b) / XF))
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, glow)))
    if t >= T_CTA:
        f = cta(t, f)
    else:
        put_title(f, "ЛАУНЧЕР, КОТОРЫЙ\nМЕНЯЕТСЯ ПО СЕЗОНУ", t, 0.3, 230, 76, t_out=T_SPRING - 0.4, dur=0.9)
        put_title(f, "🌸 ВЕСНА — «ЦВЕТЕНИЕ»", t, T_SPRING + 0.1, 250, 68, t_out=T_SUMMER - 0.35)
        put_title(f, "☀️ ЛЕТО — «ПРУД»", t, T_SUMMER + 0.15, 250, 72, t_out=T_AUTUMN - 0.35)
        put_title(f, "🍂 ОСЕНЬ — «ЛИСТОПАД»", t, T_AUTUMN + 0.15, 250, 68, t_out=T_WINTER - 0.35)
        put_title(f, "❄️ ЗИМА — «СПЯЩИЙ ПРУД»", t, T_WINTER + 0.1, 200, 60, t_out=T_CTA - 0.3)
        put_title(f, "🎄 А НА НГ — «КВА-НОВЫЙ ГОД»", t, T_NY + 0.15, 300, 60, (254, 202, 202), t_out=T_CTA - 0.3)
    return f.convert("RGB")


def main():
    args = sys.argv[1:]
    if "--only" in args:
        for ts in args[args.index("--only") + 1].split(","):
            frame_at(float(ts)).save(HERE / "frames" / f"f_{float(ts):05.2f}.png")
        return
    out = args[0] if args else str(HERE / "out" / "video_noaudio.mp4")
    ff = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                           "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-", "-c:v", "libx264",
                           "-preset", "medium", "-crf", "17", "-pix_fmt", "yuv420p", out], stdin=subprocess.PIPE)
    n = round(DUR * FPS)
    for i in range(n):
        ff.stdin.write(frame_at(i / FPS).tobytes())
        if i % 60 == 0:
            print(f"{i}/{n}", flush=True)
    ff.stdin.close()
    ff.wait()
    print("done", out)


if __name__ == "__main__":
    main()
