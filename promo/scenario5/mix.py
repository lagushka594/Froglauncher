"""Звук для сценария 5: бит 120 BPM (25%) из сценария 3, SFX по сценарию, озвучка (audio/vt_N.wav).

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


def const(name):
    return float(re.search(rf"^{name} = ([\d.]+)", src, re.M).group(1))


def consts(line):
    return [float(x) for x in re.search(rf"^{line}.* = (.*)$", src, re.M).group(1).split(",")]


S1, S2, S3, S4, S5 = consts("S1, S2, S3, S4, S5")
DUR = consts("W, H, FPS, DUR")[3]
T = {k: const(k) for k in ("T_HOOK_CLICK", "T_PICK", "T_INSTALL", "T_DEPS", "T_TOAST", "T_LIST", "T_PLAY")}
T_TYPE = [float(x) for x in re.search(r"^T_TYPE = \[(.*)\]", src, re.M).group(1).split(",")]
T_DL = [float(x) for x in re.search(r"^T_DL = \((.*)\)", src, re.M).group(1).split(",")]
T_DEP_END = [float(x) for x in re.findall(r"\(([\d.]+), ([\d.]+)\)", re.search(r"^T_DEP_DL = (.*)$", src, re.M).group(1))
             for x in [x[1]]]

# бит и SFX сценария 3: подкладываем ему наши границы вместо его render.py
sys.modules["render"] = types.SimpleNamespace(END=DUR, S2=S2, S3=S3, S4=S4, S5=S5, S6=S5)
spec = importlib.util.spec_from_file_location("mix3", HERE.parent / "scenario3" / "mix.py")
M = importlib.util.module_from_spec(spec)
spec.loader.exec_module(M)
del sys.modules["render"]
M.BPM = 120
SR, N, put, tt, env, lowpass, read_wav = M.SR, M.N, M.put, M.tt, M.env, M.lowpass, M.read_wav

VO_AT = [0.08, S2 + 0.05, S3 + 0.05, S4 + 0.05, S5 + 0.1]
MUSIC_GAIN = 0.25
rng = np.random.default_rng(7)


def key():
    """Щелчок клавиши."""
    n = rng.standard_normal(int(SR * 0.025)).astype(np.float32)
    t = tt(0.025)
    x = (n - lowpass(n, 1500)) * 0.5 + np.sin(2 * np.pi * rng.uniform(1800, 2600) * t)
    return x * env(len(t), 0.0005, 0.005) * 0.5


def sfx():
    s = np.zeros(N, dtype=np.float32)
    put(s, M.click(), T["T_HOOK_CLICK"], 1.0)                 # крючок: клик + chime
    put(s, M.chime(), T["T_HOOK_CLICK"] + 0.45, 1.0)
    for b in (S2, S4, S5):                                   # склейки
        put(s, M.whoosh(0.4), b - 0.2, 0.5)
    put(s, M.click(), S2 + 0.45, 0.8)                        # клик в поиск
    for i, tk in enumerate(T_TYPE):                          # быстрый набор
        put(s, key(), tk, 1.0)
        put(s, key(), tk + 0.06, 0.6)
    put(s, M.ding(), T["T_PICK"], 0.9)                       # выбор REI
    put(s, M.click(), T["T_PICK"], 0.8)
    put(s, M.click(), T["T_INSTALL"], 1.0)
    put(s, M.pop(), T["T_DEPS"], 0.7)                        # всплыла карточка зависимостей
    for ti in (T_DL[1] + 0.05, max(T_DEP_END) + 0.15, T["T_LIST"] + 0.2):  # pop на каждом ✅
        put(s, M.pop(), ti, 1.0)
    put(s, M.chime(), T["T_TOAST"], 1.0)                     # success chime в конце
    put(s, M.click(), T["T_PLAY"], 1.0)
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
    print("done", out)


if __name__ == "__main__":
    main()
