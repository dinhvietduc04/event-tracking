@echo off
pwsh -NoLogo -NoProfile -File "%~dp0scripts\Migrate-Database.ps1" %*
exit /b %errorlevel%
