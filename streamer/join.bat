@echo off
setlocal enabledelayedexpansion

:: Check if a directory was passed
if "%~1"=="" (
    echo Usage: %~nx0 "path\to\tsfiles"
    exit /b
)

set "WORKDIR=%~1"
set "OUTPUT=%~2"

:: Move into that directory
pushd "%WORKDIR%" || (
    echo Error: Could not enter directory "%WORKDIR%"
    exit /b
)

:: 1. Create a list file for ffmpeg concat
echo Creating file list...
(
  for %%f in (*.ts) do (
    echo file '%%f'
  )
) > file_list.txt

:: 2. Run ffmpeg to combine and convert to mp3
echo Combining .ts files into "%WORKDIR%\%OUTPUT%.mp3" ...
c:/ffmpeg/bin/ffmpeg -f concat -safe 0 -i file_list.txt -c:a libmp3lame -q:a 2 %OUTPUT%.mp3

:: 3. Cleanup
del file_list.txt

echo Done! Output file is "%WORKDIR%\%OUTPUT%.mp3"
pause

:: Go back to original directory
popd
