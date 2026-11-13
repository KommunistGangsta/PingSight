@echo off
title PingSight
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0PingSight.ps1" %*
if errorlevel 1 pause
