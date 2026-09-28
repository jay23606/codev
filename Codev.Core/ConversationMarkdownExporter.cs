using System.IO;
using System.Text;

namespace Codev;

public static class ConversationMarkdownExporter
{
    public static string Export(Conversation conversation)
    {
        var output = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(conversation.Title) ? "New conversation" : conversation.Title.Trim();
        output.Append("# ").AppendLine(title).AppendLine();
        output.Append("- **Model:** ").AppendLine(conversation.Model);
        output.Append("- **Updated:** ").AppendLine(conversation.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm zzz"));
        if (!string.IsNullOrWhiteSpace(conversation.ProjectPath))
            output.Append("- **Project:** ").AppendLine(Path.GetFileName(conversation.ProjectPath));
        if (conversation.ContextFiles.Count > 0)
        {
            output.AppendLine("- **Context files:**");
            foreach (var file in conversation.ContextFiles) output.Append("  - `").Append(file.Replace('\\', '/').Replace("`", "\\`", StringComparison.Ordinal)).AppendLine("`");
        }

        output.AppendLine().AppendLine("---").AppendLine();
        foreach (var message in conversation.Messages)
        {
            output.Append("## ").AppendLine(message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "You" : "Codev").AppendLine();
            output.AppendLine(message.Content).AppendLine();
        }

        if (conversation.FileChanges.Count > 0)
        {
            output.AppendLine("---").AppendLine().AppendLine("## Reviewed file changes").AppendLine();
            foreach (var change in conversation.FileChanges.OrderBy(c => c.ChangedAt))
                output.Append("- **").Append(change.Kind).Append("** `").Append(change.RelativePath.Replace('\\', '/').Replace("`", "\\`", StringComparison.Ordinal)).Append("` · ").AppendLine(change.ChangedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        }

        return output.ToString();
    }
}
