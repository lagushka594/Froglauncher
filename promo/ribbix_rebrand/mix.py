"""Звук ролика о Ribbix: бит 120 BPM из сценария 3 (25%), SFX, озвучка ×TEMPO.

Тайминги берутся из rebrand.py (STARTS, VO_AT, STEPS).
Запуск: python mix.py  ->  out/audio_mix.wav
"""
import importlib.util
import subprocess
import sys
import types
import wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE))
import rebrand as V  # noqa: E402

DUR = V.DUR
_saved = sys.modules.get("render")
sys.modules["render"] = types.SimpleNamespace(END=DUR, S2=V.S2, S3=V.S3, S4=V.S4, S5=V.S5, S6=V.S5)
spec = importlib.util.spec_from_file_location("mix3", HERE.parent / "scenario3" / "mix.py")
M = importlib.util.module_from_spec(spec)
spec.loader.exec_module(M)
sys.modules["render"] = _saved
M.BPM = 120

SR, N, put, read_wav = M.SR, M.N, M.put, M.read_wav
MUSIC_GAIN = 0.25


def sfx():
    s = np.zeros(N, dtype=np.float32)
    put(s, M.whoosh(0.5), 0.0, 0.8)
    t_swap = V.S1 + V.LEAD + 0.5 * V.VO[0]                       # смена логотипа
    put(s, M.whoosh(0.5), t_swap - 0.3, 1.0)
    put(s, M.chime(), t_swap, 1.0)
    for b in (V.S2, V.S3, V.S4, V.S5):
        put(s, M.whoosh(0.4), b - 0.2, 0.5)
    v2 = V.VO[1]
    for frac in (0.2, 0.38, 0.52, 0.72):                         # подписи экранов
        put(s, M.pop(), V.S2 + V.LEAD + frac * v2, 0.6)
    put(s, M.ding(), V.S2 + V.LEAD + 0.86 * v2, 0.8)              # «ИГРА — БЕСПЛАТНО»
    v3 = V.VO[2]
    put(s, M.error_beep(), V.S3 + 0.15, 0.6)                      # краш
    for frac in (0.42, 0.74):
        put(s, M.pop(), V.S3 + V.LEAD + frac * v3, 0.7)
    v4 = V.VO[3]
    t_go, t_done = V.LEAD + 0.12 * v4, V.LEAD + 0.7 * v4
    step = (t_done - t_go) / (len(V.STEPS) - 1)
    for k in range(len(V.STEPS) - 1):                             # каждый запуск при поиске
        put(s, M.tick(k % 2 == 0), V.S4 + t_go + k * step, 0.9)
        put(s, M.click(), V.S4 + t_go + k * step + 0.05, 0.5)
    put(s, M.error_beep(), V.S4 + t_done, 0.7)                    # нашли виновника
    put(s, M.chime(), V.S4 + V.LEAD + 0.86 * v4, 1.0)             # моды вернулись
    put(s, M.chime(), V.S5 + 0.25, 0.8)
    return s


def voice():
    v = np.zeros(N, dtype=np.float32)
    for i, at in enumerate(V.VO_AT, 1):
        src = HERE / "audio" / f"vt_{i}.wav"
        dst = HERE / "audio" / f"vf_{i}.wav"
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(src), "-af",
                        f"atempo={V.TEMPO},highpass=f=80,equalizer=f=3200:t=q:w=1:g=3,"
                        "acompressor=threshold=-20dB:ratio=3:attack=5:release=90:makeup=2",
                        "-ar", str(SR), "-ac", "1", str(dst)], check=True)
        x = read_wav(dst)
        print(f"VO{i}: {at:5.2f} - {at + len(x) / SR:5.2f}")
        put(v, x, at)
    return v / (np.abs(v).max() + 1e-9) * 0.95


def main():
    v = voice()
    m = M.music() * MUSIC_GAIN
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
    print("done", out, f"{DUR:.2f}s")


if __name__ == "__main__":
    main()
