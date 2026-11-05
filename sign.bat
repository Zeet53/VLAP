@echo off
chcp 65001 >nul
cd /d "%~dp0"

set "EXE=%~dp0publish\VLAP.exe"
if not exist "%EXE%" (
    echo === Нет publish\VLAP.exe — сначала запустите build.bat ===
    exit /b 0
)

rem --- signtool: сначала PATH, потом Windows Kits (берём самую свежую версию) ---
set "WINKITS=%ProgramFiles(x86)%\Windows Kits\10\bin"
set "SIGNTOOL="
for /f "delims=" %%P in ('where signtool 2^>nul') do if not defined SIGNTOOL set "SIGNTOOL=%%P"
if not defined SIGNTOOL for /d %%V in ("%WINKITS%\10.*") do if exist "%%V\x64\signtool.exe" set "SIGNTOOL=%%V\x64\signtool.exe"
if not defined SIGNTOOL goto notool

echo === Подпись: publish\VLAP.exe ===
"%SIGNTOOL%" sign /n "VLAP Self-Signed" /fd SHA256 /a "%EXE%"
if errorlevel 1 goto nocert

echo === Публичный сертификат: publish\VLAP.cer ===
powershell -NoProfile -Command "(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq 'CN=VLAP Self-Signed' } | Select-Object -First 1) | Export-Certificate -FilePath 'publish\VLAP.cer' -Type CERT | Out-Null"

echo === Контрольная сумма: publish\VLAP.exe.sha256 ===
powershell -NoProfile -Command "$h = (Get-FileHash -Algorithm SHA256 'publish\VLAP.exe').Hash.ToLower(); [IO.File]::WriteAllText('publish\VLAP.exe.sha256', $h + '  VLAP.exe')"

echo === Проверка подписи ===
powershell -NoProfile -Command "Get-AuthenticodeSignature 'publish\VLAP.exe' | Select-Object Status, StatusMessage | Format-List"
goto end

:notool
echo === signtool.exe не найден (Windows SDK) — подпись пропущена ===
echo     exe останется неподписанным, сборка не считается ошибкой.
goto end

:nocert
echo === Подписать не удалось: нет сертификата "CN=VLAP Self-Signed" ===
echo     Создайте его один раз в PowerShell:
echo     New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=VLAP Self-Signed" -KeyUsage DigitalSignature -KeySpec Signature -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(10) -CertStoreLocation Cert:\CurrentUser\My
echo     exe останется неподписанным, сборка не считается ошибкой.
goto end

:end
exit /b 0
