@echo off
setlocal
rem ---------------------------------------------------------------
rem  NetSynq - build AUTONOMA (self-contained, singolo exe)
rem  Serve: .NET 8 SDK + connessione internet SOLO per questa build
rem  (scarica una volta i runtime pack da NuGet).
rem  Risultato: dist-standalone\NetSynq.exe (~70-100 MB): copialo sui PC
rem  e parte senza installare nulla.
rem ---------------------------------------------------------------
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo [ERRORE] dotnet non trovato. Installa l'SDK .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0 & exit /b 1)

dotnet --list-sdks | findstr /b "8. 9. 10." >nul || (echo [ERRORE] Trovato solo un SDK .NET vecchio ^(vedi: dotnet --list-sdks^). Serve l'SDK .NET 8 o superiore: >&2 & echo   winget install Microsoft.DotNet.SDK.8   oppure https://dotnet.microsoft.com/download/dotnet/8.0 & echo I vecchi SDK non vengono rimossi: possono convivere. & exit /b 1)

rem Pulisce la cartella di destinazione, anche se contiene file
if exist "%~dp0dist-standalone" (
  echo Cancello la cartella dist-standalone ...
  rmdir /s /q "%~dp0dist-standalone"
  if exist "%~dp0dist-standalone" (echo [ERRORE] impossibile cancellare dist-standalone: chiudi NetSynq.exe e riprova & exit /b 1)
)

dotnet publish src\NetSynq\NetSynq.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -p:DebugSymbols=false -o dist-standalone
if errorlevel 1 (echo. & echo [ERRORE] build fallita & exit /b 1)

echo.
echo Fatto: %~dp0dist-standalone\NetSynq.exe
endlocal
