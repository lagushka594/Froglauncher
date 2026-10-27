"""Сценарий 3 «Настройки MC прямо в лаунчере» — покадровый рендер 1080x1920 @30fps.

Кадры рисуются Pillow'ом и отдаются в ffmpeg через stdin (без звука; звук — mix.py).
Запуск:  python render.py [out.mp4] [--only 12.0]   (--only: один PNG-кадр на этой секунде)
"""
import math
import random
import subprocess
import sys
from functools import lru_cache
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).parent
ASSETS = HERE.parent.parent / "assets" / "out"
W, H, FPS, DUR = 1080, 1920, 30, 30.0

# Границы сцен подогнаны под длину озвучки (см. mix.py)
S1, S2, S3, S4, S5, S6, END = 0.0, 3.6, 10.05, 16.7, 23.0, 27.2, 30.0

# --- палитра лаунчера (Themes/Theme.xaml) ---
BG = (11, 18, 32)
PANEL = (17, 26, 46)
INPUT = (14, 22, 40)
LINE = (36, 49, 79)
TEXT = (231, 248, 236)
MUTED = (147, 163, 184)
ACCENT = (34, 197, 94)
ACCENT2 = (6, 182, 212)
DANGER = (239, 68, 68)

FONTS = "C:/Windows/Fonts/"


@lru_cache(None)
def font(name, size):
    return ImageFont.truetype(FONTS + name, size)


def ui(size):
    return font("segoeui.ttf", size)


def uib(size):
    return font("segoeuib.ttf", size)


def emo(size):
    return font("seguiemj.ttf", size)


@lru_cache(None)
def asset(rel, scale=1.0):
    im = Image.open(ASSETS / rel).convert("RGBA")
    if scale != 1.0:
        im = im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS)
    return im


# --- easing ---
def clamp(x, a=0.0, b=1.0):
    return max(a, min(b, x))


def lerp(a, b, t):
    return a + (b - a) * t


def prog(t, t0, t1):
    return clamp((t - t0) / (t1 - t0)) if t1 > t0 else float(t >= t0)


def smooth(x):
    x = clamp(x)
    return x * x * (3 - 2 * x)


def out_cubic(x):
    x = clamp(x)
    return 1 - (1 - x) ** 3


def out_back(x, s=1.9):
    x = clamp(x)
    return 1 + (s + 1) * (x - 1) ** 3 + s * (x - 1) ** 2


def mix(c1, c2, t):
    return tuple(round(lerp(a, b, t)) for a, b in zip(c1, c2))


# --- текст ---
def draw_rich(d, xy, text, size, fill=TEXT, bold=False, anchor="la"):
    """Текст с ведущим эмодзи («🎨 Графика»): эмодзи цветным шрифтом, остальное Segoe UI."""
    f = uib(size) if bold else ui(size)
    x, y = xy
    parts = text.split(" ", 1)
    if len(parts) == 2 and not parts[0].isalnum() and ord(parts[0][0]) > 0x2000:
        e, rest = parts
        ef = emo(round(size * 0.9))
        ew = d.textlength(e, font=ef)
        total = ew + size * 0.35 + d.textlength(rest, font=f)
        if anchor[0] == "m":
            x -= total / 2
        d.text((x, y + size * 0.12), e, font=ef, embedded_color=True)
        d.text((x + ew + size * 0.35, y), rest, font=f, fill=fill)
        return
    if anchor[0] == "m":
        x -= d.textlength(text, font=f) / 2
    d.text((x, y), text, font=f, fill=fill)


def title_img(lines, size=96, color=(255, 255, 255), stroke=9):
    """Крупный титр: белый Impact с чёрной обводкой; эмодзи в начале строки — цветные."""
    f = font("impact.ttf", size)
    ef = emo(round(size * 0.82))
    tmp = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
    rows = []
    for ln in lines:
        e, rest = "", ln
        if ln and ord(ln[0]) > 0x2000 and ln[0] not in "→—":
            e, rest = ln.split(" ", 1)
        ew = tmp.textlength(e, font=ef) + size * 0.25 if e else 0
        rows.append((e, rest, ew, ew + tmp.textlength(rest, font=f)))
    lh = round(size * 1.12)
    wmax = round(max(r[3] for r in rows)) + stroke * 2 + 20
    img = Image.new("RGBA", (wmax, lh * len(rows) + stroke * 2 + 20), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for i, (e, rest, ew, tw) in enumerate(rows):
        x = (wmax - tw) / 2
        y = 10 + stroke + i * lh
        if e:
            d.text((x, y + size * 0.1), e, font=ef, embedded_color=True)
        d.text((x + ew, y), rest, font=f, fill=color, stroke_width=stroke, stroke_fill=(0, 0, 0))
    return img


@lru_cache(None)
def title_cached(key, size, color):
    return title_img(key.split("\n"), size, color)


def paste_title(frame, text, t, t_in, cy, size=96, color=(255, 255, 255), t_out=None, shake=0.0):
    """Поп-ин с перелётом масштаба; опционально исчезновение и тряска."""
    if t < t_in or (t_out is not None and t >= t_out + 0.12):
        return
    img = title_cached(text, size, color)
    k = out_back(prog(t, t_in, t_in + 0.28), 2.4)
    s = lerp(0.55, 1.0, k)
    a = clamp(prog(t, t_in, t_in + 0.08))
    if t_out is not None and t > t_out:
        p = prog(t, t_out, t_out + 0.12)
        s *= 1 + 0.15 * p
        a *= 1 - p
    if s <= 0.01 or a <= 0:
        return
    im = img.resize((max(1, round(img.width * s)), max(1, round(img.height * s))), Image.BILINEAR)
    if a < 1:
        im.putalpha(im.getchannel("A").point(lambda v: round(v * a)))
    dx = dy = 0
    if shake:
        dx = round(math.sin(t * 91) * shake)
        dy = round(math.cos(t * 77) * shake * 0.6)
    frame.alpha_composite(im, (round(W / 2 - im.width / 2 + dx), round(cy - im.height / 2 + dy)))


# ====================================================================
# Пиксельный «мир» — слоистый блочный пейзаж (превью и геймплей)
# ====================================================================
NLAYERS = 10
SKY_TOP, SKY_BOT = (96, 160, 255), (178, 214, 255)
FOG = (196, 222, 250)


def make_layers(w, h, seed=7, base_block=40):
    """Слои от ближнего (0) к дальнему (NLAYERS-1), без тумана. Ширина w — с запасом для панорамы."""
    rnd = random.Random(seed)
    layers = []
    for d in range(NLAYERS):
        bs = max(5, round(base_block * 0.8 ** d))
        depth = d / (NLAYERS - 1)
        base = h * lerp(0.94, 0.50, depth ** 0.7)
        amp = h * lerp(0.04, 0.30, depth ** 1.2)
        ph = [rnd.uniform(0, 6.28) for _ in range(4)]
        fr = [rnd.uniform(0.6, 1.2), rnd.uniform(1.8, 2.6), rnd.uniform(4, 6), rnd.uniform(9, 13)]
        im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        dr = ImageDraw.Draw(im)
        cols = w // bs + 1
        heights = []
        for c in range(cols):
            u = c * bs / w * 2 * math.pi
            n = (0.55 * math.sin(fr[0] * u + ph[0]) + 0.28 * math.sin(fr[1] * u + ph[1])
                 + 0.12 * math.sin(fr[2] * u + ph[2]) + 0.05 * math.sin(fr[3] * u + ph[3]))
            top = base - amp * (n + 1) / 2 * 2
            heights.append(round(top / bs) * bs)
        snow_line = h * 0.36
        for c, top in enumerate(heights):
            x = c * bs
            y = top
            k = 0
            while y < h:
                if k == 0:
                    col = (240, 246, 255) if (top < snow_line and d >= 4) else (88 + rnd.randint(-8, 8), 170 + rnd.randint(-10, 10), 60)
                elif k < 3:
                    col = (134 + rnd.randint(-8, 8), 96, 62)
                else:
                    g = 118 + rnd.randint(-10, 10)
                    col = (g, g, g + 4)
                dr.rectangle([x, y, x + bs - 1, y + bs - 1], fill=col)
                if bs >= 12:
                    dr.rectangle([x, y, x + bs - 1, y + bs - 1], outline=tuple(max(0, v - 18) for v in col))
                y += bs
                k += 1
        # деревья
        if d <= 7:
            c = rnd.randint(1, 5)
            while c < cols - 4:
                top = heights[c]
                if heights[c - 1] == top and top > snow_line:
                    th = rnd.randint(3, 5)
                    x = c * bs
                    for i in range(th):
                        dr.rectangle([x, top - (i + 1) * bs, x + bs - 1, top - i * bs - 1], fill=(104, 72, 40))
                    ly = top - th * bs
                    for r, (lw, off) in enumerate([(5, -2), (5, -2), (3, -1), (1, 0)]):
                        for j in range(lw):
                            lc = (46 + rnd.randint(-8, 10), 128 + rnd.randint(-14, 14), 42)
                            xx = x + (off + j) * bs
                            yy = ly - (r + 1) * bs + bs
                            dr.rectangle([xx, yy - bs, xx + bs - 1, yy - 1], fill=lc)
                c += rnd.randint(10, 18) if d < 2 else (rnd.randint(5, 10) if d < 4 else rnd.randint(2, 6))
        layers.append(im)
    return layers


def sky(w, h):
    im = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(im)
    for y in range(h):
        d.line([(0, y), (w, y)], fill=mix(SKY_TOP, SKY_BOT, (y / h) ** 0.8))
    return im


@lru_cache(None)
def fogged(layers_key, d, fog_q):
    """Слой d, смешанный с цветом тумана (fog_q — квантованная доля 0..20)."""
    lay = WORLDS[layers_key][d]
    if fog_q == 0:
        return lay
    solid = Image.new("RGBA", lay.size, FOG + (255,))
    out = Image.blend(lay, solid, fog_q / 20)
    out.putalpha(lay.getchannel("A"))
    return out


WORLDS = {}


def world_view(key, w, h, rd, pan=0.0, clouds_t=0.0):
    """Композиция мира под дальность прорисовки rd (чанки): видно ~rd/2 слоёв, дальше — туман."""
    layers = WORLDS[key]
    img = sky(w, h).convert("RGBA") if (key, "sky") not in WORLDS else WORLDS[(key, "sky")].copy()
    d = ImageDraw.Draw(img)
    # солнце
    sx, sy, ss = round(w * 0.78), round(h * 0.16), round(h * 0.09)
    d.rectangle([sx - ss, sy - ss, sx + ss, sy + ss], fill=(255, 240, 150))
    d.rectangle([sx - ss + 6, sy - ss + 6, sx + ss - 6, sy + ss - 6], fill=(255, 251, 210))
    vis = rd / 2.0
    for dd in range(NLAYERS - 1, -1, -1):
        a = clamp(vis - dd)
        if a <= 0:
            continue
        fog = clamp((dd / max(vis, 1)) ** 1.3 * 0.9)
        lay = fogged(key, dd, round(fog * 20))
        off = round(pan * (1.0 / (1 + dd * 0.55))) % (lay.width - w) if pan else 0
        crop = lay.crop((off, 0, off + w, h))
        if a < 1:
            crop.putalpha(crop.getchannel("A").point(lambda v, a=a: round(v * a)))
        img.alpha_composite(crop)
    # облака
    rnd = random.Random(3)
    for i in range(7):
        cw = rnd.randint(4, 9) * h // 60
        ch = h // 40
        cx = (rnd.uniform(0, w * 1.4) + clouds_t * h * 0.03) % (w * 1.4) - w * 0.2
        cy = h * rnd.uniform(0.05, 0.22)
        d.rectangle([cx, cy, cx + cw, cy + ch], fill=(255, 255, 255, 235))
        d.rectangle([cx + cw * 0.2, cy - ch, cx + cw * 0.7, cy], fill=(255, 255, 255, 235))
    # дымка у горизонта, сильнее на малой дальности
    haze = clamp(1.2 - rd / 14)
    if haze > 0:
        g = Image.new("L", (1, h))
        for y in range(h):
            g.putpixel((0, y), round(255 * haze * clamp(1 - abs(y / h - 0.52) / 0.22) * 0.85))
        g = g.resize((w, h))
        img.paste(Image.new("RGBA", (w, h), FOG + (255,)), (0, 0), g)
    return img


# ====================================================================
# Мокап лаунчера (вкладка «🎨 Графика» из InstancesView.xaml)
# ====================================================================
PRESETS = [
    ("Производительность", "Слабые ПК и ноутбуки", "⚡", dict(gfx=0, rd=8, fov=70, fps=120)),
    ("Сбалансированный", "Средние ПК — рекомендуем", "⚖️", dict(gfx=1, rd=12, fov=80, fps=144)),
    ("Качество", "Мощные ПК, видеокарта 6+ ГБ", "💎", dict(gfx=2, rd=24, fov=90, fps=260)),
]
GFX = ["Быстрая (Fast)", "Красивая (Fancy)", "Fabulous!"]
CW = 1000  # ширина окна лаунчера


def slider(d, x, y, w, v, vmin, vmax, active=False):
    p = (v - vmin) / (vmax - vmin)
    d.rounded_rectangle([x, y - 5, x + w, y + 5], 5, fill=LINE)
    d.rounded_rectangle([x, y - 5, x + max(10, w * p), y + 5], 5, fill=ACCENT)
    kx = x + w * p
    r = 17 if active else 14
    if active:
        d.ellipse([kx - r - 8, y - r - 8, kx + r + 8, y + r + 8], fill=(34, 197, 94, 70))
    d.ellipse([kx - r, y - r, kx + r, y + r], fill=(240, 255, 244), outline=ACCENT, width=4)
    return kx


def launcher(st):
    """Рисует окно лаунчера в координатах контента. Возвращает (img, позиции элементов)."""
    ch = round(230 * out_cubic(st.get("cards", 0)))
    pv = round(390 * out_cubic(st.get("preview", 0)))
    hh = 1430 + ch + pv
    img = Image.new("RGBA", (CW, hh), PANEL + (255,))
    d = ImageDraw.Draw(img)
    pos = {}
    # заголовок с фирменным фоном
    bg = asset("main/bg.png").resize((CW, round(CW * 298 / 638)), Image.LANCZOS).crop((0, 120, CW, 230))
    img.alpha_composite(bg, (0, 0))
    d.rectangle([0, 0, CW, 110], fill=(11, 18, 32, 150))
    img.alpha_composite(asset("ui/icon_frog.png", 0.58), (24, 14))
    d.text((100, 26), "FrogLauncher", font=uib(38), fill=TEXT)
    d.text((CW - 30, 40), "1.21.1 · Fabric", font=ui(26), fill=MUTED, anchor="ra")
    # вкладки
    x = 28
    for i, tab in enumerate(["🎨 Графика", "🎮 Игра", "🔊 Звук", "🧩 Моды"]):
        tw = d.textlength(tab, font=uib(28)) + 44
        if i == 0:
            d.rounded_rectangle([x, 128, x + tw, 184], 14, fill=ACCENT)
            draw_rich(d, (x + 20, 136), tab, 28, fill=(6, 30, 14), bold=True)
        else:
            d.rounded_rectangle([x, 128, x + tw, 184], 14, outline=LINE, width=2)
            draw_rich(d, (x + 20, 136), tab, 28, fill=MUTED, bold=True)
        x += tw + 14
    y = 214
    # «Подобрать под мой ПК»
    hov = st.get("auto_hover", 0)
    press = st.get("auto_press", 0)
    bx0, by0, bx1, by1 = 28, y, CW - 28, y + 96
    sh = round(press * 4)
    d.rounded_rectangle([bx0 + sh, by0 + sh, bx1 - sh, by1 - sh], 20,
                        fill=mix((22, 60, 40), (30, 110, 62), hov), outline=ACCENT, width=3)
    draw_rich(d, ((bx0 + bx1) / 2, by0 + 24), "🧠 Подобрать под мой ПК", 36, bold=True, anchor="ma")
    pos["auto"] = ((bx0 + bx1) / 2 + 180, (by0 + by1) / 2)
    y = by1 + 16
    d.text((32, y), "Ryzen 5 5600 · RTX 3060 · 16 ГБ RAM", font=ui(25), fill=MUTED)
    d.text((32, y + 36), "Рекомендуем: Сбалансированный", font=uib(25), fill=ACCENT)
    y += 86
    # карточки пресетов
    if ch > 4:
        cw3 = (CW - 56 - 28) / 3
        for i, (name, who, ic, _) in enumerate(PRESETS):
            k = out_back(prog(st.get("cards", 0), i * 0.18, 0.55 + i * 0.18), 2.0)
            if k <= 0:
                continue
            cx0 = 28 + i * (cw3 + 14)
            sel = st.get("sel", -1) == i
            hv = st.get("card_hover", -1) == i
            card = Image.new("RGBA", (round(cw3), 210), (0, 0, 0, 0))
            cd = ImageDraw.Draw(card)
            cd.rounded_rectangle([0, 0, card.width - 1, 209], 22,
                                 fill=(20, 70, 44) if sel else ((26, 38, 64) if hv else INPUT),
                                 outline=ACCENT if (sel or hv) else LINE, width=4 if sel else 2)
            cd.text((22, 18), ic, font=emo(44), embedded_color=True)
            cd.text((22, 84), name, font=uib(27 if len(name) < 17 else 24), fill=TEXT)
            cd.text((22, 124), who, font=ui(20), fill=MUTED)
            if sel:
                cx = card.width - 60
                cd.line([(cx, 42), (cx + 12, 56), (cx + 36, 28)], fill=ACCENT, width=7, joint="curve")
            if i == 1:
                cd.rounded_rectangle([22, 162, 172, 194], 10, fill=(34, 197, 94, 60))
                cd.text((34, 164), "рекомендуем", font=uib(19), fill=ACCENT)
            s = lerp(0.6, 1.0, k)
            cs = card.resize((max(1, round(card.width * s)), max(1, round(card.height * s))), Image.BILINEAR)
            img.alpha_composite(cs, (round(cx0 + cw3 / 2 - cs.width / 2), round(y + 105 - cs.height / 2)))
            pos[f"card{i}"] = (cx0 + cw3 / 2, y + 105)
        y += ch
    flash = st.get("flash", 0)  # подсветка значений после применения пресета
    def row_bg(yy, hgt):
        if flash > 0:
            ov = Image.new("RGBA", (CW - 36, hgt + 10), (0, 0, 0, 0))
            ImageDraw.Draw(ov).rounded_rectangle([0, 0, ov.width - 1, ov.height - 1], 16,
                                                 fill=(34, 197, 94, round(70 * flash)))
            img.alpha_composite(ov, (18, yy - 10))
    # Качество
    d.text((32, y), "Качество", font=uib(34), fill=TEXT)
    y += 62
    row_bg(y, 70)
    d.text((32, y + 12), "Графика", font=ui(28), fill=MUTED)
    d.rounded_rectangle([CW - 400, y, CW - 32, y + 60], 14, fill=INPUT, outline=LINE, width=2)
    d.text((CW - 380, y + 12), GFX[st["gfx"]], font=uib(28), fill=TEXT)
    d.polygon([(CW - 72, y + 24), (CW - 50, y + 24), (CW - 61, y + 38)], fill=MUTED)
    y += 96
    # Дальность прорисовки
    rd = st["rd"]
    rdi = round(rd)
    row_bg(y, 110)
    d.text((32, y), "Дальность прорисовки: ", font=ui(30), fill=MUTED)
    lx = 32 + d.textlength("Дальность прорисовки: ", font=ui(30))
    d.text((lx, y), f"{rdi} чанков ≈ {rdi * 16} блоков", font=uib(30),
           fill=ACCENT if st.get("rd_active") else TEXT)
    kx = slider(d, 40, y + 74, CW - 80, rd, 2, 32, st.get("rd_active", False))
    pos["rd_knob"] = (kx, y + 74)
    pos["rd_x"] = (40, CW - 80)
    y += 120
    # Превью мира
    if pv > 4:
        ph = pv - 30
        prev = world_view("preview", CW - 64, 360, rd)
        prev = prev.crop((0, 0, CW - 64, ph))
        mask = Image.new("L", prev.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, prev.width - 1, prev.height - 1], 18, fill=255)
        img.paste(prev, (32, y), mask)
        d.rounded_rectangle([32, y, CW - 32, y + ph], 18, outline=LINE, width=3)
        if ph > 80:
            d.rounded_rectangle([50, y + 18, 310, y + 64], 12, fill=(11, 18, 32, 200))
            draw_rich(d, (66, y + 22), "👁 Превью мира", 26, bold=True)
        pos["preview"] = (CW / 2, y + ph / 2)
        y += pv
    # FOV
    row_bg(y, 110)
    fov = st["fov"]
    d.text((32, y), "Поле зрения (FOV): ", font=ui(30), fill=MUTED)
    lx = 32 + d.textlength("Поле зрения (FOV): ", font=ui(30))
    d.text((lx, y), f"{round(fov)}°", font=uib(30), fill=TEXT)
    slider(d, 40, y + 74, CW - 80, fov, 30, 110)
    y += 130
    # FPS
    row_bg(y, 110)
    fps = st["fps"]
    d.text((32, y), "Максимальный FPS: ", font=ui(30), fill=MUTED)
    lx = 32 + d.textlength("Максимальный FPS: ", font=ui(30))
    d.text((lx, y), "Без ограничений" if fps >= 255 else str(round(fps)), font=uib(30), fill=TEXT)
    slider(d, 40, y + 74, CW - 80, fps, 10, 260)
    y += 130
    # Облака / частицы
    d.text((32, y + 6), "Облака", font=ui(28), fill=MUTED)
    d.rounded_rectangle([200, y, 520, y + 56], 14, fill=INPUT, outline=LINE, width=2)
    d.text((220, y + 10), "Красивые" if st["gfx"] else "Быстрые", font=uib(27), fill=TEXT)
    d.text((560, y + 6), "V-Sync", font=ui(28), fill=MUTED)
    vs = st["gfx"] == 1
    d.rounded_rectangle([680, y + 6, 760, y + 50], 22, fill=ACCENT if vs else LINE)
    d.ellipse([722 if vs else 686, y + 10, 758 if vs else 722, y + 46], fill=(240, 255, 244))
    y += 100
    # Играть
    pb = asset("main/btn_play.png", 1.55)
    ps = 1 - 0.06 * st.get("play_press", 0) + 0.04 * st.get("play_hover", 0)
    pbs = pb.resize((round(pb.width * ps), round(pb.height * ps)), Image.LANCZOS)
    img.alpha_composite(pbs, (round(CW / 2 - pbs.width / 2), round(y + 110 - pbs.height / 2)))
    pos["play"] = (CW / 2 - 30, y + 110)
    return img, pos


def phone_bg(t):
    """Тёмный фон с мягким зелёным свечением."""
    if "bg" not in WORLDS:
        im = Image.new("RGB", (W, H), BG)
        glow = Image.new("RGB", (W, H), (0, 0, 0))
        gd = ImageDraw.Draw(glow)
        gd.ellipse([-200, 1000, 900, 2200], fill=(16, 90, 48))
        gd.ellipse([500, -300, 1400, 700], fill=(6, 70, 90))
        glow = glow.filter(ImageFilter.GaussianBlur(220))
        from PIL import ImageChops
        im = ImageChops.add(im, glow)
        gd = ImageDraw.Draw(im)
        for x in range(0, W, 60):
            gd.line([(x, 0), (x, H)], fill=(20, 30, 50))
        for y in range(0, H, 60):
            gd.line([(0, y), (W, y)], fill=(20, 30, 50))
        WORLDS["bg"] = im.convert("RGBA")
    return WORLDS["bg"].copy()


def place(frame, img, scale, cx, top, radius=34, shadow=True):
    """Кладёт окно (контент) на кадр с масштабом; возвращает функцию контент->экран."""
    sw, sh = round(img.width * scale), round(img.height * scale)
    im = img.resize((sw, sh), Image.BILINEAR) if scale != 1 else img
    x0 = round(cx - sw / 2)
    if shadow:
        sd = Image.new("RGBA", (sw + 120, sh + 120), (0, 0, 0, 0))
        ImageDraw.Draw(sd).rounded_rectangle([60, 70, sw + 60, sh + 70], radius, fill=(0, 0, 0, 150))
        frame.alpha_composite(sd.filter(ImageFilter.GaussianBlur(26)), (x0 - 60, round(top) - 60))
    mask = Image.new("L", (sw, sh), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, sw - 1, sh - 1], radius, fill=255)
    frame.paste(im, (x0, round(top)), mask)
    ImageDraw.Draw(frame).rounded_rectangle([x0, round(top), x0 + sw - 1, round(top) + sh - 1], radius,
                                            outline=(40, 70, 60), width=3)
    return lambda p: (x0 + p[0] * scale, top + p[1] * scale)


def cursor(frame, xy, clicking=False, scale=1.6):
    cur = asset("ui/cursor_click.png" if clicking else "ui/cursor.png", scale)
    frame.alpha_composite(cur, (round(xy[0] - 6 * scale), round(xy[1] - 4 * scale)))


def ripple(frame, xy, t, t0):
    p = prog(t, t0, t0 + 0.35)
    if 0 < p < 1:
        r = 20 + 60 * out_cubic(p)
        ImageDraw.Draw(frame).ellipse([xy[0] - r, xy[1] - r, xy[0] + r, xy[1] + r],
                                      outline=(180, 255, 200, round(220 * (1 - p))), width=6)


def path(points, t):
    """Кусочно-линейный путь курсора со сглаживанием: points=[(t,x,y),...]."""
    if t <= points[0][0]:
        return points[0][1:]
    for (t0, x0, y0), (t1, x1, y1) in zip(points, points[1:]):
        if t <= t1:
            k = smooth(prog(t, t0, t1))
            return (lerp(x0, x1, k), lerp(y0, y1, k))
    return points[-1][1:]


BASE = dict(gfx=1, rd=8, fov=70, fps=120)


# ---------------- Сцена 1: крючок ----------------
def scene1(t):
    f = phone_bg(t)
    st = dict(BASE, preview=0)
    img, pos = launcher(st)
    scroll = 260 * smooth(prog(t, 0.2, 3.4))
    zoom = lerp(1.08, 1.0, out_cubic(prog(t, 0, 3.6)))
    view = img.crop((0, round(scroll), CW, round(scroll) + 1180))
    to = place(f, view, zoom, W / 2, 680)
    # «палец»-курсор тянет вверх
    cy = lerp(1500, 1180, smooth(prog(t, 0.2, 3.4)))
    cursor(f, (720 + 10 * math.sin(t * 3), cy), clicking=0.2 < t < 3.4)
    paste_title(f, "ТЫ ВСЁ ЕЩЁ НАСТРАИВАЕШЬ\nГРАФИКУ ВНУТРИ ИГРЫ?", t, 0.12, 400, 88)
    return f


# ---------------- Сцена 2: проблема (мокап игры) ----------------
GAME = (60, 700, 1020, 1300)  # окно игры на экране


def mc_button(d, x, y, w, label, hover=False):
    h = 64
    d.rectangle([x, y, x + w, y + h], fill=(0, 0, 0))
    d.rectangle([x + 3, y + 3, x + w - 3, y + h - 3], fill=(120, 126, 160) if hover else (111, 111, 111))
    d.rectangle([x + 3, y + 3, x + w - 3, y + 8], fill=(160, 166, 200) if hover else (150, 150, 150))
    d.rectangle([x + 3, y + h - 9, x + w - 3, y + h - 3], fill=(70, 70, 90) if hover else (76, 76, 76))
    d.text((x + w / 2 + 2, y + h / 2 + 2), label, font=font("consolab.ttf", 28), fill=(60, 60, 60), anchor="mm")
    d.text((x + w / 2, y + h / 2), label, font=font("consolab.ttf", 28),
           fill=(255, 255, 160) if hover else (240, 240, 240), anchor="mm")


def game_screen(lt, period):
    """Один цикл «запустил → настройки → FOV → вышел». lt в долях цикла 0..1."""
    gw, gh = GAME[2] - GAME[0], GAME[3] - GAME[1]
    if "menu_bg" not in WORLDS:
        WORLDS["menu_bg"] = world_view("preview", gw, gh, 12).filter(ImageFilter.GaussianBlur(5))
    im = Image.new("RGBA", (gw, gh), (0, 0, 0, 255))
    d = ImageDraw.Draw(im)
    bw = 520
    bx = gw / 2 - bw / 2
    cur = (gw / 2, gh / 2)
    click = False
    stage = ""
    if lt < 0.22:  # загрузка
        d.rectangle([0, 0, gw, gh], fill=(28, 20, 18))
        p = prog(lt, 0, 0.2)
        d.text((gw / 2, gh / 2 - 60), "Загрузка мира…", font=font("consolab.ttf", 40), fill=(230, 230, 230), anchor="mm")
        d.rectangle([gw / 2 - 260, gh / 2, gw / 2 + 260, gh / 2 + 24], outline=(230, 230, 230), width=3)
        d.rectangle([gw / 2 - 254, gh / 2 + 6, gw / 2 - 254 + 508 * p, gh / 2 + 18], fill=(230, 230, 230))
        d.text((gw / 2, gh / 2 + 70), f"{round(p * 100)}%", font=font("consolab.ttf", 30), fill=(180, 180, 180), anchor="mm")
        cur = (gw * 0.7, gh * 0.75)
        stage = "load"
    elif lt < 0.42:  # главное меню
        im.alpha_composite(WORLDS["menu_bg"])
        d.text((gw / 2 + 4, 104), "MINECRAFT", font=font("impact.ttf", 92), fill=(40, 40, 40), anchor="mm")
        d.text((gw / 2, 100), "MINECRAFT", font=font("impact.ttf", 92), fill=(220, 220, 220), anchor="mm")
        k = prog(lt, 0.22, 0.40)
        cur = path([(0, gw * 0.7, gh * 0.75), (0.7, bx + bw * 0.6, 390), (1, bx + bw * 0.6, 390)], k)
        for i, lab in enumerate(["Одиночная игра", "Сетевая игра", "Настройки…", "Выйти из игры"]):
            mc_button(d, bx, 190 + i * 76, bw, lab, hover=(i == 2 and k > 0.7))
        click = k > 0.85
    elif lt < 0.80:  # настройки графики, крутим FOV
        im.alpha_composite(WORLDS["menu_bg"].point(lambda v: v // 3))
        d.text((gw / 2, 60), "Настройки графики", font=font("consolab.ttf", 36), fill=(255, 255, 255), anchor="mm")
        k = prog(lt, 0.42, 0.78)
        fov = 70 + 40 * math.sin(k * math.pi * 1.5) ** 2 if k > 0.15 else 70
        fov = round(fov)
        for i, lab in enumerate(["Дальность: 8", f"Поле зрения: {fov}", "Графика: Fancy", "Яркость: 50%"]):
            y = 130 + i * 82
            mc_button(d, bx, y, bw, lab, hover=(i == 1))
            if i == 1:
                kx = bx + 8 + (bw - 30) * (fov - 30) / 80
                d.rectangle([kx, y, kx + 16, y + 64], fill=(200, 200, 200), outline=(0, 0, 0), width=2)
                cur = (kx + 8, y + 34)
        mc_button(d, bx, 470, bw, "Готово", hover=k > 0.92)
        click = 0.15 < k < 0.9
        if k > 0.92:
            cur = (bx + bw / 2, 500)
            click = True
    else:  # выход
        im.alpha_composite(WORLDS["menu_bg"])
        k = prog(lt, 0.80, 1.0)
        for i, lab in enumerate(["Одиночная игра", "Сетевая игра", "Настройки…", "Выйти из игры"]):
            mc_button(d, bx, 190 + i * 76, bw, lab, hover=(i == 3 and k > 0.4))
        cur = path([(0, bx + bw * 0.6, 400), (0.4, bx + bw * 0.5, 460), (1, bx + bw * 0.5, 460)], k)
        click = k > 0.55
        if k > 0.75:  # окно «схлопывается»
            a = round(255 * prog(k, 0.75, 1.0))
            im.alpha_composite(Image.new("RGBA", im.size, (0, 0, 0, a)))
        stage = "exit"
    # нервный курсор
    j = 7
    cur = (cur[0] + math.sin(lt * period * 53) * j, cur[1] + math.cos(lt * period * 47) * j)
    return im, cur, click, stage


def scene2(t):
    f = phone_bg(t)
    lt0 = t - S2
    first = 2.7  # первый цикл (уже 1.5x), дальше — повторы ещё быстрее
    if lt0 < first:
        lt, period, cyc = lt0 / first, first, 0
    else:
        rep = 1.2
        lt, period, cyc = ((lt0 - first) % rep) / rep, rep, 1 + int((lt0 - first) // rep)
    im, cur, click, stage = game_screen(lt, period)
    # окно игры с тонкой рамкой-заголовком
    d = ImageDraw.Draw(f)
    gx0, gy0, gx1, gy1 = GAME
    d.rounded_rectangle([gx0 - 6, gy0 - 56, gx1 + 6, gy1 + 6], 16, fill=(30, 30, 34))
    d.text((gx0 + 16, gy0 - 44), "Minecraft 1.21.1", font=ui(28), fill=(200, 200, 200))
    for i, c in enumerate([(255, 95, 86), (255, 189, 46), (39, 201, 63)]):
        d.ellipse([gx1 - 30 - i * 40, gy0 - 40, gx1 - 6 - i * 40, gy0 - 16], fill=c)
    f.alpha_composite(im, (gx0, gy0))
    cursor(f, (gx0 + cur[0], gy0 + cur[1]), click)
    # счётчик потраченного времени
    spent = 12 + (t - S2) * 9
    clock = f"⏱ потрачено {int(spent // 60)}:{int(spent % 60):02d}"
    draw_rich(d, (W / 2, 1380), clock, 46, fill=(255, 200, 200), bold=True, anchor="ma")
    if cyc:
        draw_rich(d, (W / 2, 1460), f"🔁 и снова… ×{cyc + 1}", 40, fill=MUTED, bold=True, anchor="ma")
    # красная виньетка нарастает
    red = clamp((t - S2) / 6) * 70
    if red > 1:
        v = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        ImageDraw.Draw(v).rectangle([0, 0, W, H], outline=(200, 20, 20, round(red)), width=60)
        f.alpha_composite(v.filter(ImageFilter.GaussianBlur(40)))
    # титры по одному
    T = [S2 + 0.10, S2 + 0.70, S2 + 1.30, S2 + 1.95]
    labs = ["❌ Запустил игру", "❌ Зашёл в настройки", "❌ Покрутил FOV", "❌ ВЫШЕЛ ИЗ ИГРЫ"]
    for i, (ti, lab) in enumerate(zip(T, labs)):
        last = i == 3
        t_out = None if last else T[i + 1] - 0.06
        paste_title(f, lab, t, ti, 400, 100 if last else 90, (255, 90, 90) if last else (255, 255, 255),
                    t_out=t_out, shake=6 if (last and t < ti + 0.5) else 0)
    return f


# ---------------- Сцена 3: решение ----------------
def scene3(t):
    f = phone_bg(t)
    lt = t - S3
    rdk = smooth(prog(lt, 1.4, 3.6))
    rd = lerp(8, 16, rdk)
    dragging = 1.2 < lt < 3.8
    st = dict(BASE, rd=rd, preview=prog(lt, 0.0, 0.5), rd_active=dragging)
    img, pos = launcher(st)
    # прокрутка к слайдеру и превью
    scroll = 330
    view = img.crop((0, scroll, CW, scroll + 1160))
    s = lerp(0.94, 1.0, out_cubic(prog(lt, 0, 0.5)))
    to = place(f, view, s, W / 2, 660)
    kn = to((pos["rd_knob"][0], pos["rd_knob"][1] - scroll))
    start = to((pos["rd_x"][0] + pos["rd_x"][1] * (8 - 2) / 30, pos["rd_knob"][1] - scroll))
    if lt < 1.2:
        c = path([(0, W * 0.75, 1750), (1.1, start[0] + 4, start[1] + 6)], lt)
    else:
        c = (kn[0] + 4, kn[1] + 6)
    cursor(f, c, dragging)
    T =[S3 + 0.25, S3 + 1.90, S3 + 5.10]
    labs = ["✅ Настройки — в лаунчере", "✅ Слайдер → превью сразу", "✅ Без запуска игры"]
    d = ImageDraw.Draw(f)
    for i, (ti, lab) in enumerate(zip(T, labs)):
        if t < ti:
            continue
        img_t = title_cached(lab, 70, (255, 255, 255))
        k = out_back(prog(t, ti, ti + 0.3), 2.2)
        sc = lerp(0.5, 1, k)
        im = img_t.resize((max(1, round(img_t.width * sc)), max(1, round(img_t.height * sc))), Image.BILINEAR)
        f.alpha_composite(im, (round(W / 2 - im.width / 2), round(270 + i * 120 - im.height / 2)))
    return f


# ---------------- Сцена 4: умный пресет ----------------
def scene4(t):
    f = phone_bg(t)
    lt = t - S4
    t_click1, t_click2 = 0.75, 2.75
    cards = prog(lt, t_click1 + 0.05, t_click1 + 0.85)
    apply = smooth(prog(lt, t_click2 + 0.05, t_click2 + 0.9))
    tgt = PRESETS[1][3]
    st = dict(gfx=tgt["gfx"] if apply > 0.3 else BASE["gfx"], rd=lerp(8, tgt["rd"], apply),
              fov=lerp(70, tgt["fov"], apply), fps=lerp(120, tgt["fps"], apply),
              cards=cards, sel=1 if lt > t_click2 else -1,
              card_hover=1 if lt > t_click2 - 0.5 else -1,
              auto_hover=clamp(prog(lt, 0.3, 0.7) - prog(lt, 1.2, 1.5)),
              auto_press=1 if t_click1 < lt < t_click1 + 0.15 else 0,
              flash=(1 - prog(lt, t_click2 + 0.9, t_click2 + 2.0)) if lt > t_click2 + 0.1 else 0)
    img, pos = launcher(st)
    view = img.crop((0, 0, CW, 1180))
    to = place(f, view, 1.0, W / 2, 640)
    a = to(pos["auto"])
    c1 = to(pos.get("card1", (CW / 2, 420)))
    c = path([(0, W * 0.8, 1750), (0.6, a[0], a[1]), (1.6, a[0], a[1]), (2.5, c1[0] + 20, c1[1] + 10)], lt)
    cursor(f, c, abs(lt - t_click1) < 0.12 or abs(lt - t_click2) < 0.12)
    ripple(f, a, lt, t_click1)
    ripple(f, (c1[0] + 20, c1[1] + 10), lt, t_click2)
    # тост «Применено»
    if lt > t_click2 + 0.3:
        k = out_back(prog(lt, t_click2 + 0.3, t_click2 + 0.6))
        d = ImageDraw.Draw(f)
        ty = lerp(1900, 1690, k)
        d.rounded_rectangle([W / 2 - 330, ty, W / 2 + 330, ty + 96], 28, fill=ACCENT)
        draw_rich(d, (W / 2, ty + 22), "✨ Пресет применён", 40, fill=(6, 30, 14), bold=True, anchor="ma")
    paste_title(f, "НЕ ЗНАЕШЬ ЧТО СТАВИТЬ?", t, S4 + 0.1, 330, 92)
    paste_title(f, "→ АВТО-ПРЕСЕТ", t, S4 + 1.9, 460, 104, (120, 255, 150))
    return f


# ---------------- Сцена 5: результат ----------------
def scene5(t):
    lt = t - S5
    t_click = 0.55
    if lt < t_click + 0.35:
        f = phone_bg(t)
        st = dict(BASE, gfx=1, rd=12, fov=80, fps=144,
                  play_hover=prog(lt, 0.2, 0.45), play_press=1 if abs(lt - t_click) < 0.1 else 0)
        img, pos = launcher(st)
        view = img.crop((0, img.height - 1180, CW, img.height))
        zoom = 1 + 0.5 * smooth(prog(lt, t_click + 0.05, t_click + 0.35))
        p = (pos["play"][0], pos["play"][1] - (img.height - 1180))
        top = 640 - (p[1] * zoom - p[1]) * 1.0
        to = place(f, view, zoom, W / 2, top)
        pp = to(p)
        c = path([(0, W * 0.8, 1750), (0.45, pp[0] + 10, pp[1] + 10)], lt)
        cursor(f, c, abs(lt - t_click) < 0.1)
        ripple(f, pp, lt, t_click)
        wf = prog(lt, t_click + 0.15, t_click + 0.35)
        if wf > 0:
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(255 * wf))))
    else:
        gl = lt - (t_click + 0.35)
        f = world_view("game", W, H, 12, pan=gl * 420, clouds_t=gl).convert("RGBA")
        wf = 1 - prog(gl, 0, 0.3)
        if wf > 0:
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(255 * wf))))
        d = ImageDraw.Draw(f)
        fps = 144 + round(math.sin(gl * 7) * 2)
        d.rounded_rectangle([36, 1560, 470, 1730], 22, fill=(11, 18, 32, 190))
        d.text((62, 1578), f"{fps} FPS", font=font("consolab.ttf", 46), fill=(120, 255, 150))
        d.text((62, 1640), "Fancy · 12 чанков · FOV 80", font=ui(30), fill=TEXT)
        img_f = asset("ui/frog_cool.png", 1.6)
        f.alpha_composite(img_f, (W - img_f.width - 40, 1580 + round(math.sin(gl * 5) * 6)))
    paste_title(f, "ЗАПУСТИЛ —\nВСЁ УЖЕ НАСТРОЕНО", t, S5 + 0.1, 400, 100)
    return f


# ---------------- Сцена 6: CTA ----------------
def scene6(t):
    lt = t - S6
    f = Image.new("RGBA", (W, H), (22, 163, 74, 255))
    d = ImageDraw.Draw(f)
    for i in range(14):  # лучи
        ang = i / 14 * math.tau + lt * 0.4
        x1, y1 = W / 2 + math.cos(ang) * 1600, 760 + math.sin(ang) * 1600
        x2, y2 = W / 2 + math.cos(ang + 0.12) * 1600, 760 + math.sin(ang + 0.12) * 1600
        d.polygon([(W / 2, 760), (x1, y1), (x2, y2)], fill=(34, 197, 94, 255))
    lg = asset("main/logo.png", 2.6)
    k = out_back(prog(lt, 0, 0.45), 2.2)
    s = lerp(0.3, 1, k) * (1 + 0.02 * math.sin(lt * 6))
    lgs = lg.resize((max(1, round(lg.width * s)), max(1, round(lg.height * s))), Image.LANCZOS)
    f.alpha_composite(lgs, (round(W / 2 - lgs.width / 2), round(760 - lgs.height / 2)))
    paste_title(f, "🐸 FrogLauncher", t, S6 + 0.25, 1220, 104)
    paste_title(f, "БЕСПЛАТНО", t, S6 + 0.45, 1350, 120, (255, 236, 90))
    paste_title(f, "👇 Ссылка в профиле", t, S6 + 0.95, 1490, 80)
    if lt > 0.3:
        d.text((W / 2, 1600), "@froglauncher", font=uib(56), fill=(6, 40, 18), anchor="ma")
    if lt > 1.0:
        bob = abs(math.sin(lt * 6)) * 40
        d.text((W / 2 - 60, 1680 + bob), "👇", font=emo(110), embedded_color=True)
    return f


def frame_at(t):
    if t < S2:
        f = scene1(t)
    elif t < S3:
        f = scene2(t)
    elif t < S4:
        f = scene3(t)
    elif t < S5:
        f = scene4(t)
    elif t < S6:
        f = scene5(t)
    else:
        f = scene6(t)
    # короткая белая вспышка-склейка на стыках
    for b in (S2, S3, S4, S6):
        if 0 <= t - b < 0.12:
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(170 * (1 - (t - b) / 0.12)))))
    return f.convert("RGB")


def init():
    WORLDS["preview"] = make_layers(CW - 64, 360, seed=11, base_block=26)
    WORLDS["game"] = make_layers(W * 4, H, seed=5, base_block=72)
    WORLDS[("game", "sky")] = sky(W, H).convert("RGBA")
    WORLDS[("preview", "sky")] = sky(CW - 64, 360).convert("RGBA")


def main():
    args = sys.argv[1:]
    init()
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
