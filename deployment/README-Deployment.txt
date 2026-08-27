MCP VS2010 Bridge 1.0.18 deployment package

Close Visual Studio 2010, then run Install-McpVs2010-Bridge.cmd.
The installer removes old user installations and stale registrations before installing the new version.
Visual Studio 2010 VSIX support and the .NET 10 runtime are required.

After installation, use Tools > MCP server for URL, status, STOP/START, Port and Apply.
The port is stored at HKCU\Software\McpVs2010\HttpStreamPort (REG_DWORD).
Project-specific plug-ins such as Qt are installed and managed separately by the user.
