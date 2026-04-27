using System.Text.Json.Serialization;

namespace PipelineNoteGrounder;

// ── Extract ──────────────────────────────────────────────────────────────────

record Concept
{
    [JsonPropertyName("label")]        public required string Label { get; init; }
    [JsonPropertyName("category")]     public required string Category { get; init; }
    [JsonPropertyName("needsSearch")]  public bool NeedsSearch { get; init; }
    [JsonPropertyName("searchQuery")]  public string? SearchQuery { get; init; }
    [JsonPropertyName("reason")]       public string? Reason { get; init; }
    [JsonPropertyName("surfaceForms")] public required List<string> SurfaceForms { get; init; }
}

record ParagraphResult
{
    [JsonPropertyName("index")]    public int Index { get; init; }
    [JsonPropertyName("hash")]     public required string Hash { get; init; }
    [JsonPropertyName("text")]     public required string Text { get; init; }
    [JsonPropertyName("type")]     public required string Type { get; init; }
    [JsonPropertyName("concepts")] public required List<Concept> Concepts { get; init; }
}

record ExtractResult
{
    [JsonPropertyName("sourceFile")]     public required string SourceFile { get; init; }
    [JsonPropertyName("model")]          public required string Model { get; init; }
    [JsonPropertyName("sourceHash")]     public required string SourceHash { get; init; }
    [JsonPropertyName("conceptsHash")]   public required string ConceptsHash { get; init; }
    [JsonPropertyName("paragraphCount")] public int ParagraphCount { get; init; }
    [JsonPropertyName("conceptCount")]   public int ConceptCount { get; init; }
    [JsonPropertyName("paragraphs")]     public required List<ParagraphResult> Paragraphs { get; init; }
}

// ── Dedupe ───────────────────────────────────────────────────────────────────

record ConceptGroup
{
    [JsonPropertyName("canonical")]  public required string Canonical { get; init; }
    [JsonPropertyName("ids")]        public required List<int> Ids { get; init; }
    [JsonPropertyName("aliases")]    public required List<string> Aliases { get; init; }
    [JsonPropertyName("rationale")]  public string? Rationale { get; init; }
}

record DedupeResult
{
    [JsonPropertyName("sourceFile")]  public required string SourceFile { get; init; }
    [JsonPropertyName("dedupeHash")]  public required string DedupeHash { get; init; }
    [JsonPropertyName("groups")]      public required List<ConceptGroup> Groups { get; init; }
}

// ── Search ───────────────────────────────────────────────────────────────────

record SearchSource
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("url")]   public required string Url { get; init; }
}

record SearchEntry
{
    [JsonPropertyName("canonical")]  public required string Canonical { get; init; }
    [JsonPropertyName("summary")]    public required string Summary { get; init; }
    [JsonPropertyName("keyPoints")]  public required List<string> KeyPoints { get; init; }
    [JsonPropertyName("sources")]    public required List<SearchSource> Sources { get; init; }
}

record SearchResult
{
    [JsonPropertyName("sourceFile")]        public required string SourceFile { get; init; }
    [JsonPropertyName("model")]             public required string Model { get; init; }
    [JsonPropertyName("resultsByCanonical")] public required Dictionary<string, SearchEntry> ResultsByCanonical { get; init; }
}

// ── Ground ───────────────────────────────────────────────────────────────────

record GroundingItem
{
    public required string Canonical { get; init; }
    public required List<string> SurfaceForms { get; init; }
    public required string Summary { get; init; }
    public required List<SearchSource> Sources { get; init; }
}
