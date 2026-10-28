"""Рекламная интеграция 16:9. Мужская нейро-озвучка (Microsoft Dmitry Neural через edge-tts, нужен интернет).

Каждая реплика -> audio/vo_N.mp3 -> audio/vt_N.wav (обрезана тишина, 48 кГц моно).
Запуск: python tts_neural.py   (pip install edge-tts)
"""
import asyncio
import re
import subprocess
import sys
from pathlib import Path

import edge_tts

HERE = Path(__file__).parent
VOICE = "ru-RU-DmitryNeural"

# Рекламная интеграция ~2 мин. Каждая строка — отдельный блок ролика (см. SECTIONS в ad.py).
# Английские названия записаны по-русски, чтобы голос их не коверкал; кавычки «» сервис не принимает.
LINES = [
    ("Кстати, о Майнкрафте. Если вы до сих пор качаете моды руками, ищете нужную Джаву и перезапускаете игру ради одной настройки — знакомьтесь: ФрогЛаунчер.", "+0%"),
    ("Это бесплатный лаунчер с открытым кодом. Ванилла, Фабрик, Квилт, Фордж от версии один-семь-десять и до самых свежих, НеоФордж — всё ставится в пару кликов. Нужную Джаву лаунчер подберёт и скачает сам. А при первом запуске за минуту спросит ник, подберёт память под ваш компьютер и поставит оптимизированную версию с Содиумом.", "+0%"),
    ("Моды — прямо внутри. Встроенный каталог Модринта: ищете, видите версии и загрузчики, жмёте: установить. Зависимости лаунчер подтянет сам. Целые сборки ставятся так же, в один клик.", "+0%"),
    ("Настройки Майнкрафта — тоже в лаунчере, ещё до запуска игры: графика, дальность прорисовки, поле зрения, звук, управление. Не знаете, что ставить? Кнопка: подобрать под мой ПК — сама определит железо и предложит пресет.", "+0%"),
    ("Во вкладке серверов — живой онлайн, пинг и версия. Нажали: играть — лаунчер запустит нужную версию и сразу подключит к серверу.", "+0%"),
    ("Есть статистика: сколько часов вы наиграли, серия дней, график по дням и все достижения из ваших миров с прогрессом. А за часы в игре — бейджи, от Новичка до Легенды.", "+0%"),
    ("Миры под защитой: раз в сутки лаунчер сам делает бэкап, а восстановить можно в один клик. Сборку можно экспортировать в формат Модринта и отправить другу — или перенести из Призм Лаунчера.", "+0%"),
    ("И он просто красивый. Пять сезонных тем меняются сами: сакура весной, пруд со стрекозой летом, листопад осенью, снег зимой и Ква-Новый год с гирляндой.", "+0%"),
    ("Плюс мелочи, которые решают: поиск по всему лаунчеру на Контрол-К, трей, напоминание о перерыве, карточки скриншотов для ТикТока, статус в Дискорде и пять языков.", "+0%"),
    ("ФрогЛаунчер бесплатный — играть можно на любом тарифе, даже без аккаунта. Ссылка — в описании. Попробуйте — ква!", "+0%"),
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
        if (HERE / "audio" / f"vt_{i}.wav").exists() and "--force" not in sys.argv:
            continue  # уже записана; --force — перезаписать всё
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
