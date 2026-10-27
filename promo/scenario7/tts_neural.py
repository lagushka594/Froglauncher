"""Сценарий 7 «Статистика игрока». Мужская нейро-озвучка (Microsoft Dmitry Neural через edge-tts, нужен интернет).

Каждая реплика -> audio/vo_N.mp3 -> audio/vt_N.wav (обрезана тишина, 48 кГц моно).
Запуск: python tts_neural.py   (pip install edge-tts)
"""
import asyncio
import re
import subprocess
from pathlib import Path

import edge_tts

HERE = Path(__file__).parent
VOICE = "ru-RU-DmitryNeural"

LINES = [
    ("Сколько часов ты реально играл в Майнкрафт? Мой лаунчер знает.", "+0%"),
    ("47 часов. 128 сессий. И это только за этот инстанс.", "+0%"),
    ("87 достижений из 122. Видно, какие остались — и куда идти дальше.", "+0%"),
    ("И бейджи. Новичок, Опытный, Ветеран, Легенда. Я пока Ветеран — до Легенды осталось немного.", "+0%"),
    ("И видно, когда я задротил. По выходным, конечно.", "+0%"),
    ("А ты сколько наиграл? Напиши в комментах.", "+0%"),
]

TRIM = ("silenceremove=start_periods=1:start_threshold=-45dB,areverse,"
        "silenceremove=start_periods=1:start_threshold=-45dB,areverse")


async def synth(text, rate, mp3):
    for attempt in range(6):  # сервис иногда отвечает пустым потоком или зависает
        try:
            await asyncio.wait_for(edge_tts.Communicate(text, VOICE, rate=rate).save(str(mp3)), 30)
            break
        except (edge_tts.exceptions.NoAudioReceived, asyncio.TimeoutError):
            if attempt == 5:
                raise
            await asyncio.sleep(3 * (attempt + 1))


async def synth_parts(texts, rate, mp3, stem):
    """Синтез по частям и склейка с паузой 0.12 с."""
    parts = [mp3.with_name(f"{stem}_{k}.mp3") for k in range(len(texts))]
    for part, t in zip(parts, texts):
        await synth(t, rate, part)
    ins = sum([["-i", str(p)] for p in parts], [])
    fc = "".join(f"[{k}]apad=pad_dur=0.12[a{k}];" for k in range(len(parts)))
    fc += "".join(f"[a{k}]" for k in range(len(parts))) + f"concat=n={len(parts)}:v=0:a=1"
    subprocess.run(["ffmpeg", "-v", "error", "-y", *ins, "-filter_complex", fc, str(mp3)], check=True)
    for part in parts:
        part.unlink()


async def main():
    for i, (text, rate) in enumerate(LINES, 1):
        mp3 = HERE / "audio" / f"vo_{i}.mp3"
        if isinstance(text, list):
            await synth_parts(text, rate, mp3, f"vo_{i}")
        else:
            try:
                await synth(text, rate, mp3)
            except edge_tts.exceptions.NoAudioReceived:
                # некоторые фразы сервис целиком не берёт — режем по предложениям
                await synth_parts(re.split(r"(?<=[.?!])\s+", text), rate, mp3, f"vo_{i}")
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(mp3), "-af", TRIM, "-ar", "48000", "-ac", "1",
                        str(HERE / "audio" / f"vt_{i}.wav")], check=True)
        print("ok", i)


asyncio.run(main())
