@echo off
setlocal
rem  Esegue i test automatici del nucleo (server + client su loopback, cartelle temporanee).
cd /d "%~dp0"
dotnet run --project tests\CoreTests\CoreTests.csproj -c Release
endlocal
