using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PclCmu.Admin;

internal static class CrossVolumeTests
{
    public static async Task Run(string tempRoot, string destinationRoot, Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Cross-volume fixture requires isolated Linux mounts.");
        Directory.CreateDirectory(destinationRoot);
        static string Device(string path)
        {
            var start = new ProcessStartInfo("stat") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("%d"); start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Cannot inspect fixture filesystem device.");
            return output;
        }
        Console.WriteLine("\n== real cross-filesystem publication ==");
        var sourceDevice = Device(tempRoot);
        var targetDevice = Device(destinationRoot);
        check(sourceDevice != targetDevice, $"source and destination are distinct devices ({sourceDevice} -> {targetDevice})");
        if (sourceDevice == targetDevice) throw new InvalidOperationException("Cross-volume test must not silently run on one filesystem.");
        foreach (var scenario in new[] { "after-transfer", "partial-copy", "corrupt-transfer" })
        {
            var source = Path.Combine(tempRoot, "volume-" + scenario);
            var web = Path.Combine(Path.GetFullPath(destinationRoot), "csumcl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);
            var data = Path.Combine(source, "data");
            var keyPath = Path.Combine(source, "key.pem");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            try
            {
                var store = new InstanceStore(data, web, "https://cdn.example.com", keyPath);
                store.Create(new Instance { Id = "test", Name = "test" });
                var pack = Path.Combine(source, "pack.mrpack");
                using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
                {
                    using (var entry = zip.CreateEntry("modrinth.index.json").Open())
                        entry.Write(Encoding.UTF8.GetBytes("""{"files":[{"path":"mods/test.jar","downloads":["https://cdn.example.com/test.jar"],"hashes":{"sha1":"aa"},"fileSize":1}]}"""));
                    using var payload = zip.CreateEntry("overrides/cross-volume.bin", CompressionLevel.NoCompression).Open();
                    payload.Write(RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
                }
                var target = Path.Combine(web, "packs", "test", "1.mrpack");
                var checkpoint = scenario == "partial-copy" ? "after-journal" : "after-transfer";
                var injected = false;
                store.Checkpoint = stage =>
                {
                    if (stage != checkpoint) return;
                    injected = true;
                    check(!File.Exists(target), scenario + ": final URL file absent before destination rename");
                    throw new IOException("Simulated interruption between filesystems.");
                };
                try
                {
                    using var input = File.OpenRead(pack);
                    await new PackPublishService(store, 4 * 1024 * 1024).PublishAsync("test", 0, input);
                }
                catch (PublicationPendingException) { }
                var journalPath = Path.Combine(data, "publication.pending.json");
                check(injected && store.RecoveryRequired && File.Exists(journalPath), scenario + ": durable journal retains interrupted publication");
                using var journal = JsonDocument.Parse(File.ReadAllBytes(journalPath));
                var stagedName = journal.RootElement.GetProperty("StagedPack").GetString()!;
                var staged = Path.Combine(data, "publishing", stagedName);
                var transfer = Path.Combine(web, "packs", "test", $".1-{stagedName}.publishing");
                Directory.CreateDirectory(Path.GetDirectoryName(transfer)!);
                if (scenario == "partial-copy")
                {
                    File.WriteAllBytes(transfer, [1, 2, 3]);
                    check(File.Exists(staged), "partial-copy: valid source survives incomplete destination copy");
                }
                else
                {
                    check(!File.Exists(staged) && File.Exists(transfer), scenario + ": real cross-device transfer moved bytes to non-public name");
                    if (scenario == "corrupt-transfer") File.WriteAllBytes(transfer, [1, 2, 3]);
                }
                if (scenario == "corrupt-transfer")
                {
                    var refused = false;
                    try { _ = new InstanceStore(data, web, "https://cdn.example.com", keyPath); }
                    catch (IOException) { refused = true; }
                    check(refused && !File.Exists(target) && File.Exists(journalPath), "corrupt-transfer: recovery refuses corrupt bytes and retains evidence");
                    continue;
                }
                var recovered = new InstanceStore(data, web, "https://cdn.example.com", keyPath);
                using var original = File.OpenRead(pack);
                using var published = File.OpenRead(target);
                check(SHA256.HashData(original).SequenceEqual(SHA256.HashData(published)), scenario + ": recovered public bytes exactly match original");
                check(recovered.Get("test")?.Revision == 1 && !File.Exists(journalPath) && !File.Exists(transfer), scenario + ": revision commits once and owned recovery files are removed");
                var manifest = File.ReadAllBytes(Path.Combine(web, "manifest.json"));
                using var signature = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(web, "manifest.json.sig")));
                check(key.VerifyData(manifest, Convert.FromBase64String(signature.RootElement.GetProperty("signature").GetString()!), HashAlgorithmName.SHA256),
                    scenario + ": recovered manifest signature verifies");
            }
            finally { if (Directory.Exists(web)) Directory.Delete(web, recursive: true); }
        }
    }
}
