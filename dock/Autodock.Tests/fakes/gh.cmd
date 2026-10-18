@echo off
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0gh.ps1" %*
