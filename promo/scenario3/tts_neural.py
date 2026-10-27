"""Мужская нейро-озвучка (Microsoft Dmitry Neural через edge-tts, нужен интернет).

Каждая реплика -> audio/vo_N.mp3 -> audio/vt_N.wav (обрезана тишина, 48 кГц моно).
Запуск: python tts_neural.py   (pip install edge-tts)
"""
import asyncio
import subprocess
from pathlib import Path

import edge_tts

HERE = Path(__file__).parent
VOICE = "ru-RU-DmitryNeural"

LINES = [
    ("Ты всё ещё заходишь в игру, чтобы поменять FOV? Серьёзно?", "+15%"),
    ("Запустил. Зашёл. Покрутил. Вышел. И так каждый раз, когда захотел что-то поменять.", "+15%"),
    ("А теперь — всё в лаунчере. Двигаешь слайдер — сразу видишь, как изменится мир. Без запуска игры.", "+12%"),
    ("Не разбираешься в настройках? Лаунчер сам подберёт под твой компьютер. Один клик — и всё готово.", "+12%"),
    ("Запустил — и играешь с идеальными настройками. Без танцев с бубном.", "+10%"),
    # целиком сервис эту фразу не синтезирует — две части, склеиваются с паузой
    (["Бесплатно.", "Ссылка в профиле."], "+0%"),
]

TRIM = ("silenceremove=start_periods=1:start_threshold=-45dB,areverse,"
        "silenceremove=start_periods=1:start_threshold=-45dB,areverse")


async def synth(text, rate, mp3):
    for attempt in range(5):  # сервис иногда отвечает пустым потоком
        try:
            await edge_tts.Communicate(text, VOICE, rate=rate).save(str(mp3))
            break
        except edge_tts.exceptions.NoAudioReceived:
            if attempt == 4:
                raise
            await asyncio.sleep(2)


async def main():
    for i, (text, rate) in enumerate(LINES, 1):
        mp3 = HERE / "audio" / f"vo_{i}.mp3"
        if isinstance(text, list):
            parts = [HERE / "audio" / f"vo_{i}_{k}.mp3" for k in range(len(text))]
            for part, t in zip(parts, text):
                await synth(t, rate, part)
            ins = sum([["-i", str(p)] for p in parts], [])
            fc = "".join(f"[{k}]apad=pad_dur=0.12[a{k}];" for k in range(len(parts)))
            fc += "".join(f"[a{k}]" for k in range(len(parts))) + f"concat=n={len(parts)}:v=0:a=1"
            subprocess.run(["ffmpeg", "-v", "error", "-y", *ins, "-filter_complex", fc, str(mp3)], check=True)
        else:
            await synth(text, rate, mp3)
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(mp3), "-af", TRIM, "-ar", "48000", "-ac", "1",
                        str(HERE / "audio" / f"vt_{i}.wav")], check=True)
        print("ok", i)


asyncio.run(main())
