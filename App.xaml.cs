using System.Windows;

namespace ForzavistaFreeRoam;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        SessionLog.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            SessionLog.Write("app_unhandled_exception", args.ExceptionObject.ToString());
        DispatcherUnhandledException += (_, args) =>
            SessionLog.Write("ui_unhandled_exception", args.Exception.ToString());
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SessionLog.Write("app_exit", $"exitCode={e.ApplicationExitCode}");
        base.OnExit(e);
    }
}
