@echo off
REM 重新打包 JavaDeck（无需联网，使用本地缓存）
cd /d "%~dp0"
dotnet publish -c Release --configfile nuget.offline.config -p:DebugType=none -p:SelfContained=false -o dist --nologo
if errorlevel 1 pause
echo.
echo 输出: %~dp0dist\JavaDeck.exe
pause
