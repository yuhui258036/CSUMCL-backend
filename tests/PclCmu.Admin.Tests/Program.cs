using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PclCmu.Admin;

// ---------------------------------------------------------------------------
// Self-contained test harness for the incremental .mrpack file-index feature.
// No external test framework: assertions accumulate failures and exit non-zero.
// ---------------------------------------------------------------------------

var failures = 0;
var checks = 0;

void Check(bool condition, string what)
{
    checks++;
    if (condition)
    {
        Console.WriteLine($"  ok: {what}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL: {what}");
    }
}

void Section(string name) => Console.WriteLine($"\n== {name} ==");

// ---------------------------------------------------------------- temp dirs

var tempRoot = Path.Combine(Path.GetTempPath(), $"pclcmu-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);
try
{
    if (!args.Contains("--http-only", StringComparer.Ordinal))
    {
        TestPathSafety();
        TestFileIndexBuild();
        TestOverridesGroundTruth();
        TestInstanceStoreManifest();
        TestDependencyMapping();
        TestUploadMetadata();
        TestStartupBackfill();
        await TestPublicationTransactions();
        await TestChunkUploads();
        await TestMalformedUpload();
        await UploadGuardTests.Run(tempRoot, Check);
    }
    await HttpUploadTests.Run(tempRoot, Check, args.Contains("--browser", StringComparer.Ordinal));
    if (Environment.GetEnvironmentVariable("CSUMCL_TEST_SECOND_VOLUME") is { Length: > 0 } secondVolume)
        await CrossVolumeTests.Run(tempRoot, secondVolume, Check);
    if (args.Contains("--performance", StringComparer.Ordinal)) await UploadPerformance.Run(tempRoot, Check);

    Console.WriteLine($"\n{checks - failures}/{checks} checks passed, {failures} failed.");
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
}

return failures == 0 ? 0 : 1;

async Task TestMalformedUpload()
{
    Section("malformed pack isolation");
    var root = Path.Combine(tempRoot, "malformed");
    Directory.CreateDirectory(root);
    var keyPath = Path.Combine(root, "key.pem");
    using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256)) File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    var data = Path.Combine(root, "data");
    var web = Path.Combine(root, "web");
    var store = new InstanceStore(data, web, "https://cdn.example.com", keyPath);
    store.Create(new Instance { Id = "test", Name = "test" });
    using var uploads = new ChunkUploadService(data, web, store, new PackPublishService(store, 1048576),
        new ChunkUploadOptions { MaxFileBytes = 1048576, QuotaBytes = 4194304, MinFreeBytes = 0 });
    await uploads.StartAsync(default);
    try
    {
        foreach (var malformed in new[] { true, false })
        {
            var inputDir = Path.Combine(root, malformed ? "bad" : "good");
            Directory.CreateDirectory(inputDir);
            var pack = WriteSyntheticPack(inputDir, malformed ? "{\"files\":[null]}" : PackIndexWithDeps("{}"), []);
            var bytes = File.ReadAllBytes(pack);
            var status = await uploads.CreateAsync(new CreateUpload("test", "pack.mrpack", bytes.Length, Sha256Hex(bytes), 0), default);
            var grant = await uploads.RenewAsync(status.Id, default);
            using var input = new MemoryStream(bytes);
            await uploads.PutChunkAsync(status.Id, 0, grant.Token, Sha256Hex(bytes), bytes.Length, input, default);
            await uploads.CompleteAsync(status.Id, default);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do { await Task.Delay(10); status = await uploads.GetAsync(status.Id, default); }
            while (status.State is "queued" or "verifying" && DateTime.UtcNow < deadline);
            Check(status.State == (malformed ? "failed" : "completed"), malformed
                ? "null file entry fails only its own upload" : "same worker publishes valid upload after malformed pack");
            Check(uploads.ExecuteTask is { IsCompleted: false }, "upload worker stays alive after validation result");
        }
    }
    finally { await uploads.StopAsync(default); }
}

async Task TestChunkUploads()
{
    Section("durable chunk uploads");
    var root = Path.Combine(tempRoot, "chunks");
    Directory.CreateDirectory(root);
    var key = Path.Combine(root, "key.pem");
    using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256)) File.WriteAllText(key, ecdsa.ExportPkcs8PrivateKeyPem());
    var data = Path.Combine(root, "data");
    var web = Path.Combine(root, "web");
    var store = new InstanceStore(data, web, "https://cdn.example.com", key);
    store.Create(new Instance { Id = "test", Name = "Test" });
    var publisher = new PackPublishService(store, 1024 * 1024);
    var options = new ChunkUploadOptions { ChunkSizeBytes = 65536, MaxFileBytes = 1024 * 1024, MinFreeBytes = 0, QuotaBytes = 4 * 1024 * 1024 };
    var path = WriteSyntheticPack(root, PackIndexWithDeps("""{"minecraft":"1.21.1"}"""), [("overrides/test.bin", RandomNumberGenerator.GetBytes(180000))]);
    var bytes = File.ReadAllBytes(path);
    var request = new CreateUpload("test", "sample.mrpack", bytes.Length, Sha256Hex(bytes), 0);
    using var first = new ChunkUploadService(data, web, store, publisher, options);
    var created = await first.CreateAsync(request, default);
    Check(created.ChunkCount == 3, "pack spans three chunks");
    Check((await first.CreateAsync(request, default)).Id == created.Id, "create is idempotent after response loss");
    var grant = await first.RenewAsync(created.Id, default);
    var metadata = File.ReadAllText(Path.Combine(data, "uploads", created.Id, "session.json"));
    Check(!metadata.Contains(grant.Token, StringComparison.Ordinal), "raw upload token is never persisted");
    byte[] Chunk(int index) => bytes.Skip(index * options.ChunkSizeBytes).Take(options.ChunkSizeBytes).ToArray();
    async Task Send(ChunkUploadService service, int index, string token, string? hash = null, byte[]? payload = null)
    {
        var chunk = payload ?? Chunk(index);
        await using var stream = new MemoryStream(chunk);
        await service.PutChunkAsync(created.Id, index, token, hash ?? Sha256Hex(chunk), chunk.Length, stream, default);
    }
    async Task Reject(Func<Task> operation, int status, string description)
    {
        var actual = 0;
        try { await operation(); } catch (UploadApiException ex) { actual = ex.Status; }
        Check(actual == status, description + $" (HTTP {actual})");
    }
    await Reject(() => Send(first, 2, new string('0', 64)), 401, "bad grant rejected before chunk is stored");
    await Reject(() => Send(first, 2, grant.Token, new string('0', 64)), 422, "chunk hash mismatch rejected");
    await Send(first, 2, grant.Token);
    await Send(first, 2, grant.Token);
    Check((await first.GetAsync(created.Id, default)).Received.SequenceEqual([2]), "out-of-order duplicate creates exactly one receipt");
    await Reject(() => Send(first, 2, grant.Token, new string('f', 64)), 409, "conflicting duplicate rejected");
    await Reject(async () => { await first.CompleteAsync(created.Id, default); }, 409, "cannot complete with missing chunks");
    await Reject(() => Send(first, 0, grant.Token, payload: new byte[1]), 400, "wrong chunk size rejected");
    var renewed = await first.RenewAsync(created.Id, default);
    await Reject(() => Send(first, 0, grant.Token), 401, "renewal invalidates old grant");

    using var resumed = new ChunkUploadService(data, web, store, publisher, options);
    Check((await resumed.GetAsync(created.Id, default)).Received.SequenceEqual([2]), "restart preserves chunk receipt");
    await Task.WhenAll(Send(resumed, 0, renewed.Token), Send(resumed, 1, renewed.Token));
    Check((await resumed.GetAsync(created.Id, default)).Received.Length == 3, "parallel chunks retained after restart");
    var queued = await resumed.CompleteAsync(created.Id, default);
    Check(queued.State == "queued", "completion persists job before worker starts");
    Check((await resumed.CompleteAsync(created.Id, default)).State == "queued", "duplicate complete does not create a new job");

    using var worker = new ChunkUploadService(data, web, store, publisher, options);
    await worker.StartAsync(default);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    UploadStatus status;
    do
    {
        await Task.Delay(25);
        status = await worker.GetAsync(created.Id, default);
    } while (status.State is "queued" or "verifying" && DateTime.UtcNow < deadline);
    Check(status.State == "completed" && status.Revision == 1, "queued job resumes after second restart and publishes");
    Check(File.ReadAllBytes(Path.Combine(web, "packs", "test", "1.mrpack")).SequenceEqual(bytes), "published bytes match reassembled original");
    Check((await worker.CompleteAsync(created.Id, default)).Revision == 1, "completed retry returns same revision");
    Check(!Directory.EnumerateFiles(Path.Combine(data, "uploads", created.Id), "*.chunk").Any(), "completed job releases chunk storage");
    await worker.StopAsync(default);

    using var last = new ChunkUploadService(data, web, store, publisher, options);
    Check((await last.GetAsync(created.Id, default)).State == "completed", "completion receipt survives restart");
    var cancelled = await last.CreateAsync(request with { BaseRevision = 1, Sha256 = new string('a', 64) }, default);
    await last.CancelAsync(cancelled.Id, default);
    await Reject(async () => { await last.GetAsync(cancelled.Id, default); }, 410, "cancelled task cannot be resumed");
    var invalid = request with { BaseRevision = 1, Size = options.MaxFileBytes + 1 };
    await Reject(async () => { await last.CreateAsync(invalid, default); }, 400, "file size cap enforced before allocation");

    // A failed full-file check must still reserve the retained chunks' quota.
    options.QuotaBytes = 2 * 1024 * 1024;
    using var failing = new ChunkUploadService(data, web, store, publisher, options);
    var failed = await failing.CreateAsync(request with { BaseRevision = 1, Sha256 = new string('b', 64) }, default);
    var failedGrant = await failing.RenewAsync(failed.Id, default);
    for (var i = 0; i < failed.ChunkCount; i++)
    {
        var chunk = Chunk(i);
        using var stream = new MemoryStream(chunk);
        await failing.PutChunkAsync(failed.Id, i, failedGrant.Token, Sha256Hex(chunk), chunk.Length, stream, default);
    }
    await failing.CompleteAsync(failed.Id, default);
    await failing.StartAsync(default);
    deadline = DateTime.UtcNow.AddSeconds(10);
    do { await Task.Delay(25); status = await failing.GetAsync(failed.Id, default); }
    while (status.State is "queued" or "verifying" && DateTime.UtcNow < deadline);
    Check(status.State == "failed" && store.Get("test")?.Revision == 1, "whole-file mismatch fails without publishing");
    await Reject(async () => { await failing.CreateAsync(request with { BaseRevision = 1, Size = 1024 * 1024, Sha256 = new string('c',64) }, default); },
        507, "failed uploads still reserve disk quota");
    await failing.CancelAsync(failed.Id, default);
    Check(!Directory.EnumerateFiles(Path.Combine(data, "uploads", failed.Id), "*.chunk").Any(), "cancelling failed upload releases disk bytes");
    await failing.StopAsync(default);

    foreach (var faultState in new[] { "verifying", "completed" })
    {
        var id = "storage-" + faultState;
        store.Create(new Instance { Id = id, Name = id });
        using var recoverable = new ChunkUploadService(data, web, store, publisher, options);
        var upload = await recoverable.CreateAsync(request with { InstanceId = id }, default);
        var permission = await recoverable.RenewAsync(upload.Id, default);
        for (var i = 0; i < upload.ChunkCount; i++)
        {
            var chunk = Chunk(i);
            using var stream = new MemoryStream(chunk);
            await recoverable.PutChunkAsync(upload.Id, i, permission.Token, Sha256Hex(chunk), chunk.Length, stream, default);
        }
        await recoverable.CompleteAsync(upload.Id, default);
        var injected = false;
        recoverable.SaveCheckpoint = state =>
        {
            if (state == faultState && !injected) { injected = true; throw new IOException("Injected session disk failure"); }
        };
        await recoverable.StartAsync(default);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!recoverable.StorageUnavailable && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(injected && recoverable.StorageUnavailable && recoverable.ExecuteTask is { IsCompleted: false },
            $"{faultState} write failure retains live worker instead of stopping host");
        deadline = DateTime.UtcNow.AddSeconds(10);
        do { await Task.Delay(25); status = await recoverable.GetAsync(upload.Id, default); }
        while (status.State != "completed" && DateTime.UtcNow < deadline);
        Check(status.State == "completed" && store.Get(id)?.Revision == 1, $"{faultState} failure automatically recovers without duplicate publication");
        Check(!recoverable.StorageUnavailable, $"{faultState} recovery clears upload storage hold");
        Check(!Directory.EnumerateFiles(Path.Combine(data, "uploads", upload.Id), "*.chunk").Any(), $"{faultState} recovery reclaims chunks");
        await recoverable.StopAsync(default);
    }
}

async Task TestPublicationTransactions()
{
    Section("publication concurrency and crash recovery");
    var key = Path.Combine(tempRoot, "transaction-key.pem");
    using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        File.WriteAllText(key, ecdsa.ExportPkcs8PrivateKeyPem());

    foreach (var checkpoint in new[] { "before-journal", "after-journal", "after-transfer", "after-pack", "after-index", "after-instances", "after-signature", "after-manifest" })
    {
        var root = Path.Combine(tempRoot, checkpoint);
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "data");
        var web = Path.Combine(root, "web");
        var store = new InstanceStore(data, web, "https://cdn.example.com", key);
        store.Create(new Instance { Id = "test", Name = "Test" });
        var pack = WriteSyntheticPack(root, PackIndexWithDeps("""{"minecraft":"1.21.1"}"""), []);
        var originalBytes = File.ReadAllBytes(pack);
        var oldManifest = File.ReadAllBytes(Path.Combine(web, "manifest.json"));
        store.Checkpoint = step => { if (step == checkpoint) throw new IOException("Injected disk interruption"); };
        Exception? failure = null;
        try
        {
            await using var input = File.OpenRead(pack);
            await new PackPublishService(store, 1024 * 1024).PublishAsync("test", 0, input);
        }
        catch (IOException ex) { failure = ex; }
        Check(failure is not null, $"{checkpoint}: failure injected");
        if (checkpoint == "before-journal")
        {
            Check(store.Get("test")?.Revision == 0 && !store.RecoveryRequired, "pre-commit failure rolls back memory");
            Check(oldManifest.SequenceEqual(File.ReadAllBytes(Path.Combine(web, "manifest.json"))), "pre-commit failure preserves published manifest");
            Check(!Directory.EnumerateFiles(store.StagingDirectory).Any(), "pre-commit failure cleans only its staging file");
        }
        else
        {
            Check(failure is PublicationPendingException && store.RecoveryRequired, $"{checkpoint}: durable commit blocks further writes");
            var recovered = new InstanceStore(data, web, "https://cdn.example.com", key);
            Check(recovered.Get("test")?.Revision == 1, $"{checkpoint}: restart recovers revision");
            var published = Path.Combine(web, "packs", "test", "1.mrpack");
            Check(File.ReadAllBytes(published).SequenceEqual(originalBytes), $"{checkpoint}: published bytes survive");
            var index = File.ReadAllBytes(Path.Combine(web, "packs", "test", "1.files.json"));
            Check(recovered.Get("test")?.Pack?.FilesSha256 == Sha256Hex(index), $"{checkpoint}: index hash matches manifest");
            Check(!File.Exists(Path.Combine(data, "publication.pending.json")), $"{checkpoint}: recovery journal completed");
        }
    }

    var concurrentRoot = Path.Combine(tempRoot, "concurrent");
    Directory.CreateDirectory(concurrentRoot);
    var concurrentStore = new InstanceStore(Path.Combine(concurrentRoot, "data"), Path.Combine(concurrentRoot, "web"), "https://cdn.example.com", key);
    concurrentStore.Create(new Instance { Id = "test", Name = "Test" });
    var concurrentPack = WriteSyntheticPack(concurrentRoot, PackIndexWithDeps("""{"minecraft":"1.21.1"}"""), []);
    async Task<bool> Publish()
    {
        try
        {
            await using var input = File.OpenRead(concurrentPack);
            await new PackPublishService(concurrentStore, 1024 * 1024).PublishAsync("test", 0, input);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
    var outcomes = await Task.WhenAll(Task.Run(Publish), Task.Run(Publish));
    Check(outcomes.Count(x => x) == 1, "concurrent publishers: exactly one succeeds");
    var winningPack = Path.Combine(concurrentRoot, "web", "packs", "test", "1.mrpack");
    Check(File.ReadAllBytes(winningPack).SequenceEqual(File.ReadAllBytes(concurrentPack)), "losing upload cannot overwrite/delete winner");
    Check(concurrentStore.Get("test")?.Revision == 1, "concurrent publishers increment revision once");

    var badData = Path.Combine(tempRoot, "corrupt-store");
    Directory.CreateDirectory(badData);
    File.WriteAllText(Path.Combine(badData, "instances.json"), "{corrupt");
    var refused = false;
    try { _ = new InstanceStore(badData, Path.Combine(tempRoot, "corrupt-web"), "https://cdn.example.com", key); }
    catch (InvalidDataException) { refused = true; }
    Check(refused && File.ReadAllText(Path.Combine(badData, "instances.json")) == "{corrupt", "corrupt store fails closed without erasing data");

    var cleanupRoot = Path.Combine(tempRoot, "staging-cleanup");
    var cleanupData = Path.Combine(cleanupRoot, "data");
    var cleanupWeb = Path.Combine(cleanupRoot, "web");
    var staging = Path.Combine(cleanupData, "publishing");
    Directory.CreateDirectory(staging);
    var orphan = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".mrpack");
    File.WriteAllBytes(orphan, [1, 2, 3]);
    File.WriteAllText(Path.Combine(staging, "operator-note.txt"), "preserve");
    var cleanupStore = new InstanceStore(cleanupData, cleanupWeb, "https://cdn.example.com", key);
    Check(!File.Exists(orphan), "startup reclaims pre-commit validation remnants");
    Check(File.ReadAllText(Path.Combine(staging, "operator-note.txt")) == "preserve", "startup cleanup only removes owned GUID pack files");
    if (OperatingSystem.IsWindows())
    {
        cleanupStore.Create(new Instance { Id = "test", Name = "Test" });
        var cleanupPack = WriteSyntheticPack(cleanupRoot, PackIndexWithDeps("""{"minecraft":"1.21.1"}"""), []);
        var cleanupPublisher = new PackPublishService(cleanupStore, 1024 * 1024);
        FileStream? held = null;
        cleanupStore.Checkpoint = state =>
        {
            if (state != "before-journal") return;
            held = new FileStream(Directory.EnumerateFiles(staging, "*.mrpack").Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
            throw new IOException("Injected validation/cleanup failure");
        };
        try
        {
            using var input = File.OpenRead(cleanupPack);
            try { await cleanupPublisher.PublishAsync("test", 0, input); } catch (IOException) { }
            Check(Directory.EnumerateFiles(staging, "*.mrpack").Any(), "locked staging file simulates cleanup failure");
        }
        finally { held?.Dispose(); cleanupStore.Checkpoint = null; }
        using var retryInput = File.OpenRead(cleanupPack);
        var recoveredRevision = await cleanupPublisher.PublishAsync("test", 0, retryInput).WaitAsync(TimeSpan.FromSeconds(5));
        Check(recoveredRevision == 1, "cleanup failure does not strand the publisher semaphore");
    }
}

// ------------------------------------------------------------- path safety

void TestPathSafety()
{
    Section("path safety (IsSafeRelativePath)");
    Check(FileIndexBuilder.IsSafeRelativePath("mods/foo.jar"), "mods/foo.jar is safe");
    Check(FileIndexBuilder.IsSafeRelativePath("config/a/b.json"), "config/a/b.json is safe");
    Check(!FileIndexBuilder.IsSafeRelativePath("../evil.jar"), "../evil.jar rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("a/../../evil.jar"), "a/../../evil.jar rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("/abs.jar"), "absolute /abs.jar rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("\\abs.jar"), "absolute \\abs.jar rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("C:\\Windows\\evil.jar"), "drive-letter C:\\ rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("mods/./foo.jar"), "dot segment rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("mods//foo.jar"), "empty segment rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath(""), "empty path rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath(null), "null path rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("   "), "whitespace path rejected");
}

// ------------------------------------------------------------ build helpers

string WriteSyntheticPack(string dir, string indexJson, IEnumerable<(string Name, byte[] Bytes)> overrides)
{
    var path = Path.Combine(dir, "pack.mrpack");
    using (var fs = File.Create(path))
    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
    {
        var index = zip.CreateEntry("modrinth.index.json");
        using (var s = index.Open())
            s.Write(Encoding.UTF8.GetBytes(indexJson));

        foreach (var (name, bytes) in overrides)
        {
            var e = zip.CreateEntry(name);
            using var s = e.Open();
            s.Write(bytes);
        }
    }
    return path;
}

string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

// ----------------------------------------------------------- file index build

void TestFileIndexBuild()
{
    Section("FileIndexBuilder.Build (shape, filtering, hashes)");

    var indexJson = """
    {
      "formatVersion": 1,
      "game": "minecraft",
      "versionId": "1.0.0",
      "name": "Test Pack",
      "files": [
        { "path": "mods/foo.jar", "downloads": ["https://cdn.example.com/mods/foo.jar", "http://insecure.example.com/foo.jar"], "hashes": { "sha1": "ABCDEF0123456789abcdef0123456789abcdef01", "sha512": "00AA11BB22CC33DD44EE55FF6677889900AA11BB22CC33DD44EE55FF6677889900AA11BB22CC33DD44EE55FF66778899" }, "fileSize": 1234 },
        { "path": "config/bar.json", "downloads": ["http://insecure.example.com/bar.json"], "hashes": { "sha1": "11", "sha512": "22" }, "fileSize": 56 },
        { "path": "../evil.jar", "downloads": ["https://cdn.example.com/evil.jar"], "hashes": { "sha1": "33", "sha512": "44" }, "fileSize": 78 },
        { "path": "/abs.jar", "downloads": ["https://cdn.example.com/abs.jar"], "hashes": { "sha1": "55", "sha512": "66" }, "fileSize": 90 },
        { "path": "mods/nourl.jar", "downloads": [], "hashes": { "sha1": "77", "sha512": "88" }, "fileSize": 100 },
        { "path": "resourcepacks/foo.zip", "downloads": ["https://cdn.example.com/rp/foo.zip"], "hashes": { "sha1": "11AA", "sha512": "22BB" }, "fileSize": 200 }
      ],
      "dependencies": { "minecraft": "1.20.1" }
    }
    """;

    var overrides = new List<(string, byte[])>
    {
        ("overrides/config/foo.txt", Encoding.UTF8.GetBytes("hello overrides")),
        ("overrides/mods/extra.dat", new byte[] { 1, 2, 3, 4 }),
        ("client-overrides/config/client.txt", Encoding.UTF8.GetBytes("client cfg")),
    };

    var zipPath = WriteSyntheticPack(tempRoot, indexJson, overrides);
    var packBytes = File.ReadAllBytes(zipPath);
    var packSha256 = Sha256Hex(packBytes);

    var build = FileIndexBuilder.Build(zipPath, revision: 7, packSha256: packSha256);
    var index = build.Index;

    Check(index.FormatVersion == 1, "formatVersion == 1");
    Check(index.Revision == 7, "revision == 7");
    Check(index.PackSha256 == packSha256, "packSha256 matches SHA-256 of pack bytes");
    Check(index.Files.Count == 2, $"files has 2 entries (got {index.Files.Count})");
    Check(build.SkippedFiles.Count == 4, $"4 files skipped (got {build.SkippedFiles.Count})");

    var foo = index.Files.SingleOrDefault(f => f.Path == "mods/foo.jar");
    Check(foo is not null, "mods/foo.jar present");
    if (foo is not null)
    {
        Check(foo.Downloads.Count == 1 && foo.Downloads[0] == "https://cdn.example.com/mods/foo.jar",
            "http URL filtered; only HTTPS kept");
        Check(foo.Hashes.Sha1 == "abcdef0123456789abcdef0123456789abcdef01", "sha1 normalized to lowercase");
        Check(foo.Hashes.Sha512 == "00aa11bb22cc33dd44ee55ff6677889900aa11bb22cc33dd44ee55ff6677889900aa11bb22cc33dd44ee55ff66778899",
            "sha512 normalized to lowercase");
        Check(foo.FileSize == 1234, "fileSize == 1234");
    }

    var rp = index.Files.SingleOrDefault(f => f.Path == "resourcepacks/foo.zip");
    Check(rp is not null, "resourcepacks/foo.zip present (non-mod file included)");

    Check(!index.Files.Any(f => f.Path == "../evil.jar" || f.Path == "/abs.jar"
        || f.Path == "config/bar.json" || f.Path == "mods/nourl.jar"),
        "unsafe/no-URL files excluded");

    // Compact serialization + files_sha256 over exact bytes.
    var bytes = FileIndexBuilder.Serialize(index);
    var json = Encoding.UTF8.GetString(bytes);
    Check(!json.Contains('\n') && !json.Contains("  "), "serialized JSON is compact (no newlines)");
    var filesSha256 = FileIndexBuilder.ComputeSha256(bytes);
    Check(filesSha256 == Sha256Hex(bytes), "files_sha256 == SHA-256 of exact serialized bytes");

    // overrides hash: independent recomputation + stability.
    var expectedOverrides = ComputeOverridesIndependent(zipPath);
    Check(index.OverridesSha256 == expectedOverrides, "overridesSha256 matches independent computation");

    var build2 = FileIndexBuilder.Build(zipPath, revision: 7, packSha256: packSha256);
    Check(build2.Index.OverridesSha256 == index.OverridesSha256, "overridesSha256 stable across rebuilds");

    // Changing override content must change the hash.
    var overridesChanged = new List<(string, byte[])>
    {
        ("overrides/config/foo.txt", Encoding.UTF8.GetBytes("hello overrides CHANGED")),
        ("overrides/mods/extra.dat", new byte[] { 1, 2, 3, 4 }),
        ("client-overrides/config/client.txt", Encoding.UTF8.GetBytes("client cfg")),
    };
    var zipPath2 = WriteSyntheticPack(tempRoot, indexJson, overridesChanged);
    var build3 = FileIndexBuilder.Build(zipPath2, revision: 7, packSha256: packSha256);
    Check(build3.Index.OverridesSha256 != index.OverridesSha256, "overridesSha256 changes when override content changes");
}

string ComputeOverridesIndependent(string zipPath)
{
    using var zip = ZipFile.OpenRead(zipPath);
    var entries = zip.Entries
        .Where(e => !e.FullName.EndsWith('/'))
        .Where(e => e.FullName.StartsWith("overrides/", StringComparison.Ordinal)
                    || e.FullName.StartsWith("client-overrides/", StringComparison.Ordinal))
        .OrderBy(e => e.FullName, StringComparer.Ordinal)
        .ToList();

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var e in entries)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(e.FullName));
        hash.AppendData(new byte[] { 0x00 });
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        hash.AppendData(ms.ToArray());
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

void TestOverridesGroundTruth()
{
    Section("overridesSha256 ground truth (single entry)");

    var indexJson = """
    { "formatVersion": 1, "game": "minecraft", "versionId": "1", "name": "T", "files": [
        { "path": "mods/m.jar", "downloads": ["https://cdn.example.com/m.jar"], "hashes": { "sha1": "aa", "sha512": "bb" }, "fileSize": 1 }
    ], "dependencies": {} }
    """;

    var zipPath = WriteSyntheticPack(tempRoot, indexJson,
        new List<(string, byte[])> { ("overrides/a.txt", Encoding.UTF8.GetBytes("hello")) });

    var build = FileIndexBuilder.Build(zipPath, 1, "0".PadLeft(64, '0'));

    var expected = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("overrides/a.txt\u0000hello"))).ToLowerInvariant();

    Check(build.Index.OverridesSha256 == expected, "overridesSha256 == SHA256(name || NUL || bytes)");
}

// ------------------------------------------------------- instance store test

void TestInstanceStoreManifest()
{
    Section("InstanceStore manifest (files_url / files_sha256)");

    var dataDir = Path.Combine(tempRoot, "data");
    var wwwroot = Path.Combine(tempRoot, "wwwroot");
    Directory.CreateDirectory(dataDir);
    Directory.CreateDirectory(wwwroot);

    var keyPath = Path.Combine(tempRoot, "key.pem");
    using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());

    var store = new InstanceStore(dataDir, wwwroot, "https://cdn.example.com", keyPath);

    store.Create(new Instance
    {
        Id = "t",
        Name = "T",
        Revision = 0,
        Required = true,
        Minecraft = new MinecraftInfo { GameVersion = "1.20.1", Loader = "fabric", LoaderVersion = "0.15.11" },
        Server = new ServerInfo { Address = "127.0.0.1", Port = 25565 },
    });

    var filesSha256 = "a".PadLeft(64, 'a');
    var revision = store.RecordUpload("t", "s1", "s256", "s512", 312, filesSha256, expectedRevision: 1);
    Check(revision == 1, "RecordUpload returns revision 1");

    Check(store.RecordUpload("missing", "s1", "s256", "s512", 1, filesSha256, expectedRevision: 1) == -1,
        "RecordUpload returns -1 for unknown instance");

    bool raced = false;
    try
    {
        store.RecordUpload("t", "s1", "s256", "s512", 312, filesSha256, expectedRevision: 99);
    }
    catch (InvalidOperationException)
    {
        raced = true;
    }
    Check(raced, "RecordUpload fails closed when revision raced");

    // Manifest JSON must include files_url + files_sha256.
    var manifest = JsonDocument.Parse(store.GetManifestJson()).RootElement;
    var pack = manifest.GetProperty("instances")[0].GetProperty("pack");
    Check(pack.GetProperty("url").GetString() == "https://cdn.example.com/packs/t/1.mrpack", "pack.url unchanged semantics");
    Check(pack.GetProperty("files_url").GetString() == "https://cdn.example.com/packs/t/1.files.json",
        "files_url == .../packs/t/1.files.json");
    Check(pack.GetProperty("files_sha256").GetString() == filesSha256, "files_sha256 present");
    Check(pack.GetProperty("kind").GetString() == "mrpack", "kind == mrpack");

    // Clone preserves the new fields.
    var clone = store.Get("t");
    Check(clone?.Pack?.FilesUrl == "https://cdn.example.com/packs/t/1.files.json", "Clone preserves FilesUrl");
    Check(clone?.Pack?.FilesSha256 == filesSha256, "Clone preserves FilesSha256");

    // A persisted instances.json now carries the new fields.
    var persisted = File.ReadAllText(Path.Combine(dataDir, "instances.json"));
    Check(persisted.Contains("\"files_url\"", StringComparison.Ordinal)
          && persisted.Contains("\"files_sha256\"", StringComparison.Ordinal),
        "instances.json persists files_url/files_sha256");

    // Old revisions (no index) still serialize with null files_url/files_sha256.
    store.Create(new Instance
    {
        Id = "old",
        Name = "Old",
        Revision = 2,
        Required = false,
        Pack = new PackInfo { Kind = "mrpack", Url = "https://cdn.example.com/packs/old/2.mrpack", Sha1 = "x", Sha256 = "y", Sha512 = "z", Size = 10 },
    });
    var manifest2 = JsonDocument.Parse(store.GetManifestJson()).RootElement;
    var oldPack = manifest2.GetProperty("instances")
        .EnumerateArray().Single(i => i.GetProperty("id").GetString() == "old").GetProperty("pack");
    Check(oldPack.TryGetProperty("files_url", out var fu) && fu.ValueKind == JsonValueKind.Null,
        "old revision files_url is null");
    Check(oldPack.TryGetProperty("files_sha256", out var fsh) && fsh.ValueKind == JsonValueKind.Null,
        "old revision files_sha256 is null");

    // Signature file is produced for the signed manifest.
    Check(File.Exists(Path.Combine(wwwroot, "manifest.json.sig")), "manifest.json.sig written");
}

// --------------------------------------------------- dependency mapping tests

string PackIndexWithDeps(string depsJson) => $$"""
    { "formatVersion": 1, "game": "minecraft", "versionId": "1", "name": "T", "files": [
        { "path": "mods/m.jar", "downloads": ["https://cdn.example.com/m.jar"], "hashes": { "sha1": "aa", "sha512": "bb" }, "fileSize": 1 }
    ], "dependencies": {{depsJson}} }
    """;

void TestDependencyMapping()
{
    Section("dependency -> Minecraft metadata mapping");

    // fabric-loader
    var fabric = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(fabric is not null, "fabric-loader yields Minecraft metadata");
    Check(fabric?.GameVersion == "1.26.3", "fabric game_version == 1.26.3");
    Check(fabric?.Loader == "fabric", "fabric loader == fabric");
    Check(fabric?.LoaderVersion == "0.20.1", "fabric loader_version == 0.20.1");

    // forge
    var forge = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.20.1", "forge": "47.2.0" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(forge?.Loader == "forge", "forge loader == forge");

    // neoforge
    var neo = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.20.1", "neoforge": "47.1.106" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(neo?.Loader == "neoforge", "neoforge loader == neoforge");

    // quilt-loader
    var quilt = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.20.1", "quilt-loader": "0.23.2" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(quilt?.Loader == "quilt", "quilt-loader loader == quilt");

    // no loader dependency -> vanilla (empty loader)
    var vanilla = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.20.1" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(vanilla is not null && vanilla.Loader == "" && vanilla.LoaderVersion == "", "no loader -> vanilla (empty loader)");

    // no minecraft dependency -> empty game version (caller keeps existing)
    var noGame = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "fabric-loader": "0.20.1" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(noGame is not null && noGame.GameVersion == "" && noGame.Loader == "fabric",
        "no minecraft -> empty game version, loader still mapped");

    // no dependencies object at all -> null metadata
    var noDepsJson = """
    { "formatVersion": 1, "game": "minecraft", "versionId": "1", "name": "T", "files": [
        { "path": "mods/m.jar", "downloads": ["https://cdn.example.com/m.jar"], "hashes": { "sha1": "aa", "sha512": "bb" }, "fileSize": 1 }
    ] }
    """;
    var noDeps = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, noDepsJson, []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(noDeps is null, "no dependencies object -> null metadata");

    // TryReadMinecraftMetadata: reads only the index (fast path) with the same result.
    var zipPath = WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""), []);
    Check(FileIndexBuilder.TryReadMinecraftMetadata(zipPath, out var fast) && fast is not null,
        "TryReadMinecraftMetadata succeeds for a valid pack");
    Check(fast?.GameVersion == "1.26.3" && fast?.Loader == "fabric" && fast?.LoaderVersion == "0.20.1",
        "TryReadMinecraftMetadata returns the same metadata as Build");

    // TryReadMinecraftMetadata: corrupt pack -> false (no throw).
    var corruptPath = Path.Combine(tempRoot, "corrupt.mrpack");
    File.WriteAllBytes(corruptPath, new byte[] { 1, 2, 3, 4, 5 });
    Check(!FileIndexBuilder.TryReadMinecraftMetadata(corruptPath, out var corruptMeta) && corruptMeta is null,
        "TryReadMinecraftMetadata returns false for corrupt pack");

    // TryReadMinecraftMetadata: ZIP without index -> false (no throw).
    var noIndexPath = Path.Combine(tempRoot, "noindex.mrpack");
    using (var fs = File.Create(noIndexPath))
    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        zip.CreateEntry("random.txt").Open().Dispose();
    Check(!FileIndexBuilder.TryReadMinecraftMetadata(noIndexPath, out var noIndexMeta) && noIndexMeta is null,
        "TryReadMinecraftMetadata returns false when index entry missing");
}

// -------------------------------------------------------- upload metadata test

void TestUploadMetadata()
{
    Section("upload metadata (RecordUpload updates Instance.Minecraft)");

    var dataDir = Path.Combine(tempRoot, "upload-data");
    var wwwroot = Path.Combine(tempRoot, "upload-wwwroot");
    Directory.CreateDirectory(dataDir);
    Directory.CreateDirectory(wwwroot);

    var keyPath = Path.Combine(tempRoot, "upload-key.pem");
    using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());

    var store = new InstanceStore(dataDir, wwwroot, "https://cdn.example.com", keyPath);

    store.Create(new Instance
    {
        Id = "t",
        Name = "T",
        Revision = 0,
        Required = true,
        Minecraft = new MinecraftInfo { GameVersion = "1.20.1", Loader = "fabric", LoaderVersion = "0.15.11" },
        Server = new ServerInfo { Address = "127.0.0.1", Port = 25565 },
    });

    // Build a pack whose dependency section carries the corrected metadata.
    var packPath = WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""), []);
    var packBytes = File.ReadAllBytes(packPath);
    var build = FileIndexBuilder.Build(packPath, revision: 1, packSha256: Sha256Hex(packBytes));

    var revision = store.RecordUpload("t", "s1", Sha256Hex(packBytes), "s512", packBytes.Length,
        filesSha256: "a".PadLeft(64, 'a'), expectedRevision: 1, minecraft: build.Minecraft);
    Check(revision == 1, "RecordUpload returns revision 1");

    var updated = store.Get("t");
    Check(updated?.Minecraft?.GameVersion == "1.26.3", "upload corrected game_version to 1.26.3");
    Check(updated?.Minecraft?.Loader == "fabric", "upload kept loader fabric");
    Check(updated?.Minecraft?.LoaderVersion == "0.20.1", "upload corrected loader_version to 0.20.1");

    // The signed manifest carries the corrected minecraft fields.
    var manifest = JsonDocument.Parse(store.GetManifestJson()).RootElement;
    var mc = manifest.GetProperty("instances")[0].GetProperty("minecraft");
    Check(mc.GetProperty("game_version").GetString() == "1.26.3", "manifest game_version == 1.26.3");
    Check(mc.GetProperty("loader_version").GetString() == "0.20.1", "manifest loader_version == 0.20.1");
}

// ------------------------------------------------------- startup backfill test

void TestStartupBackfill()
{
    Section("startup backfill (InstanceStore corrects stale metadata)");

    // (a) Backfill corrects a stale instance and rewrites instances.json.
    var (dataDir, wwwroot) = SetupBackfillDir("a");
    var keyPath = Path.Combine(tempRoot, "backfill-key.pem");
    using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());

    WriteInstanceJson(dataDir, "test", revision: 1, game: "1.20.1", loader: "fabric", loaderVersion: "0.15.11");
    WritePack(wwwroot, "test", 1, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""));

    var store = new InstanceStore(dataDir, wwwroot, "https://cdn.example.com", keyPath);
    var inst = store.Get("test");
    Check(inst?.Minecraft?.GameVersion == "1.26.3", "backfill corrected game_version");
    Check(inst?.Minecraft?.LoaderVersion == "0.20.1", "backfill corrected loader_version");

    var persisted = File.ReadAllText(Path.Combine(dataDir, "instances.json"));
    Check(persisted.Contains("\"1.26.3\"", StringComparison.Ordinal) && persisted.Contains("\"0.20.1\"", StringComparison.Ordinal),
        "backfill rewrote instances.json with corrected metadata");

    // (b) Already-correct metadata -> instances.json is not rewritten.
    var (dataDir2, wwwroot2) = SetupBackfillDir("b");
    WriteInstanceJson(dataDir2, "test", revision: 1, game: "1.26.3", loader: "fabric", loaderVersion: "0.20.1");
    WritePack(wwwroot2, "test", 1, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""));
    var before = File.ReadAllBytes(Path.Combine(dataDir2, "instances.json"));
    _ = new InstanceStore(dataDir2, wwwroot2, "https://cdn.example.com", keyPath);
    var after = File.ReadAllBytes(Path.Combine(dataDir2, "instances.json"));
    Check(before.SequenceEqual(after), "no-change backfill leaves instances.json untouched");

    // (c) Missing pack -> skipped, no crash, metadata unchanged.
    var (dataDir3, wwwroot3) = SetupBackfillDir("c");
    WriteInstanceJson(dataDir3, "test", revision: 1, game: "1.20.1", loader: "fabric", loaderVersion: "0.15.11");
    var store3 = new InstanceStore(dataDir3, wwwroot3, "https://cdn.example.com", keyPath);
    Check(store3.Get("test")?.Minecraft?.GameVersion == "1.20.1", "missing pack leaves metadata unchanged");

    // (d) Corrupt pack -> skipped, no crash, metadata unchanged.
    var (dataDir4, wwwroot4) = SetupBackfillDir("d");
    WriteInstanceJson(dataDir4, "test", revision: 1, game: "1.20.1", loader: "fabric", loaderVersion: "0.15.11");
    Directory.CreateDirectory(Path.Combine(wwwroot4, "packs", "test"));
    File.WriteAllBytes(Path.Combine(wwwroot4, "packs", "test", "1.mrpack"), new byte[] { 9, 9, 9 });
    var store4 = new InstanceStore(dataDir4, wwwroot4, "https://cdn.example.com", keyPath);
    Check(store4.Get("test")?.Minecraft?.GameVersion == "1.20.1", "corrupt pack leaves metadata unchanged");
}

(string DataDir, string WwwRoot) SetupBackfillDir(string tag)
{
    var dataDir = Path.Combine(tempRoot, $"bf-{tag}-data");
    var wwwroot = Path.Combine(tempRoot, $"bf-{tag}-wwwroot");
    Directory.CreateDirectory(dataDir);
    Directory.CreateDirectory(wwwroot);
    return (dataDir, wwwroot);
}

void WriteInstanceJson(string dataDir, string id, int revision, string game, string loader, string loaderVersion)
{
    var json = $$"""
    [
      {
        "id": "{{id}}",
        "name": "T",
        "revision": {{revision}},
        "required": true,
        "minecraft": { "game_version": "{{game}}", "loader": "{{loader}}", "loader_version": "{{loaderVersion}}" },
        "pack": {
          "kind": "mrpack",
          "url": "https://cdn.example.com/packs/{{id}}/{{revision}}.mrpack",
          "sha1": "s1",
          "sha256": "s256",
          "sha512": "s512",
          "size": 312
        },
        "server": { "address": "127.0.0.1", "port": 25565 }
      }
    ]
    """;
    File.WriteAllText(Path.Combine(dataDir, "instances.json"), json);
}

void WritePack(string wwwroot, string id, int revision, string indexJson)
{
    var packDir = Path.Combine(wwwroot, "packs", id);
    Directory.CreateDirectory(packDir);
    var zipPath = Path.Combine(packDir, $"{revision}.mrpack");
    using (var fs = File.Create(zipPath))
    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("modrinth.index.json");
        using var s = entry.Open();
        s.Write(Encoding.UTF8.GetBytes(indexJson));
    }
}
