using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ForzavistaFreeRoam;

// Local diagnostics beside the executable. An explicit directory overrides
// this location; logging errors never prevent normal menu operation.
internal static class SessionLog
{
    private static readonly object Gate = new();
    private static string? _filePath;

    internal static string? FilePath => _filePath;
    internal static string? LastError { get; private set; }

    internal static void Start()
    {
        if (Environment.GetEnvironmentVariable("FORZAVISTA_DISABLE_SESSION_LOG") == "1") return;
        var directory = Environment.GetEnvironmentVariable("FORZAVISTA_SESSION_LOG_DIR");
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(AppContext.BaseDirectory, "Diagnostics");
        try
        {
            if (!Path.IsPathFullyQualified(directory))
                throw new InvalidOperationException("Session log directory must be an absolute path.");
            Directory.CreateDirectory(directory);
            _filePath = Path.Combine(directory,
                $"session-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.jsonl");
            Write("app_start", $"Forzavista {typeof(SessionLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}; " +
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
            lock (Gate)
            {
                // Keep the recent tail of unusually long sessions, including
                // cleanup. No per-frame color/material data is logged.
                if (File.Exists(path) && new FileInfo(path).Length >= 8 * 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) { LastError = ex.Message; }
    }
}
