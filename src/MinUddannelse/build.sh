#!/bin/bash
# Build script for MinUddannelse following BUILD.md instructions

set -e

echo "Building MinUddannelse following BUILD.md..."

# Navigate to the correct directory
cd src/MinUddannelse

# Run the exact command from BUILD.md
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

echo "Build completed successfully!"
echo "Output: src/MinUddannelse/bin/Release/net9.0/win-x64/publish/MinUddannelse.exe"

# Show the result
ls -la bin/Release/net9.0/win-x64/publish/MinUddannelse.exe
