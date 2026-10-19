namespace SqlDam.Desktop;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        // Enforce single instance of Desktop App
        const string mutexName = "Global\\SqlDamDesktopApp_SingleInstance";
        _mutex = new Mutex(true, mutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Another instance is already running; bring it forward or alert
            MessageBox.Show("SQL DAM Desktop is already running. Check your system tray or taskbar.",
                "SQL DAM Desktop", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());

        GC.KeepAlive(_mutex);
    }
}
