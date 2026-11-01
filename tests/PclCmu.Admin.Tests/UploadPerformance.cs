using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PclCmu.Admin;

internal static class UploadPerformance
{
    public static async Task Run(string tempRoot, Action<bool, string> check)
    {
        Console.WriteLine("\n== bounded local upload performance (not WAN/CDN bandwidth) ==");
        var root = Path.Combine(tempRoot, "performance");
        Directory.CreateDirectory(root);
        var pack = Path.Combine(root, "sample.mrpack");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            using (var index = zip.CreateEntry("modrinth.index.json").Open())
                index.Write(Encoding.UTF8.GetBytes("""{"files":[{"path":"mods/test.jar","downloads":["https://cdn.example.com/test.jar"],"hashes":{"sha1":"aa"},"fileSize":1}],"dependencies":{"minecraft":"1.21.1"}}"""));
            using var payload = zip.CreateEntry("overrides/benchmark.bin", CompressionLevel.NoCompression).Open();
            var buffer = RandomNumberGenerator.GetBytes(1024 * 1024);
            for (var i = 0; i < 64; i++) payload.Write(buffer);
        }
        var size = new FileInfo(pack).Length;
        string digest;
        using (var input = File.OpenRead(pack)) digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var key = Path.Combine(root, "key.pem");
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256)) File.WriteAllText(key, ecdsa.ExportPkcs8PrivateKeyPem());
        var data = Path.Combine(root, "data");
        var web = Path.Combine(root, "web");
        var store = new InstanceStore(data, web, "https://cdn.example.com", key);
        var publisher = new PackPublishService(store, 128 * 1024 * 1024);
        var options = new ChunkUploadOptions { MaxFileBytes = 128 * 1024 * 1024, QuotaBytes = 1024 * 1024 * 1024, MinFreeBytes = 0 };
        using var uploads = new ChunkUploadService(data, web, store, publisher, options);
        await uploads.StartAsync(default);
        try
        {
            foreach (var concurrency in new[] { 1, 2, 4 })
            {
                var instance = "benchmark-" + concurrency;
                store.Create(new Instance { Id = instance, Name = instance });
                var session = await uploads.CreateAsync(new CreateUpload(instance, "sample.mrpack", size, digest, 0), default);
                var grant = await uploads.RenewAsync(session.Id, default);
                var clock = Stopwatch.StartNew();
                await Parallel.ForEachAsync(Enumerable.Range(0, session.ChunkCount), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (index, ct) =>
                {
                    var length = checked((int)Math.Min(options.ChunkSizeBytes, size - (long)index * options.ChunkSizeBytes));
                    var bytes = new byte[length];
                    await using (var input = File.OpenRead(pack))
                    {
                        input.Position = (long)index * options.ChunkSizeBytes;
                        await input.ReadExactlyAsync(bytes, ct);
                    }
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    using var stream = new MemoryStream(bytes, writable: false);
                    await uploads.PutChunkAsync(session.Id, index, grant.Token, hash, length, stream, ct);
                });
                var uploadSeconds = clock.Elapsed.TotalSeconds;
                await uploads.CompleteAsync(session.Id, default);
                var deadline = DateTime.UtcNow.AddSeconds(90);
                UploadStatus status;
                do { await Task.Delay(10); status = await uploads.GetAsync(session.Id, default); }
                while (status.State is "queued" or "verifying" && DateTime.UtcNow < deadline);
                clock.Stop();
                check(status.State == "completed" && status.Revision == 1, $"64 MiB benchmark with {concurrency} lanes publishes successfully");
                using var published = File.OpenRead(Path.Combine(web, "packs", instance, "1.mrpack"));
                check(Convert.ToHexString(SHA256.HashData(published)).Equals(digest, StringComparison.OrdinalIgnoreCase),
                    $"64 MiB benchmark with {concurrency} lanes preserves exact bytes");
                using var process = Process.GetCurrentProcess();
                Console.WriteLine("PERFORMANCE " + JsonSerializer.Serialize(new
                {
                    platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    bytes = size, chunkBytes = options.ChunkSizeBytes, concurrency,
                    uploadSeconds = Math.Round(uploadSeconds, 3),
                    processingSeconds = Math.Round(clock.Elapsed.TotalSeconds - uploadSeconds, 3),
                    uploadMiBPerSecond = Math.Round(size / 1048576.0 / uploadSeconds, 2),
                    processPeakWorkingSetMiB = Math.Round(process.PeakWorkingSet64 / 1048576.0, 2),
                }));
            }
        }
        finally { await uploads.StopAsync(default); }
    }
}
