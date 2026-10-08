namespace IsoWriter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ShellForm(args.Contains("--preview")));
    }
}
