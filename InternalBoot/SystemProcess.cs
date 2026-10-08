using System.Diagnostics;
using System.Text;

namespace IsoWriter.InternalBoot;

internal static class SystemProcess
{
    internal static async Task<string> Run(string executable, IEnumerable<string> args, CancellationToken token = default, int timeoutSeconds = 90)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить " + executable);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeoutSeconds > 0) limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(limit.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        string result = await output, diagnostic = await error;
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)}: {diagnostic}\n{result}");
        return result;
    }
    internal static Task<string> PowerShell(string script, CancellationToken token = default, int timeoutSeconds = 90) => Run(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }"))], token, timeoutSeconds);
}
