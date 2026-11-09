# Venuekit – in-game plugin for FFXIV (Dalamud)

Play **Venue Clash**, the trading card game of FFXIV venues on [venuekit.club](https://venuekit.club), right in-game.

- `/clash` – open / close the Venue Clash window
- Right-click a player → **Challenge to Venue Clash** (or target them and type `/clash duel`)
- `/clash accept` – open the last challenge you got · `/clash settings`

## Install
1. In-game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**
2. Add `https://raw.githubusercontent.com/venuekitclub/venuekit-plugin/main/repo.json`, tick the box, save
3. `/xlplugins` → search **Venuekit** → install
4. Type `/clash` and log in with Discord once – done.

On the first start the plugin downloads the Chromium browser component (CefSharp, verified by SHA256).

## Credits & license
The in-game browser (render process, shared textures, input handling) is based on
[Browsingway](https://github.com/Styr1x/Browsingway) by Styr1x, itself a fork of BrowserHost by ackwell.
This plugin is licensed under the **GNU GPL v3** (see `LICENSE`). The Venuekit website and server are separate and not part of this repository.

Use of third-party tools is at your own risk and not supported by Square Enix.
