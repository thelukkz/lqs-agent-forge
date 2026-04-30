# lqs-agent-forge

A personal lab for building and testing AI agents, pipelines, and tools. Each experiment lives in its own numbered folder with a self-contained .NET solution. The goal is not to ship a framework but to iterate fast on real problems and keep every prototype runnable and readable.

---

## Index

| # | Project | Description |
|---|---------|-------------|
| 01 | [Pipeline Note Grounder](#01--pipeline-note-grounder) | Transforms a Markdown note into a self-contained HTML document with hover tooltips linking key concepts to their sources |
| 02 | [Tool Conversation Demo](#02--tool-conversation-demo) | Demonstrates multi-turn conversation with an LLM: streaming responses, conversation history, and usage tracking |
| 03 | [Pipeline Structured Extractor](#03--pipeline-structured-extractor) | Extracts structured person data (name, age, occupation, skills) from plain text using JSON Schema structured outputs |
| 04 | [Agent Sandbox Filesystem](#04--agent-sandbox-filesystem) | Agent that performs sandboxed file operations via tool use (function calling) with path traversal protection |
| 05 | [Agent MCP Core](#05--agent-mcp-core) | Demonstrates all core MCP capabilities (tools, resources, prompts, sampling, elicitation) via stdio transport |
| 06 | [Agent MCP Translator](#06--agent-mcp-translator) | Autonomous Polish-to-English file translator: watches a directory, uses MCP file tools and the Responses API agentic loop to translate each file |
| 07 | [Agent MCP Uploader](#07--agent-mcp-uploader) | Agent orchestrating two local MCP servers (stdio + HTTP) to upload files from a source workspace to a local vault, demonstrating multi-server routing and placeholder resolution |

---

## Projects

### 01 — Pipeline Note Grounder

**Folder:** `01-pipeline-note-grounder`

Takes a Markdown note as input and produces a grounded HTML document where key concepts are annotated with source references.

**What it does:**

1. **Extract** — splits the note into paragraphs and asks an LLM to identify concepts worth verifying: claims, results, methods, definitions, metrics, entities, and references.
2. **Dedupe** — groups semantically equivalent concepts from across paragraphs into canonical entries, reducing redundant lookups.
3. **Search** — for each canonical concept, asks an LLM to produce a summary and structured source references (title, URL).
4. **Ground** — rewrites each paragraph as HTML, wrapping concept mentions in annotated spans with embedded grounding data (summary + sources).

**How it does it:**

The pipeline runs each stage with parallel batch processing controlled by a configurable concurrency limit and an optional per-request delay to handle rate-limited APIs. Every stage caches its output to disk as JSON, keyed by a SHA-256 hash of the inputs, so re-runs skip completed work automatically. Structured outputs are requested via JSON Schema response format, which keeps LLM responses machine-readable without post-processing hacks.

The implementation uses the official OpenAI .NET SDK against the Chat Completions API, making it compatible with OpenAI, OpenRouter, and LM Studio out of the box. Grounding data is injected into HTML attributes programmatically after the LLM response, avoiding encoding issues. The output is a self-contained HTML file with built-in CSS and JavaScript that renders source tooltips on hover — no extra files needed.

**Why:**

Reading notes full of claims and references is useful only if you can trust or trace them. This pipeline automates the grounding step: it surfaces what in a note is verifiable, finds candidate sources, and embeds that context directly into the output so a reader can follow up without manual searching.

**Run:**

```bash
cd 01-pipeline-note-grounder/PipelineNoteGrounder
dotnet run -- path/to/notes.md [--force] [--batch=N]
```

| Flag | Description |
|------|-------------|
| `--force` | Ignore cached results and reprocess all stages from scratch |
| `--batch=N` | Override the concurrency limit (1–10) for this run, e.g. `--batch=1` for sequential processing |

Output is written to an `output/` folder next to the input file. If a `template.html` file exists in the same folder as the input, it is used instead of the built-in template — it must contain a `<!--CONTENT-->` placeholder.

Configuration in `appsettings.json` — provider, model, batch size, retries, and request delay are all adjustable without recompiling.

---

### 02 — Tool Conversation Demo

**Folder:** `02-tool-conversation-demo`

A minimal demonstration of a two-turn conversation with an LLM: asks a math question, then asks a follow-up that requires the previous answer. Streams the response token by token and reports reasoning token usage after each reply.

**What it teaches:**

1. **Multi-turn conversation** — the model has no memory between calls. Every request must include the full history of prior exchanges for the model to maintain context. The pattern is: append user message → call API → append assistant reply → repeat.
2. **Streaming** — `CompleteChatStreamingAsync` yields partial updates as the model generates them. Each update carries a content delta written to the console immediately. The full text is accumulated in a `StringBuilder` for the history entry. Usage metadata arrives in the final update.
3. **Reasoning token tracking** — some models perform internal chain-of-thought reasoning before producing output. These reasoning tokens are invisible in the response text but counted in `usage.output_tokens_details.reasoning_tokens`. This demo reads that field from the last streaming chunk and appends it to each reply.
4. **Output format control** — without instruction, models format responses in markdown or LaTeX, which renders as noise in a terminal. A system message at the start of the history tells the model to respond in plain text.
5. **Custom endpoint configuration** — the OpenAI .NET SDK targets OpenAI by default. Pointing it at a compatible provider (e.g. OpenRouter) requires only setting `OpenAIClientOptions.Endpoint` — no other code changes.

**Run:**

```bash
cd 02-tool-conversation-demo/ToolConversationDemo
dotnet run
```

Configuration in `appsettings.json` — API base URL and model are adjustable without recompiling.

---

### 03 — Pipeline Structured Extractor

**Folder:** `03-pipeline-structured-extractor`

Extracts structured person data (name, age, occupation, skills) from plain text using JSON Schema structured outputs.

**What it teaches:**

1. **Structured outputs** — passing a JSON Schema to the model via `ChatResponseFormat.CreateJsonSchemaFormat` with `strict: true` guarantees the response matches the schema exactly, eliminating the need to validate or defensively parse the model's reply.
2. **Nullable fields in schema** — fields that may not appear in the source text (name, age, occupation) are declared as `["string", "null"]` or `["number", "null"]`, allowing the model to return `null` rather than hallucinate a value.
3. **Typed deserialization** — the JSON response is deserialized directly into a C# record (`PersonInfo`) using `JsonSerializer`, giving compile-time safety and clean property access instead of dynamic `JsonNode` traversal.

**Run:**

```bash
cd 03-pipeline-structured-extractor/PipelineStructuredExtractor
dotnet run
```

Configuration in `appsettings.json` — API base URL and model are adjustable without recompiling.

---

### 04 — Agent Sandbox Filesystem

**Folder:** `04-agent-sandbox-filesystem`

An agent that uses function calling to perform file operations (list, read, write, delete, create directory, get metadata) within an isolated sandbox directory. Includes a deliberate security test: attempting to escape the sandbox via `../` is blocked.

**What it teaches:**

1. **Tool use (function calling)** — tools are defined as JSON Schema objects passed to the model via `ChatCompletionOptions.Tools`. The model decides which tools to call and with what arguments; the application executes them and returns results. This loop continues until the model produces a final text response.
2. **Agentic loop** — after each API call, check `FinishReason`. If it is `ToolCalls`, append the assistant message (including tool call metadata), execute each tool, append a `ToolChatMessage` per result, and call the API again. Repeat until `Stop` or a round limit is reached.
3. **AssistantChatMessage with tool calls** — when adding the assistant's tool-calling turn to the history, construct it from the full `ChatCompletion` object so the tool call IDs and arguments are preserved. The subsequent `ToolChatMessage` entries reference these IDs to correlate results.
4. **Sandbox path validation** — resolving user-supplied paths with `Path.GetFullPath` and checking that the result starts with the sandbox root prefix is the standard defense against path traversal attacks (`../`, absolute paths, symlink escapes).
5. **Returning structured tool output** — tool handlers serialize their results as JSON objects (success/error/data fields) so the model can reliably interpret them without free-text parsing.

**Run:**

```bash
cd 04-agent-sandbox-filesystem/AgentSandboxFilesystem
dotnet run
```

The sandbox is created as a `sandbox/` folder next to the executable. Configuration in `appsettings.json` — API base URL and model are adjustable without recompiling.

---

### 05 — Agent MCP Core

**Folder:** `05-agent-mcp-core`

A complete tour of the Model Context Protocol (MCP) in a single .NET project. The same executable runs as either an MCP server (spawned as a subprocess) or an MCP client — a switch determined by the `--server` command-line flag.

**What it demonstrates:**

1. **Tools** — Two tools registered on the server: `calculate` (basic arithmetic) and `summarize_with_confirmation` (a multi-step tool that chains elicitation and sampling before returning a result). The attribute-based model (`[McpServerToolType]` / `[McpServerTool]`) generates the JSON schema automatically from method signatures and `[Description]` attributes.
2. **Resources** — Two resources accessible by URI: `config://project` (static JSON metadata) and `data://stats` (dynamic uptime and request count). The `[McpServerResourceType]` / `[McpServerResource]` attributes handle registration and URI routing.
3. **Prompts** — A `code-review` prompt template that accepts parameters (`code`, `language`, `focus`) and returns a `GetPromptResult` with a pre-filled user message. The client retrieves it with `GetPromptAsync` and gets typed `PromptMessage` objects back.
4. **Elicitation** — The server initiates a form request to the client via `McpServer.ElicitAsync`. The client handles it with an `ElicitationHandler` that auto-fills defaults from the `RequestedSchema` and returns `ElicitResult { Action = "accept" }`. This lets the server collect structured input without blocking on user interaction.
5. **Sampling** — The server requests an LLM completion from the client via `McpServer.SampleAsync`. The client's `SamplingHandler` translates `CreateMessageRequestParams` into an OpenAI `ChatClient` call and returns a `CreateMessageResult`. The server never needs an API key — all AI calls go through the client.
6. **Stdio transport** — The client spawns itself as a subprocess (`Process.GetCurrentProcess().MainModule.FileName --server`) and communicates over stdin/stdout using `StdioClientTransport`. The server suppresses console logging to keep the stdio channel clean for JSON-RPC.

**Run:**

```bash
cd 05-agent-mcp-core/AgentMcpCore
dotnet run
```

Configuration in `appsettings.json` — API base URL and model are adjustable without recompiling. By default it targets OpenRouter with `openai/gpt-4o-mini`.

---

### 06 — Agent MCP Translator

**Folder:** `06-agent-mcp-translator`

An autonomous translation agent that watches a directory for Polish files and translates them to English using an agentic loop over the OpenAI Responses API. File operations (read, write, list, mkdir) are handled by a local MCP file server that the agent spawns as a subprocess and communicates with over stdio.

**What it teaches:**

1. **Responses API agentic loop** — unlike the Chat Completions API, the Responses API tracks state via `InputItems` and `OutputItems` lists rather than `messages`. The loop appends all output items (including `FunctionCallResponseItem`) to the next request's inputs, then appends `FunctionCallOutputResponseItem` for each tool result. The cycle repeats until the model returns no more tool calls.
2. **MCP client with stdio transport** — `McpClient.CreateAsync(StdioClientTransport)` spawns a subprocess (the same executable with `--server`) and establishes a JSON-RPC channel over stdin/stdout. `ListToolsAsync()` returns `McpClientTool` objects whose `JsonSchema` property directly feeds `ResponseTool.CreateFunctionTool()` — no manual schema translation needed.
3. **Self-spawning MCP server** — the executable detects `--server` on the command line and switches into MCP server mode using `Host.CreateApplicationBuilder` + `AddMcpServer().WithStdioServerTransport().WithTools<T>()`. The parent passes `FS_ROOT` via environment variable to scope all file operations to the workspace directory.
4. **Sandboxed file tools** — `FileSystemTools` resolves every path through `Path.GetFullPath(Path.Combine(fsRoot, …))` and verifies the result starts with `fsRoot` before touching the filesystem, blocking path traversal attacks.
5. **Chunked translation strategy** — the system prompt instructs the model to check line count first, then translate in 80-line chunks for large files (read → translate → write/append), keeping each API call within token limits.
6. **HTTP translation endpoint** — `HttpListener` exposes `POST /api/translate` for on-demand text translation without the file watching loop.

**Run:**

```bash
cd 06-agent-mcp-translator/AgentMcpTranslator
dotnet run
```

Drop any `.md`, `.txt`, `.html`, or `.json` file into `workspace/translate/` — the agent detects it within 5 seconds and writes the English version to `workspace/translated/`. The example file `protokol-mcp.md` is included to test immediately.

On-demand translation via HTTP:

```bash
curl -s -X POST http://localhost:3000/api/translate \
  -H "Content-Type: application/json" \
  -d '{"text": "Witaj świecie! To jest przykładowy tekst po polsku."}' | jq
```

Configuration in `appsettings.json` — model, API base URL, source/target directories, poll interval, and HTTP port are all adjustable without recompiling.

---

### 07 — Agent MCP Uploader

**Folder:** `07-agent-mcp-uploader`

An agent that orchestrates two local MCP servers simultaneously to upload files from a source workspace to a local vault. The same executable runs in three modes: as the `files` MCP server, as the `vault` MCP server, or as the agent that spawns and connects to both.

**What it teaches:**

1. **Multi-server MCP orchestration** — a single agent maintains two independent `McpClient` connections at the same time: one to the `files` server (reads source files) and one to the `vault` server (stores files). Each server runs as a subprocess spawned via `StdioClientTransport`. This is the core step beyond single-server MCP: the agent operates across independent capability domains simultaneously.

2. **Tool name prefixing as a routing mechanism** — tools from both servers are exposed to the LLM under prefixed names: `files__fs_list`, `files__fs_read`, `vault__vault_store`, `vault__vault_list`. When the model calls a tool, the agent strips the prefix to identify the target server and the actual tool name. This pattern scales to any number of servers without schema collisions.

3. **Placeholder resolution** — instead of calling `files__fs_read` to get base64 content and passing it to `vault__vault_store`, the model writes `{{file:hello.md}}` in the `base64` argument. Before the MCP call is dispatched, `PlaceholderResolver` replaces every `{{file:path}}` occurrence with the actual base64-encoded file content read directly from disk. The model never handles raw bytes; the conversation history stays small.

4. **Dual-purpose MCP server process** — the vault subprocess runs two things concurrently: a stdio MCP server (JSON-RPC over stdin/stdout, for tool calls from the agent) and an `HttpListener` HTTP server (for serving stored files at `http://localhost:5001/files/{name}`). Both are started from the same process with no framework overhead.

**Run:**

```bash
cd 07-agent-mcp-uploader/AgentMcpUploader
dotnet run
```

Drop additional files into `workspace/source/` before running — the agent will upload everything not yet in the vault. Uploaded files land in `workspace/vault/` and are accessible at `http://localhost:5001/files/{filename}` for the duration of the run.

Configuration in `appsettings.json` — model, API base URL, workspace paths, and vault HTTP port are adjustable without recompiling.
