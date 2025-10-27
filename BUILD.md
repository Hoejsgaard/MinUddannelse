# MinUddannelse Build Instructions

## Self-Contained Production Release

**ALWAYS use this exact command for production builds:**

```bash
cd /mnt/d/git/MinUddannelse/src/MinUddannelse
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

**Output location:** `/mnt/d/git/MinUddannelse/src/MinUddannelse/bin/Release/net9.0/win-x64/publish/MinUddannelse.exe`

## Build Parameters Explained

- `-c Release` - Release configuration (optimized)
- `-r win-x64` - Target Windows x64 platform
- `--self-contained true` - Include .NET runtime (no .NET installation required)
- `-p:PublishSingleFile=true` - Single executable file
- **DO NOT USE** `-p:PublishTrimmed=true` - Breaks dynamic types and JSON serialization

## Development Build

For development/testing:

```bash
cd /mnt/d/git/MinUddannelse/src/MinUddannelse
dotnet build
dotnet run
```

## Verification

After build, verify the executable exists:

```bash
ls -la /mnt/d/git/MinUddannelse/src/MinUddannelse/bin/Release/net9.0/win-x64/publish/MinUddannelse.exe
```

## Deployment

**For production deployment:**

Copy the executable to the production directory. That's it.

```bash
cp /mnt/d/git/MinUddannelse/src/MinUddannelse/bin/Release/net9.0/win-x64/publish/MinUddannelse.exe /path/to/production/
```

**Production deployment structure:**
```
production-directory/
└── MinUddannelse.exe
```

## Notes

- Always build from the project directory (`/mnt/d/git/MinUddannelse/src/MinUddannelse`)
- The self-contained executable is ~84MB because it includes the .NET runtime
- Configuration files are embedded in the executable - no external config files needed
- No .NET installation required on target machine