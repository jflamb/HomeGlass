# HomeGlass

HomeGlass is a native Windows 11 smart home app concept inspired by the broad interaction model of Apple's Home app, built with WinUI 3 and the Windows App SDK.

## Current scope

- Native Windows 11 shell with Mica backdrop and NavigationView.
- Dashboard, Rooms, Devices, Scenes, Settings, and About pages.
- Static starter data to establish the information architecture and visual direction.

## Requirements

- Windows 11
- .NET 8 SDK or newer
- Windows App SDK dependencies restored from NuGet.org

## Build

```powershell
dotnet restore
dotnet build HomeGlass.sln -p:Platform=x64
```

## Run

```powershell
dotnet run
```
