# islandboiinwindows

A Dynamic Island for Windows, with an Apple Liquid Glass look.

## What it does

- Idle: a slim glass bar at the top of the screen. Clicks pass through around it.
- Hover: it swells so it's easy to grab.
- Drag it left or right along the top edge. It stretches with speed, bounces back, snaps to left / center / right, and remembers where you left it.
- Click: morphs into the expanded card. Click again, or move the mouse away, to collapse.
- Music: while anything plays (Spotify, a browser, Media Player...), the island becomes a compact pill with the album art and a moving waveform. Click it for the full card: art, title, artist, progress, and previous / play-pause / next. The card also shows the current lyric line above the progress bar when [LRCLIB](https://lrclib.net) has synced lyrics for the song (it receives the title and artist).
- Notifications: new Windows notifications drop down from the island for 5 seconds (hover to keep one open, click to open the app). Windows asks once for notification access. To stop Windows' own pop-ups doubling up, turn off "Show notification banners" in Settings > System > Notifications.
- Volume, brightness (laptop screens) and plugging in the charger show a slim pop-up with an icon and level bar.
- Tailored notices: battery low (20/10/5%), Bluetooth device connected (with its battery when the device reports it), Wi-Fi connected, and charging, as a slim pill with an icon, text and a level ring. Click a notice to open its Settings page (battery, Bluetooth, Wi-Fi).
- Clicks step through: bar → now-playing pill → full card → opens the playing app. Moving the mouse away collapses it.
- Right-click: quit.

## Run

Needs Windows 10 2004+ (Windows 11 recommended), the .NET 8 SDK or newer, and Visual Studio 2022 with the ".NET desktop" and "Windows App SDK" workloads.

```powershell
cd src/DynamicIsland
dotnet run
```

Or open `src/DynamicIsland/DynamicIsland.csproj` in Visual Studio, pick x64, and press F5.

## How the glass works

The app captures the monitor (excluding its own window), then redraws the area behind the island through a set of "lens" rings that bend the background more strongly toward the edge, plus blur, saturation and a bright rim. See `Glass.cs`.
