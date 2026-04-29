using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace AgentMcpCore.Server;

[McpServerPromptType]
public class McpPrompts
{
    [McpServerPrompt(Name = "code-review")]
    [Description("Generates a structured code review prompt")]
    public static GetPromptResult CodeReview(
        [Description("Code to review")] string code,
        [Description("Programming language, e.g. csharp, python, javascript")] string language = "unknown",
        [Description("Focus area: security, performance, readability, or all")] string focus = "all")
    {
        var focusInstruction = focus switch
        {
            "security"    => "Focus especially on security vulnerabilities, injection risks, and unsafe patterns.",
            "performance" => "Focus especially on performance bottlenecks, allocations, and optimization opportunities.",
            "readability" => "Focus especially on naming, clarity, cohesion, and maintainability.",
            _             => "Cover all aspects: security, performance, and readability."
        };

        var promptText =
            $"Please review the following {language} code.\n" +
            $"{focusInstruction}\n\n" +
            $"```{language}\n{code}\n```";

        return new GetPromptResult
        {
            Messages =
            [
                new PromptMessage
                {
                    Role    = Role.User,
                    Content = new TextContentBlock { Text = promptText }
                }
            ]
        };
    }
}
