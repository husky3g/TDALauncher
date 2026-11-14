#!/usr/bin/env python3
"""Mirror the latest #server-announcements posts into news.json for the TDA Launcher.

Runs in GitHub Actions with the bot token from the DISCORD_BOT_TOKEN secret, so the launcher itself
never needs a Discord login or token. Discord markup (mentions, emoji, timestamps, markdown) is turned
into plain text here so the launcher can show it as-is.
"""
import datetime
import json
import os
import re
import sys
import urllib.error
import urllib.request

GUILD = "1570790498208059534"
CHANNEL = "1570791863747747890"
LIMIT = 20
MAX_TEXT = 1500

out = sys.argv[1] if len(sys.argv) > 1 else "news.json"
token = os.environ.get("DISCORD_BOT_TOKEN", "").strip()
if not token:
    print("::warning::The DISCORD_BOT_TOKEN secret is not set - skipping (see README, 'Discord announcements').")
    sys.exit(0)


def api(path):
    req = urllib.request.Request(
        "https://discord.com/api/v10" + path,
        headers={"Authorization": "Bot " + token, "User-Agent": "DiscordBot (https://github.com/husky3g/TDALauncher, 1.0)"},
    )
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)


def lookup(path):
    try:
        return {x["id"]: x["name"] for x in api(path)}
    except urllib.error.HTTPError as e:
        print(f"::warning::{path} returned {e.code}; names will be generic")
        return {}


try:
    messages = api(f"/channels/{CHANNEL}/messages?limit={LIMIT}")
except urllib.error.HTTPError as e:
    hint = {401: "the bot token is wrong", 403: "the bot can't see the channel (needs View Channel + Read Message History)", 404: "channel not found / bot not in the server"}
    print(f"::error::Reading the announcements channel failed: HTTP {e.code} - {hint.get(e.code, e.reason)}")
    sys.exit(1)

channels = lookup(f"/guilds/{GUILD}/channels")
roles = lookup(f"/guilds/{GUILD}/roles")


def display(user):
    return user.get("global_name") or user.get("username") or "Unknown"


def stamp(m):
    t = datetime.datetime.fromtimestamp(int(m.group(1)), datetime.timezone.utc)
    style = m.group(2) or "f"
    if style in "tT":
        return t.strftime("%H:%M UTC")
    if style in "dD":
        return t.strftime("%d %b %Y")
    return t.strftime("%d %b %Y %H:%M UTC")


def clean(text, users):
    text = re.sub(r"<@!?(\d+)>", lambda m: "@" + users.get(m.group(1), "user"), text)
    text = re.sub(r"<@&(\d+)>", lambda m: "@" + roles.get(m.group(1), "role"), text)
    text = re.sub(r"<#(\d+)>", lambda m: "#" + channels.get(m.group(1), "channel"), text)
    text = re.sub(r"<a?:(\w+):\d+>", r":\1:", text)
    text = re.sub(r"<t:(-?\d+)(?::([tTdDfFR]))?>", stamp, text)
    text = re.sub(r"\[([^\]]+)\]\(<?https?://[^)>\s]+>?\)", r"\1", text)
    text = re.sub(r"<(https?://[^>\s]+)>", r"\1", text)
    text = re.sub(r"^(#{1,3}|-#)\s+", "", text, flags=re.M)
    text = re.sub(r"^>{1,3}\s?", "", text, flags=re.M)
    text = re.sub(r"```\w*\n?", "", text)
    for mark in ("**", "__", "~~", "||", "`"):
        text = text.replace(mark, "")
    text = re.sub(r"(?<![\w*])\*(?!\s)([^*\n]+?)(?<!\s)\*(?![\w*])", r"\1", text)
    return re.sub(r"\n{3,}", "\n\n", text).strip()


posts = []
for msg in messages:
    if msg.get("type") not in (0, 19):  # normal messages and replies only
        continue
    users = {u["id"]: display(u) for u in msg.get("mentions", [])}
    parts = [msg.get("content", "")]
    for snap in msg.get("message_snapshots", []):  # forwarded messages
        parts.append(snap.get("message", {}).get("content", ""))
    for e in msg.get("embeds", []):
        parts += [e.get("title", ""), e.get("description", "")]
        parts += [f"{f.get('name', '')}: {f.get('value', '')}" for f in e.get("fields", [])]
    text = clean("\n\n".join(p for p in parts if p), users)
    files = len(msg.get("attachments", []))
    if files:
        text += ("\n\n" if text else "") + f"[{files} attachment{'s' if files > 1 else ''} - open in Discord]"
    if not text:
        continue
    if len(text) > MAX_TEXT:
        text = text[:MAX_TEXT].rstrip() + "..."
    posts.append({
        "id": msg["id"],
        "author": display(msg.get("author", {})),
        "time": msg["timestamp"],
        "text": text,
        "url": f"https://discord.com/channels/{GUILD}/{CHANNEL}/{msg['id']}",
    })

with open(out, "w", encoding="utf-8") as f:
    json.dump({"channel": channels.get(CHANNEL, "server-announcements"), "posts": posts}, f, ensure_ascii=False, indent=1)
print(f"Wrote {len(posts)} announcements to {out}")
