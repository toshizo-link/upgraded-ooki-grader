namespace OokiGrader.Tool;

internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options;

    private CommandLine(
        string command,
        string? subcommand,
        Dictionary<string, string?> options)
    {
        Command = command;
        Subcommand = subcommand;
        _options = options;
    }

    public string Command { get; }

    public string? Subcommand { get; }

    public bool Json => HasFlag("json");

    public static CommandLine Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0)
        {
            throw new ToolUsageException(
                "command_required",
                "A command is required. Use --help for supported commands.");
        }

        if (arguments.Length == 1
            && arguments[0] is "--help" or "-h" or "help")
        {
            return new CommandLine(
                "help",
                subcommand: null,
                new Dictionary<string, string?>(StringComparer.Ordinal));
        }

        var command = arguments[0];
        var index = 1;
        string? subcommand = null;
        if (command is "backup" or "restore")
        {
            if (arguments.Length <= index
                || (arguments[index].Length > 0
                    && arguments[index][0] == '-'))
            {
                throw new ToolUsageException(
                    "subcommand_required",
                    "The selected command requires a subcommand.");
            }

            subcommand = arguments[index++];
        }

        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (index < arguments.Length)
        {
            var token = arguments[index++];
            if (!token.StartsWith("--", StringComparison.Ordinal)
                || token.Length <= 2)
            {
                throw new ToolUsageException(
                    "option_invalid",
                    "Only named --options are accepted.");
            }

            var name = token[2..];
            if (!KnownOptions.Contains(name))
            {
                throw new ToolUsageException(
                    "option_unknown",
                    "An unsupported option was supplied.");
            }

            if (!options.TryAdd(name, null))
            {
                throw new ToolUsageException(
                    "option_duplicate",
                    "Each option may be supplied only once.");
            }

            if (FlagOptions.Contains(name))
            {
                continue;
            }

            if (index >= arguments.Length
                || arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ToolUsageException(
                    "option_value_required",
                    "A required option value is missing.");
            }

            options[name] = arguments[index++];
        }

        return new CommandLine(command, subcommand, options);
    }

    public bool HasFlag(string name) => _options.ContainsKey(name);

    public string RequireValue(string name)
    {
        if (!_options.TryGetValue(name, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            throw new ToolUsageException(
                "required_option_missing",
                "One or more required options are missing.");
        }

        return value;
    }

    public string? OptionalValue(string name) =>
        _options.TryGetValue(name, out var value)
            ? value
            : null;

    public void AllowOnly(params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        if (_options.Keys.Any(name => !allowedSet.Contains(name)))
        {
            throw new ToolUsageException(
                "option_not_allowed",
                "An option is not valid for the selected command.");
        }
    }

    private static readonly HashSet<string> FlagOptions =
    [
        "json",
        "destination-encryption-confirmed",
        "maintenance-confirmed",
        "offline-confirmed",
    ];

    private static readonly HashSet<string> KnownOptions =
    [
        "json",
        "database",
        "data-root",
        "content-root",
        "destination",
        "destination-encryption-confirmed",
        "backup-id",
        "relative-path",
        "manifest-sha256",
        "maintenance-confirmed",
        "offline-confirmed",
        "confirm-restore",
    ];
}

internal sealed class ToolUsageException(
    string errorCode,
    string safeDetail) : Exception(safeDetail)
{
    public string ErrorCode { get; } = errorCode;

    public string SafeDetail { get; } = safeDetail;
}
