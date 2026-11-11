# Desk Pal

A Windows wellbeing tray app. It lives in the notification area and nudges you to stretch, drink water and take breaks, with a friendly character on each reminder. See `docs/BRIEF.md` for the product brief.

Milestone 1: tray icon (Test nudge / Exit), native toast with Done / Snooze 5 min / Skip, action log at `%APPDATA%\DeskPal\logs\deskpal.log` (append only).

## Build

Requires the .NET 10 SDK on Windows.

    dotnet build DeskPal.sln -c Release

## Publish (portable folder)

    dotnet publish src/DeskPal -c Release -r win-x64 -o builds/beta-1

The app is unpackaged and self-contained (Windows App SDK included). Run `builds/beta-1/DeskPal.exe`.
