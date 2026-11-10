@echo off
REM 重新打包 JavaDeck（离线可用）
setlocal
cd /d "%~dp0"

echo [1/3] 发布应用...
if exist installer\payload rmdir /s /q installer\payload
dotnet publish -c Release --configfile nuget.offline.config -p:SelfContained=false -p:DebugType=none -o installer\payload --nologo
if errorlevel 1 goto :err

echo [2/3] 打包 payload...
if exist installer\payload.zip del /f /q installer\payload.zip
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path 'installer\payload').Path, (Join-Path (Get-Location) 'installer\payload.zip'), [System.IO.Compression.CompressionLevel]::Optimal, $false)"
if errorlevel 1 goto :err

echo [3/3] 编译安装器...
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WFR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:installer\JavaDeck-Setup-1.1.0.exe ^
  /win32manifest:installer\Launcher\app.manifest ^
  /resource:installer\payload.zip,payload.zip ^
  /reference:"%WFR%" /reference:System.dll /reference:System.Core.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  installer\Launcher\JavaDeckSetup.cs
if errorlevel 1 goto :err

echo.
echo 完成: installer\JavaDeck-Setup-1.1.0.exe
goto :eof

:err
echo 构建失败
pause
exit /b 1
