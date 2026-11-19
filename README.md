# islandboiinwindows

A Dynamic Island for Windows, with an Apple Liquid Glass look.

## What it does

- Idle: a slim glass bar at the top of the screen. Clicks pass through around it.
- Hover: it swells so it's easy to grab.
- Drag it left or right along the top edge. It stretches with speed, bounces back, snaps to left / center / right, and remembers where you left it.
- Click: morphs into the expanded card. Click again, or move the mouse away, to collapse.
- Music: while anything plays (Spotify, a browser, Media Player...), the island becomes a compact pill with the album art and a moving waveform. Click it for the full card: art, title, artist, progress, and previous / play-pause / next.
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
