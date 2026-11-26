@echo off
chcp 65001 >nul
title Build MultiPoint AutoClicker
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK не найден.
  echo Установи .NET 8 SDK для Windows x64:
  echo https://dotnet.microsoft.com/download/dotnet/8.0
  echo Нужен именно SDK, не только Runtime.
  echo После установки снова запусти BUILD_EXE.bat.
  pause
  exit /b 1
)

echo Сборка автономного EXE для Windows x64...
dotnet publish MultiPointAutoClicker.csproj -c Release -r win-x64 --self-contained true ^
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

if errorlevel 1 (
  echo.
  echo Сборка завершилась с ошибкой.
  pause
  exit /b 1
)

echo.
echo ГОТОВО! EXE находится здесь:
echo bin\Release\net8.0-windows\win-x64\publish\MultiPointAutoClicker.exe
start "" "bin\Release\net8.0-windows\win-x64\publish"
pause
