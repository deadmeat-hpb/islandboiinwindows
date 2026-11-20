# islandboiinwindows

A Dynamic Island for Windows, with an Apple Liquid Glass look.

## What it does

### The island
- Idle: a slim glass bar hanging from the top edge, lit softly from below (the song's colour while music plays). Clicks pass through around it. With nothing playing it shows the time and date.
- Hover: it swells so it's easy to grab.
- Drag it left or right along the top edge. It stretches with speed, bounces back, snaps to left / center / right, and remembers where you left it.
- Clicks step through stages: bar → pill → card. Right-click goes back a stage. Moving the mouse away collapses a pill to the bar; a card you opened stays until you close it.
- Every pill and card is floating glass with a soft inner light in its source's colour (album, app, notice). Text is set in Inter (bundled, SIL Open Font License).
- Fullscreen games, videos and presentations: the island stays the idle bar.

### Music
- Pill: album art and a moving waveform. With nothing playing, the pill is a clock (time left, date right).
- Card: art, title, artist, controls, and a progress bar in the album colour with a light running along it. Click or drag the bar to seek. Only a click on the album art brings the playing app forward.
- Synced lyrics from [LRCLIB](https://lrclib.net) (it receives the title and artist) slide in beside the title as a three-line list that scrolls with the song; the previous and next lines are faded.
- Minimize the window that's playing: a drop of light flows from its taskbar button into the bar, which opens into the pill.

### Notifications and notices
- New Windows notifications drop down as a pill for 5 seconds (hover keeps them). Click to open the card, click the card to open the app, right-click to dismiss; they slide up into the edge when they leave. Windows asks once for notification access; turn off "Show notification banners" in Settings > System > Notifications to avoid doubles.
- Tailored designs: messages (sender + bubble), mail (sender, subject, preview), calendar (time + countdown), calls, browser push (site), screenshots (the snip as a thumbnail), Logitech G HUB DPI, and a generic card.
- Built-in Windows apps get their own glyph badge in the app's colour: Clock alarms and timers ring (ripples and a shaking bell, the time or "Time's up" large, up to 30 seconds), Windows Security (green Protected / red Action needed), Windows Update (Restart required), Microsoft Store (Installed / Updated), Game Bar clips and screenshots, Phone Link messages and calls, Microsoft To Do reminders as calendar cards, and Windows system toasts: USB drives (AutoPlay), low disk space, printing, Bluetooth pairing, OneDrive, Windows Backup, new startup apps, audio and Wi-Fi. Snipping Tool screen recordings get the recording design. Badge cards size themselves to their text.
- `DynamicIsland.exe --preview <folder>` renders every notification design to PNG and exits (the island hides itself from screenshots). Windows only gives the island a notification's text and app icon, so images inside toasts, toast buttons and inline replies aren't available.
- Volume (card: slider, mute, switch output device), brightness (laptop panels), charging / battery low, Bluetooth connected (with battery when reported) and Wi-Fi connected show a pill; click it for its card.
- Stacking: while a notification or notice is up, the music pill sits below it. Click either one to open its card in place.

## Run

Needs Windows 10 2004+ (Windows 11 recommended) and the .NET 8 SDK or newer. Visual Studio is optional: the project builds with the dotnet CLI (`EnableMsixTooling`).

```powershell
cd src/DynamicIsland
dotnet build -c Release -p:Platform=x64 -r win-x64
```

The app is self-contained for the Windows App SDK; run `bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\DynamicIsland.exe`. It registers itself to start at sign-in and to restart after a crash. Quit it from Task Manager.

Unsigned builds are blocked while Windows Smart App Control is on.

## How the glass works

The app captures the monitor (excluding its own window), then redraws the area behind the island through a set of "lens" rings that bend the background more strongly toward the edge, plus blur, saturation and a bright rim. See `Glass.cs`.
