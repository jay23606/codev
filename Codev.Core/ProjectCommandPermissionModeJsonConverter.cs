using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codev;

/// <summary>Persists command modes by name and migrates the numeric values used by older builds.</summary>
internal sealed class ProjectCommandPermissionModeJsonConverter(bool settingsUseCurrentNumericValues)
    : JsonConverter<ProjectCommandPermissionMode>
{
    public override ProjectCommandPermissionMode Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<ProjectCommandPermissionMode>(reader.GetString(), ignoreCase: true, out var named) &&
            Enum.IsDefined(named))
            return named;

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            if (settingsUseCurrentNumericValues)
            {
                return number switch
                {
                    0 => ProjectCommandPermissionMode.AskEveryTime,
                    1 => ProjectCommandPermissionMode.Auto,
                    2 => ProjectCommandPermissionMode.Allowlist,
                    3 => ProjectCommandPermissionMode.ReadOnly,
                    _ => ProjectCommandPermissionMode.Auto
                };
            }

            // The registry predates Auto. Numeric 1 and 2 are ambiguous between old saved modes
            // and values written by builds that inserted Auto into the enum. Choose the more
            // restrictive interpretation so an upgrade can never silently grant shell execution.
            return number switch
            {
                0 => ProjectCommandPermissionMode.AskEveryTime,
                1 => ProjectCommandPermissionMode.AskEveryTime,
                2 => ProjectCommandPermissionMode.ReadOnly,
                3 => ProjectCommandPermissionMode.ReadOnly,
                _ => ProjectCommandPermissionMode.AskEveryTime
            };
        }

        throw new JsonException("The saved project command permission mode is invalid.");
    }

    public override void Write(Utf8JsonWriter writer, ProjectCommandPermissionMode value,
        JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("The project command permission mode is invalid.");
        writer.WriteStringValue(value.ToString());
    }
}
