@echo off
rem Installiert MailArchy nach %LOCALAPPDATA%\Programs\MailVault (ohne Administratorrechte).
rem Archiv und Einstellungen bleiben bei einer erneuten Installation erhalten.
start "" "%~dp0MailVault.exe" --install
