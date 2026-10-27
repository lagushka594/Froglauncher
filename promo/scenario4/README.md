# Промо «Сезонные темы» (сценарий 4)

9:16, 1080×1920, 30 fps, 22,6 с. Итог: `out/froglauncher_seasons_9x16.mp4`.

Темы и эффекты взяты из лаунчера: названия и цвета — `Services/ThemeService.cs`,
эффекты (лепестки, блики и стрекоза, туман и листья, иней и снег, гирлянда) — `Views/SeasonEffects.cs`.

| Файл | Что делает |
|---|---|
| `tts_neural.py` | мужская озвучка (Microsoft Dmitry Neural, edge-tts, нужен интернет) → `audio/vt_N.wav` |
| `render.py` | покадровый рендер (Pillow → ffmpeg) → `out/video_noaudio.mp4`; хелперы берёт из `../scenario3/render.py` |
| `mix.py` | эмбиент 75 BPM (20%), сезонные SFX, Jingle Bells 8-бит, озвучка с лёгким эхо → `out/audio_mix.wav` |

Пересборка:

```bash
python tts_neural.py
python render.py
python mix.py
ffmpeg -i out/video_noaudio.mp4 -i out/audio_mix.wav -af loudnorm=I=-14:TP=-1.5 -c:v copy -c:a aac -b:a 192k -shortest out/froglauncher_seasons_9x16.mp4
```

Тайминг (сдвинут под озвучку — реплики сценария звучат ~25 с при плане 20 с):
0–4.15 крючок · 4.15 весна · 8.8 лето · 12.95 осень · 16.55 зима · 17.95 НГ · 20.4–22.6 CTA.
Границы правятся константами в начале `render.py`, `mix.py` читает их оттуда же.

Публиковать за 1–2 недели до смены сезона или до 20 декабря (новогодняя тема в лаунчере включается 20.12–10.01).
