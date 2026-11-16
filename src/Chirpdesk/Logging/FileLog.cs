using System.IO;
using Chirpdesk.Core.Logging;

namespace Chirpdesk.Logging;

/// <summary>
/// Rolling file sink, hard-capped so the log stays small enough to attach to an issue.
/// Callers must never pass message bodies, contact names or URLs with query strings.
/// </summary>
public sealed class FileLog : ILog
{
    private readonly string _path;
    private readonly long _capBytes;
    private readonly object _gate = new();

    public FileLog(string directory, string fileName, long capBytes = 2 * 1024 * 1024)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, fileName);
        _capBytes = capBytes;
    }

    public void Info(string message) => Write("info", message, null);

    public void Warn(string message) => Write("warn", message, null);

    public void Error(string message, Exception? exception = null) => Write("error", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        lock (_gate)
        {
            try
            {
                var file = new FileInfo(_path);
                if (file.Exists && file.Length > _capBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that throws would be worse than no log.
            }
        }
    }
}
