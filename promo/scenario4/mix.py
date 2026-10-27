"""Звук для сценария 4: мягкий эмбиент 75 BPM (20%), сезонные SFX, озвучка (audio/vt_N.wav).

Каждая реплика ускоряется ровно под свой слот, к голосу добавлено лёгкое эхо.
Запуск: python mix.py  ->  out/audio_mix.wav
"""
import re
import subprocess
import wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).parent
SR = 48000
src = (HERE / "render.py").read_text(encoding="utf-8")
T_SPRING, T_SUMMER, T_AUTUMN, T_WINTER, T_NY, T_CTA = map(
    float, re.search(r"^T_SPRING, .* = (.*)$", src, re.M).group(1).split(","))
DUR = float(re.search(r"^W, H, FPS, DUR = .*, ([\d.]+)$", src, re.M).group(1))
N = int(SR * DUR)
rng = np.random.default_rng(4)

VO_AT = [0.25, T_SPRING + 0.1, T_SUMMER + 0.1, T_AUTUMN + 0.1, T_WINTER + 0.1, T_CTA + 0.1]
MUSIC_GAIN = 0.20


def tt(sec):
    return np.arange(int(SR * sec)) / SR


def env(n, a=0.002, r=0.1):
    t = np.arange(n) / SR
    return np.minimum(1, t / max(a, 1e-4)) * np.exp(-t / r)


def put(buf, sig, at, gain=1.0):
    i = int(at * SR)
    j = min(len(buf), i + len(sig))
    if j > i:
        buf[i:j] += sig[: j - i] * gain


def lowpass(x, cutoff):
    a = np.exp(-2 * np.pi * cutoff / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i, v in enumerate(x):
        acc = (1 - a) * v + a * acc
        y[i] = acc
    return y


def read_wav(p):
    with wave.open(str(p)) as w:
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
        return x.reshape(-1, w.getnchannels()).mean(axis=1)


def noise(sec):
    return rng.standard_normal(int(SR * sec)).astype(np.float32)


# ---------------- музыка ----------------
def note(f):
    return 440.0 * 2 ** ((f - 69) / 12)


def pad(midis, sec):
    t = tt(sec)
    x = sum(np.sin(2 * np.pi * note(m) * t) + 0.25 * np.sin(2 * np.pi * note(m) * 2.003 * t) for m in midis)
    shape = np.minimum(1, t / 0.9) * np.minimum(1, (sec - t) / 0.9)
    return x / len(midis) * shape * 0.5


def musicbox(m, sec=1.6):
    t = tt(sec)
    f = note(m)
    x = np.sin(2 * np.pi * f * t) + 0.35 * np.sin(2 * np.pi * f * 3 * t) * np.exp(-t * 6)
    return x * env(len(t), 0.002, 0.45) * 0.35


def music():
    m = np.zeros(N, dtype=np.float32)
    b = 60 / 75
    chords = [[53, 57, 60, 64], [52, 55, 59, 62], [50, 53, 57, 60], [48, 52, 55, 59]]  # Fmaj7 Em7 Dm7 Cmaj7
    arps = [[72, 76, 79, 81], [71, 74, 79, 76], [69, 72, 77, 74], [67, 71, 76, 72]]
    t, bar = 0.0, 0
    while t < DUR:
        c = chords[bar % 4]
        put(m, pad(c, b * 4 + 0.9), t, 0.9)
        for k, nn in enumerate(arps[bar % 4]):
            put(m, musicbox(nn), t + k * b, 0.8)
            if k % 2:
                put(m, musicbox(nn + 12, 0.8), t + k * b + b / 2, 0.3)
        # мягкая «бочка»-удар на каждую долю, очень тихо
        for k in range(4):
            kt = tt(0.25)
            kick = np.sin(2 * np.pi * np.cumsum(50 + 60 * np.exp(-kt * 30)) / SR) * env(len(kt), 0.002, 0.08)
            put(m, kick, t + k * b, 0.25)
        t += b * 4
        bar += 1
    vinyl = lowpass(noise(DUR), 3000) * 0.012
    m[: len(vinyl)] += vinyl
    fade = np.ones(N, dtype=np.float32)
    nfo = int(SR * 0.8)
    fade[-nfo:] = np.linspace(1, 0, nfo)
    m *= fade
    return m / (np.abs(m).max() + 1e-9) * 0.9


# ---------------- SFX ----------------
def rustle(sec, density=60, bright=4000):
    """Шорох: короткие зёрна отфильтрованного шума."""
    out = np.zeros(int(SR * sec), dtype=np.float32)
    for _ in range(int(density * sec)):
        g = noise(rng.uniform(0.01, 0.04))
        g = (g - lowpass(g, 800)) * env(len(g), 0.002, 0.01)
        put(out, g, rng.uniform(0, sec - 0.05), rng.uniform(0.1, 0.4))
    out = lowpass(out, bright)
    return out * np.sin(np.linspace(0, np.pi, len(out))) ** 0.7


def whoosh(sec=0.6):
    n = noise(sec)
    t = np.linspace(0, 1, len(n))
    out = np.zeros_like(n)
    for i in range(0, len(n), 512):
        seg = n[i:i + 512]
        out[i:i + 512] = seg - lowpass(seg, 200 + 2400 * t[i])
    return lowpass(out, 5000) * np.sin(np.pi * t) ** 2 * 0.35


def chirp():
    t = tt(0.09)
    f = 3200 + 1800 * np.sin(np.pi * t / 0.09)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.sin(np.pi * t / 0.09) * 0.3


def birds(sec):
    out = np.zeros(int(SR * sec), dtype=np.float32)
    t = 0.05
    while t < sec - 0.4:
        for k in range(rng.integers(2, 5)):
            put(out, chirp() * rng.uniform(0.6, 1.0), t + k * 0.11)
        t += rng.uniform(0.35, 0.7)
    return out


def splash():
    out = np.zeros(int(SR * 0.9), dtype=np.float32)
    n = noise(0.4)
    put(out, lowpass(n, 2500) * env(len(n), 0.003, 0.09), 0, 0.6)
    for k in range(7):  # пузырьки
        t = tt(0.06)
        f0 = rng.uniform(500, 1100)
        put(out, np.sin(2 * np.pi * np.cumsum(f0 + 4000 * t) / SR) * env(len(t), 0.002, 0.02) * 0.25,
            0.08 + k * rng.uniform(0.04, 0.09))
    return out


def croak():
    t = tt(0.45)
    pulses = (np.sin(2 * np.pi * 28 * t) > 0.3).astype(np.float32)
    voice = (2 * ((t * 110) % 1) - 1) * pulses
    return lowpass(lowpass(voice, 700), 900) * np.sin(np.pi * t / 0.45) * 0.9


def crunch():
    n = noise(0.18)
    grains = (rng.random(len(n)) < 0.02).astype(np.float32) * rng.uniform(0.5, 1, len(n))
    x = lowpass(n * 0.2 + grains * 3, 3500) - lowpass(n * 0.2 + grains * 3, 300)
    return x * env(len(n), 0.005, 0.07) * 0.6


def jingle():
    """Jingle Bells (Pierpont, 1857 — общественное достояние), 8-бит квадрат, ~2 с."""
    melody = [(76, 1), (76, 1), (76, 2), (76, 1), (76, 1), (76, 2), (76, 1), (79, 1), (72, 1.5), (74, 0.5), (76, 4)]
    step = 0.115
    out = np.zeros(int(SR * 2.6), dtype=np.float32)
    t0 = 0.0
    for m, d in melody:
        sec = d * step
        t = tt(sec * 0.9)
        f = note(m)
        sq = np.sign(np.sin(2 * np.pi * f * t)) * 0.5 + np.sign(np.sin(2 * np.pi * f / 2 * t)) * 0.15
        put(out, sq * env(len(t), 0.002, 0.25), t0, 0.35)
        t0 += sec
    # бубенцы
    for k in range(10):
        b = noise(0.08)
        put(out, (b - lowpass(b, 6000)) * env(len(b), 0.001, 0.03), k * step * 2, 0.4)
    return out


def bell():
    t = tt(1.4)
    x = sum(np.sin(2 * np.pi * f * t) * a for f, a in [(1046.5, 1), (1568, 0.5), (2093, 0.3)])
    return x * env(len(t), 0.002, 0.4) * 0.3


def sfx():
    s = np.zeros(N, dtype=np.float32)
    put(s, rustle(3.5, 40, 3000), 0.1, 0.35)                     # лепестки, тихо
    for b in (T_SPRING, T_SUMMER, T_AUTUMN, T_WINTER, T_CTA):   # мягкие переходы
        put(s, whoosh(), b - 0.3, 0.7)
    put(s, birds(2.0), T_SPRING + 0.3, 0.35)                      # птицы, 2 с
    put(s, splash(), T_SUMMER + 0.4, 0.6)
    put(s, croak(), T_SUMMER + 2.3, 0.35)
    put(s, croak(), T_SUMMER + 2.9, 0.25)
    put(s, rustle(2.6, 90, 5000), T_AUTUMN + 0.2, 0.55)           # листья
    for k in range(4):                                            # снег хрустит
        put(s, crunch(), T_WINTER + 0.1 + k * 0.33, 0.8)
    put(s, jingle(), T_NY + 0.05, 0.8)                            # Jingle Bells 8-бит
    put(s, bell(), T_CTA + 0.1, 0.6)
    return s


def voice():
    v = np.zeros(N, dtype=np.float32)
    for i, at in enumerate(VO_AT, 1):
        src_ = HERE / "audio" / f"vt_{i}.wav"
        dst = HERE / "audio" / f"vf_{i}.wav"
        slot = (VO_AT[i] if i < len(VO_AT) else DUR - 0.1) - 0.08 - at
        tempo = max(1.0, len(read_wav(src_)) / SR / slot)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(src_), "-af",
                        f"atempo={tempo:.3f},highpass=f=90,lowpass=f=9500,"
                        "acompressor=threshold=-22dB:ratio=2.5:attack=8:release=120:makeup=2,"
                        "aecho=0.85:0.6:45|90:0.18|0.10",
                        "-ar", str(SR), "-ac", "1", str(dst)], check=True)
        x = read_wav(dst)
        print(f"VO{i}: {at:5.2f} - {at + len(x) / SR:5.2f}  x{tempo:.2f}")
        put(v, x, at)
    return v / (np.abs(v).max() + 1e-9) * 0.9


def main():
    v = voice()
    m = music() * MUSIC_GAIN
    s = sfx() * 0.6
    mixd = v + m + s
    mixd /= max(1.0, np.abs(mixd).max() / 0.97)
    pcm = (np.clip(mixd, -1, 1) * 32767).astype(np.int16)
    out = HERE / "out" / "audio_mix.wav"
    with wave.open(str(out), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(np.repeat(pcm[:, None], 2, axis=1).tobytes())
    print("done", out)


if __name__ == "__main__":
    main()
