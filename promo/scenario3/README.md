# Промо «Настройки MC прямо в лаунчере» (сценарий 3)

9:16, 1080×1920, 30 fps, 30 с. Итог: `out/ribbix_settings_9x16.mp4`.

| Файл | Что делает |
|---|---|
| `tts_neural.py` | мужская озвучка (Microsoft Dmitry Neural, edge-tts, нужен интернет) → `audio/vt_N.wav` |
| `tts.ps1` | запасной офлайн-голос (Microsoft Pavel) → `audio/vo_N.wav` |
| `render.py` | покадровый рендер сцен (Pillow → ffmpeg) → `out/video_noaudio.mp4` |
| `mix.py` | бит 125 BPM (25%), SFX, озвучка (каждая реплика ускоряется под свой слот) → `out/audio_mix.wav` |

Пересборка:

```bash
python tts_neural.py                      # озвучка → audio/vt_N.wav (уже с обрезкой тишины)
python render.py                           # видео
python mix.py                              # звук
ffmpeg -i out/video_noaudio.mp4 -i out/audio_mix.wav -af loudnorm=I=-14:TP=-1.5 -c:v copy -c:a aac -b:a 192k -shortest out/ribbix_settings_9x16.mp4
```

Обрезка тишины: `ffmpeg -i vo_N.wav -af "silenceremove=start_periods=1:start_threshold=-45dB,areverse,silenceremove=start_periods=1:start_threshold=-45dB,areverse" -ar 48000 -ac 1 vt_N.wav`

Кадр для проверки: `python render.py --only 12.0,20.5` → `frames/`.

Тайминг сцен (сдвинут под длину озвучки): 0–3.6 крючок · 3.6–10.05 проблема · 10.05–16.7 решение ·
16.7–23.0 авто-пресет · 23.0–27.2 результат · 27.2–30 CTA.

Окно игры в сцене 2 и «геймплей» в сцене 5 — нарисованные мокапы. Для финала лучше вставить
настоящую запись Minecraft (23.9–27.2 с) поверх `out/video_noaudio.mp4`.
