"""Звук для сценария 3: бит 125 BPM (25%), SFX и мужская озвучка (audio/vt_N.wav).

Каждая реплика ускоряется ровно под свой слот, чтобы всё влезло в 30 секунд.
Запуск: python mix.py  ->  out/audio_mix.wav, затем ffmpeg склеивает с видео.
"""
import subprocess
import wave
from pathlib import Path

import numpy as np

import render as R

HERE = Path(__file__).parent
SR = 48000
DUR = R.END
N = int(SR * DUR)
rng = np.random.default_rng(1)


VO_AT = [0.08, R.S2 + 0.05, R.S3 + 0.05, R.S4 + 0.05, R.S5 + 0.05, R.S6 + 0.10]
MUSIC_GAIN = 0.25
BPM = 125


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


def read_wav(p):
    with wave.open(str(p)) as w:
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
        return x.reshape(-1, w.getnchannels()).mean(axis=1)


def lowpass(x, cutoff):
    a = np.exp(-2 * np.pi * cutoff / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i, v in enumerate(x):
        acc = (1 - a) * v + a * acc
        y[i] = acc
    return y


# ---------------- бит ----------------
def kick():
    t = tt(0.35)
    f = 48 + 110 * np.exp(-t * 28)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * env(len(t), 0.001, 0.12) * 1.1


def clap():
    n = rng.standard_normal(int(SR * 0.2))
    n = n - lowpass(n, 900)
    e = env(len(n), 0.001, 0.05)
    for k in (0.008, 0.017):
        e += np.roll(env(len(n), 0.001, 0.01), int(k * SR)) * 0.7
    return n * e * 0.45


def hat(open_=False):
    n = rng.standard_normal(int(SR * (0.18 if open_ else 0.05)))
    n = n - lowpass(n, 6000)
    return n * env(len(n), 0.0005, 0.06 if open_ else 0.012) * 0.35


def saw(freq, sec):
    t = tt(sec)
    return 2 * ((t * freq) % 1) - 1


def bass(freq, sec):
    x = saw(freq, sec) * 0.6 + np.sin(2 * np.pi * freq * tt(sec)) * 0.6
    return lowpass(x, 420) * env(len(x), 0.004, sec * 0.7)


def stab(freqs, sec):
    x = sum(saw(f, sec) + saw(f * 1.006, sec) for f in freqs) / (2 * len(freqs))
    return lowpass(x, 2400) * env(len(x), 0.003, 0.09) * 0.55


def music():
    bpm = BPM
    b = 60 / bpm
    m = np.zeros(N, dtype=np.float32)
    duck = np.ones(N, dtype=np.float32)
    K, C, HH, HO = kick(), clap(), hat(), hat(True)
    prog_ = [(55.0, [220.0, 261.6, 329.6]), (43.65, [174.6, 220.0, 261.6]),
             (65.41, [261.6, 329.6, 392.0]), (49.0, [196.0, 246.9, 293.7])]  # Am F C G
    beat = 0
    tpos = 0.0
    while tpos < R.S6 + 0.01:
        bar, inbar = divmod(beat, 4)
        intro = tpos < R.S2  # на крючке — без бочки, только хэты и стабы
        root, chord = prog_[bar % 4]
        if not intro:
            put(m, K, tpos)
            i = int(tpos * SR)
            dl = int(0.22 * SR)
            duck[i:i + dl] = np.minimum(duck[i:i + dl], 0.35 + 0.65 * np.linspace(0, 1, len(duck[i:i + dl])))
            put(m, bass(root, b * 0.45), tpos + b * 0.5, 0.8)
        if inbar in (1, 3):
            put(m, C, tpos, 0.9 if not intro else 0.5)
        put(m, HH, tpos + b * 0.5)
        if inbar == 3:
            put(m, HO, tpos + b * 0.75, 0.6)
        if inbar in (0, 2) or (inbar == 3 and bar % 2):
            put(m, stab(chord, 0.25), tpos + (b * 0.75 if inbar == 3 else 0), 0.8)
        beat += 1
        tpos = beat * b
    m *= duck
    # финальный удар на CTA и хвост
    put(m, kick(), R.S6, 1.4)
    put(m, stab([220.0, 261.6, 329.6, 440.0], 1.5) * np.exp(-tt(1.5)[:len(stab([1], 1.5))] * 1.2), R.S6, 1.2)
    return m / (np.abs(m).max() + 1e-9) * 0.9


# ---------------- SFX ----------------
def whoosh(sec=0.45, up=True):
    n = rng.standard_normal(int(SR * sec)).astype(np.float32)
    t = np.linspace(0, 1, len(n))
    out = np.zeros_like(n)
    # «скользящий» фильтр — блоками
    blk = 512
    for i in range(0, len(n), blk):
        c = 400 + 5000 * (t[i] if up else 1 - t[i])
        seg = n[i:i + blk]
        out[i:i + blk] = seg - lowpass(seg, c * 0.5)
    shape = np.sin(np.pi * t) ** 1.5
    return lowpass(out, 7000) * shape * 0.5


def tick(hi):
    t = tt(0.03)
    return np.sin(2 * np.pi * (2400 if hi else 1800) * t) * env(len(t), 0.0005, 0.006) * 0.5


def error_beep():
    out = np.zeros(int(SR * 0.45), dtype=np.float32)
    for k in range(2):
        t = tt(0.16)
        sq = np.sign(np.sin(2 * np.pi * 330 * t)) * 0.25 + np.sign(np.sin(2 * np.pi * 220 * t)) * 0.25
        put(out, lowpass(sq, 3000) * env(len(t), 0.002, 0.3), k * 0.2)
    return out


def ding():
    t = tt(0.9)
    x = np.sin(2 * np.pi * 1318.5 * t) + 0.5 * np.sin(2 * np.pi * 2637 * t) + 0.3 * np.sin(2 * np.pi * 1975.5 * t)
    return x * env(len(t), 0.002, 0.25) * 0.35


def pop():
    t = tt(0.07)
    f = 900 - 500 * t / 0.07
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * env(len(t), 0.001, 0.02) * 0.4


def click():
    n = rng.standard_normal(int(SR * 0.03))
    t = tt(0.03)
    return (n * 0.3 + np.sin(2 * np.pi * 3000 * t)) * env(len(t), 0.0003, 0.004) * 0.6


def chime():
    out = np.zeros(int(SR * 1.3), dtype=np.float32)
    for i, f in enumerate([523.25, 659.25, 783.99, 1046.5]):
        t = tt(0.9)
        x = np.sin(2 * np.pi * f * t) + 0.3 * np.sin(2 * np.pi * f * 2 * t)
        put(out, x * env(len(t), 0.003, 0.3) * 0.3, i * 0.075)
    return out


def sfx():
    s = np.zeros(N, dtype=np.float32)
    put(s, whoosh(0.5), 0.0, 0.9)                       # титр-крючок
    for b in (R.S2, R.S3, R.S4, R.S5 + 0.7, R.S6):     # склейки
        put(s, whoosh(0.4), b - 0.2, 0.5)
    tpos, k = R.S2, 0                                   # тиканье часов
    while tpos < R.S3 - 0.1:
        put(s, tick(k % 2 == 0), tpos, 0.8)
        tpos += 0.25
        k += 1
    put(s, error_beep(), R.S2 + 1.95, 0.9)
    for ti in (R.S3 + 0.25, R.S3 + 1.90, R.S3 + 5.10):  # ✅
        put(s, ding(), ti, 1.0)
    last = 8                                            # pop на каждом шаге слайдера
    for i in range(int(2.4 * 30)):
        lt = 1.4 + i / 30
        rd = round(R.lerp(8, 16, R.smooth(R.prog(lt, 1.4, 3.6))))
        if rd != last:
            put(s, pop(), R.S3 + lt, 0.8)
            last = rd
    for ti in (R.S4 + 0.75, R.S4 + 2.75, R.S5 + 0.55):  # клики
        put(s, click(), ti, 1.0)
    put(s, chime(), R.S4 + 2.85, 1.0)
    return s


def voice():
    v = np.zeros(N, dtype=np.float32)
    for i, at in enumerate(VO_AT, 1):
        src = HERE / "audio" / f"vt_{i}.wav"
        dst = HERE / "audio" / f"vf_{i}.wav"
        # ускоряем ровно настолько, чтобы реплика кончилась до следующей
        slot = (VO_AT[i] if i < len(VO_AT) else DUR - 0.06) - 0.06 - at
        tempo = max(1.0, len(read_wav(src)) / SR / slot)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(src), "-af",
                        f"atempo={tempo:.3f},highpass=f=80,equalizer=f=3200:t=q:w=1:g=3,"
                        "acompressor=threshold=-20dB:ratio=3:attack=5:release=90:makeup=2",
                        "-ar", str(SR), "-ac", "1", str(dst)], check=True)
        x = read_wav(dst)
        end = at + len(x) / SR
        print(f"VO{i}: {at:5.2f} - {end:5.2f}  x{tempo:.2f}")
        put(v, x, at)
    return v / (np.abs(v).max() + 1e-9) * 0.95


def main():
    v = voice()
    m = music() * MUSIC_GAIN
    s = sfx() * 0.55
    mixd = v + m + s
    mixd /= max(1.0, np.abs(mixd).max() / 0.97)
    pcm = (np.clip(mixd, -1, 1) * 32767).astype(np.int16)
    stereo = np.repeat(pcm[:, None], 2, axis=1)
    out = HERE / "out" / "audio_mix.wav"
    with wave.open(str(out), "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(stereo.tobytes())
    print("done", out)


if __name__ == "__main__":
    main()
