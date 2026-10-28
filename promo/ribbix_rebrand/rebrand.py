"""Ролик «FrogLauncher теперь Ribbix Launcher» + «Доктор крашей» — TikTok 9:16, 1080x1920 @30fps.

Кадры интерфейса — настоящие скриншоты из shots/ (1–3: главная, тарифы, профиль; 4: Доктор крашей),
по ним ездит «камера». Логотипы: shots/logo_frog_old.png (из git, df73043) и shots/logo_ribbix.png.
Длина сцен считается от озвучки audio/vt_N.wav (ускорение TEMPO).
Запуск:  python rebrand.py [out.mp4] [--only 2,8,16,25,34]
"""
import math
import random
import subprocess
import sys
import wave
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE.parent / "scenario3"))
import render as R  # noqa: E402
from render import (ACCENT, DANGER, INPUT, LINE, MUTED, PANEL, TEXT, clamp, draw_rich, emo, lerp, mix,  # noqa: E402
                    out_back, out_cubic, paste_title, phone_bg, prog, smooth, ui, uib)

W, H, FPS = 1080, 1920, 30
TEMPO = 1.12
LEAD, GAP = 0.15, 0.25


def wav_len(p):
    with wave.open(str(p)) as w:
        return w.getnframes() / w.getframerate()


VO = [wav_len(HERE / "audio" / f"vt_{i}.wav") / TEMPO for i in range(1, 6)]
STARTS = []
_t = 0.0
for v in VO:
    STARTS.append(_t)
    _t += LEAD + v + GAP
DUR = _t + 0.6
VO_AT = [s + LEAD for s in STARTS]
S1, S2, S3, S4, S5 = STARTS

SHOTS = {k: Image.open(HERE / "shots" / f).convert("RGBA")
         for k, f in [("home", "1.webp"), ("tariffs", "2.webp"), ("profile", "3.png"), ("doctor", "4.webp")]}
LOGO_OLD = Image.open(HERE / "shots" / "logo_frog_old.png").convert("RGBA")
LOGO_NEW = Image.open(HERE / "shots" / "logo_ribbix.png").convert("RGBA")
VIOLET = (139, 92, 246)
GOLD = (245, 190, 40)


def keyed(keys, t):
    """Интерполяция кадров камеры: keys=[(t, cx, cy, s), ...] (центр в пикселях скриншота, s — увеличение)."""
    if t <= keys[0][0]:
        return keys[0][1:]
    for a, b in zip(keys, keys[1:]):
        if t <= b[0]:
            k = smooth(prog(t, a[0], b[0]))
            return tuple(lerp(x, y, k) for x, y in zip(a[1:], b[1:]))
    return keys[-1][1:]


def camera(img, cx, cy, s, vw, vh, bg=(8, 12, 22)):
    """Вырезает из скриншота окно vw×vh с центром (cx, cy) и увеличением s."""
    sw, sh = vw / s, vh / s
    x0, y0 = cx - sw / 2, cy - sh / 2
    out = Image.new("RGBA", (vw, vh), bg + (255,))
    ix0, iy0 = max(0, x0), max(0, y0)
    ix1, iy1 = min(img.width, x0 + sw), min(img.height, y0 + sh)
    if ix1 > ix0 and iy1 > iy0:
        part = img.crop((round(ix0), round(iy0), round(ix1), round(iy1)))
        pw, ph = round((ix1 - ix0) * s), round((iy1 - iy0) * s)
        out.paste(part.resize((max(1, pw), max(1, ph)), Image.LANCZOS), (round((ix0 - x0) * s), round((iy0 - y0) * s)))
    return out


def device(f, view, top, glow=ACCENT):
    """Скриншот в «окне» со скруглением, тенью и неоновой рамкой."""
    w, h = view.size
    x0 = (W - w) // 2
    sd = Image.new("RGBA", (w + 120, h + 120), (0, 0, 0, 0))
    ImageDraw.Draw(sd).rounded_rectangle([60, 70, w + 60, h + 70], 34, fill=(0, 0, 0, 170))
    f.alpha_composite(sd.filter(ImageFilter.GaussianBlur(28)), (x0 - 60, top - 60))
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], 30, fill=255)
    f.paste(view, (x0, top), mask)
    ImageDraw.Draw(f).rounded_rectangle([x0 - 3, top - 3, x0 + w + 2, top + h + 2], 33, outline=glow, width=5)
    return x0


def highlight(f, box, t, t0, t1, col=GOLD):
    """Пульсирующая рамка вокруг области (координаты кадра)."""
    if not (t0 <= t < t1):
        return
    a = smooth(prog(t, t0, t0 + 0.25)) * (1 - smooth(prog(t, t1 - 0.25, t1)))
    p = 0.5 + 0.5 * math.sin(t * 8)
    x0, y0, x1, y1 = box
    pad = 6 + 6 * p
    ov = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(ov).rounded_rectangle([x0 - pad, y0 - pad, x1 + pad, y1 + pad], 18,
                                         outline=col + (round(255 * a),), width=6)
    f.alpha_composite(ov)


def caption(f, text, t, t0, t1, y, col=(255, 255, 255), size=46):
    """Плашка-подпись под скриншотом."""
    if not (t0 <= t < t1 + 0.25):
        return
    k = out_back(prog(t, t0, t0 + 0.3), 1.6) * (1 - smooth(prog(t, t1, t1 + 0.25)))
    if k <= 0:
        return
    d = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
    tw = d.textlength(text, font=uib(size)) + 80
    im = Image.new("RGBA", (round(tw), size + 44), (0, 0, 0, 0))
    idr = ImageDraw.Draw(im)
    idr.rounded_rectangle([0, 0, im.width - 1, im.height - 1], (size + 44) // 2, fill=(11, 18, 32, 235),
                          outline=ACCENT, width=3)
    draw_rich(idr, (im.width / 2, 18), text, size, fill=col, bold=True, anchor="ma")
    s = lerp(0.7, 1, k)
    ims = im.resize((max(1, round(im.width * s)), max(1, round(im.height * s))), Image.BILINEAR)
    ims.putalpha(ims.getchannel("A").point(lambda v: round(v * clamp(k))))
    f.alpha_composite(ims, (round(W / 2 - ims.width / 2), round(y)))


# ====================================================================
# 1. Крючок: FrogLauncher → Ribbix Launcher
# ====================================================================
def pixelate(img, k):
    """k=0 — как есть, k=1 — крупные «пиксели»."""
    if k <= 0.02:
        return img
    n = max(4, round(lerp(img.width, 10, k)))
    return img.resize((n, max(1, round(n * img.height / img.width))), Image.BILINEAR).resize(img.size, Image.NEAREST)


def scene1(t):
    lt = t - S1
    v = VO[0]
    f = phone_bg(t)
    t_swap = LEAD + 0.5 * v
    big = 2.9
    if lt < t_swap:
        lg = LOGO_OLD.resize((round(LOGO_OLD.width * big), round(LOGO_OLD.height * big)), Image.LANCZOS)
        k = prog(lt, t_swap - 0.5, t_swap)
        lg = pixelate(lg, k)
        s = lerp(0.85, 1.0, out_cubic(prog(lt, 0, 0.5)))
    else:
        lg = LOGO_NEW.resize((round(LOGO_NEW.width * big), round(LOGO_NEW.height * big)), Image.LANCZOS)
        k = 1 - prog(lt, t_swap, t_swap + 0.45)
        lg = pixelate(lg, k)
        s = lerp(1.15, 1.0, out_back(prog(lt, t_swap, t_swap + 0.45)))
    lgs = lg.resize((round(lg.width * s), round(lg.height * s)), Image.BILINEAR)
    # вспышка и искры на смене
    if abs(lt - t_swap) < 0.35:
        a = 1 - abs(lt - t_swap) / 0.35
        f.alpha_composite(Image.new("RGBA", (W, H), (180, 255, 200, round(160 * a))))
    f.alpha_composite(lgs, (round(W / 2 - lgs.width / 2), round(1010 - lgs.height / 2)))
    if lt > t_swap:
        rnd = random.Random(4)
        d = ImageDraw.Draw(f)
        for i in range(26):
            ang = rnd.uniform(0, math.tau)
            sp = rnd.uniform(300, 900)
            p = prog(lt, t_swap, t_swap + 1.1)
            if 0 < p < 1:
                r = sp * out_cubic(p)
                x, y = W / 2 + math.cos(ang) * r, 1010 + math.sin(ang) * r
                sz = 14 * (1 - p)
                d.rectangle([x - sz, y - sz, x + sz, y + sz], fill=rnd.choice([ACCENT, VIOLET, GOLD, (6, 182, 212)]))
    if lt < t_swap:
        paste_title(f, "FROGLAUNCHER", t, S1 + 0.1, 380, 120)
        if lt > t_swap - 0.6:  # зачёркиваем старое имя
            p = prog(lt, t_swap - 0.6, t_swap - 0.2)
            d = ImageDraw.Draw(f)
            d.line([(170, 395), (170 + 740 * p, 375)], fill=DANGER, width=16)
    else:
        paste_title(f, "ТЕПЕРЬ МЫ —\nRIBBIX LAUNCHER", t, S1 + t_swap + 0.1, 400, 112, (190, 255, 120))
        if lt > t_swap + 0.6:
            d = ImageDraw.Draw(f)
            d.text((W / 2, 1520), "бывший FrogLauncher", font=uib(48), fill=MUTED, anchor="ma")
    return f


# ====================================================================
# 2. Новый интерфейс: главная → тарифы → профиль
# ====================================================================
VW, VH, VTOP = 1040, 980, 640


def scene2(t):
    lt = t - S2
    v = VO[1]
    D = S3 - S2
    f = phone_bg(t)
    t_tar, t_prof = LEAD + 0.52 * v, LEAD + 0.72 * v
    if lt < t_tar:
        img = SHOTS["home"]
        keys = [(0, 588, 369, 0.95), (0.6, 588, 369, 0.95), (LEAD + 0.18 * v, 70, 380, 2.6),
                (LEAD + 0.34 * v, 70, 380, 2.6), (LEAD + 0.42 * v, 300, 380, 1.9), (t_tar, 520, 640, 1.6)]
        view = camera(img, *keyed(keys, lt), VW, VH)
        device(f, view, VTOP)
        caption(f, "Пиксельное меню", t, S2 + LEAD + 0.2 * v, S2 + LEAD + 0.36 * v, VTOP + VH + 40)
        caption(f, "Обновлённая главная", t, S2 + LEAD + 0.38 * v, S2 + t_tar - 0.1, VTOP + VH + 40)
    elif lt < t_prof:
        img = SHOTS["tariffs"]
        keys = [(t_tar, 300, 300, 1.25), (t_prof, 760, 480, 1.25)]
        view = camera(img, *keyed(keys, lt), VW, VH)
        device(f, view, VTOP + round((1 - out_cubic(prog(lt, t_tar, t_tar + 0.35))) * 120))
        caption(f, "Тарифы Ribbix", t, S2 + t_tar + 0.1, S2 + t_prof - 0.1, VTOP + VH + 40)
    else:
        img = SHOTS["profile"]
        keys = [(t_prof, 600, 300, 1.25), (t_prof + 1.0, 600, 80, 2.0), (D, 560, 80, 2.0)]
        view = camera(img, *keyed(keys, lt), VW, VH)
        device(f, view, VTOP + round((1 - out_cubic(prog(lt, t_prof, t_prof + 0.35))) * 120))
        caption(f, "🎁 Профиль · «Мой год»", t, S2 + t_prof + 0.1, S2 + LEAD + 0.86 * v - 0.25, VTOP + VH + 40)
    paste_title(f, "НОВЫЙ\nИНТЕРФЕЙС", t, S2 + 0.1, 380, 120)
    if lt > LEAD + 0.86 * v:
        k = out_back(prog(lt, LEAD + 0.86 * v, LEAD + 0.86 * v + 0.3))
        im = R.title_cached("ИГРА — БЕСПЛАТНО", 92, (255, 236, 90))
        ims = im.resize((max(1, round(im.width * k)), max(1, round(im.height * k))), Image.BILINEAR)
        f.alpha_composite(ims, (round(W / 2 - ims.width / 2), 1760 - ims.height // 2))
    return f


# ====================================================================
# 3. Доктор крашей (скриншот 4)
# ====================================================================
PANEL = (1440, 118, 1908, 950)   # панель доктора на скриншоте 4


def scene3(t):
    lt = t - S3
    v = VO[2]
    f = phone_bg(t)
    img = SHOTS["doctor"]
    vw, vh, top = 1040, 1180, 560
    t_zoom = LEAD + 0.18 * v
    keys = [(0, 1380, 540, 0.8), (0.5, 1420, 520, 0.85), (t_zoom, 1674, 300, 2.15),
            (LEAD + 0.55 * v, 1674, 300, 2.15), (LEAD + 0.75 * v, 1674, 420, 2.15), (S4 - S3, 1674, 440, 2.15)]
    cx, cy, s = keyed(keys, lt)
    view = camera(img, cx, cy, s, vw, vh)
    x0 = device(f, view, top, DANGER)

    def scr(bx0, by0, bx1, by1):  # координаты скриншота → кадр
        return (x0 + (bx0 - cx) * s + vw / 2, top + (by0 - cy) * s + vh / 2,
                x0 + (bx1 - cx) * s + vw / 2, top + (by1 - cy) * s + vh / 2)
    highlight(f, scr(1460, 182, 1890, 296), t, S3 + LEAD + 0.42 * v, S3 + LEAD + 0.72 * v, ACCENT)
    highlight(f, scr(1460, 318, 1890, 444), t, S3 + LEAD + 0.74 * v, S4 - 0.1, GOLD)
    paste_title(f, "🩺 ДОКТОР\nКРАШЕЙ", t, S3 + 0.1, 330, 116, (255, 120, 120))
    caption(f, "Объясняет, что сломалось", t, S3 + LEAD + 0.42 * v, S3 + LEAD + 0.72 * v, top + vh + 30)
    caption(f, "и чинит одной кнопкой", t, S3 + LEAD + 0.74 * v, S4 - 0.1, top + vh + 30)
    return f


# ====================================================================
# 4. Поиск мода-виновника — половинное деление 100 → 1
# ====================================================================
CULPRIT = 63
STEPS = [(0, 100), (50, 100), (50, 75), (62, 75), (62, 68), (62, 65), (63, 65), (63, 64)]  # [lo, hi) подозреваемые


def scene4(t):
    lt = t - S4
    v = VO[3]
    D = S5 - S4
    f = phone_bg(t)
    d = ImageDraw.Draw(f)
    t_go, t_done = LEAD + 0.12 * v, LEAD + 0.7 * v
    step_len = (t_done - t_go) / (len(STEPS) - 1)
    si = 0 if lt < t_go else min(len(STEPS) - 1, int((lt - t_go) / step_len) + 1)
    lo, hi = STEPS[si]
    plo, phi = STEPS[max(0, si - 1)]
    # карточка с модами 10×10
    cx0, cy0, cell = 120, 600, 84
    d.rounded_rectangle([cx0 - 30, cy0 - 120, cx0 + 10 * cell + 20, cy0 + 10 * cell + 30], 36,
                        fill=(17, 26, 46, 245), outline=GOLD, width=4)
    draw_rich(d, (cx0, cy0 - 96), "🔎 Ищем мод-виновника", 40, bold=True)
    k_step = smooth(prog(lt, t_go + (si - 1) * step_len, t_go + (si - 1) * step_len + 0.25)) if si else 1
    for i in range(100):
        r, c = divmod(i, 10)
        x, y = cx0 + c * cell, cy0 + r * cell
        if si == len(STEPS) - 1 and i == CULPRIT:
            col = DANGER
        elif lo <= i < hi:
            col = mix(mix(LINE, ACCENT, 0.15), ACCENT, k_step) if not (plo <= i < phi) else ACCENT
            # в шаге проверяем первую половину подозреваемых
            mid = (lo + hi) // 2
            if si < len(STEPS) - 1 and lo <= i < mid and lt > t_go:
                col = GOLD
        else:
            col = (40, 46, 62)
        d.rounded_rectangle([x + 6, y + 6, x + cell - 6, y + cell - 6], 12, fill=col)
        if si == len(STEPS) - 1 and i == CULPRIT:
            p = 0.5 + 0.5 * math.sin(lt * 9)
            d.rounded_rectangle([x - 4 - 4 * p, y - 4 - 4 * p, x + cell + 4 + 4 * p, y + cell + 4 + 4 * p], 18,
                                outline=(255, 160, 160), width=6)
    # счётчик
    n = hi - lo
    if lt < t_done:
        launch = max(1, si)
        d.text((W / 2, cy0 + 10 * cell + 70), f"Запуск {launch} из ~7 · подозреваемых: {n}",
               font=uib(44), fill=TEXT, anchor="ma")
        d.text((W / 2, cy0 + 10 * cell + 130), "игра сама запускается и закрывается", font=ui(34), fill=MUTED,
               anchor="ma")
    else:
        k = out_back(prog(lt, t_done, t_done + 0.35))
        box = Image.new("RGBA", (960, 230), (0, 0, 0, 0))
        bd = ImageDraw.Draw(box)
        bd.rounded_rectangle([0, 0, 959, 229], 34, fill=(60, 14, 14, 250), outline=DANGER, width=5)
        draw_rich(bd, (40, 28), "🎯 Виновник: fancy-furniture.jar", 44, bold=True)
        bd.text((40, 104), "Выключить его или обновить — одной кнопкой", font=ui(34), fill=(255, 210, 210))
        bd.text((40, 156), "Найдено за 7 запусков из 100 модов", font=ui(30), fill=(255, 170, 170))
        s = lerp(0.6, 1, k)
        bs = box.resize((max(1, round(box.width * s)), max(1, round(box.height * s))), Image.BILINEAR)
        f.alpha_composite(bs, (round(W / 2 - bs.width / 2), cy0 + 10 * cell + 60))
        t_ret = LEAD + 0.86 * v
        if lt > t_ret:
            kk = out_back(prog(lt, t_ret, t_ret + 0.35))
            tw = 820
            y = lerp(H + 20, 1760, kk)
            d.rounded_rectangle([W / 2 - tw / 2, y, W / 2 + tw / 2, y + 100], 30, fill=ACCENT)
            draw_rich(d, (W / 2, y + 26), "↩️ Моды вернулись как были", 40, fill=(6, 30, 14), bold=True, anchor="ma")
    paste_title(f, "САМ НАХОДИТ\nМОД-ВИНОВНИКА", t, S4 + 0.1, 290, 106, (255, 226, 140))
    return f


# ====================================================================
# 5. CTA
# ====================================================================
def scene5(t):
    lt = t - S5
    f = Image.new("RGBA", (W, H), (22, 163, 74, 255))
    d = ImageDraw.Draw(f)
    for i in range(14):
        ang = i / 14 * math.tau + lt * 0.4
        d.polygon([(W / 2, 760), (W / 2 + math.cos(ang) * 1600, 760 + math.sin(ang) * 1600),
                   (W / 2 + math.cos(ang + 0.12) * 1600, 760 + math.sin(ang + 0.12) * 1600)], fill=(34, 197, 94))
    k = out_back(prog(lt, 0, 0.45), 2.0)
    lg = LOGO_NEW.resize((round(LOGO_NEW.width * 2.6), round(LOGO_NEW.height * 2.6)), Image.LANCZOS)
    s = lerp(0.3, 1, k) * (1 + 0.02 * math.sin(lt * 6))
    lgs = lg.resize((max(1, round(lg.width * s)), max(1, round(lg.height * s))), Image.LANCZOS)
    f.alpha_composite(lgs, (round(W / 2 - lgs.width / 2), round(760 - lgs.height / 2)))
    paste_title(f, "RIBBIX LAUNCHER", t, S5 + 0.25, 1230, 104)
    paste_title(f, "БЕСПЛАТНО", t, S5 + 0.45, 1360, 116, (255, 236, 90))
    paste_title(f, "👇 Ссылка в профиле", t, S5 + 0.8, 1500, 78)
    if lt > 1.0:
        d.text((W / 2 - 55, 1610 + abs(math.sin(lt * 6)) * 40), "👇", font=emo(110), embedded_color=True)
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
    else:
        f = scene5(t)
    for b in (S2, S3, S4, S5):
        if 0 <= t - b < 0.12:
            f.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, round(160 * (1 - (t - b) / 0.12)))))
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
    print("done", out, f"{DUR:.2f}s")


if __name__ == "__main__":
    main()
