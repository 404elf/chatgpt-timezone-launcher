namespace ChatGptTimezoneLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == PackagedProcessLauncher.HelperArgument)
            return PackagedProcessLauncher.RunHelperAsync(args[1]).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "ChatGPT 时区启动器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
        return 0;
    }
}
