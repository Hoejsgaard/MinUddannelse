# Windows Deployment Guide

This guide explains how to deploy MinUddannelse to run automatically on Windows 11.

## Build the Application

1. **Build self-contained executable:**
   ```cmd
   cd src\MinUddannelse
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
   ```

This creates a single executable at `src\MinUddannelse\bin\Release\net9.0\win-x64\publish\MinUddannelse.exe` (~85MB) with everything embedded.

## Installation Methods

### Option 1: Task Scheduler (Recommended)

**Best for:** Console applications, automatic restart on failure, easier debugging

**Why Task Scheduler instead of Windows Service?**
- ❌ MinUddannelse is a console application, not designed as a Windows Service
- ✅ Task Scheduler handles console apps better with proper restart behavior
- ✅ Better error handling and logging options
- ✅ Easier to see what's happening and debug issues

**Steps:**
1. Open Task Scheduler (`taskschd.msc`)
2. Click "Create Basic Task"
3. Name: "MinUddannelse"
4. Trigger: "When the computer starts"
5. Action: "Start a program"
6. Program: `C:\path\to\your\src\MinUddannelse\bin\Release\net9.0\win-x64\publish\MinUddannelse.exe`
7. **Important:** In "Actions" tab → "Edit" → Set "Start in" field to: `C:\path\to\your\src\MinUddannelse\bin\Release\net9.0\win-x64\publish`
8. In "Conditions" tab: Uncheck "Start the task only if the computer is on AC power"
9. In "Settings" tab:
   - Check "Run task as soon as possible after a scheduled start is missed"
   - Check "If the running task does not end when requested, force it to stop"
   - If task fails, restart every: 1 minute
   - Attempt to restart up to: 3 times

**Management:**
- Find your task in Task Scheduler Library
- Right-click → "Run" to start manually
- Right-click → "End" to stop
- Check "History" tab for execution logs

### Option 2: Startup Folder (Simple)

**Best for:** Testing, only runs when user logs in

**Steps:**
1. Press `Win + R`, type `shell:startup`
2. Create a batch file `MinUddannelse.bat`:
   ```cmd
   @echo off
   cd /d "C:\path\to\your\src\MinUddannelse\bin\Release\net9.0\win-x64\publish"
   MinUddannelse.exe
   ```
3. Save in the startup folder

## Configuration

The application uses `appsettings.json` embedded in the executable. Make sure your configuration is correct before building:

1. Edit `src/MinUddannelse/appsettings.json` with your settings:
   - Children information (names, credentials)
   - API keys (OpenAI, Supabase, etc.)
   - Channel configuration (Telegram, Slack)
2. Build with embedded config using the command above
3. **Important:** Configuration cannot be changed after building - you must rebuild to update settings

## Troubleshooting

### Task won't start
1. Check Task Scheduler History tab for error details
2. Verify the executable path is correct
3. Ensure "Start in" directory is set correctly
4. Test manually: run the executable directly from Command Prompt

### Application fails during startup
1. Run manually from Command Prompt to see error messages
2. Check Windows Event Log (Windows Logs → Application)
3. Verify configuration is properly embedded (no `appsettings.json` file error)
4. Ensure network connectivity for API calls

### Build fails
1. Ensure .NET 9.0 SDK is installed
2. Check that all dependencies are available
3. Try `dotnet restore` in `src/MinUddannelse/` folder

### Application stops working
1. For Task Scheduler: Check History tab for restart attempts
2. For startup program: Check if user stayed logged in
3. Verify network connectivity and API keys

## Security Notes

- The executable contains embedded configuration including API keys
- Store the executable in a secure location (e.g., `C:\Program Files\MinUddannelse\`)
- Task Scheduler runs with user privileges - consider running as dedicated service account
- Regularly update the application and rebuild

## Updating

1. **Stop the current installation:**
   - Task Scheduler: Right-click task → "End"
   - Startup: Kill process in Task Manager or restart Windows

2. **Replace executable:**
   - Copy new `MinUddannelse.exe` over the old one
   - Configuration is embedded, so no additional files needed

3. **Restart:**
   - Task Scheduler: Right-click task → "Run"
   - Startup: Restart Windows or run manually

## Complete Removal

1. **Task Scheduler:** Delete the "MinUddannelse" task
2. **Startup Folder:** Delete `MinUddannelse.bat` from startup folder
3. **Files:** Delete the `MinUddannelse.exe` and folder