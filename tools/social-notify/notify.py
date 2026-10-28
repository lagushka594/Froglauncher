"""
Новые видео YouTube и TikTok → сообщение в Discord (вебхук). Запускается по расписанию в GitHub Actions
(.github/workflows/social-notify.yml), только стандартная библиотека Python.

YouTube — официальная RSS-лента канала: ссылка и название каждого нового ролика.
TikTok — без входа в аккаунт страница профиля отдаёт только число видео, поэтому при росте числа
приходит сообщение со ссылкой на профиль.

Переменные окружения: DISCORD_WEBHOOK_URL (обязательно), DRY_RUN=1 — ничего не отправлять, только показать.
Состояние (уже отправленные видео) — state.json рядом; первый запуск только запоминает текущие видео.
"""
import json
import os
import re
import sys
import urllib.request
import xml.etree.ElementTree as ET

YOUTUBE_CHANNEL_ID = "UCOwgS1PFdfiID4XDupDRGyw"  # @Ribbix_launcher
YOUTUBE_URL = "https://www.youtube.com/@Ribbix_launcher"
TIKTOK_USER = "froglauncher_official"
STATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "state.json")
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept-Language": "en-US,en;q=0.9"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read().decode("utf-8", "replace")


def youtube_videos():
    """[(id, title)] от новых к старым."""
    ns = {"a": "http://www.w3.org/2005/Atom", "yt": "http://www.youtube.com/xml/schemas/2015"}
    root = ET.fromstring(fetch(f"https://www.youtube.com/feeds/videos.xml?channel_id={YOUTUBE_CHANNEL_ID}"))
    return [(e.find("yt:videoId", ns).text, e.find("a:title", ns).text or "") for e in root.findall("a:entry", ns)]


def tiktok_video_count():
    """Число видео в профиле или None, если TikTok не отдал данные (капча, блок)."""
    html = fetch(f"https://www.tiktok.com/@{TIKTOK_USER}")
    m = re.search(r'<script id="__UNIVERSAL_DATA_FOR_REHYDRATION__"[^>]*>(.*?)</script>', html, re.S)
    if not m:
        return None
    user = json.loads(m.group(1)).get("__DEFAULT_SCOPE__", {}).get("webapp.user-detail", {})
    stats = user.get("userInfo", {}).get("stats") or {}
    return stats.get("videoCount")


def send(text):
    if os.environ.get("DRY_RUN"):
        print("[dry-run]", text)
        return
    hook = os.environ.get("DISCORD_WEBHOOK_URL")
    if not hook:
        sys.exit("Нет DISCORD_WEBHOOK_URL")
    body = json.dumps({"content": text, "username": "Ribbix 🐸"}).encode()
    req = urllib.request.Request(hook, data=body, headers={"Content-Type": "application/json", "User-Agent": UA})
    urllib.request.urlopen(req, timeout=30).close()


def main():
    first_run = not os.path.exists(STATE)
    state = {} if first_run else json.load(open(STATE, encoding="utf-8"))
    seen = set(state.get("youtube", []))
    messages = []

    try:
        videos = youtube_videos()
        for vid, title in reversed(videos):  # от старых к новым — в порядке выхода
            if vid not in seen and not first_run:
                messages.append(f"🎬 **Новое видео на YouTube!**\n{title}\nhttps://youtu.be/{vid}")
        state["youtube"] = sorted(seen | {v for v, _ in videos})
    except Exception as ex:  # сеть или формат ленты — пропускаем до следующего запуска
        print("YouTube:", ex)

    try:
        count = tiktok_video_count()
        if count is not None:
            old = state.get("tiktok_count")
            if old is not None and count > old and not first_run:
                n = count - old
                messages.append(f"🎵 **Новое видео в TikTok!**{f' (+{n})' if n > 1 else ''}\nhttps://www.tiktok.com/@{TIKTOK_USER}")
            state["tiktok_count"] = count
        else:
            print("TikTok: данные не получены (возможно, капча) — попробуем позже")
    except Exception as ex:
        print("TikTok:", ex)

    for text in messages:
        send(text)
    with open(STATE, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=1)
    print(("первый запуск: запомнены текущие видео" if first_run else f"отправлено: {len(messages)}"), state.get("tiktok_count"))


if __name__ == "__main__":
    main()
