using System.Text;

namespace SiteWatch;

/// <summary>Журнал в %AppData%\SiteWatch\logs, по файлу на день; старше 30 дней удаляются.</summary>
public static class Logger
{
    private static readonly object Sync = new();

    public static string Folder => Path.Combine(AppConfig.Folder, "logs");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Folder);
                var now = DateTime.Now;
                var file = Path.Combine(Folder, now.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(file, $"{now:HH:mm:ss} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // журнал не должен ронять программу
        }
    }

    public static void Cleanup(int keepDays = 30)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var border = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.GetFiles(Folder, "*.log"))
            {
                if (File.GetLastWriteTime(f) < border) File.Delete(f);
            }
        }
        catch
        {
        }
    }
}
