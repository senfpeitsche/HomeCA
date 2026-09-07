using Markdig;

namespace HomeCA.Service.Localization;

/// <summary>Centralizes the deliberately HTML-free Markdown policy for in-app help.</summary>
public static class SafeMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string Render(string markdown) => Markdown.ToHtml(markdown, Pipeline);
}
