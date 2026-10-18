namespace Cark.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Dev tools: run headless, no UI, no hook, no clipboard, allowed next to the tray instance.
        int run = Array.IndexOf(args, "--run");
        if (run >= 0 && run + 2 < args.Length) return HeadlessRun(args[(run + 1)], args[(run + 2)..]);
        int radar = Array.IndexOf(args, "--radar");
        if (radar >= 0 && radar + 1 < args.Length)
        {
            var found = Radar.Scan(args[radar + 1]);
            Log.Write($"radar-result [{string.Join(", ", found)}] file={args[radar + 1]}");
            return 0;
        }

        using var single = new Mutex(true, @"Local\Cark.SingleInstance", out bool first);
        if (!first) return 0;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"ui exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        Log.Write($"start args=[{string.Join(" ", args)}]");
        Application.Run(new CarkContext(args));
        Log.Write("exit");
        return 0;
    }

    /// <summary>--run step,step,... file1 [file2 ...]: runs the steps and logs the result.</summary>
    private static int HeadlessRun(string stepList, string[] files)
    {
        var steps = stepList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            var r = FileActions.Run(files, steps);
            Log.Write($"run-result ok={r.Ok} title={r.Title} detail={r.Detail} output={r.OutputPath ?? "-"} ext={r.OutputExt ?? "-"} clip={(r.ClipboardText is null ? "-" : r.ClipboardText.Replace("\n", "|"))}");
            return r.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Write($"run-result ok=False crashed: {ex}");
            return 1;
        }
    }
}
