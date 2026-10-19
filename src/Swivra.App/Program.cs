namespace Swivra.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        int lang = Array.IndexOf(args, "--lang");
        if (lang >= 0 && lang + 1 < args.Length) Lang.Override(args[lang + 1]);

        // Dev tools: run headless, no UI, no hook, no clipboard, allowed next to the tray instance.
        int run = Array.IndexOf(args, "--run");
        if (run >= 0 && run + 2 < args.Length)
        {
            var rest = args[(run + 2)..].ToList();
            string? password = null;
            int pw = rest.IndexOf("--password");
            if (pw >= 0 && pw + 1 < rest.Count)
            {
                password = rest[pw + 1];
                rest.RemoveRange(pw, 2);
            }
            bool withUi = rest.Remove("--with-ui"); // also run the UI-thread part (clipboard file list)
            Rectangle? crop = null;
            int cr = rest.IndexOf("--crop");
            if (cr >= 0 && cr + 1 < rest.Count)
            {
                int[] v = rest[cr + 1].Split(',').Select(int.Parse).ToArray();
                crop = new Rectangle(v[0], v[1], v[2], v[3]);
                rest.RemoveRange(cr, 2);
            }
            return HeadlessRun(args[run + 1], rest.ToArray(), new StepOptions(crop, password), withUi);
        }
        int radar = Array.IndexOf(args, "--radar");
        if (radar >= 0 && radar + 1 < args.Length)
        {
            var found = Radar.Scan(args[radar + 1]);
            Log.Write($"radar-result [{string.Join(", ", found)}] file={args[radar + 1]}");
            return 0;
        }

        // Demo runs are dev tools too: they may start next to the tray instance.
        bool demo = args.Contains("--demo") || args.Contains("--demo-tool");
        using var single = new Mutex(true, demo ? @"Local\Swivra.Demo" : @"Local\Swivra.SingleInstance", out bool first);
        if (!first) return 0;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"ui exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        Log.Write($"start args=[{string.Join(" ", args)}]");
        Application.Run(new SwivraContext(args));
        Log.Write("exit");
        return 0;
    }

    /// <summary>--run step,step,... [--password pw] [--crop x,y,w,h] file1 [file2 ...]: runs the steps and logs the result.</summary>
    private static int HeadlessRun(string stepList, string[] files, StepOptions options, bool withUi)
    {
        var steps = stepList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            var r = FileActions.Run(files, steps, options);
            if (withUi && r.OnUi is not null) Log.Write($"ui-part ok={r.OnUi()}");
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
