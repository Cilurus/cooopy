using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Cooopy.Services;

namespace Cooopy
{
    public partial class App : Application
    {
        private Mutex _mutex;
        private EventWaitHandle _showEvent;
        private Thread _waiter;

        public static DataStore Store { get; private set; }

        private static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cooopy", "cooopy.log");

        private static readonly object LogLock = new();

        public static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                    try
                    {
                        var info = new FileInfo(LogFile);
                        if (info.Exists && info.Length > 1_000_000)
                        {
                            File.Copy(LogFile, LogFile + ".1", true);
                            File.WriteAllText(LogFile, string.Empty);
                        }
                    }
                    catch
                    {
                        // rotation is best effort
                    }

                    File.AppendAllText(LogFile,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // logging must never throw
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log("unhandled domain exception: " + args.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, args) =>
                Log("unobserved task exception: " + args.Exception);
            DispatcherUnhandledException += (_, args) =>
            {
                Log("dispatcher exception: " + args.Exception);
                MessageBox.Show(args.Exception.Message, "cooopy 出现异常", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            try
            {
                Log("=== startup === args=" + string.Join(" ", e.Args));
                StartCore(e.Args);
            }
            catch (Exception ex)
            {
                Log("startup failed: " + ex);
                MessageBox.Show(ex.Message, "cooopy 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        private void StartCore(string[] args)
        {
            var silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));

            _mutex = new Mutex(true, @"Local\cooopy-single-instance", out var createdNew);
            if (!createdNew)
            {
                Log("another instance is running");
                if (!silent)
                {
                    // Wake the existing instance; retry briefly in case it is still starting up.
                    for (var attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            EventWaitHandle.OpenExisting(@"Local\cooopy-show").Set();
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == 4) Log("activate existing failed: " + ex.Message);
                            Thread.Sleep(120);
                        }
                    }
                }

                Shutdown();
                return;
            }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\cooopy-show");
            _waiter = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                    }
                    catch
                    {
                        return;
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (MainWindow is Cooopy.MainWindow window) window.ShowFromExternal();
                    }));
                }
            })
            {
                IsBackground = true,
                Name = "cooopy-single-instance"
            };
            _waiter.Start();

            Store = DataStore.Load();
            Log($"data loaded: {Store.Data.Records.Count} records, {Store.Data.Tags.Count} tags, " +
                $"error='{Store.LastLoadError}'");

            var main = new Cooopy.MainWindow(Store);
            MainWindow = main;
            main.InitializeApp(show: !silent && !Store.Data.Settings.StartMinimized);
            Log("window initialized");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("=== exit " + e.ApplicationExitCode + " ===");
            try
            {
                Store?.Shutdown();
            }
            catch
            {
                // ignored
            }

            _mutex?.Dispose();
            _showEvent?.Dispose();
            base.OnExit(e);
        }
    }
}
