using Wayfarer.Util;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>Preserves the editor validation contract while delegating rich HTML policy to the shared authority.</summary>
internal static class EditorRichNotesRequestHtml
{
    /// <summary>Detects unsupported direct uploads before editor validation accepts the request.</summary>
    public static bool ContainsDataImageSource(string? notesHtml) => RichNotes.ContainsDataImageSource(notesHtml);

    /// <summary>Editor replacement treats absent notes as empty, unlike legacy patch adapters.</summary>
    public static string? NormalizeForPersistence(string? notesHtml) => RichNotes.Normalize(notesHtml) ?? string.Empty;
}
