namespace Codev.Avalonia.ViewModels;

public interface IUserAgentProfileEditorService
{
    Task<IReadOnlyList<Codev.AgentProfileDocument>> GetUserAgentProfileDocumentsAsync(CancellationToken cancellationToken = default);
    Task SaveUserAgentProfileAsync(string fileName, string contents, CancellationToken cancellationToken = default);
}
