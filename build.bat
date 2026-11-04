@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo === Закрываю запущенный exe из publish ===
powershell -NoProfile -Command "Get-Process VLAP -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 800" >nul 2>&1

echo === Сборка и публикация ===
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 goto fail

echo.
echo === Готово: %~dp0publish\VLAP.exe ===
goto end

:fail
echo.
echo === ОШИБКА СБОРКИ ===

:end
pause
