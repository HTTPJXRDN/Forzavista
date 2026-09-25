using System.IO;
using System.Text;
using System.Text.Json;

namespace ForzavistaFreeRoam;

// Optional crash-investigation log. The caller chooses its location via an
// environment variable; the app never falls back to a profile or C: folder.
internal static class SessionLog
{
    private static readonly object Gate = new();
    private static string? _filePath;

    internal static string? FilePath => _filePath;
    internal static string? LastError { get; private set; }

    internal static void Start()
    {
        var directory = Environment.GetEnvironmentVariable("FORZAVISTA_SESSION_LOG_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            if (!Path.IsPathFullyQualified(directory))
                throw new InvalidOperationException("Session log directory must be an absolute path.");
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory,
                $"session-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.jsonl");
            Write("app_start", $"Forzavista {typeof(SessionLog).Assembly.GetName().Version}; " +
                $"windowsEnabled={NativeCarControl.WindowControlsEnabled}");
        }
        catch (Exception ex)
        {
            _filePath = null;
            LastError = ex.Message;
        }
    }

    internal static void Write(string kind, string? message = null, string? action = null,
        int? gamePid = null, string? carToken = null, ulong? component = null)
    {
        var path = _filePath;
        if (path is null) return;
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow.ToString("O"),
                kind,
                action,
                gamePid,
                carToken,
                component = component.HasValue ? $"0x{component.Value:X}" : null,
                message
            });
            lock (Gate) File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) { LastError = ex.Message; }
    }
}
