# islandboiinwindows

A Dynamic Island for Windows, with an Apple Liquid Glass look.

## What it does

### The island
- Idle: a slim glass bar hanging from the top edge, lit softly from below (the song's colour while music plays). Clicks pass through around it. It always shows the time and date (while music plays, the bar is lit in the album colour); when it opens into a pill, the time drops into the pill and fades as the pill's content rises in.
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
- Full-screen captures (Win+PrtScn) saved to PicturesScreenshots get the screenshot island too (one island per capture; click it to open the image).
- Tailored designs: messages (sender + bubble), mail (sender, subject, preview), calendar (time + countdown), calls, browser push (site), screenshots (the snip as a thumbnail), Logitech G HUB DPI, and a generic card.
- Built-in Windows apps get their own glyph badge in the app's colour: Clock alarms and timers (still halo rings, the time or "Time's up" large, held up to 30 seconds), Windows Security (green Protected / red Action needed), Windows Update (Restart required), Microsoft Store (Installed / Updated), Game Bar clips and screenshots, Phone Link messages and calls, Microsoft To Do reminders as calendar cards, and Windows system toasts: USB drives (AutoPlay), low disk space, printing, Bluetooth pairing, OneDrive, Windows Backup, new startup apps, audio and Wi-Fi. Snipping Tool screen recordings get the recording design. Badge cards size themselves to their text.
- `DynamicIsland.exe --preview <folder>` renders every notification design to PNG and exits (the island hides itself from screenshots). Windows only gives the island a notification's text and app icon, so images inside toasts, toast buttons and inline replies aren't available.
- Volume (card: slider, mute, switch output device), brightness (laptop panels), charging / unplugged / battery low, energy saver on / off, Bluetooth connected and Wi-Fi connected drop down as a badge pill (battery capsule, device battery, signal bars); click it for its card with the value large and a settings button.
- Microphone and camera: while an app uses them, a still dot sits at the end of the bar (orange for the microphone, green for the camera, as on the iPhone), and the app that starts gets a short pill; click it for the privacy page. It reads the same registry record as the taskbar's microphone icon and only wakes when that changes.
- Calls: while Zoom, Teams, Google Meet (in a browser) or Discord holds the microphone, the island rests as a call pill with the app and the call's length (one redraw a second). Click it to bring the call back; right-click hides it until the next call.
- Downloads: a file finishing in Downloads (a browser renaming its .crdownload / .part file, or a file saved there) gets a pill with its name; click it to see the file selected in its folder.
- Focus sessions starting and ending (Windows 11 24H2 and later), and airplane mode on or off.
- Caps Lock on / off, and Copied: what you copy as one line (file names for files; password-like text only as its length; copies that password managers mark private are never shown).
- Stacking: while a notification or notice is up, the music pill sits below it. Click either one to open its card in place.

## Settings

Dynamic Island lives in the taskbar's hidden icons (the ^ arrow). Click the icon, or open the app again while it runs, to open Settings; right-click it for Settings, Hide island and Quit. Settings is a normal Windows 11 window with five pages, and every change saves at once (%LOCALAPPDATA%\DynamicIslandData\settings.json) and applies live:

- **General**: start with Windows or only when opened, show or hide the island, quit, reset all settings.
- **Island behaviour**: switch each island on or off (music, notifications, screenshots, volume and brightness, battery, Bluetooth, Wi-Fi, microphone and camera, Caps Lock, Copied, calls, downloads, Focus and airplane mode); clock or slim resting bar; notification time (3, 5 or 8 s); keep the slim bar or hide in full-screen apps; the drop animation from the music player or Clock; Clock timers on or off.
- **Appearance**: liquid glass or a solid dark island, size (small, default, large), 24- or 12-hour clock, date on or off, re-centre the island.
- **Power and performance**: Quality, Balanced or Battery saver (solid island, 15 fps waveform); Animations Full, Reduced (no wobble, stretch, drip or drop; still waveform, pill redraws once a second) or Off (everything appears in place); pausing the glass on battery.
- **About**: version, the crash log folder, the source.

## Run

Needs Windows 10 2004+ (Windows 11 recommended) and the .NET 8 SDK or newer. Visual Studio is optional: the project builds with the dotnet CLI (`EnableMsixTooling`).

```powershell
cd src/DynamicIsland
dotnet build -c Release -p:Platform=x64 -r win-x64
```

The app is self-contained for the Windows App SDK; run `bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\DynamicIsland.exe`. It starts at sign-in (unless switched off in Settings) and restarts after a crash. Quit it from the tray icon. On the laptop a scheduled task "DynamicIsland Restart" also relaunches it when Windows logs a crash (Application Error 1000), which covers crashes in the first minute; errors are logged to %LOCALAPPDATA%DynamicIslandDataash.log.

Unsigned builds are blocked while Windows Smart App Control is on.

## How the glass works

The app captures the monitor (excluding its own window), then redraws the area behind the island through a set of "lens" rings that bend the background more strongly toward the edge, plus blur, saturation and a bright rim. See `Glass.cs`.
