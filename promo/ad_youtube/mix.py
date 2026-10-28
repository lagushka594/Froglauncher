"""Звук рекламной интеграции: бит 120 BPM из сценария 3 (тихо, 16%), SFX, озвучка без ускорения.

Тайминги блоков и пунктов берутся из ad.py (STARTS, VO_AT, SECTIONS).
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
import ad  # noqa: E402  (тянет мокапы сценариев — только ради таймингов)

DUR = ad.DUR
_saved = sys.modules.get("render")
# бит сценария 3: вступление без бочки до конца интро, финальный удар за 1.5 с до конца
sys.modules["render"] = types.SimpleNamespace(END=DUR, S2=ad.STARTS[1], S3=0, S4=0, S5=0, S6=DUR - 1.5)
spec = importlib.util.spec_from_file_location("mix3", HERE.parent / "scenario3" / "mix.py")
M = importlib.util.module_from_spec(spec)
spec.loader.exec_module(M)
sys.modules["render"] = _saved
M.BPM = 120

SR, N, put, read_wav = M.SR, M.N, M.put, M.read_wav
MUSIC_GAIN = 0.16


def sfx():
    s = np.zeros(N, dtype=np.float32)
    for b in ad.STARTS[1:]:
        put(s, M.whoosh(0.4), b - 0.2, 0.45)
    for i, (label, title, bullets) in enumerate(ad.SECTIONS):
        for frac, _ in bullets:
            put(s, M.pop(), ad.STARTS[i] + ad.LEAD + frac * ad.VO[i], 0.35)
    # интро: «❌»-вопросы и логотип
    for frac in (0.22, 0.42, 0.62):
        put(s, M.click(), ad.LEAD + frac * ad.VO[0], 0.6)
    put(s, M.chime(), ad.LEAD + 0.86 * ad.VO[0], 0.8)
    put(s, M.chime(), ad.STARTS[9] + 0.2, 0.8)
    return s


def voice():
    v = np.zeros(N, dtype=np.float32)
    for i, at in enumerate(ad.VO_AT, 1):
        src = HERE / "audio" / f"vt_{i}.wav"
        dst = HERE / "audio" / f"vf_{i}.wav"
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(src), "-af",
                        "highpass=f=80,equalizer=f=3200:t=q:w=1:g=3,"
                        "acompressor=threshold=-20dB:ratio=3:attack=5:release=90:makeup=2",
                        "-ar", str(SR), "-ac", "1", str(dst)], check=True)
        x = read_wav(dst)
        put(v, x, at)
    return v / (np.abs(v).max() + 1e-9) * 0.95


def main():
    v = voice()
    m = M.music() * MUSIC_GAIN
    s = sfx() * 0.5
    mixd = v + m + s
    fade = np.ones(N, dtype=np.float32)
    nf = int(SR * 0.6)
    fade[-nf:] = np.linspace(1, 0, nf)
    mixd *= fade
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
