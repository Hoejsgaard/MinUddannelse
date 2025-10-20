#!/bin/bash
# MinUddannelse Production Build Script

set -e

echo "Building MinUddannelse self-contained executable..."

cd /mnt/d/git/MinUddannelse/src/MinUddannelse

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

echo "Build complete: /mnt/d/git/MinUddannelse/src/MinUddannelse/bin/Release/net9.0/win-x64/publish/MinUddannelse.exe"