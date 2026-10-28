"""Сценарий 5 «Мод в один клик» — покадровый рендер 1080x1920 @30fps, 25 с.

Мокап вкладки «Моды» (Views/InstancesView.xaml): поиск по Modrinth, «Установить» → «✓ Установлен»,
обязательные зависимости ставятся сами (ModrinthService.InstallRecursiveAsync), тост как в PageViewModels.
В ролике REI: Architectury API и Cloth Config — его настоящие зависимости (у JEI их нет).
Запуск:  python render.py [out.mp4] [--only 1.0,12.5]
"""
import math
import random
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

sys.path.insert(0, str(Path(__file__).parent.parent / "scenario3"))
import render as R  # noqa: E402
from render import (ACCENT, BG, CW, DANGER, INPUT, LINE, MUTED, PANEL, TEXT, asset, clamp, cursor,  # noqa: E402
                    draw_rich, emo, font, lerp, mix, out_back, out_cubic, paste_title, path, phone_bg,
                    place, prog, ripple, smooth, ui, uib)

HERE = Path(__file__).parent
W, H, FPS, DUR = 1080, 1920, 30, 25.0

# Границы сцен подогнаны под длину озвучки (см. mix.py)
S1, S2, S3, S4, S5 = 0.0, 3.75, 9.75, 17.25, 22.35

# ---- тайминги действий ----
T_HOOK_CLICK = 0.75
T_TYPE = [4.35, 4.5, 4.65]           # набор «REI»
T_SEARCH = (4.85, 5.45)              # «Ищем на Modrinth…»
T_PICK = 7.9                         # выбор REI
T_INSTALL = 10.25                    # клик «Установить»
T_DL = (10.3, 11.5)                  # скачивание REI
T_DEPS = 11.6                        # всплывает «Требуются зависимости»
T_DEP_DL = [(12.1, 13.0), (12.5, 13.5)]
T_TOAST = 14.3
T_LIST = 14.9                        # блок «Установленные моды (3)»
T_PLAY = 17.75

MODS = [
    # заголовок, автор, описание, иконка (буквы, цвет), загрузчики
    ("Roughly Enough Items (REI)", "shedaniel", "Рецепты и все предметы игры в одном окне", ("REI", (220, 80, 40)),
     "Fabric · Forge · NeoForge"),
    ("Just Enough Items (JEI)", "mezz", "Просмотр предметов и рецептов", ("JEI", (60, 120, 220)), "Forge · NeoForge · Fabric"),
    ("EMI", "emilyploszaj", "Современный просмотрщик рецептов", ("EMI", (140, 80, 200)), "Fabric · Forge · NeoForge"),
]
DEPS = [("Architectury API", "architectury-fabric.jar", ("A", (110, 116, 130))),
        ("Cloth Config API", "cloth-config-fabric.jar", ("CC", (40, 160, 160)))]


def mod_icon(letters, color, size):
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, size - 1, size - 1], size // 5, fill=color)
    d.rounded_rectangle([0, size * 0.55, size - 1, size - 1], size // 5, fill=mix(color, (0, 0, 0), 0.18))
    d.rectangle([0, size * 0.55, size - 1, size * 0.7], fill=mix(color, (0, 0, 0), 0.18))
    d.text((size / 2, size / 2), letters, font=uib(round(size * (0.42 if len(letters) < 3 else 0.32))),
           fill=(255, 255, 255), anchor="mm")
    return im


def spinner(d, cx, cy, r, t, col=(6, 30, 14)):
    for k in range(8):
        a = t * 8 + k * math.tau / 8
        al = round(255 * (k + 1) / 8)
        x, y = cx + math.cos(a) * r, cy + math.sin(a) * r
        d.ellipse([x - 4, y - 4, x + 4, y + 4], fill=col + (al,))


def install_button(img, x1, ycen, state, t, big=False):
    """state: 'idle' | 'hover' | 'busy' | 'done'; кнопка прижата правым краем к x1."""
    d = ImageDraw.Draw(img)
    fs = 40 if big else 26
    bw, bh = (380, 104) if big else (210, 60)
    x0, y0 = x1 - bw, ycen - bh / 2
    if state == "done":
        draw_rich(d, (x1 - bw / 2, ycen - fs * 0.62), "✓ Установлен", fs, fill=ACCENT, bold=True, anchor="ma")
        return (x1 - bw / 2, ycen)
    col = ACCENT if state != "hover" else mix(ACCENT, (255, 255, 255), 0.2)
    d.rounded_rectangle([x0, y0, x1, y0 + bh], bh // 3, fill=col)
    if state == "busy":
        spinner(d, x0 + bh * 0.55, ycen, bh * 0.18, t)
        d.text((x0 + bh, ycen), "Ставлю…", font=uib(fs), fill=(6, 30, 14), anchor="lm")
    else:
        d.text((x1 - bw / 2, ycen), "Установить", font=uib(fs), fill=(6, 30, 14), anchor="mm")
    return (x1 - bw / 2, ycen)


def chip(d, x, y, text, fs=20, fill=INPUT, fg=MUTED, outline=LINE):
    w = d.textlength(text, font=uib(fs)) + 24 + (fs * 0.4 if text.startswith("✓") else 0)
    d.rounded_rectangle([x, y, x + w, y + fs + 16], (fs + 16) // 2, fill=fill, outline=outline, width=2)
    draw_rich(d, (x + 12, y + 7), text, fs, fill=fg, bold=True)  # ✓ — цветным шрифтом, в Segoe UI его нет
    return x + w + 10


# ====================================================================
# Окно лаунчера: вкладка «Моды»
# ====================================================================
ROW_Y0, ROW_H = 400, 168


def mods_window(t, st):
    img = Image.new("RGBA", (CW, 1700), PANEL + (255,))
    d = ImageDraw.Draw(img)
    pos = {}
    bg = asset("main/bg.png").resize((CW, round(CW * 298 / 638)), Image.LANCZOS).crop((0, 120, CW, 230))
    img.alpha_composite(bg, (0, 0))
    d.rectangle([0, 0, CW, 110], fill=(11, 18, 32, 150))
    img.alpha_composite(asset("ui/icon_frog.png", 0.58), (24, 14))
    d.text((100, 26), "RibbixLauncher", font=uib(38), fill=TEXT)
    d.text((CW - 30, 40), "Моя сборка · 1.20.4 · Fabric", font=ui(26), fill=MUTED, anchor="ra")
    x = 28
    for i, tab in enumerate(["🎨 Графика", "🎮 Игра", "🧩 Моды", "📦 Паки"]):
        tw = d.textlength(tab, font=uib(28)) + 44
        act = i == 2
        if act:
            d.rounded_rectangle([x, 128, x + tw, 184], 14, fill=ACCENT)
        else:
            d.rounded_rectangle([x, 128, x + tw, 184], 14, outline=LINE, width=2)
        draw_rich(d, (x + 20, 136), tab, 28, fill=(6, 30, 14) if act else MUTED, bold=True)
        x += tw + 14
    d.text((32, 214), "Моды", font=uib(40), fill=TEXT)
    d.rounded_rectangle([CW - 330, 210, CW - 28, 266], 16, fill=ACCENT)
    draw_rich(d, (CW - 179, 218), "🔍 Каталог модов", 27, fill=(6, 30, 14), bold=True, anchor="ma")
    # поиск
    d.rounded_rectangle([28, 292, CW - 190, 362], 18, fill=INPUT, outline=ACCENT if st["focus"] else LINE, width=3)
    q = st["query"]
    if q:
        d.text((56, 306), q, font=uib(36), fill=TEXT)
    else:
        d.text((56, 308), "Поиск на Modrinth", font=ui(32), fill=(90, 104, 128))
    if st["focus"] and int(t * 2.5) % 2 == 0:
        cx = 58 + d.textlength(q, font=uib(36))
        d.rectangle([cx, 304, cx + 3, 350], fill=TEXT)
    d.rounded_rectangle([CW - 170, 292, CW - 28, 362], 18, outline=LINE, width=2)
    d.text((CW - 99, 327), "Найти", font=uib(30), fill=TEXT, anchor="mm")
    pos["search"] = (300, 327)
    if st["searching"]:
        spinner(d, 60, ROW_Y0 + 30, 12, t, ACCENT)
        d.text((86, ROW_Y0 + 12), "Ищем на Modrinth…", font=ui(30), fill=MUTED)
    # результаты
    y = ROW_Y0
    for i, (title, author, desc, (lt, lc), loaders) in enumerate(MODS):
        k = st["results"][i]
        if k <= 0:
            continue
        row = Image.new("RGBA", (CW - 56, ROW_H - 14), (0, 0, 0, 0))
        rd = ImageDraw.Draw(row)
        sel = st["sel"] == i
        rd.rounded_rectangle([0, 0, row.width - 1, row.height - 1], 22,
                             fill=(20, 60, 40) if sel else INPUT, outline=ACCENT if sel else LINE, width=3 if sel else 2)
        row.alpha_composite(mod_icon(lt, lc, 104), (20, 24))
        rd.text((144, 16), title, font=uib(30 if len(title) < 24 else 27), fill=TEXT)
        rd.text((144, 56), f"{author} · {desc}", font=ui(22), fill=MUTED)
        cx = chip(rd, 144, 96, "1.20.4")
        cx = chip(rd, cx, 96, "Fabric", fg=TEXT)
        if i == 0:
            chip(rd, cx, 96, "✓ подходит к сборке", fill=(20, 70, 44), fg=ACCENT, outline=ACCENT)
        rd.text((row.width - 24, 18), "↓ 1 млн+", font=uib(22), fill=MUTED, anchor="ra")
        if i == 0:
            bpos = install_button(row, row.width - 22, 110, st["btn"], t)
            pos["install"] = (28 + bpos[0], y + bpos[1])
            if st["dl"] > 0 and st["btn"] == "busy":
                rd.rounded_rectangle([144, row.height - 12, row.width - 250, row.height - 4], 4, fill=LINE)
                rd.rounded_rectangle([144, row.height - 12, 144 + (row.width - 394) * st["dl"], row.height - 4],
                                     4, fill=ACCENT)
        s = lerp(0.85, 1.0, out_back(k))
        a = clamp(k * 1.6)
        rs = row.resize((round(row.width * s), round(row.height * s)), Image.BILINEAR)
        if a < 1:
            rs.putalpha(rs.getchannel("A").point(lambda v, a=a: round(v * a)))
        img.alpha_composite(rs, (round(28 + (row.width - rs.width) / 2), round(y + (row.height - rs.height) / 2)))
        pos[f"row{i}"] = (CW / 2, y + row.height / 2)
        y += ROW_H
    # установленные моды
    if st["list"] > 0:
        a = out_cubic(st["list"])
        y0 = ROW_Y0 + ROW_H * 3 + 30 + (1 - a) * 40
        d.text((32, y0), "Установленные моды (3)", font=uib(34), fill=TEXT)
        rows = [("Roughly Enough Items (REI)", "roughly-enough-items-fabric.jar", ("REI", (220, 80, 40)), False)]
        rows += [(n, f, ic, True) for n, f, ic in DEPS]
        for j, (n, f, (lt, lc), dep) in enumerate(rows):
            yy = y0 + 60 + j * 104
            kk = out_back(prog(st["list"], j * 0.2, 0.5 + j * 0.2))
            if kk <= 0:
                continue
            img.alpha_composite(mod_icon(lt, lc, 72), (40, round(yy)))
            d.text((130, yy + 4), n, font=uib(28), fill=TEXT)
            d.text((130, yy + 40), ("зависимость · " if dep else "") + f, font=ui(22), fill=MUTED)
            d.rounded_rectangle([CW - 120, yy + 18, CW - 40, yy + 58], 20, fill=ACCENT)
            d.ellipse([CW - 78, yy + 22, CW - 44, yy + 54], fill=(240, 255, 244))
    return img, pos


def deps_popup(t):
    """Карточка «Требуются зависимости» с прогрессом каждой."""
    pw, ph = 900, 380
    im = Image.new("RGBA", (pw, ph), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, pw - 1, ph - 1], 30, fill=(17, 26, 46, 250), outline=(245, 158, 11), width=4)
    draw_rich(d, (36, 28), "📦 Требуются зависимости", 36, bold=True)
    d.text((36, 82), "REI не запустится без них — ставлю сам", font=ui(26), fill=MUTED)
    for j, (name, _, (lt, lc)) in enumerate(DEPS):
        y = 140 + j * 112
        im.alpha_composite(mod_icon(lt, lc, 80), (36, y))
        d.text((140, y + 4), name, font=uib(30), fill=TEXT)
        t0, t1 = T_DEP_DL[j]
        p = prog(t, t0, t1)
        d.rounded_rectangle([140, y + 52, pw - 150, y + 66], 7, fill=LINE)
        if p > 0:
            d.rounded_rectangle([140, y + 52, 140 + (pw - 290) * smooth(p), y + 66], 7, fill=ACCENT)
        if p >= 1:
            d.text((pw - 60, y + 22), "✓", font=uib(46), fill=ACCENT, anchor="ma")
        elif p > 0:
            d.text((pw - 50, y + 26), f"{round(p * 100)}%", font=uib(26), fill=MUTED, anchor="ra")
    return im


def toast(f, text, t, t0, t1):
    if not (t0 <= t < t1 + 0.3):
        return
    k = out_back(prog(t, t0, t0 + 0.35)) * (1 - smooth(prog(t, t1, t1 + 0.3)))
    d = ImageDraw.Draw(f)
    tw = d.textlength(text, font=uib(32)) + 120
    y = lerp(1950, 1700, k)
    d.rounded_rectangle([W / 2 - tw / 2, y, W / 2 + tw / 2, y + 96], 28, fill=ACCENT)
    draw_rich(d, (W / 2, y + 26), text, 32, fill=(6, 30, 14), bold=True, anchor="ma")


# ---------------- Сцена 1: крючок ----------------
def scene1(t):
    f = phone_bg(t)
    card = Image.new("RGBA", (980, 640), (0, 0, 0, 0))
    d = ImageDraw.Draw(card)
    d.rounded_rectangle([0, 0, 979, 639], 40, fill=PANEL, outline=(40, 70, 60), width=3)
    card.alpha_composite(mod_icon("REI", (220, 80, 40), 200), (44, 44))
    d.text((280, 52), "Roughly Enough", font=uib(54), fill=TEXT)
    d.text((280, 116), "Items (REI)", font=uib(54), fill=TEXT)
    d.text((280, 194), "shedaniel · Modrinth", font=ui(30), fill=MUTED)
    d.text((44, 280), "Рецепты и все предметы игры в одном окне", font=ui(34), fill=TEXT)
    cx = chip(d, 44, 350, "1.20.4", 28)
    cx = chip(d, cx, 350, "Fabric", 28, fg=TEXT)
    chip(d, cx, 350, "↓ 1 млн+", 28)
    if t < T_HOOK_CLICK:
        state = "hover" if t > 0.45 else "idle"
    elif t < T_HOOK_CLICK + 0.45:
        state = "busy"
    else:
        state = "done"
    bpos = install_button(card, 936, 530, state, t, big=True)
    if state == "done":  # «Установлено ✅» как в сценарии
        k = out_back(prog(t, T_HOOK_CLICK + 0.45, T_HOOK_CLICK + 0.75), 2.5)
        ov = Image.new("RGBA", card.size, (0, 0, 0, 0))
        od = ImageDraw.Draw(ov)
        od.rounded_rectangle([556, 478, 936, 582], 34, fill=(20, 70, 44), outline=ACCENT, width=4)
        draw_rich(od, (746, 504), "✅ Установлено", 38, bold=True, anchor="ma")
        if k < 1:
            ov.putalpha(ov.getchannel("A").point(lambda v: round(v * clamp(k))))
        card.alpha_composite(ov)
    zoom = lerp(1.0, 1.06, smooth(prog(t, 0, S2)))
    to = place(f, card, zoom, W / 2, 760 - (zoom - 1) * 300, radius=40)
    bp = to(bpos)
    c = path([(0, W * 0.85, 1650), (0.55, bp[0] + 30, bp[1] + 10)], t)
    cursor(f, c, abs(t - T_HOOK_CLICK) < 0.12, 2.0)
    ripple(f, (bp[0] + 30, bp[1] + 10), t, T_HOOK_CLICK)
    paste_title(f, "МОД ЗА 1 КЛИК.\nБЕЗ CURSEFORGE.", t, 0.1, 400, 104)
    return f


def mods_state(t):
    q = "REI"[: sum(1 for x in T_TYPE if t >= x)]
    st = dict(query=q, focus=S2 + 0.2 < t < T_PICK, searching=T_SEARCH[0] <= t < T_SEARCH[1],
              results=[prog(t, T_SEARCH[1] + i * 0.15, T_SEARCH[1] + 0.45 + i * 0.15) for i in range(3)],
              sel=0 if t >= T_PICK else -1, btn="idle", dl=0, list=prog(t, T_LIST, T_LIST + 1.2))
    if t >= T_INSTALL - 0.4:
        st["btn"] = "hover"
    if t >= T_INSTALL:
        st["btn"] = "busy"
        st["dl"] = prog(t, *T_DL)
    if t >= T_DEP_DL[1][1] + 0.2:
        st["btn"] = "done"
    return st


# ---------------- Сцены 2–3: поиск и зависимости ----------------
def scene23(t):
    f = phone_bg(t)
    st = mods_state(t)
    img, pos = mods_window(t, st)
    scroll = round(330 * smooth(prog(t, T_LIST - 0.3, T_LIST + 0.5)))  # к списку установленных
    view = img.crop((0, scroll, CW, scroll + 1260))
    place_to = place(f, view, 1.0, W / 2, 600)
    to = lambda p: place_to((p[0], p[1] - scroll))  # noqa: E731
    srch, row0 = to(pos["search"]), to(pos.get("row0", (CW / 2, ROW_Y0 + 80)))
    inst = to(pos.get("install", (CW - 140, ROW_Y0 + 110)))
    c = path([(S2, W * 0.8, 1750), (S2 + 0.45, srch[0], srch[1] + 10), (T_SEARCH[1] + 0.6, srch[0], srch[1] + 10),
              (T_PICK - 0.2, row0[0] - 150, row0[1]), (T_PICK + 0.4, row0[0] - 150, row0[1]),
              (T_INSTALL - 0.3, inst[0], inst[1] + 6), (T_DEP_DL[1][1] + 0.6, inst[0], inst[1] + 6),
              (T_LIST + 0.8, W * 0.82, 1640)], t)
    clicking = any(abs(t - x) < 0.1 for x in (S2 + 0.45, T_PICK, T_INSTALL))
    cursor(f, c, clicking)
    ripple(f, (row0[0] - 150, row0[1]), t, T_PICK)
    ripple(f, inst, t, T_INSTALL)
    # «Требуются зависимости»
    if T_DEPS <= t < T_TOAST + 0.35:
        k = out_back(prog(t, T_DEPS, T_DEPS + 0.35)) * (1 - smooth(prog(t, T_TOAST, T_TOAST + 0.3)))
        pop = deps_popup(t)
        s = lerp(0.6, 1.0, k)
        ps = pop.resize((max(1, round(pop.width * s)), max(1, round(pop.height * s))), Image.BILINEAR)
        ps.putalpha(ps.getchannel("A").point(lambda v: round(v * clamp(k))))
        f.alpha_composite(ps, (round(W / 2 - ps.width / 2), round(1300 - ps.height / 2)))
    toast(f, "🐸 «REI» установлен + 2 зависимости", t, T_TOAST, T_LIST + 0.6)
    if t < S3:
        paste_title(f, "ИЩУ ПРЯМО В ЛАУНЧЕРЕ", t, S2 + 0.1, 400, 96)
    else:
        for i, (ti, lab) in enumerate([(T_DL[1] + 0.05, "✅ Скачался REI"),
                                       (T_DEP_DL[1][1] + 0.15, "✅ + 2 зависимости автоматом"),
                                       (T_LIST + 0.2, "✅ Готово")]):
            if t < ti:
                continue
            im = R.title_cached(lab, 70, (255, 255, 255))
            k = out_back(prog(t, ti, ti + 0.3), 2.2)
            sc = lerp(0.5, 1, k)
            im = im.resize((max(1, round(im.width * sc)), max(1, round(im.height * sc))), Image.BILINEAR)
            f.alpha_composite(im, (round(W / 2 - im.width / 2), round(250 + i * 115 - im.height / 2)))
    return f


# ====================================================================
# Сцена 4: игра с открытым инвентарём и панелью REI
# ====================================================================
def item_icons(n=60, px=16, scale=3):
    rnd = random.Random(21)
    blocks = [(134, 96, 62), (125, 125, 125), (160, 120, 70), (90, 170, 60), (220, 210, 160), (60, 60, 70),
              (200, 80, 40), (110, 80, 50), (240, 240, 250), (40, 120, 180)]
    metals = [(110, 230, 230), (250, 210, 60), (220, 220, 225), (60, 220, 120), (200, 60, 60)]
    out = []
    for i in range(n):
        im = Image.new("RGBA", (px, px), (0, 0, 0, 0))
        d = ImageDraw.Draw(im)
        kind = i % 5
        if kind in (0, 1):  # блок
            c = rnd.choice(blocks)
            d.rectangle([1, 1, 14, 14], fill=c)
            for _ in range(22):
                x, y = rnd.randint(1, 14), rnd.randint(1, 14)
                d.point((x, y), fill=mix(c, (0, 0, 0) if rnd.random() < .5 else (255, 255, 255), 0.18))
            d.rectangle([1, 1, 14, 14], outline=mix(c, (0, 0, 0), 0.35))
        elif kind == 2:  # руда
            d.rectangle([1, 1, 14, 14], fill=(125, 125, 125), outline=(90, 90, 90))
            m = rnd.choice(metals)
            for _ in range(5):
                x, y = rnd.randint(3, 11), rnd.randint(3, 11)
                d.rectangle([x, y, x + 1, y + 1], fill=m)
        elif kind == 3:  # инструмент
            m = rnd.choice(metals)
            for k in range(9):
                d.point((3 + k, 12 - k), fill=(110, 80, 50))
            d.line([(7, 2), (13, 2), (13, 8)], fill=m, width=2)
        else:  # слиток / еда
            m = rnd.choice(metals + [(220, 40, 40)])
            d.polygon([(2, 10), (5, 5), (14, 5), (11, 10)], fill=m, outline=mix(m, (0, 0, 0), 0.35))
        out.append(im.resize((px * scale, px * scale), Image.NEAREST))
    return out


ICONS = None
GAME_BG = None


def slot(d, x, y, s=62):
    d.rectangle([x, y, x + s - 1, y + s - 1], fill=(139, 139, 139))
    d.line([(x, y), (x + s - 1, y)], fill=(55, 55, 55), width=3)
    d.line([(x, y), (x, y + s - 1)], fill=(55, 55, 55), width=3)
    d.line([(x, y + s - 1), (x + s - 1, y + s - 1)], fill=(255, 255, 255), width=3)
    d.line([(x + s - 1, y), (x + s - 1, y + s - 1)], fill=(255, 255, 255), width=3)


def game_frame(t, gl):
    global ICONS, GAME_BG
    if ICONS is None:
        R.init()
        ICONS = item_icons()
        GAME_BG = R.world_view("game", W, H, 12).filter(ImageFilter.GaussianBlur(3))
        GAME_BG.alpha_composite(Image.new("RGBA", (W, H), (0, 0, 0, 120)))
    f = GAME_BG.copy()
    d = ImageDraw.Draw(f)
    # инвентарь слева
    x0, y0 = 30, 640
    pw, ph = 9 * 62 + 40, 760
    d.rectangle([x0, y0, x0 + pw, y0 + ph], fill=(198, 198, 198), outline=(0, 0, 0), width=4)
    d.line([(x0 + 4, y0 + 4), (x0 + pw - 4, y0 + 4)], fill=(255, 255, 255), width=4)
    d.rectangle([x0 + 20, y0 + 20, x0 + 230, y0 + 300], fill=(0, 0, 0))
    frog = asset("ui/frog_idle.png", 2.0)
    f.alpha_composite(frog, (x0 + 125 - frog.width // 2, y0 + 170 - frog.height // 2))
    for r in range(2):
        for c in range(2):
            slot(d, x0 + 300 + c * 62, y0 + 70 + r * 62)
    d.text((x0 + 450, y0 + 120), "→", font=uib(50), fill=(90, 90, 90))
    slot(d, x0 + 500, y0 + 101)
    rnd = random.Random(5)
    for r in range(4):
        yy = y0 + 340 + r * 62 + (24 if r == 3 else 0)
        for c in range(9):
            xx = x0 + 20 + c * 62
            slot(d, xx, yy)
            if rnd.random() < (0.85 if r == 3 else 0.35):
                f.alpha_composite(ICONS[rnd.randrange(len(ICONS))], (xx + 7, yy + 7))
    d.text((x0 + 20, y0 + 312), "Инвентарь", font=font("consolab.ttf", 26), fill=(64, 64, 64))
    # панель REI справа
    rx0, ry0 = 660, 560
    cols, rows = 6, 14
    d.rounded_rectangle([rx0 - 6, ry0 - 70, rx0 + cols * 64 + 6, ry0 + rows * 64 + 80], 8, fill=(0, 0, 0, 90))
    d.text((rx0 + cols * 32, ry0 - 52), "◀  1 / 42  ▶", font=font("consolab.ttf", 30), fill=(255, 255, 255), anchor="ma")
    k = 0
    hover = None
    hover_i = 6 * 4 + 2
    for r in range(rows):
        for c in range(cols):
            xx, yy = rx0 + c * 64, ry0 + r * 64
            a = clamp((gl - 0.25 - k * 0.006) / 0.15)
            if a > 0:
                ic = ICONS[(k * 7 + 3) % len(ICONS)]
                if k == hover_i and gl > 2.2:
                    d.rectangle([xx, yy, xx + 63, yy + 63], fill=(255, 255, 255, 90))
                    hover = (xx, yy)
                if a < 1:
                    ic = ic.copy()
                    ic.putalpha(ic.getchannel("A").point(lambda v, a=a: round(v * a)))
                f.alpha_composite(ic, (xx + 8, yy + 8))
            k += 1
    sy = ry0 + rows * 64 + 16
    d.rectangle([rx0, sy, rx0 + cols * 64, sy + 52], fill=(0, 0, 0), outline=(160, 160, 160), width=2)
    d.text((rx0 + 12, sy + 10), "Поиск…", font=font("consolab.ttf", 28), fill=(140, 140, 140))
    # курсор и подсказка
    cpos = path([(1.0, 520, 1500), (2.1, rx0 + 2 * 64 + 40, ry0 + 4 * 64 + 40)], gl)
    if hover:
        tx, ty = hover[0] - 380, hover[1] - 110
        d.rectangle([tx, ty, tx + 360, ty + 100], fill=(16, 0, 16, 240), outline=(40, 0, 127), width=4)
        d.text((tx + 16, ty + 12), "Алмазная кирка", font=font("consolab.ttf", 30), fill=(255, 255, 255))
        d.text((tx + 16, ty + 54), "Roughly Enough Items", font=font("consolab.ttf", 24), fill=(85, 85, 255))
    cursor(f, cpos, False, 1.8)
    # FPS-плашка «без ошибок»
    d.rounded_rectangle([30, 1460, 630, 1560], 20, fill=(11, 18, 32, 210))
    draw_rich(d, (54, 1482), "✅ 3 мода загружено · 0 ошибок", 32, fill=(120, 255, 150), bold=True)
    return f


def scene4(t):
    lt = t - S4
    t_click = T_PLAY - S4
    if lt < t_click + 0.35:
        f = phone_bg(t)
        d = ImageDraw.Draw(f)
        st = mods_state(t)
        img, pos = mods_window(t, st)
        view = img.crop((0, 0, CW, 1260))
        to = place(f, view, 1.0, W / 2, 360)
        # нижняя панель с «Играть»
        pb = asset("main/btn_play.png", 1.7)
        k = 1 + 0.05 * prog(lt, 0.1, 0.4) - 0.07 * (abs(lt - t_click) < 0.1)
        pbs = pb.resize((round(pb.width * k), round(pb.height * k)), Image.LANCZOS)
        pc = (W / 2, 1720)
        f.alpha_composite(pbs, (round(pc[0] - pbs.width / 2), round(pc[1] - pbs.height / 2)))
        c = path([(0, W * 0.8, 1450), (t_click - 0.1, pc[0] - 20, pc[1] + 10)], lt)
        cursor(f, c, abs(lt - t_click) < 0.1)
        ripple(f, (pc[0] - 20, pc[1] + 10), lt, t_click)
        wf = prog(lt, t_click + 0.1, t_click + 0.35)
        if wf > 0:
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(255 * wf))))
    else:
        gl = lt - (t_click + 0.35)
        f = game_frame(t, gl)
        wf = 1 - prog(gl, 0, 0.3)
        if wf > 0:
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(255 * wf))))
    paste_title(f, "ЗАПУСТИЛ —\nМОД РАБОТАЕТ", t, S4 + 0.1, 260, 100)
    return f


def frame_at(t):
    if t < S2:
        f = scene1(t)
    elif t < S4:
        f = scene23(t)
    elif t < S5:
        f = scene4(t)
    else:
        f = R.scene6(t)
    for b in (S2, S4, S5):
        if 0 <= t - b < 0.12:
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(170 * (1 - (t - b) / 0.12)))))
    return f.convert("RGB")


R.S6 = S5  # CTA берём из сценария 3 со своим временем старта


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
