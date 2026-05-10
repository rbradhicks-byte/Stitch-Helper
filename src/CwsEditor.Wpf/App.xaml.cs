using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace CwsEditor.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        string? cwsPath = FindStartupCwsPath(e.Args);
        MainWindow window = new();
        MainWindow = window;
        if (!string.IsNullOrWhiteSpace(cwsPath))
        {
            window.Loaded += async (_, _) => await window.LoadDocumentFromPathAsync(cwsPath);
        }

        window.Show();
    }

    private static string? FindStartupCwsPath(string[] args)
    {
        string? directPath = args.FirstOrDefault(IsCwsFilePath);
        if (!string.IsNullOrWhiteSpace(directPath))
        {
            return directPath;
        }

        string joinedPath = string.Join(" ", args).Trim().Trim('"');
        return IsCwsFilePath(joinedPath) ? joinedPath : null;
    }

    private static bool IsCwsFilePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetExtension(path), ".cws", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string logPath = TryWriteCrashLog(e.Exception);
        string message =
            "Stitch Helper hit an unhandled error and needs to close." +
            Environment.NewLine +
            Environment.NewLine +
            e.Exception.Message;

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            message += Environment.NewLine +
                       Environment.NewLine +
                       $"Crash details were written to:{Environment.NewLine}{logPath}";
        }

        MessageBox.Show(message, "Stitch Helper", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }

    private static string TryWriteCrashLog(Exception exception)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StitchHelper");
            Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(
                path,
                BuildCrashLog(exception),
                Encoding.UTF8);
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildCrashLog(Exception exception)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Timestamp: {DateTime.Now:O}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($".NET: {Environment.Version}");
        builder.AppendLine();
        builder.AppendLine(exception.ToString());
        return builder.ToString();
    }
}
