# TDA Launcher

One-click launcher for **The Day After**, a Breaking Point (Arma 3) server at `62.226.2.116:2302`.

Download [`build/TDALauncher.exe`](build/TDALauncher.exe) and run it. Nothing else to install; it uses the .NET Framework 4 that ships with Windows 10/11.

## What it does

1. **Checks Steam and Arma 3**. If Steam isn't running, the button reads *Start Steam*. If Arma 3 isn't installed, it reads *Get Arma 3* (opens the Steam install dialog). Unusual install locations can be picked with *Arma 3 folder…*.
2. **Checks the required Workshop mods** through Steam itself:
   - [CUP Terrains - Core](https://steamcommunity.com/sharedfiles/filedetails/?id=583496184)
   - [Breaking Point](https://steamcommunity.com/sharedfiles/filedetails/?id=426629791)
   - [Tembelan Island](https://steamcommunity.com/sharedfiles/filedetails/?id=1252091296)
3. **Install / Update**: subscribes you to any missing mods and downloads them (and pending updates) with per-mod progress.
4. **Play**: only appears once Arma 3 and all three mods are installed and up to date. It starts Arma 3 through BattlEye with the mods enabled and connects straight to the server.

The Discord button opens the official server Discord: https://discord.gg/fSqqJg5W3

The header also shows live server status (online, player count, map) using the Steam server query on port 2303.

## How the auto-subscribe works

The launcher re-runs itself as a short-lived helper process (`TDALauncher.exe --workshop check|install …`) that loads Arma 3's own `steam_api64.dll` as app 107410 and calls Steam's `ISteamUGC` API (`GetItemState`, `SubscribeItem`, `DownloadItem`). While that helper runs, Steam briefly shows you as "in Arma 3". If the Steam API can't be used (for example, you aren't logged in), the launcher falls back to opening the Workshop pages so you can click **Subscribe**, and keeps watching until the mods arrive.

## Building

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This compiles `src\*.cs` with the C# compiler included in Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) and writes `build\TDALauncher.exe`. Server address, Discord link and the mod list are in `Config` at the top of `src\Core.cs`.
