@echo off
powershell -NoProfile -Command "try { $r=Invoke-RestMethod 'http://127.0.0.1:4096/global/health' -TimeoutSec 2; if($r.healthy){exit 0} else {exit 1} } catch { exit 1 }" >nul 2>&1
if %errorlevel%==0 (
    echo.
    echo OpenCode is ALREADY running at http://127.0.0.1:4096
    echo Voice assistant can connect. Do not start a second server.
    echo.
    pause
    exit /b 0
)
echo.
echo OpenCode is NOT running. Open ZapretUI and click Launch OpenCode button.
echo Or run: taskkill /F /IM opencode.exe  then launch again from ZapretUI.
echo.
pause
