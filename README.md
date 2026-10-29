# Hadaf · هدف

Goals and daily tasks for Windows: deadlines, steps, reminders, streaks. WPF on .NET 10, Arabic and English.

## Build

Small build (needs the .NET 10 Desktop Runtime on the PC), output `release\Hadaf.exe`:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o release
```

Portable build (runs on any Windows 10/11 x64 PC, ~150 MB), output `release\portable\Hadaf.exe`.
Restoring from the local package cache only keeps it offline:

```
dotnet restore -r win-x64 --source %USERPROFILE%\.nuget\packages -p:SelfContained=true -p:RuntimeFrameworkVersion=10.0.9
dotnet publish -c Release -r win-x64 --self-contained true --no-restore -p:RuntimeFrameworkVersion=10.0.9 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o release\portable
```

## Data

`%APPDATA%\Hadaf\data.json`, with hourly backups in `%APPDATA%\Hadaf\backups` (newest 20 kept).
An unreadable data file is never overwritten: it is kept as `data.unreadable-*.json` and the newest good backup is restored.

Settings → Export writes a readable copy to a file of your choice; Import replaces the goals, history and categories
with such a file's, after taking a backup of the current ones (and offers Undo).

## Testing without touching real data

Set `HADAF_DATA_DIR` to a scratch folder. That copy then uses its own data, its own single-instance lock,
and a test registry key instead of the real start-with-Windows entry. Adding `HADAF_TEST_OFFSCREEN=1` opens
the window off-screen and unfocused, for automated runs.
