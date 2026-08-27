# Emmanim Lag Fix dedicated loader

This is a source fork of
[`radistmorse/CosmoteerModLoader`](https://github.com/radistmorse/CosmoteerModLoader)
at commit `2aee1c7d0175c7c3508435f3eccb5411b103581e`.

The upstream loader is licensed under LGPL-2.1. The original license remains in
`LICENSE.txt`; modified source is distributed with Emmanim Lag Fix.

## Deliberate restrictions

- Only an enabled mod whose exact ID is `nayuri.emmanim_lag_fix` is scanned.
- Only `0Harmony.dll` 2.4.2 and `EmmanimLagFix.Code.dll` are eligible to load.
- DLLs from every other enabled mod are ignored.
- The normal game launch path remains available by uninstalling `winmm.dll` and
  `ModLoader.dll` with the supplied uninstaller.

These restrictions reduce the attack surface and make loader behaviour
predictable for a single optimization package. This fork is not intended to be
a general-purpose C# mod ecosystem.

## Current code patch

`EmmanimLagFix.Code` caches the expensive selected-ship aggregation performed by
the upper-right resource display for one second. Widget animation, fading,
flashing and layout continue to run every frame. Only displayed totals can be
up to one second old.

The ship-to-ship transfer window and station trade tab retain their last full
resource snapshot for at most 200 ms. Their expensive all-resource scans run at
5 Hz instead of once per rendered frame, while each transfer row's own input
handlers remain immediate.

## Build

The projects target `net10.0-windows7.0`, matching Cosmoteer 0.30.4c. Set the
`CosmoteerBin` MSBuild property if the game is installed somewhere other than
the default in `Directory.Build.props`.

Build managed code with a .NET 10 SDK:

```powershell
dotnet build ModLoader.sln -c Release
```

Build the Windows x64 proxy DLL from `CosmoDoorstop`:

```powershell
.\build.ps1 -Arch x64
```
