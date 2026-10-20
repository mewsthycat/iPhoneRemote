@echo off
rem Double-click to start iPhone Remote. Pass -CreateShortcut to add a Desktop shortcut.
start "" /min powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0scripts\launch.ps1" %*
