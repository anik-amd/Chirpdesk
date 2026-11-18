using System.Text.Json;

namespace Chirpdesk.Core.Integrations;

public sealed record IntegrationInitializeMessage(int ProtocolVersion, string Token, string? PluginVersion, string? AppVersion);

public sealed record IntegrationNotifyMessage(string Title, string? Body, string? Tag, bool Silent, string? ActivateCommand);

public sealed record IntegrationUnreadMessage(int Count);

public sealed record IntegrationStatusMessage(string State, string? Detail);

public sealed record IntegrationLogMessage(string Level, string Message);

public sealed record IntegrationCommandMessage(string Id, string? Argument);

/// <summary>
/// The closed method surface and the payload rules for both directions. Nothing outside these
/// lists is dispatched, and every parameter is length-checked before it reaches app state.
/// </summary>
public static class IntegrationProtocol
{
    public const int Version = 1;
    public const int MaxTitleLength = 64;
    public const int MaxBodyLength = 512;
    public const int MaxTagLength = 64;
    public const int MaxDetailLength = 200;
    public const int MaxLogLength = 500;
    public const int MaxArgumentLength = 500;
    public const int MaxTokenLength = 128;
    public const int MaxVersionLength = 40;
    public const int MaxUnreadCount = 9999;

    private static readonly HashSet<string> HelperMethods = new(StringComparer.Ordinal)
    {
        "initialize", "notify", "unread", "status", "log",
    };

    private static readonly HashSet<string> HostMethods = new(StringComparer.Ordinal)
    {
        "ping", "command", "shutdown",
    };

    private static readonly HashSet<string> StatusStates = new(StringComparer.Ordinal)
    {
        "ready", "busy", "error",
    };

    private static readonly HashSet<string> LogLevels = new(StringComparer.Ordinal)
    {
        "info", "warn", "error",
    };

    /// <summary>A method a helper may call on the host.</summary>
    public static bool IsHelperMethod(string? method) => method is not null && HelperMethods.Contains(method);

    /// <summary>A method the host may call on a helper.</summary>
    public static bool IsHostMethod(string? method) => method is not null && HostMethods.Contains(method);

    /// <summary>The permission a helper method needs, or null when it is always allowed.</summary>
    public static string? RequiredPermission(string? method) => method switch
    {
        "notify" => "notifications",
        "unread" => "unread",
        _ => null,
    };

    public static bool TryReadInitialize(JsonElement parameters, out IntegrationInitializeMessage? message, out string? error)
    {
        message = null;

        if (!TryObject(parameters, out var root, out error)
            || !TryInt(root, "protocolVersion", required: true, out var protocolVersion, out error)
            || !TryString(root, "token", required: true, max: MaxTokenLength, out var token, out error)
            || !TryString(root, "pluginVersion", required: false, max: MaxVersionLength, out var pluginVersion, out error)
            || !TryString(root, "appVersion", required: false, max: MaxVersionLength, out var appVersion, out error))
        {
            return false;
        }

        if (protocolVersion is null)
        {
            error = "missing 'protocolVersion'";
            return false;
        }

        message = new IntegrationInitializeMessage(protocolVersion.Value, token!, pluginVersion, appVersion);
        return true;
    }

    public static bool TryReadNotify(JsonElement parameters, out IntegrationNotifyMessage? message, out string? error)
    {
        message = null;

        if (!TryObject(parameters, out var root, out error)
            || !TryString(root, "title", required: true, max: MaxTitleLength, out var title, out error)
            || !TryString(root, "body", required: false, max: MaxBodyLength, out var body, out error)
            || !TryString(root, "tag", required: false, max: MaxTagLength, out var tag, out error)
            || !TryString(root, "activateCommand", required: false, max: 32, out var activateCommand, out error))
        {
            return false;
        }

        var silent = false;

        if (root.TryGetProperty("silent", out var silentElement))
        {
            if (silentElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = "'silent' must be a boolean";
                return false;
            }

            silent = silentElement.GetBoolean();
        }

        if (activateCommand is not null && !IntegrationManifestCodec.IsValidCommandId(activateCommand))
        {
            error = "'activateCommand' is not a command id";
            return false;
        }

        message = new IntegrationNotifyMessage(title!, body, tag, silent, activateCommand);
        return true;
    }

    public static bool TryReadUnread(JsonElement parameters, out IntegrationUnreadMessage? message, out string? error)
    {
        message = null;

        if (!TryObject(parameters, out var root, out error)
            || !TryInt(root, "count", required: true, out var count, out error))
        {
            return false;
        }

        if (count is null || count < 0 || count > MaxUnreadCount)
        {
            error = $"'count' must be 0..{MaxUnreadCount}";
            return false;
        }

        message = new IntegrationUnreadMessage(count.Value);
        return true;
    }

    public static bool TryReadStatus(JsonElement parameters, out IntegrationStatusMessage? message, out string? error)
    {
        message = null;

        if (!TryObject(parameters, out var root, out error)
            || !TryString(root, "state", required: true, max: 16, out var state, out error)
            || !TryString(root, "detail", required: false, max: MaxDetailLength, out var detail, out error))
        {
            return false;
        }

        if (state is null || !StatusStates.Contains(state))
        {
            error = "'state' must be ready, busy or error";
            return false;
        }

        message = new IntegrationStatusMessage(state, detail);
        return true;
    }

    public static bool TryReadLog(JsonElement parameters, out IntegrationLogMessage? message, out string? error)
    {
        message = null;

        if (!TryObject(parameters, out var root, out error)
            || !TryString(root, "level", required: true, max: 8, out var level, out error)
            || !TryString(root, "message", required: true, max: MaxLogLength, out var text, out error))
        {
            return false;
        }

        if (level is null || !LogLevels.Contains(level))
        {
            error = "'level' must be info, warn or error";
            return false;
        }

        message = new IntegrationLogMessage(level, text!);
        return true;
    }

    public static bool TryReadCommand(JsonElement parameters, out IntegrationCommandMessage? message, out string? error)
    {
        message = null;

        if (!TryObject(parameters, out var root, out error)
            || !TryString(root, "id", required: true, max: 32, out var id, out error)
            || !TryString(root, "argument", required: false, max: MaxArgumentLength, out var argument, out error))
        {
            return false;
        }

        if (id is null || !IntegrationManifestCodec.IsValidCommandId(id))
        {
            error = "'id' is not a command id";
            return false;
        }

        message = new IntegrationCommandMessage(id, argument);
        return true;
    }

    private static bool TryObject(JsonElement element, out JsonElement root, out string? error)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            root = element;
            error = null;
            return true;
        }

        root = default;
        error = "params must be an object";
        return false;
    }

    private static bool TryString(
        JsonElement root,
        string name,
        bool required,
        int max,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;

        if (!root.TryGetProperty(name, out var element))
        {
            if (required)
            {
                error = $"missing '{name}'";
                return false;
            }

            return true;
        }

        if (element.ValueKind == JsonValueKind.Null && !required)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"'{name}' must be a string";
            return false;
        }

        var text = element.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            if (required)
            {
                error = $"'{name}' is empty";
                return false;
            }

            return true;
        }

        if (text.Length > max)
        {
            error = $"'{name}' is longer than {max} characters";
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryInt(JsonElement root, string name, bool required, out int? value, out string? error)
    {
        value = null;
        error = null;

        if (!root.TryGetProperty(name, out var element))
        {
            if (required)
            {
                error = $"missing '{name}'";
                return false;
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number))
        {
            error = $"'{name}' must be an integer";
            return false;
        }

        value = number;
        return true;
    }
}
