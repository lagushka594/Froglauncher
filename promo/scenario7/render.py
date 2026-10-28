"""Сценарий 7 «Статистика игрока» — покадровый рендер 1080x1920 @30fps.

Мокап «Профиль → 📊 Статистика» (Views/ProfileView.xaml, ViewModels/ImmersionViewModels.StatsViewModel):
бейдж с прогрессом «До «Легенда» 👑 осталось …», плитки, график за 14 дней, «В мирах Minecraft», «Любимые версии».
Пороги бейджей — PlayStatsService.Badges. Список достижений с прогрессом — ниже на той же странице (README, v2.2).
Запуск:  python render.py [out.mp4] [--only 2.0,12.0]
"""
import math
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).parent.parent / "scenario3"))
import render as R  # noqa: E402
from render import (ACCENT, CW, INPUT, LINE, MUTED, PANEL, TEXT, asset, clamp, cursor, draw_rich,  # noqa: E402
                    emo, lerp, mix, out_back, out_cubic, paste_title, path, phone_bg, place, prog, ripple,
                    smooth, ui, uib)

HERE = Path(__file__).parent
W, H, FPS, DUR = 1080, 1920, 30, 28.85

# Границы сцен подогнаны под длину озвучки (см. mix.py)
S1, S2, S3, S4, S5, S6 = 0.0, 4.15, 9.6, 15.3, 22.27, 25.88

GOLD = (245, 190, 40)
VIOLET = (139, 92, 246)

# ---- данные профиля (вымышленный игрок) ----
TOTAL_H = 461 + 10 / 60          # Ветеран (100–500 ч), до Легенды 38 ч 50 мин
DAYS = [(14, "ср", 1.2), (15, "чт", 0.8), (16, "пт", 1.5), (17, "сб", 5.8), (18, "вс", 6.7), (19, "пн", 0.0),
        (20, "вт", 1.1), (21, "ср", 0.9), (22, "чт", 1.4), (23, "пт", 2.0), (24, "сб", 5.2), (25, "вс", 6.1),
        (26, "пн", 1.6), (27, "вт", 0.9)]
BADGES = [("🌱", "Новичок", "0+ ч"), ("🐸", "Опытный", "10+ ч"), ("⚔️", "Ветеран", "100+ ч"), ("👑", "Легенда", "500+ ч")]
ACHS = [  # (иконка, название, сделано, из)
    ("💎", "Добыть алмаз", 1, 1), ("🔥", "Построить портал в Незер", 1, 1), ("🏰", "Найти крепость", 1, 1),
    ("🐱", "Приручить все виды кошек", 7, 11), ("🗺️", "Посетить все биомы", 34, 50), ("💀", "Победить Иссушителя", 0, 1),
    ("🐉", "Освободить Энд", 1, 1),
]


def fmt_h(h):
    return f"{int(h)} ч {round((h - int(h)) * 60):02d} мин" if h >= 1 else f"{round(h * 60)} мин"


def card(d, x0, y0, x1, y1, outline=LINE):
    d.rounded_rectangle([x0, y0, x1, y1], 24, fill=INPUT, outline=outline, width=2)


def bar(d, x0, y, x1, p, col=ACCENT, h=14):
    d.rounded_rectangle([x0, y, x1, y + h], h // 2, fill=LINE)
    if p > 0:
        d.rounded_rectangle([x0, y, x0 + max(h, (x1 - x0) * p), y + h], h // 2, fill=col)


# ====================================================================
# Окно «Профиль → Статистика»
# ====================================================================
Y_HERO, Y_CHART, Y_TILES, Y_FAV, Y_MC = 214, 520, 900, 1210, 1520


def profile(t, st):
    img = Image.new("RGBA", (CW, 2300), PANEL + (255,))
    d = ImageDraw.Draw(img)
    pos = {}
    bg = asset("main/bg.png").resize((CW, round(CW * 298 / 638)), Image.LANCZOS).crop((0, 120, CW, 230))
    img.alpha_composite(bg, (0, 0))
    d.rectangle([0, 0, CW, 110], fill=(11, 18, 32, 150))
    img.alpha_composite(asset("ui/icon_frog.png", 0.58), (24, 14))
    d.text((100, 26), "RibbixLauncher", font=uib(38), fill=TEXT)
    d.text((CW - 30, 40), "Профиль · Kvakusha", font=ui(26), fill=MUTED, anchor="ra")
    x = 28
    for i, tab in enumerate(["👤 Аккаунты", "📊 Статистика", "🖼️ Скриншоты"]):
        tw = d.textlength(tab, font=uib(28)) + 44
        act = i == 1
        if act:
            d.rounded_rectangle([x, 128, x + tw, 184], 14, fill=ACCENT)
        else:
            d.rounded_rectangle([x, 128, x + tw, 184], 14, outline=LINE, width=2)
        draw_rich(d, (x + 20, 136), tab, 28, fill=(6, 30, 14) if act else MUTED, bold=True)
        x += tw + 14
    # --- бейдж и общее время ---
    card(d, 28, Y_HERO, CW - 28, Y_HERO + 280, outline=mix(LINE, GOLD, 0.5))
    d.rounded_rectangle([56, Y_HERO + 34, 400, Y_HERO + 124], 45, fill=(60, 44, 10), outline=GOLD, width=3)
    d.text((80, Y_HERO + 44), "⚔️", font=emo(52), embedded_color=True)
    d.text((160, Y_HERO + 46), "Ветеран", font=uib(50), fill=(255, 226, 140))
    pos["badge"] = (228, Y_HERO + 79)
    tot = st["total"]
    d.text((CW - 56, Y_HERO + 34), fmt_h(tot).replace(" мин", "м").replace(" ч ", " ч "), font=uib(58),
           fill=TEXT, anchor="ra")
    d.text((CW - 56, Y_HERO + 106), "всего в игре", font=ui(26), fill=MUTED, anchor="ra")
    p = clamp((tot - 100) / 400)
    bar(d, 56, Y_HERO + 168, CW - 56, p, GOLD, 18)
    rem = max(0.0, 500 - tot)
    d.text((56, Y_HERO + 206), "До «Легенда»", font=ui(28), fill=MUTED)
    lx = 56 + d.textlength("До «Легенда» ", font=ui(28))
    d.text((lx, Y_HERO + 208), "👑", font=emo(26), embedded_color=True)
    d.text((lx + 40, Y_HERO + 206), f"осталось {fmt_h(rem)}", font=ui(28), fill=MUTED)
    # --- график ---
    card(d, 28, Y_CHART, CW - 28, Y_CHART + 350)
    d.text((56, Y_CHART + 24), "Время в игре за 14 дней", font=uib(34), fill=TEXT)
    d.text((CW - 56, Y_CHART + 32), "макс. 6 ч 42 мин в день", font=ui(24), fill=MUTED, anchor="ra")
    bw = (CW - 112) / 14
    base = Y_CHART + 290
    for i, (day, wd, hrs) in enumerate(DAYS):
        g = out_cubic(prog(st["chart"], i * 0.04, 0.45 + i * 0.04))
        hgt = hrs / 6.7 * 190 * g
        weekend = wd in ("сб", "вс")
        col = GOLD if (weekend and st["peaks"]) else (ACCENT if i == 13 else mix(ACCENT, PANEL, 0.35))
        x0 = 56 + i * bw + 8
        if hgt > 1:
            d.rounded_rectangle([x0, base - hgt, x0 + bw - 16, base], 8, fill=col)
        d.text((x0 + (bw - 16) / 2, base + 8), str(day), font=uib(20), fill=TEXT if weekend else MUTED, anchor="ma")
        d.text((x0 + (bw - 16) / 2, base + 32), wd, font=ui(17), fill=GOLD if weekend else MUTED, anchor="ma")
        if weekend and st["peaks"] and hrs > 5 and g > 0.9:
            d.text((x0 + (bw - 16) / 2, base - hgt - 34), f"{hrs:.0f}ч", font=uib(22), fill=GOLD, anchor="ma")
    pos["chart"] = (CW / 2, Y_CHART + 175)
    # --- плитки ---
    tiles = [(f"{round(st['sessions'])}", "сессий в лаунчере"), ("5 ч 12 мин", "самая долгая сессия"),
             ("8 дн.", "серия дней подряд"), ("35 ч 12 мин", "за 14 дней")]
    tw2 = (CW - 56 - 20) / 2
    for i, (v, lab) in enumerate(tiles):
        x0 = 28 + (i % 2) * (tw2 + 20)
        y0 = Y_TILES + (i // 2) * 150
        hl = i == 0 and st["hl_sessions"]
        card(d, x0, y0, x0 + tw2, y0 + 130, outline=ACCENT if hl else LINE)
        d.text((x0 + 28, y0 + 18), v, font=uib(48), fill=ACCENT if hl else TEXT)
        d.text((x0 + 28, y0 + 82), lab, font=ui(24), fill=MUTED)
    # --- любимые версии ---
    d.text((32, Y_FAV), "Любимые версии", font=uib(34), fill=TEXT)
    favs = [("Выживание · 1.21.1", 268.4, False), ("Моя сборка · 1.20.4 · Fabric", st["inst"], True),
            ("Скайблок · 1.20.1", 31.0, False)]
    for j, (name, hrs, me) in enumerate(favs):
        y0 = Y_FAV + 60 + j * 78
        if me and st["hl_inst"]:
            d.rounded_rectangle([20, y0 - 10, CW - 20, y0 + 66], 18, fill=(20, 60, 40), outline=ACCENT, width=3)
        d.text((44, y0), name, font=uib(28) if me else ui(28), fill=TEXT)
        d.text((CW - 44, y0), fmt_h(hrs), font=uib(28), fill=ACCENT if me else MUTED, anchor="ra")
        bar(d, 44, y0 + 44, CW - 44, hrs / 268.4, ACCENT if me else mix(ACCENT, PANEL, 0.5), 8)
    pos["inst"] = (CW / 2, Y_FAV + 60 + 78 + 28)
    # --- в мирах Minecraft ---
    d.text((32, Y_MC), "В мирах Minecraft", font=uib(34), fill=TEXT)
    d.text((32, Y_MC + 48), "Из статистики всех миров всех версий", font=ui(22), fill=MUTED)
    mc = [("87", "достижений выполнено"), ("1 284,6 км", "пройдено пешком"), ("52 310", "прыжков"),
          ("3 907", "мобов побеждено"), ("214", "смертей"), ("19", "миров")]
    tw3 = (CW - 56 - 40) / 3
    for i, (v, lab) in enumerate(mc):
        x0 = 28 + (i % 3) * (tw3 + 20)
        y0 = Y_MC + 96 + (i // 3) * 140
        hl = i == 0 and st["hl_ach"]
        card(d, x0, y0, x0 + tw3, y0 + 120, outline=GOLD if hl else LINE)
        d.text((x0 + 22, y0 + 16), v, font=uib(40), fill=GOLD if hl else TEXT)
        d.text((x0 + 22, y0 + 74), lab, font=ui(20), fill=MUTED)
        if i == 0:
            pos["ach"] = (x0 + tw3 / 2, y0 + 60)
    return img, pos


def state(t):
    st = dict(total=TOTAL_H, chart=1.0, peaks=False, sessions=128, inst=47.3, hl_sessions=False,
              hl_inst=False, hl_ach=False)
    if S2 <= t < S3:
        k = smooth(prog(t, S2 + 0.5, S2 + 2.4))
        st.update(inst=47.3 * k, hl_inst=t > S2 + 0.4)
        st.update(sessions=128 * smooth(prog(t, S2 + 2.2, S2 + 3.6)), hl_sessions=t > S2 + 2.1)
    if t >= S3:
        st["hl_ach"] = True
    if t >= S5:
        st["chart"] = prog(t, S5 + 0.1, S5 + 1.2)
        st["peaks"] = t > S5 + 1.0
    return st


# ---------------- сцены ----------------
def view_scroll(t):
    """Прокрутка окна по сценам: шапка → любимые версии/плитки → «В мирах» → график."""
    keys = [(S1, 0), (S2 + 0.1, 0), (S2 + 0.5, 760), (S2 + 2.0, 760), (S2 + 2.4, 560), (S3, 560),
            (S3 + 0.4, 1060), (S5, 1060), (S5 + 0.01, 480), (S6, 480)]
    for (t0, a), (t1, b) in zip(keys, keys[1:]):
        if t < t1:
            return lerp(a, b, smooth(prog(t, t0, t1)))
    return keys[-1][1]


def launcher_frame(t):
    f = phone_bg(t)
    st = state(t)
    img, pos = profile(t, st)
    sc = view_scroll(t)
    vh = 1220
    view = img.crop((0, round(sc), CW, round(sc) + vh))
    zoom = 1.0
    if t < S2:  # крючок: плавный зум от бейджа
        zoom = lerp(1.35, 1.0, smooth(prog(t, 0.1, S2 - 0.3)))
    top = 600 - (zoom - 1) * 200
    to0 = place(f, view, zoom, W / 2 + (zoom - 1) * 260, top)
    return f, (lambda p: to0((p[0], p[1] - sc))), pos


def achievements_panel(t):
    pw, ph = 980, 900
    im = Image.new("RGBA", (pw, ph), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, pw - 1, ph - 1], 32, fill=(17, 26, 46, 252), outline=GOLD, width=4)
    draw_rich(d, (36, 28), "🏆 Достижения", 40, bold=True)
    n = round(87 * smooth(prog(t, S3 + 0.5, S3 + 1.6)))
    d.text((pw - 36, 32), f"{n} из 122", font=uib(40), fill=GOLD, anchor="ra")
    bar(d, 36, 100, pw - 36, n / 122, GOLD, 20)
    for i, (ic, name, done, of) in enumerate(ACHS):
        k = out_back(prog(t, S3 + 0.7 + i * 0.13, S3 + 1.1 + i * 0.13))
        if k <= 0:
            continue
        y = 150 + i * 104
        ok = done >= of
        row = Image.new("RGBA", (pw - 48, 92), (0, 0, 0, 0))
        rd = ImageDraw.Draw(row)
        rd.rounded_rectangle([0, 0, row.width - 1, 91], 20, fill=(60, 44, 10) if ok else INPUT,
                             outline=GOLD if ok else LINE, width=3 if ok else 2)
        rd.text((20, 14), ic, font=emo(50), embedded_color=True)
        rd.text((100, 12), name, font=uib(30), fill=(255, 226, 140) if ok else TEXT)
        if ok:
            rd.text((row.width - 76, 18), "✅", font=emo(42), embedded_color=True)
            rd.text((100, 52), "выполнено", font=ui(22), fill=(220, 190, 110))
        else:
            bar(rd, 100, 60, row.width - 170, done / of, ACCENT, 12)
            rd.text((row.width - 26, 48), f"{done}/{of}", font=uib(28), fill=MUTED, anchor="ra")
        s = lerp(0.7, 1, k)
        rs = row.resize((max(1, round(row.width * s)), max(1, round(row.height * s))), Image.BILINEAR)
        im.alpha_composite(rs, (round(24 + (row.width - rs.width) / 2), round(y + (92 - rs.height) / 2)))
    return im


def shine_badge(t):
    """Большой бейдж «Ветеран» с бликом и лестница бейджей."""
    lt = t - S4
    im = Image.new("RGBA", (W, 1300), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    k = out_back(prog(lt, 0.1, 0.6), 2.0)
    if k > 0:
        r = round(250 * lerp(0.4, 1, k))
        cx, cy = W // 2, 330
        glow = Image.new("RGBA", (W, 700), (0, 0, 0, 0))
        gd = ImageDraw.Draw(glow)
        for i in range(12):  # лучи
            a = i / 12 * math.tau + lt * 0.5
            gd.polygon([(cx, 330), (cx + math.cos(a) * 520, 330 + math.sin(a) * 520),
                        (cx + math.cos(a + 0.16) * 520, 330 + math.sin(a + 0.16) * 520)],
                       fill=(245, 190, 40, round(40 * clamp(k))))
        im.alpha_composite(glow)
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(60, 44, 10), outline=GOLD, width=10)
        d.ellipse([cx - r + 24, cy - r + 24, cx + r - 24, cy + r - 24], outline=(255, 226, 140), width=4)
        es = round(220 * lerp(0.4, 1, k))
        # рисуем эмодзи отдельно и центрируем по реальным пикселям — метрики цветного шрифта врут
        eimg = Image.new("RGBA", (es * 2, es * 2), (0, 0, 0, 0))
        ImageDraw.Draw(eimg).text((es // 2, es // 4), "⚔️", font=emo(es), embedded_color=True)
        eimg = eimg.crop(eimg.getbbox())
        im.alpha_composite(eimg, (cx - eimg.width // 2, cy - eimg.height // 2))
        # блик проходит по медали
        sp = prog(lt, 0.7, 1.4)
        if 0 < sp < 1:
            sh = Image.new("RGBA", (2 * r, 2 * r), (0, 0, 0, 0))
            sd = ImageDraw.Draw(sh)
            x = lerp(-r, 3 * r, sp)
            sd.polygon([(x, 0), (x + 70, 0), (x - 2 * r + 70, 2 * r), (x - 2 * r, 2 * r)], fill=(255, 255, 255, 150))
            mask = Image.new("L", (2 * r, 2 * r), 0)
            ImageDraw.Draw(mask).ellipse([0, 0, 2 * r - 1, 2 * r - 1], fill=255)
            sh.putalpha(Image.composite(sh.getchannel("A"), Image.new("L", sh.size, 0), mask))
            im.alpha_composite(sh, (cx - r, cy - r))
        d.text((cx, cy + r + 30), "Ветеран", font=R.font("impact.ttf", 110), fill=(255, 226, 140), anchor="ma",
               stroke_width=6, stroke_fill=(0, 0, 0))
    # лестница
    for i, (e, name, req) in enumerate(BADGES):
        kk = out_back(prog(lt, 1.6 + i * 0.35, 2.0 + i * 0.35))
        if kk <= 0:
            continue
        x0 = 30 + i * 260
        y0 = 830
        got = i <= 2
        cur = i == 2
        cardi = Image.new("RGBA", (240, 300), (0, 0, 0, 0))
        cd = ImageDraw.Draw(cardi)
        cd.rounded_rectangle([0, 0, 239, 299], 30, fill=(60, 44, 10) if cur else (INPUT if got else (20, 20, 30)),
                             outline=GOLD if cur else (ACCENT if got else (70, 60, 100)), width=5 if cur else 3)
        cd.text((120, 24), e, font=emo(96), embedded_color=True, anchor="ma")
        cd.text((120, 168), name, font=uib(36), fill=TEXT if got else (150, 140, 180), anchor="ma")
        cd.text((120, 222), req, font=uib(28), fill=GOLD if i == 3 else MUTED, anchor="ma")
        if not got:
            cardi.alpha_composite(Image.new("RGBA", cardi.size, (10, 6, 20, 90)))
            cd.text((200, 14), "🔒", font=emo(34), embedded_color=True)
        s = lerp(0.5, 1, kk)
        cs = cardi.resize((max(1, round(240 * s)), max(1, round(300 * s))), Image.BILINEAR)
        im.alpha_composite(cs, (round(x0 + 120 - cs.width / 2), round(y0 + 150 - cs.height / 2)))
        if i < 3:
            d.text((x0 + 250, y0 + 120), "›", font=uib(60), fill=MUTED, anchor="ma")
    # прогресс до Легенды
    if lt > 3.4:
        a = smooth(prog(lt, 3.4, 4.0))
        d.rounded_rectangle([60, 1170, W - 60, 1290], 30, fill=(17, 26, 46, round(240 * a)), outline=GOLD, width=3)
        bar(d, 100, 1250, W - 100, clamp((TOTAL_H - 100) / 400) * a, GOLD, 16)
        d.text((100, 1190), "500+ часов = Легенда", font=uib(36), fill=(255, 226, 140, round(255 * a)))
        d.text((W - 100, 1194), f"ещё {fmt_h(500 - TOTAL_H)}", font=uib(30), fill=(200, 200, 220, round(255 * a)),
               anchor="ra")
    return im


def title_emoji(f, text, emoji, t, t_in, cy, size):
    """Титр Impact с эмодзи в конце строки (title_img из сценария 3 умеет только ведущее)."""
    if t < t_in:
        return
    key = ("te", text, emoji, size)
    if key not in CACHE:
        base = R.title_img(text.split("\n"), size)
        es = round(size * 0.95)
        out = Image.new("RGBA", (base.width + es + 20, base.height), (0, 0, 0, 0))
        out.alpha_composite(base, (0, 0))
        ImageDraw.Draw(out).text((base.width - 10, base.height - es - 22), emoji, font=emo(es), embedded_color=True)
        CACHE[key] = out
    img = CACHE[key]
    k = out_back(prog(t, t_in, t_in + 0.28), 2.4)
    s = lerp(0.55, 1.0, k)
    im = img.resize((max(1, round(img.width * s)), max(1, round(img.height * s))), Image.BILINEAR)
    f.alpha_composite(im, (round(W / 2 - im.width / 2), round(cy - im.height / 2)))


CACHE = {}


def cta(t):
    lt = t - S6
    f = Image.new("RGBA", (W, H), (22, 163, 74, 255))
    d = ImageDraw.Draw(f)
    for i in range(14):
        ang = i / 14 * math.tau + lt * 0.4
        d.polygon([(W / 2, 700), (W / 2 + math.cos(ang) * 1600, 700 + math.sin(ang) * 1600),
                   (W / 2 + math.cos(ang + 0.12) * 1600, 700 + math.sin(ang + 0.12) * 1600)], fill=(34, 197, 94))
    lg = asset("main/logo.png", 2.3)
    k = out_back(prog(lt, 0, 0.45), 2.2)
    s = lerp(0.3, 1, k)
    lgs = lg.resize((max(1, round(lg.width * s)), max(1, round(lg.height * s))), Image.LANCZOS)
    f.alpha_composite(lgs, (round(W / 2 - lgs.width / 2), round(700 - lgs.height / 2)))
    title_emoji(f, "А ТЫ СКОЛЬКО\nНАИГРАЛ?", "👇", t, S6 + 0.25, 1220, 110)
    if lt > 0.4:
        d.text((W / 2, 1420), "@ribbix", font=uib(60), fill=(6, 40, 18), anchor="ma")
    if lt > 0.8:
        d.text((W / 2 - 60, 1540 + abs(math.sin(lt * 6)) * 40), "👇", font=emo(110), embedded_color=True)
    return f


def frame_at(t):
    if t >= S6:
        f = cta(t)
    elif S4 <= t < S5:
        f = phone_bg(t)
        f.alpha_composite(shine_badge(t), (0, 560))
        paste_title(f, "НОВИЧОК → ОПЫТНЫЙ →\nВЕТЕРАН → ЛЕГЕНДА", t, S4 + 0.1, 330, 86)
    else:
        f, to, pos = launcher_frame(t)
        if t < S2:
            paste_title(f, "МОЙ ЛАУНЧЕР\nСЧИТАЕТ ЗА МЕНЯ", t, 0.12, 360, 104)
        elif t < S3:
            paste_title(f, "47 ЧАСОВ", t, S2 + 0.6, 300, 118)
            paste_title(f, "128 СЕССИЙ", t, S2 + 2.3, 450, 104, (120, 255, 150))
        elif t < S4:
            a = to(pos["ach"])
            c = path([(S3, W * 0.8, 1750), (S3 + 0.35, a[0], a[1])], t)
            k = out_back(prog(t, S3 + 0.4, S3 + 0.75), 1.4)
            if k > 0:
                pan = achievements_panel(t)
                s = lerp(0.6, 1, k)
                ps = pan.resize((max(1, round(pan.width * s)), max(1, round(pan.height * s))), Image.BILINEAR)
                ps.putalpha(ps.getchannel("A").point(lambda v: round(v * clamp(k))))
                f.alpha_composite(ps, (round(W / 2 - ps.width / 2), round(1220 - ps.height / 2)))
            else:
                cursor(f, c, abs(t - S3 - 0.38) < 0.1)
                ripple(f, a, t, S3 + 0.38)
            paste_title(f, "87 ДОСТИЖЕНИЙ\nИЗ 122", t, S3 + 0.5, 360, 108, (255, 226, 140))
        else:
            title_emoji(f, "И ВИДНО, КОГДА\nЯ ЗАДРОТИЛ", "😅", t, S5 + 0.1, 360, 100)
    for b in (S2, S3, S4, S5, S6):
        if 0 <= t - b < 0.12:
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(150 * (1 - (t - b) / 0.12)))))
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
