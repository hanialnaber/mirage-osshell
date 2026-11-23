#!/usr/bin/env bash
# Build Mirage for Windows from Linux or macOS: tests, exe, installer, portable zip, source zip.
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${VERSION:-1.0.0}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

dotnet test tests/Mirage.Tests -c Release
rm -rf out/publish dist && mkdir -p dist
dotnet publish src/Mirage.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:Version="$VERSION" -o out/publish

(cd installer && makensis -V2 -DVERSION="$VERSION" mirage.nsi)
mv "dist/Mirage-Setup-$VERSION.exe" dist/Mirage-Setup.exe

rm -rf out/portable && mkdir -p out/portable/Mirage
cp out/publish/Mirage.exe LICENSE out/portable/Mirage/
printf 'Mirage portable\r\n\r\nThis file turns on portable mode: settings and notes are kept in the "data" folder next to Mirage.exe.\r\nDelete it to use %%APPDATA%%\\Mirage instead.\r\n' > out/portable/Mirage/portable.txt
(cd out/portable && zip -qr ../../dist/Mirage-Portable.zip Mirage)

git archive --format=zip --prefix=mirage-launcher/ -o dist/Mirage-Source.zip HEAD 2>/dev/null || \
  zip -qr dist/Mirage-Source.zip . -x 'out/*' 'dist/*' '*/bin/*' '*/obj/*' '.git/*' 'video/frames/*'
(cd dist && sha256sum * > SHA256SUMS.txt)
ls -la dist
