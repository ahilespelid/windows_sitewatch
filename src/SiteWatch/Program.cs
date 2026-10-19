namespace SiteWatch;

internal static class Program
{
    /// <summary>С этим ключом программу запускает автозапуск Windows — сразу в трей, без окна.</summary>
    public const string TrayArg = "--tray";

    [STAThread]
    private static void Main(string[] args)
    {
        // Сигнал «покажи окно» для уже запущенной копии.
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SiteWatch.Show");
        using var mutex = new Mutex(true, @"Local\SiteWatch.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // Программа уже работает — просто показываем её окно.
            showSignal.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Logger.Error("Ошибка интерфейса", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Error("Необработанная ошибка", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Ошибка фоновой задачи", e.Exception);
            e.SetObserved();
        };

        Logger.Cleanup();
        var startHidden = args.Any(a => string.Equals(a, TrayArg, StringComparison.OrdinalIgnoreCase));
        Logger.Info($"Запуск SiteWatch {Application.ProductVersion}{(startHidden ? " (автозапуск, в трей)" : "")}");
        Application.Run(new TrayContext(startHidden, showSignal));
        Logger.Info("Выход");
    }
}
