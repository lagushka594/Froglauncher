"""Звук для сценария 7: чилл-подложка из сценария 4 (25%), SFX из сценария 3, озвучка (audio/vt_N.wav).

Каждая реплика ускоряется ровно под свой слот. Тайминги берутся из render.py этого сценария.
Запуск: python mix.py  ->  out/audio_mix.wav
"""
import importlib.util
import re
import subprocess
import sys
import types
import wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).parent
src = (HERE / "render.py").read_text(encoding="utf-8")
S1, S2, S3, S4, S5, S6 = [float(x) for x in re.search(r"^S1, .* = (.*)$", src, re.M).group(1).split(",")]
DUR = float(re.search(r"^W, H, FPS, DUR = .*, ([\d.]+)$", src, re.M).group(1))


def load(name, rel):
    spec = importlib.util.spec_from_file_location(name, HERE.parent / rel)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# SFX сценария 3 (ему нужен свой render — подменяем заглушкой с нашими границами)
sys.modules["render"] = types.SimpleNamespace(END=DUR, S2=S2, S3=S3, S4=S4, S5=S5, S6=S6)
M3 = load("mix3", "scenario3/mix.py")
del sys.modules["render"]
# подложка сценария 4: длину подменяем на нашу
M4 = load("mix4", "scenario4/mix.py")
M4.DUR, M4.N = DUR, int(M4.SR * DUR)

SR = M3.SR
N = int(SR * DUR)
put, read_wav = M3.put, M3.read_wav
VO_AT = [0.12, S2 + 0.05, S3 + 0.05, S4 + 0.05, S5 + 0.05, S6 + 0.05]
MUSIC_GAIN = 0.25


def ticks(s, t0, t1, step=0.07, gain=0.6):
    t, k = t0, 0
    while t < t1:
        put(s, M3.tick(k % 2 == 0), t, gain)
        t += step
        k += 1


def shine():
    """Блик: восходящий мерцающий «дзынь»."""
    t = M3.tt(1.0)
    x = sum(np.sin(2 * np.pi * f * t * (1 + 0.15 * t)) * a for f, a in [(1568, 1), (2093, 0.6), (3136, 0.4)])
    trem = 0.6 + 0.4 * np.sin(2 * np.pi * 14 * t)
    return x * trem * M3.env(len(t), 0.01, 0.35) * 0.3


def sfx():
    s = np.zeros(N, dtype=np.float32)
    put(s, M3.whoosh(0.5), 0.0, 0.9)                          # титр-крючок
    for b in (S2, S3, S4, S5, S6):
        put(s, M3.whoosh(0.4), b - 0.2, 0.45)
    ticks(s, S2 + 0.5, S2 + 2.4)                              # 47 часов
    put(s, M3.ding(), S2 + 2.4, 0.6)
    ticks(s, S2 + 2.2, S2 + 3.6)                              # 128 сессий
    put(s, M3.ding(), S2 + 3.6, 0.6)
    put(s, M3.click(), S3 + 0.38, 0.9)                        # открыли достижения
    ticks(s, S3 + 0.5, S3 + 1.6, 0.06, 0.45)
    for i in range(7):
        put(s, M3.pop(), S3 + 0.7 + i * 0.13, 0.5)
    put(s, M3.whoosh(0.4), S4 + 0.05, 0.5)                    # бейдж
    put(s, shine(), S4 + 0.7, 1.0)
    for i in range(4):
        put(s, M3.pop(), S4 + 1.6 + i * 0.35, 0.8)
    put(s, M3.ding(), S4 + 3.4, 0.6)
    for i in range(14):                                       # столбики графика
        put(s, M3.pop(), S5 + 0.15 + i * 0.05, 0.25)
    put(s, M3.chime(), S6 + 0.25, 0.8)
    return s


def voice():
    v = np.zeros(N, dtype=np.float32)
    for i, at in enumerate(VO_AT, 1):
        s_ = HERE / "audio" / f"vt_{i}.wav"
        dst = HERE / "audio" / f"vf_{i}.wav"
        slot = (VO_AT[i] if i < len(VO_AT) else DUR - 0.08) - 0.06 - at
        tempo = max(1.0, len(read_wav(s_)) / SR / slot)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(s_), "-af",
                        f"atempo={tempo:.3f},highpass=f=80,equalizer=f=3200:t=q:w=1:g=3,"
                        "acompressor=threshold=-20dB:ratio=3:attack=5:release=90:makeup=2",
                        "-ar", str(SR), "-ac", "1", str(dst)], check=True)
        x = read_wav(dst)
        print(f"VO{i}: {at:5.2f} - {at + len(x) / SR:5.2f}  x{tempo:.2f}")
        put(v, x, at)
    return v / (np.abs(v).max() + 1e-9) * 0.95


def main():
    v = voice()
    m = M4.music()[:N] * MUSIC_GAIN
    s = sfx() * 0.55
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
