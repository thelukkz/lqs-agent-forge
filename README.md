# lqs-agent-forge

A personal lab for building and testing AI agents, pipelines, and tools. Each experiment lives in its own numbered folder with a self-contained .NET solution. The goal is not to ship a framework but to iterate fast on real problems and keep every prototype runnable and readable.

---

## Index

| # | Project | Description |
|---|---------|-------------|
| 01 | [Pipeline Note Grounder](#01--pipeline-note-grounder) | Transforms a Markdown note into a self-contained HTML document with hover tooltips linking key concepts to their sources |
| 02 | [Tool Conversation Demo](#02--tool-conversation-demo) | Demonstrates multi-turn conversation with an LLM: streaming responses, conversation history, and usage tracking |

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
