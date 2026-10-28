"""
Коммиты и релизы репозитория → Discord. Запускается GitHub Actions (.github/workflows/discord-updates.yml)
на push в main и на публикацию релиза; событие читается из GITHUB_EVENT_PATH.

Вебхуки (секреты репозитория): DISCORD_COMMITS_WEBHOOK_URL — коммиты, DISCORD_RELEASES_WEBHOOK_URL — релизы;
если их нет — DISCORD_WEBHOOK_URL. DRY_RUN=1 — только показать сообщение.
"""
import json
import os
import sys
import urllib.request

UA = "Ribbix-Updates/1.0"
# Служебные коммиты, о которых писать не нужно.
SKIP_PREFIXES = ("chore: social-notify", "Merge branch", "Merge pull request")
GREEN, PURPLE = 0x22C55E, 0x8B5CF6


def hook(kind):
    return os.environ.get(f"DISCORD_{kind}_WEBHOOK_URL") or os.environ.get("DISCORD_WEBHOOK_URL")


def post(url, payload):
    if os.environ.get("DRY_RUN"):
        print("[dry-run]", json.dumps(payload, ensure_ascii=False, indent=1))
        return
    if not url:
        sys.exit("Нет вебхука Discord (DISCORD_WEBHOOK_URL)")
    req = urllib.request.Request(url, data=json.dumps(payload).encode(),
                                 headers={"Content-Type": "application/json", "User-Agent": UA})
    urllib.request.urlopen(req, timeout=30).close()


def cut(text, n):
    return text if len(text) <= n else text[: n - 1].rstrip() + "…"


def on_push(ev):
    commits = [c for c in ev.get("commits", []) if c.get("distinct", True)
               and not c["message"].startswith(SKIP_PREFIXES)]
    if not commits:
        print("нечего отправлять")
        return
    branch = ev["ref"].rsplit("/", 1)[-1]
    lines = []
    for c in commits[-10:]:
        title = c["message"].splitlines()[0]
        lines.append(f"[`{c['id'][:7]}`]({c['url']}) {cut(title, 120)} — *{c['author'].get('username') or c['author']['name']}*")
    more = len(commits) - 10
    if more > 0:
        lines.append(f"…и ещё {more}")
    # Подробности последнего коммита (тело сообщения), без строк соавторов.
    body = "\n".join(l for l in commits[-1]["message"].splitlines()[1:] if not l.lower().startswith("co-authored-by")).strip()
    desc = "\n".join(lines) + (f"\n\n{cut(body, 1500)}" if body else "")
    word = "коммит" if len(commits) == 1 else ("коммита" if 2 <= len(commits) % 10 <= 4 and not 12 <= len(commits) % 100 <= 14 else "коммитов")
    post(hook("COMMITS"), {
        "username": "Ribbix 🐸",
        "embeds": [{
            "title": f"🔨 {len(commits)} {word} в {branch}",
            "url": ev.get("compare") or ev["repository"]["html_url"],
            "description": cut(desc, 4000),
            "color": PURPLE,
        }],
    })


def on_release(ev):
    r = ev["release"]
    if r.get("draft"):
        return
    name = r.get("name") or r["tag_name"]
    setup = next((a["browser_download_url"] for a in r.get("assets", []) if a["name"] == "RibbixLauncherSetup.exe"), None)
    desc = cut((r.get("body") or "").strip(), 3000)
    desc += f"\n\n📦 **Скачать:** {setup or r['html_url']}\nУже стоит Ribbix? Обновление придёт само 🔄"
    post(hook("RELEASES"), {
        "username": "Ribbix 🐸",
        "content": "@everyone" if os.environ.get("PING_EVERYONE") else None,
        "embeds": [{
            "title": f"🚀 Вышел Ribbix Launcher {name}" + (" (бета)" if r.get("prerelease") else ""),
            "url": r["html_url"],
            "description": desc,
            "color": GREEN,
        }],
    })


def main():
    ev = json.load(open(os.environ["GITHUB_EVENT_PATH"], encoding="utf-8"))
    name = os.environ.get("GITHUB_EVENT_NAME", "")
    if name == "push":
        on_push(ev)
    elif name == "release":
        on_release(ev)
    else:
        print("событие", name, "не обрабатывается")


if __name__ == "__main__":
    main()
