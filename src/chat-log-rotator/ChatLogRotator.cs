using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;
using Umpk.Commands;

/// <summary>The rotation period that decides which file receives a line.</summary>
public enum RotationMode
{
    Day,
    Week,
    Month,
}

/// <summary>The message filter modes, spelled as the config spells them.</summary>
public enum MessageFilter
{
    All,
    Messages,
    Chat,
    PrivateChat,
    InternalMsg,
}

/// <summary>What one filter mode saves.</summary>
public readonly record struct FilterSelection(bool Chat, bool Private, bool Other, bool Internal);

/// <summary>Pure helpers for rotation buckets, file names, filters and line formatting.</summary>
public static class RotatorFormat
{
    public const string DefaultDirectory = "../../chat-log/data";
    public const string DefaultFile = "chatlog-%username%-%serverip%-%period%.txt";
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Parses a rotation period; an unknown value returns false.</summary>
    public static bool TryParseRotation(string? value, out RotationMode mode)
    {
        mode = RotationMode.Day;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "day":
                mode = RotationMode.Day;
                return true;
            case "week":
                mode = RotationMode.Week;
                return true;
            case "month":
                mode = RotationMode.Month;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Renders a rotation period as the config spells it.</summary>
    public static string RotationName(RotationMode mode) => mode switch
    {
        RotationMode.Week => "week",
        RotationMode.Month => "month",
        _ => "day",
    };

    /// <summary>Parses a filter mode; an unknown value returns false.</summary>
    public static bool TryParseFilter(string? value, out MessageFilter filter)
    {
        filter = MessageFilter.Messages;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "all":
                filter = MessageFilter.All;
                return true;
            case "messages":
                filter = MessageFilter.Messages;
                return true;
            case "chat":
                filter = MessageFilter.Chat;
                return true;
            case "private_chat":
                filter = MessageFilter.PrivateChat;
                return true;
            case "internal_msg":
                filter = MessageFilter.InternalMsg;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Renders a filter mode as the config spells it.</summary>
    public static string FilterName(MessageFilter filter) => filter switch
    {
        MessageFilter.All => "all",
        MessageFilter.Chat => "chat",
        MessageFilter.PrivateChat => "private_chat",
        MessageFilter.InternalMsg => "internal_msg",
        _ => "messages",
    };

    /// <summary>Which message kinds each mode saves.</summary>
    public static FilterSelection Select(MessageFilter filter) => filter switch
    {
        MessageFilter.All => new FilterSelection(Chat: true, Private: true, Other: true, Internal: true),
        MessageFilter.Chat => new FilterSelection(Chat: true, Private: false, Other: false, Internal: true),
        MessageFilter.PrivateChat => new FilterSelection(Chat: false, Private: true, Other: false, Internal: true),
        MessageFilter.InternalMsg => new FilterSelection(Chat: false, Private: false, Other: false, Internal: true),
        _ => new FilterSelection(Chat: true, Private: true, Other: false, Internal: true),
    };

    /// <summary>Builds the log line for one classified message, or null when the filter drops it.</summary>
    public static string? BuildLine(FilterSelection selection, ChatKind kind, string? sender, string body, string raw)
    {
        if (selection.Chat && kind == ChatKind.Chat)
            return string.Create(CultureInfo.InvariantCulture, $"Chat {sender ?? string.Empty}: {body}");

        if (selection.Private && kind == ChatKind.PrivateMessage)
            return string.Create(CultureInfo.InvariantCulture, $"Private {sender ?? string.Empty}: {body}");

        if (selection.Other)
            return string.Create(CultureInfo.InvariantCulture, $"Other: {raw}");

        return null;
    }

    /// <summary>Prepends the timestamp when requested.</summary>
    public static string ApplyTimestamp(string line, bool addDateTime, DateTime now)
        => addDateTime
            ? string.Concat(now.ToString(TimestampFormat, CultureInfo.InvariantCulture), " ", line)
            : line;

    /// <summary>Computes the file bucket for the rotation period. Week uses the ISO 8601 week (Monday start).</summary>
    public static string Bucket(RotationMode mode, DateTime now) => mode switch
    {
        RotationMode.Week => string.Create(
            CultureInfo.InvariantCulture,
            $"{ISOWeek.GetYear(now)}-W{ISOWeek.GetWeekOfYear(now):00}"),
        RotationMode.Month => now.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Expands %period%, %username% and %serverip%, then replaces the characters a file system will not take.
    /// The period is inserted on every write, so a line can never land in the wrong bucket because a timer fired late.
    /// </summary>
    public static string ExpandFileName(string template, string username, string serverIp, string period)
    {
        string expanded = template
            .Replace("%period%", period, StringComparison.OrdinalIgnoreCase)
            .Replace("%username%", username, StringComparison.OrdinalIgnoreCase)
            .Replace("%serverip%", serverIp, StringComparison.OrdinalIgnoreCase);

        var builder = new StringBuilder(expanded.Length);
        foreach (char c in expanded)
            builder.Append(IsFileNameSafe(c) ? c : '_');

        string name = builder.ToString().Trim();
        return name.Length == 0 ? "chatlog.txt" : name;
    }

    /// <summary>Ensures the file name template contains %period%, adding it before the extension when absent.</summary>
    public static string NormalizeTemplate(string? template)
    {
        string value = string.IsNullOrWhiteSpace(template) ? DefaultFile : template.Trim();
        if (value.Contains("%period%", StringComparison.OrdinalIgnoreCase))
            return value;

        int dot = value.LastIndexOf('.');
        int separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        return dot > separator && dot > 0
            ? value[..dot] + "-%period%" + value[dot..]
            : value + "-%period%";
    }

    private static bool IsFileNameSafe(char c)
        => !char.IsControl(c)
            && c is not (':' or '*' or '?' or '"' or '<' or '>' or '|' or '/' or '\\');
}

/// <summary>The persisted settings for ChatLogRotator.</summary>
public sealed class ChatLogRotatorSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>Whether to prepend a yyyy-MM-dd HH:mm:ss timestamp to every line.</summary>
    [TomlInlineComment("$settings.add_datetime$")]
    public bool Add_DateTime { get; set; } = true;

    /// <summary>The rotation period: day, week or month.</summary>
    [TomlInlineComment("$settings.rotation$")]
    public string Rotation { get; set; } = "day";

    /// <summary>The log directory. The default keeps the original chat-log folder.</summary>
    [TomlInlineComment("$settings.log_directory$")]
    public string Log_Directory { get; set; } = RotatorFormat.DefaultDirectory;

    /// <summary>The file name template, resolved inside the log directory.</summary>
    [TomlInlineComment("$settings.log_file$")]
    public string Log_File { get; set; } = RotatorFormat.DefaultFile;

    /// <summary>The filter mode: all, messages, chat, private_chat or internal_msg.</summary>
    [TomlInlineComment("$settings.filter$")]
    public string Filter { get; set; } = "messages";

    /// <summary>Normalizes on load or reload.</summary>
    public void Validate()
    {
        Rotation = RotatorFormat.RotationName(
            RotatorFormat.TryParseRotation(Rotation, out RotationMode parsedRotation) ? parsedRotation : RotationMode.Day);
        Filter = RotatorFormat.FilterName(
            RotatorFormat.TryParseFilter(Filter, out MessageFilter parsedFilter) ? parsedFilter : MessageFilter.Messages);
        Log_Directory = string.IsNullOrWhiteSpace(Log_Directory) ? RotatorFormat.DefaultDirectory : Log_Directory.Trim();
        Log_File = RotatorFormat.NormalizeTemplate(Log_File);
    }
}

/// <summary>
/// Chat log with per-day, per-ISO-week or per-month files. It keeps the chat-log feature set
/// (filters, timestamp, %username%/%serverip% placeholders) and adds a rotation period that
/// the <c>/chat-log-rotator period</c> command changes and persists.
/// </summary>
public sealed class ChatLogRotatorPlugin : IPlugin
{
    private readonly object _gate = new();
    private PluginContext _context = null!;
    private ChatLogRotatorSettings _settings = new();
    private FilterSelection _selection = RotatorFormat.Select(MessageFilter.Messages);
    private RotationMode _rotation = RotationMode.Day;
    private string _logDirectory = string.Empty;

    /// <inheritdoc />
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "chat-log-rotator";
        descriptor.Version = "1.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<ChatLogRotatorSettings>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _settings = context.Settings.Load<ChatLogRotatorSettings>();

        MessageFilter filter = RotatorFormat.TryParseFilter(_settings.Filter, out MessageFilter parsedFilter)
            ? parsedFilter
            : MessageFilter.Messages;
        _selection = RotatorFormat.Select(filter);
        _rotation = RotatorFormat.TryParseRotation(_settings.Rotation, out RotationMode parsedRotation)
            ? parsedRotation
            : RotationMode.Day;
        _logDirectory = ResolveDirectory(context);

        if (_settings.Enabled && filter == MessageFilter.InternalMsg)
            context.Logger.LogWarning("{Message}", context.Strings.Get("internal_unavailable"));

        context.Commands.Register(new RotatorCommand(context.Strings, ShowStatus, SetRotation, SetFilter, SetTimestamp));

        // Re-subscribe on every session, so a reconnect keeps logging.
        context.SessionStarted += (_, e) =>
            e.Session.Events.Subscribe<ChatMessageReceived>(message => OnChat(message));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken ct) => Task.CompletedTask;

    private void OnChat(ChatMessageReceived message)
    {
        bool enabled;
        bool addDateTime;
        string logFile;
        FilterSelection selection;
        RotationMode rotation;
        string directory;
        lock (_gate)
        {
            enabled = _settings.Enabled;
            addDateTime = _settings.Add_DateTime;
            logFile = _settings.Log_File;
            selection = _selection;
            rotation = _rotation;
            directory = _logDirectory;
        }

        if (!enabled)
            return;

        ClassifiedChat classified = _context.Chat.Classify(message);
        string? line = RotatorFormat.BuildLine(selection, classified.Kind, classified.Sender, classified.Body, classified.Raw);
        if (line is null)
            return;

        DateTime now = DateTime.Now;
        string text = RotatorFormat.ApplyTimestamp(line, addDateTime, now);
        string name = RotatorFormat.ExpandFileName(logFile, CurrentUsername(), CurrentServerLabel(), RotatorFormat.Bucket(rotation, now));
        Save(directory, name, text);
    }

    private void Save(string directory, string name, string text)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            lock (_gate)
                File.AppendAllText(path, text + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _context.Logger.LogWarning(ex, "{Message}", _context.Strings.Format("write_failed", name));
        }
    }

    private int SetRotation(string value, CommandContext call)
    {
        if (!RotatorFormat.TryParseRotation(value, out RotationMode rotation))
            return call.Result.Fail(_context.Strings.Format("command.period_invalid", value));

        lock (_gate)
        {
            _rotation = rotation;
            _settings.Rotation = RotatorFormat.RotationName(rotation);
            _context.Settings.Save(_settings);
        }

        DateTime now = DateTime.Now;
        string bucket = RotatorFormat.Bucket(rotation, now);
        string name = RotatorFormat.ExpandFileName(_settings.Log_File, CurrentUsername(), CurrentServerLabel(), bucket);
        return call.Result.Ok(_context.Strings.Format(
            "command.period_set",
            RotatorFormat.RotationName(rotation),
            Path.Combine(_logDirectory, name)));
    }

    private int SetFilter(string value, CommandContext call)
    {
        if (!RotatorFormat.TryParseFilter(value, out MessageFilter filter))
            return call.Result.Fail(_context.Strings.Format("command.filter_invalid", value));

        lock (_gate)
        {
            _selection = RotatorFormat.Select(filter);
            _settings.Filter = RotatorFormat.FilterName(filter);
            _context.Settings.Save(_settings);
        }

        if (filter == MessageFilter.InternalMsg)
            _context.Logger.LogWarning("{Message}", _context.Strings.Get("internal_unavailable"));

        return call.Result.Ok(_context.Strings.Format("command.filter_set", RotatorFormat.FilterName(filter)));
    }

    private int SetTimestamp(bool enabled, CommandContext call)
    {
        lock (_gate)
        {
            _settings.Add_DateTime = enabled;
            _context.Settings.Save(_settings);
        }

        return call.Result.Ok(_context.Strings.Format("command.timestamp_set", enabled ? "on" : "off"));
    }

    private string ShowStatus()
    {
        string logFile;
        string directory;
        RotationMode rotation;
        MessageFilter filter;
        bool addDateTime;
        lock (_gate)
        {
            logFile = _settings.Log_File;
            directory = _logDirectory;
            rotation = _rotation;
            addDateTime = _settings.Add_DateTime;
            filter = RotatorFormat.TryParseFilter(_settings.Filter, out MessageFilter parsedFilter)
                ? parsedFilter
                : MessageFilter.Messages;
        }

        DateTime now = DateTime.Now;
        string bucket = RotatorFormat.Bucket(rotation, now);
        string name = RotatorFormat.ExpandFileName(logFile, CurrentUsername(), CurrentServerLabel(), bucket);
        return _context.Strings.Format(
            "command.status",
            RotatorFormat.RotationName(rotation),
            RotatorFormat.FilterName(filter),
            addDateTime ? "on" : "off",
            directory,
            bucket,
            name);
    }

    private string CurrentUsername()
    {
        string? username = _context.CurrentSession?.State.Self.Username;
        return string.IsNullOrWhiteSpace(username) ? "username" : username;
    }

    private string CurrentServerLabel()
    {
        if (_context.Client.Configuration is { } config && !string.IsNullOrWhiteSpace(config.ResolvedHost))
            return string.Create(CultureInfo.InvariantCulture, $"{config.ResolvedHost}-{config.ResolvedPort}");
        return "server";
    }

    private string ResolveDirectory(PluginContext context)
    {
        string configured = _settings.Log_Directory;
        if (string.IsNullOrWhiteSpace(configured))
            configured = RotatorFormat.DefaultDirectory;

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(context.Storage.DataDirectory, configured));
    }

    /// <summary>The command surface: status, and the period setter the whole rotation feature hangs on.</summary>
    private sealed class RotatorCommand(
        IPluginLocalization strings,
        Func<string> status,
        Func<string, CommandContext, int> setRotation,
        Func<string, CommandContext, int> setFilter,
        Func<bool, CommandContext, int> setTimestamp)
        : CommandBase
    {
        public override string CmdName => "chat-log-rotator";

        public override string CmdDesc => strings.Get("command.desc");

        public override string CmdUsage =>
            "chat-log-rotator period <day|week|month> | filter <all|messages|chat|private_chat|internal_msg> | timestamp <on|off>";

        public override IReadOnlyList<string> Examples =>
        [
            "chat-log-rotator status",
            "chat-log-rotator period week",
            "chat-log-rotator filter private_chat",
            "chat-log-rotator timestamp off",
        ];

        public override void Register(CommandBuilder<CommandContext> builder)
        {
            CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
            builder.Literal(CmdName, command => command
                .Executes(ctx => ctx.Source.Result.Ok(status()))
                .ThenLiteral("status", node => node
                    .Executes(ctx => ctx.Source.Result.Ok(status())))
                .ThenLiteral("period", node => node
                    .ThenArgument("value", Arguments.Word(), value => value
                        .Executes(ctx => setRotation(ctx.GetArgument<string>("value"), ctx.Source))))
                .ThenLiteral("filter", node => node
                    .ThenLiteral("all", value => value.Executes(ctx => setFilter("all", ctx.Source)))
                    .ThenLiteral("messages", value => value.Executes(ctx => setFilter("messages", ctx.Source)))
                    .ThenLiteral("chat", value => value.Executes(ctx => setFilter("chat", ctx.Source)))
                    .ThenLiteral("private_chat", value => value.Executes(ctx => setFilter("private_chat", ctx.Source)))
                    .ThenLiteral("internal_msg", value => value.Executes(ctx => setFilter("internal_msg", ctx.Source))))
                .ThenLiteral("timestamp", node => node
                    .ThenLiteral("on", value => value.Executes(ctx => setTimestamp(true, ctx.Source)))
                    .ThenLiteral("off", value => value.Executes(ctx => setTimestamp(false, ctx.Source))))
                .ThenLiteral("_help", node => node
                    .Executes(ctx => ShowUsage(ctx.Source))
                    .RedirectTo(help)));
        }
    }
}
