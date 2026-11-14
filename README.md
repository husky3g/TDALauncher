# TDA Launcher

One-click launcher for **The Day After**, a Breaking Point (Arma 3) server at `62.226.2.116:2302`.

**[Download TDALauncher.exe](https://github.com/husky3g/TDALauncher/releases/latest/download/TDALauncher.exe)** and run it. There's nothing else to install; it uses the .NET Framework 4 that ships with Windows 10/11. It keeps itself up to date.

## What it does

1. **Checks Steam and Arma 3**. If Steam isn't running, the button reads *Start Steam*. If Arma 3 isn't installed, it reads *Get Arma 3* (opens the Steam install dialog). Unusual install locations can be picked with *Arma 3 folder…*.
2. **Checks the required Workshop mods** through Steam itself:
   - [CUP Terrains - Core](https://steamcommunity.com/sharedfiles/filedetails/?id=583496184)
   - [Breaking Point](https://steamcommunity.com/sharedfiles/filedetails/?id=426629791)
   - [Tembelan Island](https://steamcommunity.com/sharedfiles/filedetails/?id=1252091296)
3. **Install / Update**: subscribes you to any missing mods and downloads them (and pending updates) with per-mod progress.
4. **Play**: only appears once Arma 3 and all three mods are installed and up to date. It starts Arma 3 through BattlEye with the mods enabled and connects straight to the server.

Also in the window: live server status (online, players, map), a button for the [official Discord](https://discord.gg/fSqqJg5W3), and the latest posts from **#server-announcements** (click a post to open it in Discord).

## Auto-update

On every start the launcher asks GitHub for the [latest release](https://github.com/husky3g/TDALauncher/releases/latest). If that release is newer than the running version and has a `TDALauncher.exe` asset, it downloads it, checks the size and SHA-256 that GitHub reports, swaps the exe in place and restarts. If mods are downloading at that moment, it waits until they finish. If the folder isn't writable (for example, Program Files), it shows an error and carries on with the current version.

### Publishing a new version

1. Bump `AppVersion.Text` in [`src/Version.cs`](src/Version.cs), for example to `1.2.0`, and commit.
2. Push a matching tag:
   ```
   git tag v1.2.0
   git push origin v1.2.0
   ```
3. The [Release workflow](.github/workflows/release.yml) builds the exe on GitHub and publishes the release. Every launcher updates itself the next time it starts.

## Discord announcements

Discord doesn't let anyone read channel messages without a login, and a bot token can't be shipped inside the launcher (anyone could extract it). So a [scheduled GitHub Action](.github/workflows/discord-news.yml) does the reading. Every 10 minutes it uses a bot token, stored as a repository secret, to read the latest posts from #server-announcements. It converts them to plain text and publishes `news.json` on the `news` branch. The launcher downloads that public file every 5 minutes, so **players never sign in to anything**.

One-time setup (server owner):

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications), click **New Application** and name it (for example, *TDA Launcher News*).
2. Open **Bot**. Click **Reset Token** and copy the token. Further down, turn on **Message Content Intent** (otherwise post text comes through empty).
3. Open **OAuth2 → URL Generator**. Tick the `bot` scope, then the **View Channels** and **Read Message History** permissions. Open the generated URL and add the bot to The Day After's server. Make sure it can see #server-announcements.
4. In this GitHub repo, go to **Settings → Secrets and variables → Actions → New repository secret**. Name it `DISCORD_BOT_TOKEN` and paste the token.
5. Go to **Actions → Discord announcements → Run workflow** to publish right away. After that it runs every 10 minutes (GitHub sometimes delays scheduled runs by a few minutes).

The bot never needs to be online or hosted anywhere; it only acts as an identity for those reads.

## Building

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This compiles `src\*.cs` with the C# compiler included in Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) and writes `build\TDALauncher.exe`. The server address, Discord links, repo and mod list are in `Config` at the top of [`src/Core.cs`](src/Core.cs).

To preview the announcements panel offline, set `TDA_NEWS_FILE` to a local `news.json` before starting the exe.
