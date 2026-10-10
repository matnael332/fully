using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WorkContinuity.Security.Tests;

/// <summary>
/// A throwaway copy of one owner-run Stage B1 script (Gate 3G, owner decisions B-2, B-8 and B-9 of 2026-10-08) and the files
/// it reads, run by PowerShell with a stand-in <c>az</c> first on the PATH. The stand-in records every call, keeps a copy
/// of the request body the script hands it, and answers each call from the answers a test gives it, by the call's
/// position. The run also gets an empty Azure CLI configuration directory, so that even the real Azure CLI would find no
/// sign-in. No Azure, Entra or other network endpoint is ever reached, and nothing outside the sandbox is written.
/// </summary>
internal sealed class HarnessAzureCliSandbox : IDisposable
{
    private static readonly Lazy<string> PowerShell = new(FindPowerShell);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wce-b1-script-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the sandbox with copies of the given repository files.</summary>
    /// <param name="files">Repository-relative paths, copied to the same relative paths.</param>
    internal HarnessAzureCliSandbox(params string[] files)
    {
        Assert.False(_root.Any(char.IsWhiteSpace), "The sandbox path must hold no whitespace: " + _root);
        Directory.CreateDirectory(ShimDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(ConfigurationDirectory);
        foreach (var file in files)
        {
            var target = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(RepositoryLayout.Root.FullName, file), target);
        }

        WriteShim();
    }

    /// <summary>Gets the sandbox's root.</summary>
    internal string Root => _root;

    /// <summary>Gets the temporary directory the script is given.</summary>
    internal string TempDirectory => Path.Combine(_root, "temp");

    private string ShimDirectory => Path.Combine(_root, "shim");

    private string ConfigurationDirectory => Path.Combine(_root, "azure-config");

    /// <summary>Removes the throwaway directory.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a process that has just ended: the temporary directory is left to the system.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    /// <summary>A recorded call without the quotes PowerShell adds around an argument with a space.</summary>
    internal static string Unquote(string invocation) => invocation.Replace("\"", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>The arguments of a recorded call; none of the arguments compared this way holds a space.</summary>
    internal static List<string> Tokens(string invocation) => [.. Unquote(invocation).Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>The lines of a text, without line-break characters.</summary>
    internal static List<string> Lines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    /// <summary>A text without any whitespace, so that PowerShell's own line wrapping does not matter.</summary>
    internal static string Squash(string text) => Regex.Replace(text, @"\s+", string.Empty);

    /// <summary>Makes the stand-in az print the given text and end with the given exit code at the given call (from 1).</summary>
    internal void Answer(int call, string standardOutput, int exitCode = 0)
    {
        File.WriteAllText(Path.Combine(ShimDirectory, "answer-" + call.ToString(CultureInfo.InvariantCulture) + ".txt"), standardOutput + "\n");
        File.WriteAllText(Path.Combine(ShimDirectory, "exit-" + call.ToString(CultureInfo.InvariantCulture) + ".txt"), exitCode.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The request body the script handed the stand-in az at the given call (from 1), or null.</summary>
    internal string? Body(int call)
    {
        var path = Path.Combine(ShimDirectory, "body-" + call.ToString(CultureInfo.InvariantCulture) + ".json");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>The request-body files the script left behind in its temporary directory.</summary>
    internal IReadOnlyList<string> LeftoverBodies() => Directory.EnumerateFiles(TempDirectory, "wce-harness-identity-*").ToList();

    /// <summary>Runs one of the sandbox's scripts with PowerShell.</summary>
    /// <param name="script">The script's repository-relative path.</param>
    /// <param name="arguments">The script's arguments.</param>
    /// <returns>What it printed, and what the stand-in az recorded.</returns>
    internal async Task<HarnessScriptRun> RunAsync(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo(PowerShell.Value)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
        }

        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(_root, script));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The stand-in comes first on the PATH; the temporary directory and the Azure CLI configuration are the sandbox's.
        start.Environment["PATH"] = ShimDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["TMP"] = TempDirectory;
        start.Environment["TEMP"] = TempDirectory;
        start.Environment["TMPDIR"] = TempDirectory;
        start.Environment["AZURE_CONFIG_DIR"] = ConfigurationDirectory;
        start.Environment["AZURE_CORE_COLLECT_TELEMETRY"] = "false";
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        start.Environment["POWERSHELL_UPDATECHECK"] = "Off";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        process.StandardInput.Close();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(TimeSpan.FromMinutes(2));
        var output = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var errors = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var invocationsFile = Path.Combine(ShimDirectory, "invocations.txt");
        var invocations = File.Exists(invocationsFile) ? await File.ReadAllLinesAsync(invocationsFile, bounded.Token) : [];
        return new HarnessScriptRun(process.ExitCode, await output, await errors, invocations);
    }

    private static string FindPowerShell()
    {
        // Windows PowerShell, the owner's shell, on Windows; PowerShell 7 elsewhere, as CI has it.
        if (OperatingSystem.IsWindows())
        {
            var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(windowsPowerShell))
            {
                return windowsPowerShell;
            }
        }

        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No PowerShell was found, so the owner-run script cannot be run: CI has pwsh, and Windows has Windows PowerShell.");
    }

    private void WriteShim()
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(ShimDirectory, "az.cmd"),
                string.Join(
                    "\r\n",
                    "@echo off",
                    "setlocal EnableDelayedExpansion",
                    "set \"here=%~dp0\"",
                    "set \"n=0\"",
                    "if exist \"%here%count.txt\" set /p n=<\"%here%count.txt\"",
                    "set /a n=n+1",
                    ">\"%here%count.txt\" echo !n!",
                    ">>\"%here%invocations.txt\" echo(%*",
                    "if exist \"%TMP%\\wce-harness-identity-*.json\" copy /b /y \"%TMP%\\wce-harness-identity-*.json\" \"%here%body-!n!.json\" >nul",
                    "if exist \"%here%answer-!n!.txt\" type \"%here%answer-!n!.txt\"",
                    "set \"code=0\"",
                    "if exist \"%here%exit-!n!.txt\" set /p code=<\"%here%exit-!n!.txt\"",
                    "exit /b !code!",
                    string.Empty));
        }
        else
        {
            var shim = Path.Combine(ShimDirectory, "az");
            File.WriteAllText(
                shim,
                string.Join(
                    "\n",
                    "#!/bin/sh",
                    "here=$(dirname \"$0\")",
                    "n=0",
                    "if [ -f \"$here/count.txt\" ]; then n=$(cat \"$here/count.txt\"); fi",
                    "n=$((n + 1))",
                    "printf '%s\\n' \"$n\" > \"$here/count.txt\"",
                    "printf '%s\\n' \"$*\" >> \"$here/invocations.txt\"",
                    "for body in \"$TMPDIR\"/wce-harness-identity-*.json; do",
                    "  if [ -f \"$body\" ]; then cp \"$body\" \"$here/body-$n.json\"; fi",
                    "done",
                    "if [ -f \"$here/answer-$n.txt\" ]; then cat \"$here/answer-$n.txt\"; fi",
                    "code=0",
                    "if [ -f \"$here/exit-$n.txt\" ]; then code=$(cat \"$here/exit-$n.txt\"); fi",
                    "exit \"$code\"",
                    string.Empty));
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// <summary>What a sandboxed run printed, its exit code, and every call the stand-in az recorded, in order.</summary>
internal sealed record HarnessScriptRun(int ExitCode, string Output, string Errors, IReadOnlyList<string> Invocations)
{
    /// <summary>Gets everything the run printed.</summary>
    internal string All => Output + Errors;

    /// <summary>Gets the lines of the standard output.</summary>
    internal List<string> Lines => HarnessAzureCliSandbox.Lines(Output);
}
