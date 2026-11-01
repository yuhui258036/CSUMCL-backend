using System.Diagnostics;

internal static class BrowserUploadTests
{
    public static async Task Run(string admin, string password, string pack, string gateway, Action<bool, string> check)
    {
        var script = Path.GetFullPath("tests/PclCmu.Admin.Tests/browser-upload.test.cjs");
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(script);
        start.Environment["CSUMCL_TEST_ADMIN"] = admin;
        start.Environment["CSUMCL_TEST_PASSWORD"] = password;
        start.Environment["CSUMCL_TEST_PACK"] = pack;
        start.Environment["CSUMCL_TEST_GATEWAY"] = gateway;
        using var process = Process.Start(start)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        check(process.ExitCode == 0, "real Chromium upload, lost-response retry, pause and refresh-resume workflow");
        if (process.ExitCode != 0) throw new InvalidOperationException("Browser upload acceptance failed.");
    }
}
