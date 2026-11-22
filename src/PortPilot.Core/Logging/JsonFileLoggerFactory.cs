using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PortPilot.Core.Logging;

/// <summary>
/// A small structured logger that writes one JSON object per line to a daily rolling file.
/// Writing happens on a background task so logging never blocks the UI thread.
/// </summary>
public sealed class JsonFileLoggerFactory : ILoggerFactory
{
    private const int RetainedDays = 7;

    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly ConcurrentDictionary<string, JsonFileLogger> _loggers = new();
    private readonly Task _writer;

    public JsonFileLoggerFactory(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        _writer = Task.Run(WriteLoopAsync);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortPilot", "logs");

    public string CurrentFile => Path.Combine(_directory, $"portpilot-{DateTime.Now:yyyyMMdd}.jsonl");

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new JsonFileLogger(name, this));

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    internal bool IsEnabled(LogLevel level) => level >= _minimumLevel && level != LogLevel.None;

    internal void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            PruneOldFiles();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var builder = new StringBuilder();
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            builder.Clear();
            while (_queue.Reader.TryRead(out var line))
            {
                builder.Append(line).Append('\n');
            }

            try
            {
                await File.AppendAllTextAsync(CurrentFile, builder.ToString()).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Logging must never take the application down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void PruneOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-RetainedDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "portpilot-*.jsonl"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class JsonFileLogger(string category, JsonFileLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            using var stream = new MemoryStream(256);
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("ts", DateTimeOffset.Now);
                json.WriteString("level", logLevel.ToString());
                json.WriteString("category", category);
                json.WriteString("message", formatter(state, exception));

                if (state is IReadOnlyList<KeyValuePair<string, object?>> properties)
                {
                    foreach (var (key, value) in properties)
                    {
                        if (key == "{OriginalFormat}")
                        {
                            continue;
                        }

                        WriteValue(json, key, value);
                    }
                }

                if (exception is not null)
                {
                    json.WriteString("exception", exception.ToString());
                }

                json.WriteEndObject();
            }

            owner.Enqueue(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
        }

        private static void WriteValue(Utf8JsonWriter json, string key, object? value)
        {
            switch (value)
            {
                case null:
                    json.WriteNull(key);
                    break;
                case int or long or short or uint or ushort or byte:
                    json.WriteNumber(key, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case double or float or decimal:
                    json.WriteNumber(key, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case bool b:
                    json.WriteBoolean(key, b);
                    break;
                default:
                    json.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }
    }
}
