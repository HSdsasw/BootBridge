using System.Diagnostics;
using System.Text.Json;
using IsoWriter.InternalBoot;

namespace IsoWriter.Platform;

internal sealed record WorkRequest(string Operation, JsonElement Payload);
internal sealed record BootRequest(string Path, BootPartition Target);
internal sealed record ShrinkRequest(BootPartition Target, int StagingGiB, int LinuxGiB);
internal sealed record RawRequest(Disk Disk, string Path);
internal sealed record ManageRequest(string Receipt, bool Schedule);
internal sealed class ActionProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

// The helper accepts typed operations only. All destructive backends re-read and validate devices.
internal static class HelperHost
{
    internal static async Task<JsonElement> DispatchAsync(WorkRequest request, IProgress<string> progress, CancellationToken token)
    {
        T Get<T>() => request.Payload.Deserialize<T>() ?? throw new IOException("Invalid request.");
        object? result = request.Operation switch
        {
            "boot-plan" => await BootInstaller.PlanAsync(Get<BootRequest>().Path, Get<BootRequest>().Target, token),
            "boot-install" => await BootInstaller.InstallAsync(Get<BootPlan>(), progress, token),
            "shrink-plan" => await PartitionPreparation.PlanAsync(Get<ShrinkRequest>().Target, Get<ShrinkRequest>().StagingGiB, Get<ShrinkRequest>().LinuxGiB, token),
            "shrink-apply" => await PartitionPreparation.ApplyAsync(Get<ShrinkPlan>()),
            "raw-write" => await ImageWriter.WriteAsync(Get<RawRequest>().Disk, Get<RawRequest>().Path,
                new ActionProgress<WriteProgress>(p => progress.Report($"{p.Stage}: {p.Percent}% • {p.Detail}")), token),
            "manage" => await Manage(Get<ManageRequest>()),
            "receipt" => BootInstaller.LoadReceipt(Get<string>()),
            _ => throw new IOException("Unknown helper operation.")
        };
        return JsonSerializer.SerializeToElement(result, result?.GetType() ?? typeof(object));
    }
    private static async Task<bool> Manage(ManageRequest request) { await BootInstaller.ManageAsync(request.Receipt, request.Schedule); return true; }
    internal static async Task<int> RunAsync()
    {
        object outputLock = new();
        void Send(object value) { lock (outputLock) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); } }
        try
        {
            LinuxNative.RequireRoot();
            // Kernel-held process lock prevents two privileged helpers changing the same boot state.
            LinuxNative.AssertSafeDirectory("/run"); Directory.CreateDirectory("/run/bootbridge");
            using var guard = LinuxNative.GuardDirectory("/run/bootbridge");
            string line = await Console.In.ReadLineAsync() ?? throw new IOException("No request received.");
            if (line.Length > 128 * 1024) throw new IOException("The request is too large.");
            var request = JsonSerializer.Deserialize<WorkRequest>(line) ?? throw new IOException("Invalid request.");
            using var cts = new CancellationTokenSource();
            _ = Task.Run(async () => { if (await Console.In.ReadLineAsync() == "cancel") cts.Cancel(); });
            var result = await DispatchAsync(request, new ActionProgress<string>(p => Send(new { kind = "progress", message = p })), cts.Token);
            Send(new { kind = "result", value = result }); return 0;
        }
        catch (Exception ex) { Send(new { kind = "error", message = ex.Message }); return 1; }
    }
}

internal static class PrivilegedWork
{
    internal static async Task<T> RunAsync<T>(string operation, object payload, IProgress<string> progress, CancellationToken token = default)
    {
        var request = new WorkRequest(operation, JsonSerializer.SerializeToElement(payload));
        if (!OperatingSystem.IsLinux() || LinuxNative.geteuid() == 0)
            return (await HelperHost.DispatchAsync(request, progress, token)).Deserialize<T>()!;
        string executable = Environment.ProcessPath ?? throw new IOException("Could not find the application executable.");
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet") throw new IOException("Use the published ./BootBridge executable for disk operations instead of dotnet run.");
        var info = new ProcessStartInfo(LinuxCommand.Find("pkexec")) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(executable); info.ArgumentList.Add("--helper");
        using var process = Process.Start(info) ?? throw new IOException("Could not start the polkit helper.");
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)); await process.StandardInput.FlushAsync();
        using var cancel = token.Register(() => { try { process.StandardInput.WriteLine("cancel"); process.StandardInput.Flush(); } catch (IOException) { } catch (InvalidOperationException) { } });
        JsonElement result = default; string? error = null;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.Length > 256 * 1024) throw new IOException("The helper response is too large.");
            using var message = JsonDocument.Parse(line); var root = message.RootElement;
            switch (root.GetProperty("kind").GetString())
            {
                case "progress": progress.Report(root.GetProperty("message").GetString()!); break;
                case "error": error = root.GetProperty("message").GetString(); break;
                case "result": result = root.GetProperty("value").Clone(); break;
            }
        }
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || error is not null || result.ValueKind == JsonValueKind.Undefined)
            throw new IOException(error ?? "Polkit authorization was denied or the helper exited: " + await errors);
        return result.Deserialize<T>()!;
    }
}
