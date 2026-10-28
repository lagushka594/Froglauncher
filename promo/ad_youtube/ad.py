"""Рекламная интеграция RibbixLauncher для YouTube — 16:9, 1920x1080 @30fps, ~2 мин.

Слева — заголовок блока и пункты (появляются под голос), справа — экран лаунчера.
Мокапы вкладок берутся из сценариев 3 (графика), 4 (сезонные темы), 5 (моды), 7 (статистика);
«Новая версия», «Серверы», «Миры» и палитра Ctrl+K нарисованы здесь. Функции — по README лаунчера.
Длины блоков считаются от озвучки audio/vt_N.wav.
Запуск:  python ad.py [out.mp4] [--only 5,40,80]
"""
import importlib.util
import json
import math
import subprocess
import sys
import wave
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

HERE = Path(__file__).parent
PROMO = HERE.parent
sys.path.insert(0, str(PROMO / "scenario3"))
import render as R  # noqa: E402  (сценарий 3: палитра, шрифты, окно графики, курсор)
from render import (ACCENT, ACCENT2, BG, CW, INPUT, LINE, MUTED, PANEL, TEXT, asset, clamp, cursor,  # noqa: E402
                    draw_rich, emo, font, lerp, mix, out_back, out_cubic, path, prog, ripple, smooth, ui, uib)


def load(name, rel):
    spec = importlib.util.spec_from_file_location(name, PROMO / rel)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


S4M = load("s4render", "scenario4/render.py")   # сезонные темы
S5M = load("s5render", "scenario5/render.py")   # моды
S7M = load("s7render", "scenario7/render.py")   # статистика

W, H, FPS = 1920, 1080, 30
GOLD = (245, 190, 40)
PANEL_CX, PANEL_TOP, PANEL_H = 1370, 60, 960   # правая колонка с окном лаунчера
LX = 110                                       # левая колонка с текстом


def wav_len(p):
    with wave.open(str(p)) as w:
        return w.getnframes() / w.getframerate()


VO = [wav_len(HERE / "audio" / f"vt_{i}.wav") for i in range(1, 11)]
LEAD = 0.35       # голос начинается через LEAD после начала блока
TAIL = [0.5] * 9 + [2.2]
STARTS = []
_t = 0.0
for i, v in enumerate(VO):
    STARTS.append(_t)
    _t += LEAD + v + TAIL[i]
DUR = _t
VO_AT = [s + LEAD for s in STARTS]

SECTIONS = [
    # (метка, заголовок, пункты [(доля реплики, текст)])
    ("", "", []),
    ("02 · ВЕРСИИ", "Любая версия\nи любой загрузчик",
     [(0.12, "Бесплатно и с открытым кодом"), (0.28, "Vanilla · Fabric · Quilt · Forge · NeoForge"),
      (0.42, "Forge — от 1.7.10 до 1.21+"), (0.55, "☕ Java подбирается и качается сама"),
      (0.72, "🐸 Знакомство за минуту: ник, RAM, Sodium")]),
    ("03 · МОДЫ", "Моды\nв один клик",
     [(0.05, "Каталог Modrinth внутри"), (0.3, "Версия и загрузчик — сразу видно"),
      (0.62, "Зависимости ставятся сами"), (0.82, "Сборки (модпаки) — тоже в один клик")]),
    ("04 · НАСТРОЙКИ", "Настройки\nдо запуска игры",
     [(0.1, "Графика, дальность, FOV"), (0.3, "Звук и управление"),
      (0.58, "🧠 «Подобрать под мой ПК»"), (0.78, "Производительность · Баланс · Качество")]),
    ("05 · СЕРВЕРЫ", "Серверы\nс живым онлайном",
     [(0.08, "Онлайн, пинг и версия — вживую"), (0.35, "Фильтры: режим, регион, избранное"),
      (0.62, "🎮 «Играть» — сразу на сервер")]),
    ("06 · СТАТИСТИКА", "Статистика\nи достижения",
     [(0.08, "Часы, сессии, серия дней"), (0.3, "График по дням"),
      (0.5, "🏆 Все достижения миров с прогрессом"), (0.75, "Бейджи: от Новичка до Легенды")]),
    ("07 · МИРЫ", "Миры\nпод защитой",
     [(0.06, "💾 Автобэкап раз в сутки"), (0.3, "♻️ Восстановление в один клик"),
      (0.55, "📤 Экспорт сборки в .mrpack"), (0.8, "📥 Импорт из Prism и MultiMC")]),
    ("08 · ТЕМЫ", "Сезонные\nтемы",
     [(0.0, "Меняются сами по дате")]),   # названия тем добавляются по ходу
    ("09 · УДОБСТВО", "Мелочи,\nкоторые решают",
     [(0.08, "🔎 Ctrl+K — поиск по всему лаунчеру"), (0.28, "📌 Трей и напоминание о перерыве"),
      (0.48, "📸 Карточки скриншотов для TikTok"), (0.66, "🎮 Статус в Discord"),
      (0.8, "🌐 RU · EN · ES · DE · PT-BR")]),
    ("", "", []),
]

_cache = {}


# ====================================================================
# Общие элементы кадра
# ====================================================================
def bg16():
    if "bg" not in _cache:
        im = Image.new("RGB", (W, H), BG)
        glow = Image.new("RGB", (W, H), (0, 0, 0))
        gd = ImageDraw.Draw(glow)
        gd.ellipse([-300, 500, 900, 1500], fill=(16, 90, 48))
        gd.ellipse([1100, -400, 2200, 600], fill=(6, 70, 90))
        im = ImageChops.add(im, glow.filter(ImageFilter.GaussianBlur(220)))
        gd = ImageDraw.Draw(im)
        for x in range(0, W, 60):
            gd.line([(x, 0), (x, H)], fill=(20, 30, 50))
        for y in range(0, H, 60):
            gd.line([(0, y), (W, y)], fill=(20, 30, 50))
        _cache["bg"] = im.convert("RGBA")
    return _cache["bg"].copy()


def season_bg(season):
    key = ("sbg", season)
    if key not in _cache:
        name, acc, acc2, bgc = S4M.THEMES[season]
        im = Image.new("RGB", (W, H), bgc)
        glow = Image.new("RGB", (W, H), (0, 0, 0))
        gd = ImageDraw.Draw(glow)
        gd.ellipse([-300, 400, 1000, 1500], fill=mix((0, 0, 0), acc, 0.4))
        gd.ellipse([1000, -400, 2300, 700], fill=mix((0, 0, 0), acc2, 0.45))
        _cache[key] = ImageChops.add(im, glow.filter(ImageFilter.GaussianBlur(220))).convert("RGBA")
    return _cache[key].copy()


def ad_label(f):
    d = ImageDraw.Draw(f)
    d.rounded_rectangle([30, 26, 160, 64], 19, fill=(0, 0, 0, 140))
    d.text((95, 45), "Реклама", font=ui(22), fill=(220, 220, 220), anchor="mm")


def progress_strip(f, t):
    d = ImageDraw.Draw(f)
    n = len(STARTS)
    seg = (W - 220) / n
    for i in range(n):
        x0 = 110 + i * seg
        end = STARTS[i + 1] if i + 1 < n else DUR
        p = clamp((t - STARTS[i]) / (end - STARTS[i]))
        d.rounded_rectangle([x0, H - 22, x0 + seg - 8, H - 16], 3, fill=(40, 52, 80))
        if p > 0:
            d.rounded_rectangle([x0, H - 22, x0 + (seg - 8) * p, H - 16], 3, fill=ACCENT)


def left_text(f, idx, lt, v, extra=()):
    label, title, bullets = SECTIONS[idx]
    d = ImageDraw.Draw(f)
    a = smooth(prog(lt, 0.05, 0.45))
    if a <= 0:
        return
    ov = Image.new("RGBA", (800, H), (0, 0, 0, 0))
    od = ImageDraw.Draw(ov)
    od.text((0, 150), label, font=uib(28), fill=ACCENT)
    for k, line in enumerate(title.split("\n")):
        od.text((0, 196 + k * 84), line, font=uib(72), fill=TEXT)
    y = 410
    for frac, text in list(bullets) + list(extra):
        tb = LEAD + frac * v
        if lt < tb:
            continue
        k = out_back(prog(lt, tb, tb + 0.35), 1.6)
        x = lerp(-40, 0, k)
        al = round(255 * clamp(k))
        od.ellipse([x, y + 8, x + 40, y + 48], fill=ACCENT + (al,))
        od.line([(x + 10, y + 28), (x + 18, y + 36), (x + 31, y + 19)], fill=(6, 30, 14, al), width=5)
        draw_rich(od, (x + 62, y + 4), text, 36, fill=TEXT + (al,), bold=False)
        y += 88
    if a < 1:
        ov.putalpha(ov.getchannel("A").point(lambda p: round(p * a)))
    f.alpha_composite(ov, (LX, 0))


def panel(f, img, lt, scale=None, cx=PANEL_CX, top=PANEL_TOP):
    """Окно лаунчера справа с въездом в начале блока; возвращает контент→экран."""
    if scale is None:
        scale = PANEL_H / img.height
    k = out_cubic(prog(lt, 0.0, 0.45))
    return R.place(f, img, scale, cx + (1 - k) * 160, top)


def win_base(right, h=1200, tabs=None, active=0):
    img = Image.new("RGBA", (CW, h), PANEL + (255,))
    d = ImageDraw.Draw(img)
    bg = asset("main/bg.png").resize((CW, round(CW * 298 / 638)), Image.LANCZOS).crop((0, 120, CW, 230))
    img.alpha_composite(bg, (0, 0))
    d.rectangle([0, 0, CW, 110], fill=(11, 18, 32, 150))
    img.alpha_composite(asset("ui/icon_frog.png", 0.58), (24, 14))
    d.text((100, 26), "RibbixLauncher", font=uib(38), fill=TEXT)
    d.text((CW - 30, 40), right, font=ui(26), fill=MUTED, anchor="ra")
    if tabs:
        x = 28
        for i, tab in enumerate(tabs):
            tw = d.textlength(tab, font=uib(28)) + 44
            if i == active:
                d.rounded_rectangle([x, 128, x + tw, 184], 14, fill=ACCENT)
            else:
                d.rounded_rectangle([x, 128, x + tw, 184], 14, outline=LINE, width=2)
            draw_rich(d, (x + 20, 136), tab, 28, fill=(6, 30, 14) if i == active else MUTED, bold=True)
            x += tw + 14
    return img, d


def chip(d, x, y, text, fs=24, on=False):
    w = d.textlength(text, font=uib(fs)) + 30
    d.rounded_rectangle([x, y, x + w, y + fs + 22], (fs + 22) // 2, fill=ACCENT if on else INPUT,
                        outline=ACCENT if on else LINE, width=2)
    d.text((x + 15, y + 10), text, font=uib(fs), fill=(6, 30, 14) if on else TEXT)
    return x + w + 12


def toast(f, text, lt, t0, t1, cx=PANEL_CX, y=930, kind=ACCENT):
    if not (t0 <= lt < t1 + 0.3):
        return
    k = out_back(prog(lt, t0, t0 + 0.35)) * (1 - smooth(prog(lt, t1, t1 + 0.3)))
    d = ImageDraw.Draw(f)
    tw = d.textlength(text, font=uib(30)) + 110
    yy = lerp(H + 20, y, k)
    d.rounded_rectangle([cx - tw / 2, yy, cx + tw / 2, yy + 84], 26, fill=kind)
    draw_rich(d, (cx, yy + 22), text, 30, fill=(6, 30, 14), bold=True, anchor="ma")


def overlay_scaled(f, im, k, cx, cy):
    if k <= 0:
        return
    s = lerp(0.6, 1.0, k)
    ims = im.resize((max(1, round(im.width * s)), max(1, round(im.height * s))), Image.BILINEAR)
    ims.putalpha(ims.getchannel("A").point(lambda v: round(v * clamp(k))))
    f.alpha_composite(ims, (round(cx - ims.width / 2), round(cy - ims.height / 2)))


# ====================================================================
# 1. Интро
# ====================================================================
def sec_intro(lt, D, v):
    if "intro_bg" not in _cache:
        b = asset("main/bg.png")
        s = H / b.height * 1.15
        b = b.resize((round(b.width * s), round(b.height * s)), Image.LANCZOS)
        _cache["intro_bg"] = b
    b = _cache["intro_bg"]
    z = 1 + 0.06 * lt / D
    bz = b.resize((round(b.width * z), round(b.height * z)), Image.BILINEAR)
    f = bz.crop(((bz.width - W) // 2, (bz.height - H) // 2, (bz.width - W) // 2 + W, (bz.height - H) // 2 + H))
    f = f.filter(ImageFilter.GaussianBlur(lerp(0, 8, smooth(prog(lt, 0.5, 2.0)))))
    f.alpha_composite(Image.new("RGBA", (W, H), (6, 10, 20, round(lerp(80, 170, smooth(prog(lt, 0.5, 2.0)))))))
    t_logo = LEAD + 0.86 * v
    pains = [(0.22, "Моды руками?"), (0.42, "Ищешь нужную Java?"), (0.62, "Перезапуск ради настройки?")]
    for i, (frac, text) in enumerate(pains):
        tb = LEAD + frac * v
        if lt < tb or lt > t_logo + 0.3:
            continue
        k = out_back(prog(lt, tb, tb + 0.3), 2) * (1 - smooth(prog(lt, t_logo, t_logo + 0.3)))
        im = R.title_cached("❌ " + text, 78, (255, 255, 255))
        overlay_scaled(f, im, k, W / 2, 330 + i * 160)
    if lt > t_logo:
        k = out_back(prog(lt, t_logo, t_logo + 0.5), 1.8)
        lg = asset("main/logo.png", 2.2)
        overlay_scaled(f, lg, k, W / 2, 470)
        a = smooth(prog(lt, t_logo + 0.4, t_logo + 0.9))
        if a > 0:
            d = ImageDraw.Draw(f)
            d.text((W / 2, 830), "Лаунчер Minecraft, который делает всё сам", font=uib(52),
                   fill=(255, 255, 255, round(255 * a)), anchor="ma")
    return f


# ====================================================================
# 2. Версии и загрузчики
# ====================================================================
VERSIONS = ["1.21.4", "1.21.1", "1.20.4", "1.20.1", "1.19.2", "1.16.5", "1.12.2", "1.7.10"]
LOADERS = ["Vanilla", "Fabric", "Quilt", "Forge", "NeoForge"]


def sec_versions(lt, D, v):
    f = bg16()
    img, d = win_base("Каталог · Новая версия", 1200)
    d.text((40, 140), "Новая версия", font=uib(52), fill=TEXT)
    d.text((40, 222), "Название", font=ui(26), fill=MUTED)
    d.rounded_rectangle([40, 260, CW - 40, 330], 16, fill=INPUT, outline=LINE, width=2)
    d.text((64, 274), "Моя сборка", font=uib(34), fill=TEXT)
    d.text((40, 356), "Версия Minecraft", font=ui(26), fill=MUTED)
    d.rounded_rectangle([40, 394, CW - 40, 700], 16, fill=INPUT, outline=LINE, width=2)
    sel_v = min(len(VERSIONS) - 1, int(prog(lt, LEAD + 0.3 * v, LEAD + 0.45 * v) * 7.99)) if lt < LEAD + 0.5 * v else 1
    sel_v = 7 if LEAD + 0.38 * v < lt < LEAD + 0.5 * v else sel_v
    for i, ver in enumerate(VERSIONS[:7] if sel_v != 7 else VERSIONS[1:]):
        y = 404 + i * 42
        actual = VERSIONS.index(ver)
        if actual == sel_v:
            d.rounded_rectangle([50, y, CW - 50, y + 40], 10, fill=(20, 70, 44))
        d.text((70, y + 4), ver, font=uib(28), fill=TEXT)
        d.text((240, y + 8), "release", font=ui(22), fill=MUTED)
    d.text((40, 726), "Модлоадер", font=ui(26), fill=MUTED)
    cyc = int(max(0.0, lt - LEAD - 0.2 * v) / 0.45)
    sel_l = 1 if lt > LEAD + 0.5 * v else min(4, cyc)
    x = 40
    for i, ln in enumerate(LOADERS):
        x = chip(d, x, 764, ln, 28, on=i == sel_l)
    d.text((40, 840), "Forge поддерживается от 1.7.10, NeoForge — от 1.20.2.", font=ui(24), fill=MUTED)
    # Java
    jt = LEAD + 0.55 * v
    if lt > jt:
        p = smooth(prog(lt, jt, jt + 1.6))
        d.rounded_rectangle([40, 890, CW - 40, 1010], 20, fill=INPUT, outline=ACCENT if p >= 1 else LINE, width=2)
        draw_rich(d, (66, 906), "☕ Java 21 — нужна для 1.21.1", 30, bold=True)
        d.text((66, 954), "готово" if p >= 1 else f"скачиваю… {round(p * 100)}%", font=ui(26),
               fill=ACCENT if p >= 1 else MUTED)
        d.rounded_rectangle([420, 966, CW - 70, 980], 7, fill=LINE)
        d.rounded_rectangle([420, 966, 420 + (CW - 490) * p, 980], 7, fill=ACCENT)
    d.rounded_rectangle([CW - 300, 1060, CW - 40, 1140], 22, fill=ACCENT)
    d.text((CW - 170, 1100), "Создать", font=uib(34), fill=(6, 30, 14), anchor="mm")
    to = panel(f, img, lt)
    # онбординг поверх
    ot = LEAD + 0.72 * v
    if lt > ot:
        k = out_back(prog(lt, ot, ot + 0.4), 1.5)
        ob = Image.new("RGBA", (760, 520), (0, 0, 0, 0))
        od = ImageDraw.Draw(ob)
        od.rounded_rectangle([0, 0, 759, 519], 34, fill=(17, 26, 46, 250), outline=ACCENT, width=4)
        ob.alpha_composite(asset("ui/frog_happy.png", 1.4), (30, 26))
        od.text((180, 40), "Привет! Как тебя звать?", font=uib(36), fill=TEXT)
        nick = "Kvakusha"[: max(0, int((lt - ot - 0.3) / 0.08))]
        od.rounded_rectangle([180, 96, 720, 156], 14, fill=INPUT, outline=ACCENT, width=2)
        od.text((200, 106), nick, font=uib(32), fill=TEXT)
        rows = [("Память", "4 ГБ — рекомендуем для вашего ПК"), ("Версия", "оптимизированная: Fabric + Sodium")]
        for i, (a, b) in enumerate(rows):
            tb = ot + 1.0 + i * 0.6
            if lt > tb:
                od.text((40, 210 + i * 110), a, font=ui(26), fill=MUTED)
                od.text((40, 248 + i * 110), b, font=uib(32), fill=TEXT)
                od.text((672, 236 + i * 110), "✅", font=emo(36), embedded_color=True)
        if lt > ot + 2.3:
            od.rounded_rectangle([440, 430, 720, 495], 20, fill=ACCENT)
            od.text((580, 462), "Установить", font=uib(30), fill=(6, 30, 14), anchor="mm")
        overlay_scaled(f, ob, k, PANEL_CX, 560)
    return f


# ====================================================================
# 3. Моды (вкладка из сценария 5, его таймлайн ужат под блок)
# ====================================================================
def sec_mods(lt, D, v):
    f = bg16()
    t0, t1 = S5M.S2, S5M.T_LIST + 1.2
    tm = lerp(t0, t1, clamp((lt - 0.2) / (D - 0.6)))
    st = S5M.mods_state(tm)
    img, pos = S5M.mods_window(tm, st)
    scroll = round(330 * smooth(prog(tm, S5M.T_LIST - 0.3, S5M.T_LIST + 0.5)))
    view = img.crop((0, scroll, CW, scroll + 1200))
    to0 = panel(f, view, lt)
    to = lambda p: to0((p[0], p[1] - scroll))  # noqa: E731
    srch, row0 = to(pos["search"]), to(pos.get("row0", (CW / 2, S5M.ROW_Y0 + 80)))
    inst = to(pos.get("install", (CW - 140, S5M.ROW_Y0 + 110)))
    c = path([(t0, PANEL_CX + 300, 1000), (t0 + 0.45, srch[0], srch[1] + 8), (S5M.T_SEARCH[1] + 0.6, srch[0], srch[1] + 8),
              (S5M.T_PICK - 0.2, row0[0] - 120, row0[1]), (S5M.T_PICK + 0.4, row0[0] - 120, row0[1]),
              (S5M.T_INSTALL - 0.3, inst[0], inst[1] + 5), (S5M.T_DEP_DL[1][1] + 0.6, inst[0], inst[1] + 5),
              (S5M.T_LIST + 0.8, PANEL_CX + 380, 980)], tm)
    cursor(f, c, any(abs(tm - x) < 0.12 for x in (t0 + 0.45, S5M.T_PICK, S5M.T_INSTALL)), 1.3)
    if S5M.T_DEPS <= tm < S5M.T_TOAST + 0.35:
        k = out_back(prog(tm, S5M.T_DEPS, S5M.T_DEPS + 0.35)) * (1 - smooth(prog(tm, S5M.T_TOAST, S5M.T_TOAST + 0.3)))
        pop = S5M.deps_popup(tm)
        pop = pop.resize((round(pop.width * 0.8), round(pop.height * 0.8)), Image.LANCZOS)
        overlay_scaled(f, pop, k, PANEL_CX, 700)
    if tm >= S5M.T_TOAST:
        toast(f, "🐸 «REI» установлен + 2 зависимости", tm, S5M.T_TOAST, t1 + 5)
    return f


# ====================================================================
# 4. Настройки (окно графики из сценария 3)
# ====================================================================
def sec_settings(lt, D, v):
    f = bg16()
    t_click1, t_click2 = LEAD + 0.55 * v, LEAD + 0.72 * v
    apply = smooth(prog(lt, t_click2 + 0.05, t_click2 + 0.9))
    phase2 = lt < t_click1 - 0.6  # сначала — слайдеры, потом пресеты
    if phase2:
        rd = lerp(8, 16, smooth(prog(lt, LEAD + 0.12 * v, LEAD + 0.3 * v)))
        fov = lerp(70, 90, smooth(prog(lt, LEAD + 0.3 * v, LEAD + 0.45 * v)))
        st = dict(gfx=1, rd=rd, fov=fov, fps=120, preview=0, rd_active=LEAD + 0.1 * v < lt < LEAD + 0.32 * v)
        img, pos = R.launcher(st)
        sc = 330
        view = img.crop((0, sc, CW, sc + 1100))
        to0 = panel(f, view, lt)
        knob = to0((pos["rd_knob"][0], pos["rd_knob"][1] - sc))
        c = path([(0, PANEL_CX + 300, 1000), (LEAD + 0.1 * v, knob[0], knob[1] + 6)], lt)
        if lt > LEAD + 0.1 * v:
            c = (knob[0] + 4, knob[1] + 6)
        cursor(f, c, st["rd_active"], 1.3)
    else:
        tgt = R.PRESETS[1][3]
        st = dict(gfx=1, rd=lerp(16, tgt["rd"], apply), fov=lerp(90, tgt["fov"], apply), fps=lerp(120, tgt["fps"], apply),
                  cards=prog(lt, t_click1 + 0.05, t_click1 + 0.85), sel=1 if lt > t_click2 else -1,
                  card_hover=1 if lt > t_click2 - 0.5 else -1, auto_hover=clamp(prog(lt, t_click1 - 0.4, t_click1)),
                  auto_press=1 if t_click1 < lt < t_click1 + 0.15 else 0,
                  flash=(1 - prog(lt, t_click2 + 0.9, t_click2 + 2.0)) if lt > t_click2 + 0.1 else 0)
        img, pos = R.launcher(st)
        view = img.crop((0, 0, CW, 1100))
        to0 = panel(f, view, 0.5 + lt - (t_click1 - 0.6))
        a, c1 = to0(pos["auto"]), to0(pos.get("card1", (CW / 2, 420)))
        c = path([(t_click1 - 0.6, PANEL_CX + 300, 1000), (t_click1 - 0.1, a[0], a[1]), (t_click1 + 0.6, a[0], a[1]),
                  (t_click2 - 0.1, c1[0] + 20, c1[1] + 10)], lt)
        cursor(f, c, abs(lt - t_click1) < 0.12 or abs(lt - t_click2) < 0.12, 1.3)
        ripple(f, a, lt, t_click1)
        ripple(f, (c1[0] + 20, c1[1] + 10), lt, t_click2)
        toast(f, "✨ Пресет «Сбалансированный» применён", lt, t_click2 + 0.3, D)
    return f


# ====================================================================
# 5. Серверы
# ====================================================================
SERVERS = json.loads((PROMO.parent / "servers.json").read_text(encoding="utf-8"))["servers"]
SHOW = ["Hypixel", "FunTime", "CubeCraft", "Wynncraft", "HolyWorld", "2b2t"]
ONLINE = {"Hypixel": 31540, "FunTime": 9870, "CubeCraft": 6120, "Wynncraft": 2410, "HolyWorld": 4380, "2b2t": 610}
PING = {"Hypixel": 92, "FunTime": 24, "CubeCraft": 58, "Wynncraft": 104, "HolyWorld": 31, "2b2t": 140}
SCOL = [(250, 190, 40), (220, 60, 60), (60, 140, 230), (90, 200, 120), (160, 90, 220), (90, 90, 90)]


def sec_servers(lt, D, v):
    f = bg16()
    img, d = win_base("Серверы", 1200)
    d.text((40, 136), "Серверы", font=uib(48), fill=TEXT)
    x = 40
    fsel = 1 if LEAD + 0.35 * v < lt < LEAD + 0.55 * v else 0
    for i, c in enumerate(["Все", "RU", "Избранные", "Онлайн"]):
        x = chip(d, x, 210, c, 24, on=i == fsel)
    d.rounded_rectangle([CW - 340, 206, CW - 40, 262], 16, fill=INPUT, outline=LINE, width=2)
    d.text((CW - 316, 218), "Поиск…", font=ui(26), fill=(90, 104, 128))
    byname = {s["name"]: s for s in SERVERS}
    rows = [n for n in SHOW if fsel == 0 or byname[n]["region"] == "RU"]
    pick = "FunTime"
    t_play = LEAD + 0.66 * v
    pos = {}
    for i, n in enumerate(rows):
        s = byname[n]
        y = 290 + i * 146
        k = out_back(prog(lt, 0.2 + i * 0.08, 0.6 + i * 0.08))
        if k <= 0:
            continue
        sel = n == pick and lt > t_play - 1.0
        d.rounded_rectangle([40, y, CW - 40, y + 132], 20, fill=(20, 60, 40) if sel else INPUT,
                            outline=ACCENT if sel else LINE, width=3 if sel else 2)
        d.rounded_rectangle([60, y + 20, 152, y + 112], 16, fill=SCOL[SHOW.index(n)])
        d.text((106, y + 66), n[0], font=uib(52), fill=(255, 255, 255), anchor="mm")
        d.text((176, y + 16), n, font=uib(32), fill=TEXT)
        d.text((176, y + 60), s["description"][:46] + ("…" if len(s["description"]) > 46 else ""), font=ui(22), fill=MUTED)
        d.text((176, y + 92), f"{s['region']} · " + " · ".join(s["tags"][:2]), font=ui(20), fill=MUTED)
        on = round(ONLINE[n] * (1 + 0.01 * math.sin(lt * 2 + i)))
        d.text((CW - 250, y + 18), f"{on:,}".replace(",", " ") + " онлайн", font=uib(24), fill=ACCENT, anchor="ra")
        pg = PING[n]
        bars = 4 if pg < 50 else (3 if pg < 100 else 2)
        for b in range(4):
            bh = 8 + b * 6
            d.rectangle([CW - 236 + b * 12, y + 44 - bh, CW - 228 + b * 12, y + 44],
                        fill=ACCENT if b < bars else LINE)
        d.text((CW - 250, y + 54), f"{pg} мс", font=ui(20), fill=MUTED, anchor="ra")
        hov = sel and lt > t_play - 0.4
        d.rounded_rectangle([CW - 210, y + 70, CW - 60, y + 116], 16, fill=mix(ACCENT, (255, 255, 255), 0.2) if hov else ACCENT)
        d.text((CW - 135, y + 93), "Играть", font=uib(24), fill=(6, 30, 14), anchor="mm")
        pos[n] = (CW - 135, y + 93)
    to = panel(f, img, lt)
    p = to(pos.get(pick, (CW - 135, 600)))
    c = path([(0, PANEL_CX + 300, 1000), (LEAD + 0.3 * v, PANEL_CX - 200, 280), (LEAD + 0.36 * v, PANEL_CX - 200, 280),
              (t_play - 0.2, p[0] + 10, p[1] + 6)], lt)
    cursor(f, c, abs(lt - t_play) < 0.12 or abs(lt - LEAD - 0.35 * v) < 0.1, 1.3)
    ripple(f, (p[0] + 10, p[1] + 6), lt, t_play)
    if lt > t_play + 0.2:
        k = out_back(prog(lt, t_play + 0.2, t_play + 0.55))
        box = Image.new("RGBA", (760, 260), (0, 0, 0, 0))
        bd = ImageDraw.Draw(box)
        bd.rounded_rectangle([0, 0, 759, 259], 30, fill=(17, 26, 46, 250), outline=ACCENT, width=4)
        bd.text((36, 30), "Запускаю 1.20.4 · Fabric", font=uib(34), fill=TEXT)
        bd.text((36, 84), "и сразу подключаю к mc.funtime.su", font=ui(28), fill=MUTED)
        pp = smooth(prog(lt, t_play + 0.4, D - 0.3))
        bd.rounded_rectangle([36, 160, 724, 180], 10, fill=LINE)
        bd.rounded_rectangle([36, 160, 36 + 688 * pp, 180], 10, fill=ACCENT)
        draw_rich(bd, (36, 192), "Подключаюсь…" if pp < 1 else "✅ На сервере!", 26, fill=ACCENT, bold=True)
        overlay_scaled(f, box, k, PANEL_CX, 560)
    return f


# ====================================================================
# 6. Статистика (профиль из сценария 7)
# ====================================================================
def sec_stats(lt, D, v):
    f = bg16()
    a0, b0 = LEAD + 0.48 * v, LEAD + 0.74 * v
    if lt < b0:
        st = dict(total=S7M.TOTAL_H, chart=prog(lt, LEAD + 0.25 * v, LEAD + 0.4 * v) if lt > 0.1 else 0,
                  peaks=lt > LEAD + 0.38 * v, sessions=128, inst=47.3, hl_sessions=LEAD + 0.08 * v < lt < LEAD + 0.25 * v,
                  hl_inst=False, hl_ach=lt > a0 - 0.4)
        if lt < LEAD + 0.25 * v:
            st["chart"] = 1.0
        img, pos = S7M.profile(lt, st)
        keys = [(0, 0), (LEAD + 0.1 * v, 0), (LEAD + 0.18 * v, 640), (LEAD + 0.24 * v, 640), (LEAD + 0.28 * v, 300),
                (a0 - 0.6, 300), (a0 - 0.2, 1180), (D, 1180)]
        sc = 0
        for (t0, x0), (t1, x1) in zip(keys, keys[1:]):
            if lt < t1:
                sc = lerp(x0, x1, smooth(prog(lt, t0, t1)))
                break
        view = img.crop((0, round(sc), CW, round(sc) + 1200))
        panel(f, view, lt)
        if lt > a0:
            pan = S7M.achievements_panel(S7M.S3 + 0.4 + (lt - a0))
            k = out_back(prog(lt, a0, a0 + 0.35), 1.4)
            pan = pan.resize((round(pan.width * 0.85), round(pan.height * 0.85)), Image.LANCZOS)
            overlay_scaled(f, pan, k, PANEL_CX, 560)
    else:
        img = S7M.shine_badge(S7M.S4 + (lt - b0) * 1.6)
        k = out_cubic(prog(lt, b0, b0 + 0.4))
        ims = img.resize((round(img.width * 0.74), round(img.height * 0.74)), Image.LANCZOS)
        ims.putalpha(ims.getchannel("A").point(lambda p: round(p * k)))
        f.alpha_composite(ims, (round(PANEL_CX - ims.width / 2), 70))
    return f


# ====================================================================
# 7. Миры: бэкапы, восстановление, экспорт
# ====================================================================
WORLDS = [("Остров", "Выживание · Сложная · 1.21.1 · 1,2 ГБ", "играли вчера", "summer"),
          ("Креатив-город", "Творчество · 1.20.4 · 640 МБ", "играли 3 дн. назад", "winter"),
          ("Хардкор №7", "Хардкор · 1.21.1 · 210 МБ", "играли 12.10", "autumn")]


def world_icon(season):
    key = ("wi", season)
    if key not in _cache:
        h = S4M.hero_base(season)
        _cache[key] = h.crop((60, 160, 60 + 520, 160 + 520)).resize((120, 120), Image.LANCZOS)
    return _cache[key]


def sec_worlds(lt, D, v):
    f = bg16()
    img, d = win_base("Моя сборка · 1.21.1", 1200, ["🌍 Миры", "📤 Экспорт", "🧩 Моды"], 0)
    d.text((40, 214), "Миры", font=uib(44), fill=TEXT)
    x = CW - 40
    for lab in ["📥 Импорт мира"]:
        w_ = d.textlength(lab, font=uib(26)) + 60
        d.rounded_rectangle([x - w_, 210, x, 266], 16, outline=LINE, width=2)
        draw_rich(d, (x - w_ / 2, 220), lab, 26, bold=True, anchor="ma")
    t_restore = LEAD + 0.36 * v
    for i, (n, meta, when, sea) in enumerate(WORLDS):
        y = 296 + i * 210
        d.rounded_rectangle([40, y, CW - 40, y + 190], 22, fill=INPUT, outline=LINE, width=2)
        ic = world_icon(sea)
        mask = Image.new("L", ic.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, 119, 119], 18, fill=255)
        img.paste(ic, (62, y + 22), mask)
        d.text((206, y + 22), n, font=uib(34), fill=TEXT)
        d.text((206, y + 70), meta, font=ui(24), fill=MUTED)
        d.text((206, y + 104), when + (" · бэкап: сегодня 09:12" if i == 0 else ""), font=ui(22), fill=MUTED)
        bx = 206
        for j, lab in enumerate(["💾 Бэкап", "♻️ Восстановить", "📤 .zip"]):
            w_ = d.textlength(lab, font=uib(22)) + 56
            hot = i == 0 and j == 1 and t_restore - 0.4 < lt < t_restore + 0.3
            d.rounded_rectangle([bx, y + 136, bx + w_, y + 178], 14, fill=(30, 110, 62) if hot else PANEL,
                                outline=ACCENT if hot else LINE, width=2)
            draw_rich(d, (bx + 16, y + 142), lab, 22, bold=True)
            if i == 0 and j == 1:
                rest_pos = (bx + w_ / 2, y + 157)
            bx += w_ + 12
    to = panel(f, img, lt)
    rp = to(rest_pos)
    t_exp = LEAD + 0.55 * v
    c = path([(0, PANEL_CX + 300, 1000), (t_restore - 0.3, rp[0], rp[1] + 6), (t_exp - 0.5, rp[0], rp[1] + 6),
              (t_exp, PANEL_CX + 380, 980)], lt)
    if lt < t_exp:
        cursor(f, c, abs(lt - t_restore) < 0.12, 1.3)
        ripple(f, rp, lt, t_restore)
    toast(f, "💾 Автобэкап: «Остров» сохранён", lt, LEAD + 0.08 * v, t_restore - 0.5)
    toast(f, "♻️ «Остров» восстановлен из копии 26.10", lt, t_restore + 0.5, t_exp - 0.2)
    if lt > t_exp:
        k = out_back(prog(lt, t_exp, t_exp + 0.4), 1.4)
        box = Image.new("RGBA", (820, 640), (0, 0, 0, 0))
        bd = ImageDraw.Draw(box)
        bd.rounded_rectangle([0, 0, 819, 639], 34, fill=(17, 26, 46, 252), outline=ACCENT, width=4)
        draw_rich(bd, (40, 34), "📤 Экспорт сборки", 40, bold=True)
        for j, (lab, sub) in enumerate([(".mrpack — формат Modrinth", "откроют Modrinth App, Prism, RibbixLauncher"),
                                        (".zip — полная копия", "для переезда на другой компьютер")]):
            y = 112 + j * 104
            on = j == 0
            bd.ellipse([40, y + 6, 76, y + 42], outline=ACCENT if on else LINE, width=4)
            if on:
                bd.ellipse([50, y + 16, 66, y + 32], fill=ACCENT)
            bd.text((96, y), lab, font=uib(30), fill=TEXT)
            bd.text((96, y + 42), sub, font=ui(22), fill=MUTED)
        for j, (lab, on) in enumerate([("Конфиги и настройки игры", True), ("Миры", False)]):
            y = 330 + j * 56
            bd.rounded_rectangle([40, y, 76, y + 36], 8, fill=ACCENT if on else INPUT, outline=ACCENT if on else LINE, width=3)
            if on:
                bd.line([(48, y + 18), (56, y + 27), (70, y + 9)], fill=(6, 30, 14), width=4)
            bd.text((96, y), lab, font=ui(28), fill=TEXT)
        bd.text((40, 450), "Никогда не попадают: логи, скриншоты, аккаунты и пароли", font=ui(22), fill=MUTED)
        pp = smooth(prog(lt, t_exp + 1.2, t_exp + 2.6))
        bd.rounded_rectangle([40, 500, 780, 520], 10, fill=LINE)
        bd.rounded_rectangle([40, 500, 40 + 740 * pp, 520], 10, fill=ACCENT)
        draw_rich(bd, (40, 544), "✅ Моя сборка.mrpack · 2,3 МБ" if pp >= 1 else "Ищу моды на Modrinth по SHA1…",
                  28, fill=ACCENT if pp >= 1 else MUTED, bold=True)
        overlay_scaled(f, box, k, PANEL_CX, 540)
        t_imp = LEAD + 0.8 * v
        toast(f, "📥 Импорт: .mrpack · Prism / MultiMC · .zip", lt, t_imp, D)
    return f


# ====================================================================
# 8. Сезонные темы (окно и частицы из сценария 4)
# ====================================================================
SEASON_SEQ = ["spring", "summer", "autumn", "winter", "newyear"]


def season_at(lt, D, v):
    t0 = LEAD + 0.12 * v
    span = (D - t0 - 0.3) / 5
    i = int(clamp((lt - t0) / span, 0, 4.999)) if lt > t0 else 0
    return i, t0 + i * span, span


def season_frame16(season, lt, D):
    f = season_bg(season)
    win = S4M.window(season, lt)
    s = PANEL_H / win.height
    R.place(f, win, s, PANEL_CX, PANEL_TOP)
    # частицы рисуются на вертикальном холсте сценария 4 — берём верх и растягиваем на кадр
    tmp = Image.new("RGBA", (S4M.W, S4M.H), (0, 0, 0, 0))
    S4M.particles(tmp, season, S4M.T_SUMMER + 0.3 + lt * 0.8 if season == "summer" else lt)
    f.alpha_composite(tmp.crop((0, 0, 1080, 608)).resize((W, H), Image.BILINEAR))
    return f


def sec_seasons(lt, D, v):
    i, ts, span = season_at(lt, D, v)
    f = season_frame16(SEASON_SEQ[i], lt, D)
    if i > 0 and lt - ts < 0.4:  # плавный переход от предыдущей темы
        prev = season_frame16(SEASON_SEQ[i - 1], lt, D)
        f = Image.blend(prev, f, smooth((lt - ts) / 0.4))
    names = [S4M.THEMES[s][0] for s in SEASON_SEQ]
    t0 = LEAD + 0.12 * v
    extra = [((t0 + k * span - LEAD) / v, names[k]) for k in range(5)]
    left_text(f, 7, lt, v, extra)
    return f, True


# ====================================================================
# 9. Удобство: Ctrl+K, Discord, напоминание, карточка скриншота
# ====================================================================
def sec_qol(lt, D, v):
    f = bg16()
    st = dict(gfx=1, rd=12, fov=80, fps=144)
    img, _ = R.launcher(st)
    view = img.crop((0, 0, CW, 1100))
    panel(f, view, lt)
    dim = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(dim).rectangle([840, 0, W, H], fill=(5, 8, 16, 150))
    f.alpha_composite(dim)
    # палитра Ctrl+K
    tk = LEAD + 0.06 * v
    if lt > tk:
        k = out_back(prog(lt, tk, tk + 0.3), 1.5)
        box = Image.new("RGBA", (880, 560), (0, 0, 0, 0))
        bd = ImageDraw.Draw(box)
        bd.rounded_rectangle([0, 0, 879, 559], 30, fill=(17, 26, 46, 252), outline=ACCENT, width=3)
        q = "наст"[: max(0, int((lt - tk - 0.3) / 0.12))]
        bd.rounded_rectangle([24, 24, 856, 96], 18, fill=INPUT, outline=ACCENT, width=2)
        draw_rich(bd, (48, 38), "🔎 " + (q or "Поиск по лаунчеру…"), 32, fill=TEXT if q else MUTED)
        bd.text((830, 60), "Ctrl+K", font=uib(22), fill=MUTED, anchor="rm")
        res = [("⚙️", "Настройки", "страница"), ("🎨", "Настройки: Моя сборка", "версия"),
               ("🔔", "Напоминание о перерыве", "настройка"), ("🌐", "Язык интерфейса", "настройка"),
               ("🧩", "Найти мод: настройки HUD", "Modrinth")]
        if q:
            sel = int((lt - tk) / 0.5) % 5
            for j, (e, a, b) in enumerate(res):
                y = 120 + j * 84
                if j == sel:
                    bd.rounded_rectangle([20, y, 860, y + 74], 16, fill=(20, 70, 44))
                bd.text((44, y + 14), e, font=emo(34), embedded_color=True)
                bd.text((104, y + 16), a, font=uib(30), fill=TEXT)
                bd.text((840, y + 22), b, font=ui(22), fill=MUTED, anchor="ra")
        if lt < LEAD + 0.3 * v:
            overlay_scaled(f, box, k, PANEL_CX, 420)
        else:
            sm = box.resize((round(box.width * 0.55), round(box.height * 0.55)), Image.LANCZOS)
            f.alpha_composite(sm, (round(PANEL_CX - 430), 90))
    # напоминание из трея
    tr = LEAD + 0.28 * v
    if lt > tr:
        k = out_back(prog(lt, tr, tr + 0.35))
        b = Image.new("RGBA", (560, 170), (0, 0, 0, 0))
        bd = ImageDraw.Draw(b)
        bd.rounded_rectangle([0, 0, 559, 169], 24, fill=(30, 36, 52, 250), outline=LINE, width=2)
        b.alpha_composite(asset("ui/icon_frog.png", 0.5), (24, 24))
        bd.text((106, 26), "RibbixLauncher", font=uib(26), fill=TEXT)
        bd.text((106, 66), "Вы играете уже 2 часа.", font=ui(26), fill=TEXT)
        bd.text((106, 104), "Пора размяться?", font=ui(26), fill=MUTED)
        overlay_scaled(f, b, k, PANEL_CX + 230, 520 if lt < tr + 99 else 520)
    # карточка скриншота
    tsc = LEAD + 0.48 * v
    if lt > tsc:
        k = out_back(prog(lt, tsc, tsc + 0.35))
        card = Image.new("RGBA", (260, 462), (0, 0, 0, 0))
        cd = ImageDraw.Draw(card)
        cd.rounded_rectangle([0, 0, 259, 461], 22, fill=(11, 18, 32))
        shot = S4M.hero_base("summer").crop((0, 80, 900, 980)).resize((236, 236), Image.LANCZOS)
        card.paste(shot, (12, 70))
        card.alpha_composite(asset("ui/icon_frog.png", 0.4), (14, 14))
        cd.text((70, 22), "RibbixLauncher", font=uib(20), fill=TEXT)
        cd.text((14, 322), "Kvakusha", font=uib(26), fill=TEXT)
        cd.text((14, 360), "1.21.1 · 26.10.2026", font=ui(18), fill=MUTED)
        cd.rounded_rectangle([14, 404, 246, 446], 14, fill=ACCENT)
        draw_rich(cd, (130, 412), "🐸 Поделиться", 20, fill=(6, 30, 14), bold=True, anchor="ma")
        overlay_scaled(f, card, k, PANEL_CX - 250, 760)
    # статус в Discord
    tdc = LEAD + 0.66 * v
    if lt > tdc:
        k = out_back(prog(lt, tdc, tdc + 0.35))
        b = Image.new("RGBA", (560, 200), (0, 0, 0, 0))
        bd = ImageDraw.Draw(b)
        bd.rounded_rectangle([0, 0, 559, 199], 24, fill=(43, 45, 49, 252), outline=(88, 101, 242), width=3)
        bd.text((26, 18), "ИГРАЕТ", font=uib(20), fill=(181, 186, 193))
        b.alpha_composite(asset("ui/icon_frog.png", 0.75), (26, 56))
        bd.text((150, 58), "RibbixLauncher", font=uib(28), fill=(242, 243, 245))
        bd.text((150, 100), "Играет в Minecraft 1.20.1 · Fabric", font=ui(22), fill=(219, 222, 225))
        mm = int(42 + (lt - tdc))
        bd.text((150, 136), f"00:{mm:02d} прошло", font=ui(22), fill=(181, 186, 193))
        overlay_scaled(f, b, k, PANEL_CX + 230, 790)
    # языки
    tl = LEAD + 0.8 * v
    if lt > tl:
        d = ImageDraw.Draw(f)
        x = PANEL_CX - 420
        for j, lang in enumerate(["RU", "EN", "ES", "DE", "PT-BR"]):
            if lt > tl + j * 0.12:
                x = chip(d, x, 960, lang, 26, on=j == 0)
    return f


# ====================================================================
# 10. CTA
# ====================================================================
def sec_cta(lt, D, v):
    f = Image.new("RGBA", (W, H), (22, 163, 74, 255))
    d = ImageDraw.Draw(f)
    cx, cy = 560, 540
    for i in range(16):
        ang = i / 16 * math.tau + lt * 0.3
        d.polygon([(cx, cy), (cx + math.cos(ang) * 2200, cy + math.sin(ang) * 2200),
                   (cx + math.cos(ang + 0.1) * 2200, cy + math.sin(ang + 0.1) * 2200)], fill=(34, 197, 94))
    k = out_back(prog(lt, 0.1, 0.6), 1.8)
    overlay_scaled(f, asset("main/logo.png", 2.6), k, cx, cy)
    items = [(0.0, "RibbixLauncher", 96, (255, 255, 255)), (0.25, "Бесплатно", 120, (255, 236, 90)),
             (0.55, "Ссылка в описании 👇", 64, (255, 255, 255))]
    y = 250
    for frac, text, size, col in items:
        tb = LEAD + frac * v
        if lt > tb:
            kk = out_back(prog(lt, tb, tb + 0.3), 2)
            if text.endswith("👇"):
                im = R.title_img([text[:-2]], size, col)
                im2 = Image.new("RGBA", (im.width + size + 20, im.height), (0, 0, 0, 0))
                im2.alpha_composite(im)
                ImageDraw.Draw(im2).text((im.width - 6, 18), "👇", font=emo(size), embedded_color=True)
                im = im2
            else:
                im = R.title_cached(text, size, col)
            overlay_scaled(f, im, kk, 1390, y + im.height / 2)
            y += im.height + 30
    if lt > LEAD + 0.4 * v:
        x = 1010
        for j, c in enumerate(["Vanilla", "Fabric", "Quilt", "Forge", "NeoForge", "Modrinth"]):
            x = chip(d, x, 860, c, 26)
        d.text((1390, 950), "Играть можно на любом тарифе — даже без аккаунта", font=ui(30), fill=(6, 40, 18),
               anchor="ma")
    return f


RENDER = [sec_intro, sec_versions, sec_mods, sec_settings, sec_servers, sec_stats, sec_worlds, sec_seasons,
          sec_qol, sec_cta]


def frame_at(t):
    i = max(j for j, s in enumerate(STARTS) if t >= s)
    lt = t - STARTS[i]
    end = STARTS[i + 1] if i + 1 < len(STARTS) else DUR
    D = end - STARTS[i]
    res = RENDER[i](lt, D, VO[i])
    own_text = isinstance(res, tuple)
    f = res[0] if own_text else res
    if not own_text and SECTIONS[i][1]:
        left_text(f, i, lt, VO[i])
    if 0 < i:
        ad_label(f)
    if 0 < i < 9:
        progress_strip(f, t)
    if lt < 0.12 and i > 0:
        f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(110 * (1 - lt / 0.12)))))
    if t > DUR - 0.6:
        f.alpha_composite(Image.new("RGBA", (W, H), (0, 0, 0, round(255 * prog(t, DUR - 0.6, DUR)))))
    return f.convert("RGB")


def main():
    args = sys.argv[1:]
    if "--only" in args:
        for ts in args[args.index("--only") + 1].split(","):
            frame_at(float(ts)).save(HERE / "frames" / f"f_{float(ts):06.2f}.png")
        return
    out = args[0] if args else str(HERE / "out" / "video_noaudio.mp4")
    ff = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                           "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-", "-c:v", "libx264",
                           "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", out], stdin=subprocess.PIPE)
    n = round(DUR * FPS)
    for i in range(n):
        ff.stdin.write(frame_at(i / FPS).tobytes())
        if i % 150 == 0:
            print(f"{i}/{n}", flush=True)
    ff.stdin.close()
    ff.wait()
    print("done", out, f"{DUR:.2f}s")


if __name__ == "__main__":
    main()
