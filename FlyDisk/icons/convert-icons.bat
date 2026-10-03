@echo off
rem �� Inkscape �� 6 �� SVG ת��ͼ�� PNG
rem Сͼ�꣺16/20/24/32   ��ͼ�꣺40/48/64   ��ͼ�꣺96/128/256
rem �����icons\light\icon-<�ߴ�>.png��ǳɫ����icons\dark\icon-<�ߴ�>.png����ɫ��

set "INKSCAPE=D:\InkscapePortable\App\Inkscape\bin\inkscape.com"
set "SRC=%~dp0"
set "OUT=%~dp0"

if not exist "%OUT%\light" mkdir "%OUT%\light"
if not exist "%OUT%\dark" mkdir "%OUT%\dark"

rem Сͼ��
for %%S in (16 20 24 32) do (
    "%INKSCAPE%" "%SRC%\Сͼ��-ǳ.svg" -w %%S -h %%S -o "%OUT%\light\icon-%%S.png"
    "%INKSCAPE%" "%SRC%\Сͼ��-��.svg" -w %%S -h %%S -o "%OUT%\dark\icon-%%S.png"
)

rem ��ͼ��
for %%S in (40 48 64) do (
    "%INKSCAPE%" "%SRC%\��ͼ��-ǳ.svg" -w %%S -h %%S -o "%OUT%\light\icon-%%S.png"
    "%INKSCAPE%" "%SRC%\��ͼ��-��.svg" -w %%S -h %%S -o "%OUT%\dark\icon-%%S.png"
)

rem ��ͼ��
for %%S in (96 128 256) do (
    "%INKSCAPE%" "%SRC%\��ͼ��-ǳ.svg" -w %%S -h %%S -o "%OUT%\light\icon-%%S.png"
    "%INKSCAPE%" "%SRC%\��ͼ��-��.svg" -w %%S -h %%S -o "%OUT%\dark\icon-%%S.png"
)

echo.
echo ת����ɣ����Ŀ¼��%OUT%
pause