using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PclCmu.Admin;

internal static class UploadGuardTests
{
    public static async Task Run(string tempRoot, Action<bool, string> check)
    {
        Console.WriteLine("\n== upload grants, limits and expiry ==");
        var root = Path.Combine(tempRoot, "guards");
        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "key.pem");
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256)) File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var data = Path.Combine(root, "data");
        var web = Path.Combine(root, "web");
        var store = new InstanceStore(data, web, "https://cdn.example.com", keyPath);
        store.Create(new Instance { Id = "test", Name = "test" });
        var publisher = new PackPublishService(store, 1048576);
        var options = new ChunkUploadOptions { ChunkSizeBytes = 65536, MaxFileBytes = 1048576, QuotaBytes = 4194304,
            MinFreeBytes = 0, MaxConcurrentChunks = 1 };
        var chunk = RandomNumberGenerator.GetBytes(options.ChunkSizeBytes);
        var hash = Convert.ToHexString(SHA256.HashData(chunk)).ToLowerInvariant();
        var request = new CreateUpload("test", "guard.mrpack", 131072, hash, 0);
        async Task Reject(Func<Task> action, int expected, string label)
        {
            var status = 0;
            try { await action(); } catch (UploadApiException ex) { status = ex.Status; }
            check(status == expected, label + $" (HTTP {status})");
        }
        UploadStatus first;
        UploadGrant grant;
        using (var initial = new ChunkUploadService(data, web, store, publisher, options))
        {
            first = await initial.CreateAsync(request, default);
            grant = await initial.RenewAsync(first.Id, default);
            var other = await initial.CreateAsync(request with { Sha256 = new string('a', 64) }, default);
            await Reject(() => initial.PutChunkAsync(other.Id, 0, grant.Token, hash, chunk.Length, new MemoryStream(chunk), default),
                401, "grant cannot write into a different upload");
            foreach (var index in new[] { -1, first.ChunkCount })
                await Reject(() => initial.PutChunkAsync(first.Id, index, grant.Token, hash, chunk.Length, new MemoryStream(chunk), default),
                    400, "negative/out-of-range chunk index rejected");
            using var blocked = new BlockedReadStream(chunk);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var writing = initial.PutChunkAsync(first.Id, 0, grant.Token, hash, chunk.Length, blocked, timeout.Token);
            try
            {
                await blocked.Started.Task.WaitAsync(timeout.Token);
                await Reject(() => initial.PutChunkAsync(first.Id, 1, grant.Token, hash, chunk.Length, new MemoryStream(chunk), default),
                    429, "active chunk writer enforces bounded concurrency");
                await Reject(async () => { await initial.CompleteAsync(first.Id, default); }, 409, "cannot publish while a chunk is being written");
                await Reject(() => initial.CancelAsync(first.Id, default), 409, "cannot delete chunks under an active writer");
            }
            finally { blocked.Release.TrySetResult(); await writing; }
            check((await initial.GetAsync(first.Id, default)).Received.SequenceEqual([0]), "busy rejection preserves the active writer receipt");
            options.MinFreeBytes = long.MaxValue;
            try
            {
                await Reject(async () => { await initial.CreateAsync(request with { Sha256 = new string('b', 64) }, default); },
                    507, "free-space floor rejects allocation without filling the disk");
            }
            finally { options.MinFreeBytes = 0; }
        }

        // Simulate elapsed wall time while stopped, rather than waiting an hour
        // or weakening production expiration rules with a test-only clock API.
        var metadataPath = Path.Combine(data, "uploads", first.Id, "session.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!;
        metadata["TokenExpiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        using (var resumed = new ChunkUploadService(data, web, store, publisher, options))
        {
            await Reject(() => resumed.PutChunkAsync(first.Id, 1, grant.Token, hash, chunk.Length, new MemoryStream(chunk), default),
                401, "expired persisted upload grant rejected after restart");
            var fresh = await resumed.RenewAsync(first.Id, default);
            using var input = new MemoryStream(chunk);
            await resumed.PutChunkAsync(first.Id, 1, fresh.Token, hash, chunk.Length, input, default);
            check((await resumed.GetAsync(first.Id, default)).Received.SequenceEqual([0, 1]), "grant renewal retains old blocks and accepts remaining block");
        }
        metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!;
        metadata["TouchedUtc"] = DateTimeOffset.UtcNow.AddHours(-25).ToString("O");
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        using var expired = new ChunkUploadService(data, web, store, publisher, options);
        await Reject(async () => { await expired.GetAsync(first.Id, default); }, 410, "idle expired upload cannot be resumed");
        await expired.StartAsync(default);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Directory.Exists(Path.GetDirectoryName(metadataPath)) && DateTime.UtcNow < deadline) await Task.Delay(10);
            check(!Directory.Exists(Path.GetDirectoryName(metadataPath)), "expiry worker reclaims owned session and chunk files");
        }
        finally { await expired.StopAsync(default); }
    }

    private sealed class BlockedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await base.ReadAsync(buffer, ct);
        }
    }
}
