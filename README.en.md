# MCP VS2010 Bridge (English)

This project provides a Windows MCP server that invokes real Visual Studio 2010 DTE builds.

## Build

Run `./build.ps1` from PowerShell. The script builds the .NET MCP server, the VS2010 VSIX bridge, and deployment ZIP files under `artifacts`.

Use `./clean.ps1` to remove build artifacts. Add `-AllBuildOutputs` to also remove source `bin` and `obj` folders. Use `-WhatIf` to preview deletions.

## Install

Close every Visual Studio 2010 instance, extract `McpVs2010-Deployment-Latest.zip` into a new folder, and run `Install-McpVs2010-Bridge.cmd`. The installer removes stale user registrations and replaces older user installations automatically.

The package requires VS2010 VSIX support and the .NET 10 runtime. Project-specific plug-ins such as Qt are installed and managed separately by the user.

## MCP server configuration

Use `Tools > MCP server` in VS2010. The dialog shows the server URL and status, provides STOP/START controls, and lets you enter a port and press Apply. Apply stores the port as `REG_DWORD` at `HKCU\\Software\\McpVs2010\\HttpStreamPort` and restarts a running server.

The default endpoint is `http://127.0.0.1:3010/stream`. The URL `copy` button copies the displayed endpoint to the Windows clipboard.
