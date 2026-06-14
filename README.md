# HomeGlass

HomeGlass is a native Windows 11 smart home app concept inspired by the broad interaction model of Apple's Home app, built with WinUI 3 and the Windows App SDK.

## Current scope

- Native Windows 11 shell with Mica backdrop and NavigationView.
- Dashboard, Rooms, Devices, Scenes, Settings, and About pages.
- Home Assistant browser-based authorization flow from Settings.
- Secure refresh token storage using Windows PasswordVault.
- Authenticated Home Assistant REST API client for status, states, and service calls.
- Rooms page populated from Home Assistant areas through the WebSocket API.

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

## Connect Home Assistant

Open Settings in HomeGlass, enter your Home Assistant server URL, and choose Connect.
HomeGlass opens the Home Assistant sign-in page in your browser, receives the authorization callback on a temporary local listener, and stores the refresh token in Windows PasswordVault.
