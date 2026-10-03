@echo off
rem 用 ImageMagick 把 icons 目录下的 PNG 打包为两个多尺寸 ICO（浅色 / 深色）
rem 需先运行 convert-icons.bat 生成 PNG
rem magick 不在 PATH 时，把下面的 MAGICK 改成完整路径，例如：
rem set "MAGICK=D:\ImageMagick\magick.exe"

set "MAGICK=magick"
set "OUT=%~dp0"

if not exist "%OUT%\light\icon-256.png" (
    echo 缺少 PNG，请先运行 convert-icons.bat
    pause
    exit /b 1
)

rem 浅色：16 20 24 32 40 48 64 96 128 256
"%MAGICK%" "%OUT%\light\icon-16.png" "%OUT%\light\icon-20.png" "%OUT%\light\icon-24.png" "%OUT%\light\icon-32.png" "%OUT%\light\icon-40.png" "%OUT%\light\icon-48.png" "%OUT%\light\icon-64.png" "%OUT%\light\icon-96.png" "%OUT%\light\icon-128.png" "%OUT%\light\icon-256.png" "%OUT%\icon-light.ico"

rem 深色：16 20 24 32 40 48 64 96 128 256
"%MAGICK%" "%OUT%\dark\icon-16.png" "%OUT%\dark\icon-20.png" "%OUT%\dark\icon-24.png" "%OUT%\dark\icon-32.png" "%OUT%\dark\icon-40.png" "%OUT%\dark\icon-48.png" "%OUT%\dark\icon-64.png" "%OUT%\dark\icon-96.png" "%OUT%\dark\icon-128.png" "%OUT%\dark\icon-256.png" "%OUT%\icon-dark.ico"

echo.
echo 打包完成：
echo   %OUT%\icon-light.ico
echo   %OUT%\icon-dark.ico
pause