# Hello, MCP!

This is a sample Markdown file used to demonstrate the **MCP Uploader** agent.

The agent reads files from this `source/` workspace using the `files` MCP server,
then stores them in the `vault/` workspace using the `vault` MCP server.

File content is transferred via a `{{file:path}}` placeholder — the agent never
reads raw bytes itself. The system resolves the placeholder to base64 before
calling the vault tool.
