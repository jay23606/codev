using Avalonia.Input;

namespace Codev.Avalonia;

internal static class PlatformKeyboardShortcuts
{
    public static string PrimaryModifierLabel => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";

    public static bool HasPrimaryModifier(KeyModifiers modifiers) => OperatingSystem.IsMacOS()
        ? modifiers.HasFlag(KeyModifiers.Meta)
        : modifiers.HasFlag(KeyModifiers.Control);
}
