# lqs-agent-forge

A personal lab for building and testing AI agents, pipelines, and tools. Each experiment lives in its own numbered folder with a self-contained .NET solution. The goal is not to ship a framework but to iterate fast on real problems and keep every prototype runnable and readable.

---

## Index

| # | Project | Description |
|---|---------|-------------|
| 01 | [Pipeline Note Grounder](#01--pipeline-note-grounder) | Transforms a Markdown note into a self-contained HTML document with hover tooltips linking key concepts to their sources |

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
