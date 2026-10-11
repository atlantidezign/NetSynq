@echo off
setlocal
rem ---------------------------------------------------------------
rem  NetSynq - build LEGGERA (framework-dependent)
rem  Serve: .NET 8 SDK sul PC dove compili.
rem  Risultato: dist\NetSynq.exe (~200 KB). Sui PC che lo eseguono serve
rem  il ".NET Desktop Runtime 8" (gia' incluso se ci hai installato l'SDK).
rem  Non scarica nulla: funziona anche offline.
rem ---------------------------------------------------------------
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo [ERRORE] dotnet non trovato. Installa l'SDK .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0 & exit /b 1)

dotnet --list-sdks | findstr /b "8. 9. 10." >nul || (echo [ERRORE] Trovato solo un SDK .NET vecchio ^(vedi: dotnet --list-sdks^). Serve l'SDK .NET 8 o superiore: >&2 & echo   winget install Microsoft.DotNet.SDK.8   oppure https://dotnet.microsoft.com/download/dotnet/8.0 & echo I vecchi SDK non vengono rimossi: possono convivere. & exit /b 1)

rem Pulisce la cartella di destinazione, anche se contiene file
if exist "%~dp0dist" (
  echo Cancello la cartella dist ...
  rmdir /s /q "%~dp0dist"
  if exist "%~dp0dist" (echo [ERRORE] impossibile cancellare dist: chiudi NetSynq.exe e riprova & exit /b 1)
)

dotnet publish src\NetSynq\NetSynq.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o dist
if errorlevel 1 (echo. & echo [ERRORE] build fallita & exit /b 1)

echo.
echo Fatto: %~dp0dist\NetSynq.exe
endlocal
