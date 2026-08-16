@echo off
chcp 65001 >nul
setlocal

net session >nul 2>&1
if errorlevel 1 (
  echo このファイルを右クリックし、「管理者として実行」を選んでください。
  pause
  exit /b 1
)

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" ^
  -NoLogo -NoProfile -ExecutionPolicy Bypass ^
  -File "%~dp0Install-OokiGrader-Host.ps1"

set "OOKI_EXIT_CODE=%ERRORLEVEL%"
if not "%OOKI_EXIT_CODE%"=="0" (
  echo.
  echo インストールを完了できませんでした。上に表示されたエラーを保存してください。
  pause
  exit /b %OOKI_EXIT_CODE%
)

echo.
echo Ooki GraderのホストPCセットアップが完了しました。
pause
exit /b 0

