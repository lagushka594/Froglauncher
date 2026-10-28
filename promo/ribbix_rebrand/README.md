# Ролик «FrogLauncher теперь Ribbix Launcher» + «Доктор крашей»

TikTok 9:16, 1080×1920, 30 fps, ~36 с. Итог: `out/ribbix_rebrand_9x16.mp4`.

Сцены: логотип FrogLauncher рассыпается в пиксели → Ribbix Launcher · новый интерфейс (настоящие скриншоты:
главная, тарифы, профиль с «Мой год») · Доктор крашей (скриншот, приближение к советам и кнопкам) ·
поиск мода-виновника половинным делением 100 → 1 за 7 запусков (схема, как в `ModBisectService`) · CTA.

`shots/` — скриншоты 1–4, `logo_frog_old.png` (из git `df73043`) и `logo_ribbix.png` (`assets/out/main/logo.png`).
Имя мода-виновника в схеме (`fancy-furniture.jar`) — условное.

| Файл | Что делает |
|---|---|
| `tts_neural.py` | мужская озвучка (Dmitry Neural, edge-tts, нужен интернет); `--force` — перезаписать строки |
| `rebrand.py` | покадровый рендер; хелперы — из `../scenario3/render.py` |
| `mix.py` | бит 120 BPM (25%) из `../scenario3/mix.py`, SFX, озвучка ×1.12 |

```bash
python tts_neural.py
python rebrand.py
python mix.py
ffmpeg -i out/video_noaudio.mp4 -i out/audio_mix.wav -af loudnorm=I=-14:TP=-1.5 -c:v copy -c:a aac -b:a 192k -shortest out/ribbix_rebrand_9x16.mp4
```
