# MCP VS2010

This project is a Windows-only MCP server that invokes real Visual Studio 2010 DTE builds.

The system consists of two processes:

- `McpVs2010.Server`: a .NET 10 Streamable HTTP MCP server.
- `McpVs2010.Bridge-<version>.vsix`: a .NET Framework 4.0 VSPackage running inside VS2010 `devenv.exe`.

The components communicate through a per-user named pipe. The VSIX does not detect, install, or validate external plug-ins such as Qt. Builds run in the current VS2010 environment, and plug-in-related failures are reported from VS2010 output.

## Build

Run in PowerShell:

```powershell
.\build.ps1
```

Use `-AllBuildOutputs` with `clean.ps1` to also remove source `bin` and `obj` folders. Use `-WhatIf` to preview deletions.

Current artifacts:

- `artifacts\server-1.0.30\McpVs2010.Server.exe`
- `artifacts\McpVs2010.Bridge-1.0.30.vsix`
- `artifacts\McpVs2010-Deployment-1.0.30.zip`

Run `.\scripts\Test-Artifacts.ps1` to validate the VSIX structure, MCP handshake, and tools.

## VSIX installation

Close every VS2010 instance, extract `McpVs2010-Deployment-Latest.zip`, and run `Install-McpVs2010-Bridge.cmd`. The installer uses the VS2010 `VSIXInstaller.exe`, removes stale user registrations, and installs the server payload.

The target PC requires VS2010 VSIX support and the .NET 10 runtime. Project-specific plug-ins such as Qt are installed and managed separately by the user. `LICENSE` covers the original project code; see `THIRD-PARTY-NOTICES.txt` for dependency licensing information.

Installed server files are placed at:

```text
%LOCALAPPDATA%\McpVs2010\McpVs2010.Server.exe
```

When VS2010 starts, the VSIX automatically starts the server from this location. When VS2010 exits, the server is stopped.

## MCP server configuration

Use `Tools > MCP server` in VS2010. The dialog shows the endpoint and status, provides STOP/START controls, includes a URL copy button, and lets you change the port with Apply. The port is stored as `REG_DWORD` at `HKCU\Software\McpVs2010\HttpStreamPort`; applying a new port restarts a running server.

The default endpoint is `http://127.0.0.1:3010/stream`. The server binds to loopback only.

## Codex connection

```toml
[mcp_servers.vs2010]
url = "http://127.0.0.1:3010/stream"
startup_timeout_sec = 20
tool_timeout_sec = 3600
```

Codex connects to the server automatically started by the VSIX. VS2010 and MCP VS2010 Bridge must be running first.

## MCP tools

- `list_vs2010_instances`: list VS2010 instances with the bridge loaded.
- `list_vs2010_recent_projects`: list recent projects and solution entries.
- `open_vs2010_recent_solution`: open a numbered recent solution.
- `get_vs2010_state`: return the open solution, configuration, projects, and build state.
- `build_vs2010_solution`: run solution-wide `clean`, `build`, or `rebuild`.
- `build_vs2010_project`: run `clean`, `build`, or `rebuild` for one Visual C++ project using **Build > Project Only**.
- `cancel_vs2010_build`: request cancellation of an active DTE build.

## Preserved errors and output

Build results preserve the VS2010 error list, build output panes, project identification, operation scope, and capture diagnostics. The MCP server does not classify failures as missing plug-ins without evidence from VS2010.

## Verification boundary

`build.ps1` verifies server compilation and VSIX packaging. Actual DTE build verification must be performed after installing the VSIX, starting VS2010, and opening the target solution.
