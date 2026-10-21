@echo off
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0cswap.ps1" %*
