using System.Windows;
using System.Windows.Threading;
using AirServerLite.Core;

namespace AirServerLite;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A background thread throwing on this app means a socket died or a decoder hiccuped.
        // Neither is worth taking the process down, but both must be visible in the log.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("app", "Unhandled exception on the UI thread", args.Exception);
            MessageBox.Show(args.Exception.Message +
                            "\n\nDetails were written to:\n" + Log.LogDirectory,
                            "AirServer-LITE", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error("app", "Unhandled exception on a background thread", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            var flattened = args.Exception.Flatten();
            var allAborted = flattened.InnerExceptions.All(ex =>
                ex is OperationCanceledException ||
                ex is System.IO.IOException ioEx && ioEx.Message.Contains("aborted", StringComparison.OrdinalIgnoreCase) ||
                ex is System.Net.Sockets.SocketException sockEx && sockEx.SocketErrorCode == System.Net.Sockets.SocketError.OperationAborted);

            if (allAborted)
            {
                Log.Debug("app", $"Suppressed unobserved aborted socket task: {flattened.InnerException?.Message}");
            }
            else
            {
                Log.Warn("app", "Unobserved task exception", args.Exception);
            }
        };

        // Must run before anything probes FairPlay or FFmpeg: on a single-file build this is
        // what puts the native libraries on disk and on the loader's search path.
        NativeAssets.Initialize();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("app", "Shutting down");
        Log.Shutdown();
        base.OnExit(e);
    }
}
