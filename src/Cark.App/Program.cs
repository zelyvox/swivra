namespace Cark.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\Cark.SingleInstance", out bool first);
        if (!first) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"ui exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        Log.Write($"start args=[{string.Join(" ", args)}]");
        Application.Run(new CarkContext(args));
        Log.Write("exit");
    }
}
