# Промо «Мод в один клик» (сценарий 5)

9:16, 1080×1920, 30 fps, 25 с. Итог: `out/froglauncher_mods_9x16.mp4`.

Мокап вкладки «🧩 Моды» (`Views/InstancesView.xaml`): поиск по Modrinth, «Установить» → «✓ Установлен»,
обязательные зависимости ставятся сами (`ModrinthService.InstallRecursiveAsync`), тост как в `PageViewModels.InstallModAsync`.

В ролике **REI**, а не JEI: Architectury API и Cloth Config — настоящие зависимости REI, у JEI зависимостей нет.

| Файл | Что делает |
|---|---|
| `tts_neural.py` | мужская озвучка (Microsoft Dmitry Neural, edge-tts, нужен интернет) → `audio/vt_N.wav` |
| `render.py` | покадровый рендер → `out/video_noaudio.mp4`; хелперы и CTA — из `../scenario3/render.py` |
| `mix.py` | бит 120 BPM (25%) из `../scenario3/mix.py`, SFX, озвучка → `out/audio_mix.wav` |

```bash
python tts_neural.py
python render.py
python mix.py
ffmpeg -i out/video_noaudio.mp4 -i out/audio_mix.wav -af loudnorm=I=-14:TP=-1.5 -c:v copy -c:a aac -b:a 192k -shortest out/froglauncher_mods_9x16.mp4
```

Тайминг (сдвинут под озвучку): 0–3.75 крючок · 3.75 поиск · 9.75 зависимости · 17.25 запуск · 22.35–25 CTA.
Все моменты (клики, набор, прогресс) — константы `T_*` в начале `render.py`; `mix.py` читает их оттуда.

Окно игры с инвентарём и панелью REI — нарисованный мокап, лучше заменить настоящей записью (≈18.1–22.35 с).
