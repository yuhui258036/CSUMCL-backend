using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace PclCmu.Admin;

public sealed class ChunkUploadOptions
{
    public int ChunkSizeBytes { get; set; } = 2 * 1024 * 1024;
    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long QuotaBytes { get; set; } = 8L * 1024 * 1024 * 1024;
    public long MinFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaxSessions { get; set; } = 100;
    public int MaxConcurrentChunks { get; set; } = 4;
    public int IdleHours { get; set; } = 24;
    public int TokenMinutes { get; set; } = 60;
    public bool PublicIngressEnabled { get; set; }
    public string PublicUploadBaseUrl { get; set; } = "";
    public string[] AllowedOrigins { get; set; } = [];

    public void Validate()
    {
        if (ChunkSizeBytes < 64 * 1024 || ChunkSizeBytes > 8 * 1024 * 1024 ||
            MaxFileBytes < 1 || MaxFileBytes > 2L * 1024 * 1024 * 1024 ||
            QuotaBytes < MaxFileBytes * 2 || MinFreeBytes < 0 ||
            MaxSessions is < 1 or > 1000 || MaxConcurrentChunks is < 1 or > 16 ||
            IdleHours is < 1 or > 168 || TokenMinutes is < 1 or > 1440)
            throw new ArgumentException("Invalid chunk upload limits.");
        if (PublicIngressEnabled && (!Uri.TryCreate(PublicUploadBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.UserInfo.Length != 0))
            throw new ArgumentException("PublicUploadBaseUrl must be an HTTPS origin.");
        foreach (var origin in AllowedOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                originUri.GetLeftPart(UriPartial.Authority) != origin ||
                (originUri.Scheme != "https" && !(originUri.Scheme == "http" && originUri.IsLoopback)))
                throw new ArgumentException("AllowedOrigins must contain exact HTTPS or loopback HTTP origins.");
    }
}

public sealed class UploadApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record CreateUpload(string InstanceId, string FileName, long Size, string Sha256, int BaseRevision);
public sealed record UploadGrant(string Token, DateTimeOffset ExpiresUtc);
public sealed record UploadStatus(string Id, string InstanceId, string FileName, long Size, string Sha256,
    int BaseRevision, int ChunkSize, int ChunkCount, int[] Received, string State,
    int? Revision, string? Error, DateTimeOffset ExpiresUtc, string ChunkBaseUrl);

// One administrator owns these sessions. Reauthentication after a restart must
// not make uploads inaccessible: cookie session IDs are deliberately not owners.
public sealed class ChunkUploadService : BackgroundService
{
    private sealed class Session
    {
        public string Id { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public int BaseRevision { get; set; }
        public int ChunkSize { get; set; }
        public string State { get; set; } = "receiving";
        public int? Revision { get; set; }
        public string? Error { get; set; }
        public string TokenHash { get; set; } = "";
        public DateTimeOffset TokenExpiresUtc { get; set; }
        public DateTimeOffset TouchedUtc { get; set; } = DateTimeOffset.UtcNow;
    }
    private sealed class LiveSession(Session data)
    {
        public Session Data { get; } = data;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ConcurrentDictionary<int, SemaphoreSlim> ChunkGates { get; } = new();
        public int Writers;
    }

    private readonly string _root;
    private readonly string _wwwroot;
    private readonly InstanceStore _store;
    private readonly PackPublishService _publisher;
    public ChunkUploadOptions Options { get; }
    public bool StorageUnavailable { get; private set; }
    internal Action<string>? SaveCheckpoint { get; set; }
    private readonly ConcurrentDictionary<string, LiveSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly SemaphoreSlim _writers;
    private readonly Channel<string> _queue;
    private static readonly Regex Hex64 = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    public ChunkUploadService(string dataDir, string wwwroot, InstanceStore store,
        PackPublishService publisher, ChunkUploadOptions options)
    {
        options.Validate();
        _root = Path.Combine(dataDir, "uploads");
        _wwwroot = wwwroot;
        _store = store;
        _publisher = publisher;
        Options = options;
        _writers = new(options.MaxConcurrentChunks, options.MaxConcurrentChunks);
        _queue = Channel.CreateBounded<string>(options.MaxSessions);
        Directory.CreateDirectory(_root);
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            var metadata = Path.Combine(directory, "session.json");
            if (!File.Exists(metadata)) continue;
            // Corrupt state fails closed and is retained for inspection.
            var data = JsonSerializer.Deserialize<Session>(File.ReadAllBytes(metadata))
                ?? throw new InvalidDataException("Corrupt upload session: " + id);
            if (data.Id != id || !Regex.IsMatch(data.InstanceId, "^[a-z0-9_-]{1,64}$") ||
                !Hex64.IsMatch(data.Sha256) || data.Size <= 0 || data.Size > 2L * 1024 * 1024 * 1024 ||
                data.ChunkSize < 64 * 1024 || data.ChunkSize > 8 * 1024 * 1024 || data.BaseRevision < 0)
                throw new InvalidDataException("Invalid persisted upload: " + id);
            foreach (var temp in Directory.EnumerateFiles(directory, "*.tmp")) File.Delete(temp);
            if (data.State is "queued" or "verifying")
            {
                if (WasPublished(data)) { data.State = "completed"; data.Revision = data.BaseRevision + 1; }
                else data.State = "queued";
                Save(data);
            }
            _sessions[id] = new(data);
            if (data.State is "completed" or "cancelled") DeleteChunks(data);
            if (data.State == "queued" && !_queue.Writer.TryWrite(id))
                throw new InvalidDataException("Too many queued uploads; increase MaxSessions to recover.");
        }
    }

    public async Task<UploadStatus> CreateAsync(CreateUpload request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.InstanceId) || !Regex.IsMatch(request.InstanceId, "^[a-z0-9_-]{1,64}$") ||
            string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 200 ||
            !request.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
            request.Sha256 is null || !Hex64.IsMatch(request.Sha256) || request.Size <= 0 ||
            request.Size > Options.MaxFileBytes || request.BaseRevision < 0)
            throw new UploadApiException(400, "Invalid file name, size, SHA-256 or revision.");
        await _creation.WaitAsync(ct);
        try
        {
            // Idempotent creation for response loss / browser refresh.
            foreach (var live in _sessions.Values)
            {
                await live.Gate.WaitAsync(ct);
                try
                {
                    var s = live.Data;
                    if (s.InstanceId == request.InstanceId && s.Sha256.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase) &&
                        s.Size == request.Size && s.BaseRevision == request.BaseRevision &&
                        s.State is "receiving" or "queued" or "verifying" or "completed" && !Expired(s))
                        return Status(s);
                }
                finally { live.Gate.Release(); }
            }
            var instance = _store.Get(request.InstanceId) ?? throw new UploadApiException(404, "Instance not found.");
            if (instance.Revision != request.BaseRevision) throw new UploadApiException(409, "Instance revision changed.");
            // Failed tasks still retain resumable bytes until cancelled/expired.
            var active = _sessions.Values.Select(x => x.Data).Where(x =>
                x.State is not ("completed" or "cancelled" or "expired") || HasChunks(x)).ToList();
            if (_sessions.Count >= Options.MaxSessions || active.Sum(x => x.Size * 2) + request.Size * 2 > Options.QuotaBytes)
                throw new UploadApiException(507, "Upload storage quota reached; cancel unused tasks or wait for expiry.");
            CheckFreeSpace(request.Size * 2);
            var session = new Session
            {
                Id = Guid.NewGuid().ToString("N"), InstanceId = request.InstanceId,
                FileName = request.FileName, Size = request.Size, Sha256 = request.Sha256.ToLowerInvariant(),
                BaseRevision = request.BaseRevision, ChunkSize = Options.ChunkSizeBytes,
            };
            Save(session);
            _sessions[session.Id] = new(session);
            return Status(session);
        }
        finally { _creation.Release(); }
    }

    public async Task<UploadStatus> GetAsync(string id, CancellationToken ct)
    {
        var live = Find(id);
        await live.Gate.WaitAsync(ct);
        try { EnsureAlive(live.Data); return Status(live.Data); }
        finally { live.Gate.Release(); }
    }

    public async Task<UploadGrant> RenewAsync(string id, CancellationToken ct)
    {
        var live = Find(id);
        await live.Gate.WaitAsync(ct);
        try
        {
            EnsureReceiving(live.Data);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            live.Data.TokenHash = Hash(Encoding.UTF8.GetBytes(token));
            live.Data.TokenExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(Options.TokenMinutes);
            live.Data.TouchedUtc = DateTimeOffset.UtcNow;
            Save(live.Data);
            return new(token, live.Data.TokenExpiresUtc);
        }
        finally { live.Gate.Release(); }
    }

    public async Task PutChunkAsync(string id, int index, string token, string expectedHash,
        long? contentLength, Stream input, CancellationToken ct)
    {
        var live = Find(id);
        if (expectedHash is null || !Hex64.IsMatch(expectedHash)) throw new UploadApiException(400, "X-Chunk-SHA256 is required.");
        expectedHash = expectedHash.ToLowerInvariant();
        await live.Gate.WaitAsync(ct);
        try
        {
            var s = live.Data;
            EnsureReceiving(s);
            if (token.Length != 64 || s.TokenExpiresUtc <= DateTimeOffset.UtcNow ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(Encoding.UTF8.GetBytes(token))), Encoding.ASCII.GetBytes(s.TokenHash)))
                throw new UploadApiException(401, "Upload grant expired or invalid; log in and renew it.");
            if (index < 0 || index >= Count(s)) throw new UploadApiException(400, "Chunk index out of range.");
            if (contentLength != Length(s, index)) throw new UploadApiException(400, "Content-Length must equal the exact chunk size.");
            if (live.Writers >= Options.MaxConcurrentChunks) throw new UploadApiException(429, "Too many chunks in flight.");
            live.Writers++;
        }
        finally { live.Gate.Release(); }
        var slot = false;
        var locked = false;
        string? temporary = null;
        var chunkGate = live.ChunkGates.GetOrAdd(index, _ => new(1, 1));
        try
        {
            // Reject instead of accumulating unbounded requests waiting for RAM/disk.
            if (!await _writers.WaitAsync(0, ct)) throw new UploadApiException(429, "Upload workers busy; retry this chunk.");
            slot = true;
            await chunkGate.WaitAsync(ct);
            locked = true;
            var existing = ChunkFile(live.Data, index);
            if (existing is not null)
            {
                if (Path.GetFileNameWithoutExtension(existing) != $"{index}-{expectedHash}")
                    throw new UploadApiException(409, "This chunk already has different content.");
                return; // Verified durable receipt; retry after lost response is safe.
            }
            CheckFreeSpace(Length(live.Data, index));
            temporary = Path.Combine(DirectoryFor(id), $"{index}-{Guid.NewGuid():N}.tmp");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > contentLength) throw new UploadApiException(413, "Chunk exceeds declared size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total != contentLength) throw new UploadApiException(400, "Incomplete chunk.");
                output.Flush(flushToDisk: true);
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new UploadApiException(422, "Chunk SHA-256 mismatch.");
            File.Move(temporary, Path.Combine(DirectoryFor(id), $"{index}-{expectedHash}.chunk"), overwrite: false);
            await live.Gate.WaitAsync(CancellationToken.None);
            try { live.Data.TouchedUtc = DateTimeOffset.UtcNow; Save(live.Data); }
            finally { live.Gate.Release(); }
        }
        finally
        {
            try { if (temporary is not null) DurableFile.TryDelete(temporary); }
            finally
            {
                if (locked) chunkGate.Release();
                if (slot) _writers.Release();
                await live.Gate.WaitAsync(CancellationToken.None);
                try { live.Writers--; }
                finally { live.Gate.Release(); }
            }
        }
    }

    public async Task<UploadStatus> CompleteAsync(string id, CancellationToken ct)
    {
        var live = Find(id);
        await live.Gate.WaitAsync(ct);
        try
        {
            var s = live.Data;
            EnsureAlive(s);
            if (s.State is "completed" or "queued" or "verifying") return Status(s);
            EnsureReceiving(s);
            if (live.Writers != 0 || Received(s).Length != Count(s)) throw new UploadApiException(409, "Chunks still missing or in flight.");
            s.State = "queued";
            s.TouchedUtc = DateTimeOffset.UtcNow;
            try { Save(s); } // Persist before enqueue; startup resumes this job.
            catch { s.State = "receiving"; throw; }
            if (!_queue.Writer.TryWrite(id)) throw new IOException("Upload queue is full; queued job retained for recovery.");
            return Status(s);
        }
        finally { live.Gate.Release(); }
    }

    public async Task CancelAsync(string id, CancellationToken ct)
    {
        var live = Find(id);
        await live.Gate.WaitAsync(ct);
        try
        {
            if (live.Data.State is "queued" or "verifying" || live.Writers != 0)
                throw new UploadApiException(409, "Wait for active requests/verification to finish before cancelling.");
            if (live.Data.State == "completed") throw new UploadApiException(409, "Published resources cannot be cancelled.");
            live.Data.State = "cancelled";
            live.Data.TokenHash = "";
            Save(live.Data);
            DeleteChunks(live.Data);
        }
        finally { live.Gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var cleanupStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var cleanup = CleanupLoopAsync(cleanupStop.Token);
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_sessions.TryGetValue(id, out var live)) continue;
                try
                {
                    try { await ProcessAsync(live, stoppingToken); }
                    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or KeyNotFoundException)
                    {
                        await live.Gate.WaitAsync(stoppingToken);
                        try
                        {
                            live.Data.State = "failed";
                            live.Data.Error = ex.Message;
                            Save(live.Data);
                            Console.Error.WriteLine($"[upload] {id}: {ex.GetType().Name}: {ex.Message}");
                        }
                        finally { live.Gate.Release(); }
                    }
                    StorageUnavailable = false;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (PublicationPendingException) { /* Store journal owns recovery; restart after disk repair. */ }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A temporary session write/cleanup failure must not crash
                    // the public download server. Keep the durable job and retry;
                    // new upload mutations are held while storage is unavailable.
                    StorageUnavailable = true;
                    Console.Error.WriteLine($"[upload-storage] {id}: {ex.GetType().Name}; retrying persisted job");
                    await live.Gate.WaitAsync(stoppingToken);
                    try { live.Data.State = "queued"; }
                    finally { live.Gate.Release(); }
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    if (!_queue.Writer.TryWrite(id))
                        throw new InvalidOperationException("Upload queue invariant violated: current job has no reserved slot.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { cleanupStop.Cancel(); await cleanup; }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                foreach (var live in _sessions.Values)
                {
                    await live.Gate.WaitAsync(ct);
                    try
                    {
                        var s = live.Data;
                        if (!Expired(s) || live.Writers != 0 || s.State is "queued" or "verifying") continue;
                        s.State = "expired";
                        Save(s);
                        // Only service-owned GUID directories under the private upload root.
                        Directory.Delete(DirectoryFor(s.Id), recursive: true);
                        _sessions.TryRemove(s.Id, out _);
                    }
                    catch (IOException ex) { Console.Error.WriteLine($"[upload-cleanup] {live.Data.Id}: {ex.GetType().Name}"); }
                    catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"[upload-cleanup] {live.Data.Id}: {ex.GetType().Name}"); }
                    finally { live.Gate.Release(); }
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ProcessAsync(LiveSession live, CancellationToken ct)
    {
        var s = live.Data;
        await live.Gate.WaitAsync(ct);
        try
        {
            if (s.State != "queued") return;
            // Publication can finish just before the session receipt write fails.
            // Inspect the immutable revision before retrying, never increment twice.
            if (WasPublished(s))
            {
                MarkCompleted(s, s.BaseRevision + 1);
                return;
            }
            s.State = "verifying";
            Save(s);
        }
        finally { live.Gate.Release(); }
        await using var input = new ChunkReadStream(Enumerable.Range(0, Count(s)).Select(i =>
            ChunkFile(s, i) ?? throw new InvalidDataException("A chunk is missing.")));
        var revision = await _publisher.PublishAsync(s.InstanceId, s.BaseRevision, input, s.Sha256, ct);
        await live.Gate.WaitAsync(CancellationToken.None);
        try { MarkCompleted(s, revision); }
        finally { live.Gate.Release(); }
    }

    private void MarkCompleted(Session s, int revision)
    {
        s.Revision = revision;
        s.State = "completed";
        s.Error = null;
        s.TokenHash = "";
        s.TouchedUtc = DateTimeOffset.UtcNow;
        Save(s);
        DeleteChunks(s);
    }

    private bool WasPublished(Session s)
    {
        var path = Path.Combine(_wwwroot, "packs", s.InstanceId, $"{s.BaseRevision + 1}.files.json");
        if (!File.Exists(path) || _store.Get(s.InstanceId)?.Revision < s.BaseRevision + 1) return false;
        try
        {
            var index = JsonSerializer.Deserialize<FileIndex>(File.ReadAllBytes(path));
            return index?.PackSha256 == s.Sha256;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Published file index is corrupt; inspect the stored revision before retrying.", ex);
        }
    }
    private LiveSession Find(string id) => Guid.TryParseExact(id, "N", out _) && _sessions.TryGetValue(id, out var s)
        ? s : throw new UploadApiException(404, "Upload not found.");
    private string DirectoryFor(string id) => Path.Combine(_root, id);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private void Save(Session s)
    {
        SaveCheckpoint?.Invoke(s.State);
        DurableFile.Write(Path.Combine(DirectoryFor(s.Id), "session.json"), JsonSerializer.SerializeToUtf8Bytes(s));
    }
    private static int Count(Session s) => checked((int)((s.Size + s.ChunkSize - 1) / s.ChunkSize));
    private static long Length(Session s, int index) => Math.Min(s.ChunkSize, s.Size - (long)index * s.ChunkSize);
    private bool Expired(Session s) => s.TouchedUtc.AddHours(Options.IdleHours) < DateTimeOffset.UtcNow;
    private void EnsureAlive(Session s) { if (Expired(s) || s.State is "cancelled" or "expired") throw new UploadApiException(410, "Upload expired or cancelled."); }
    private void EnsureReceiving(Session s) { EnsureAlive(s); if (s.State != "receiving") throw new UploadApiException(409, "Upload is not receiving chunks."); }
    private string? ChunkFile(Session s, int index) => Directory.EnumerateFiles(DirectoryFor(s.Id), $"{index}-*.chunk")
        .SingleOrDefault(p => new FileInfo(p).Length == Length(s, index));
    private int[] Received(Session s)
    {
        if (s.State == "completed") return [];
        var received = new HashSet<int>();
        foreach (var path in Directory.EnumerateFiles(DirectoryFor(s.Id), "*.chunk"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var dash = name.IndexOf('-');
            if (dash < 1 || !int.TryParse(name[..dash], out var index) || index < 0 || index >= Count(s) ||
                !Hex64.IsMatch(name[(dash + 1)..]) || new FileInfo(path).Length != Length(s, index))
                throw new InvalidDataException("Invalid persisted chunk receipt.");
            if (!received.Add(index)) throw new InvalidDataException("Conflicting persisted chunk receipts.");
        }
        return received.Order().ToArray();
    }
    private UploadStatus Status(Session s) => new(s.Id, s.InstanceId, s.FileName, s.Size, s.Sha256,
        s.BaseRevision, s.ChunkSize, Count(s), Received(s), s.State, s.Revision, s.Error,
        s.TouchedUtc.AddHours(Options.IdleHours), Options.PublicIngressEnabled
            ? Options.PublicUploadBaseUrl.TrimEnd('/') + "/api/uploads/" + s.Id + "/chunks/"
            : "/admin/api/uploads/" + s.Id + "/chunks/");
    private void DeleteChunks(Session s) { foreach (var path in Directory.EnumerateFiles(DirectoryFor(s.Id), "*.chunk")) File.Delete(path); }
    private bool HasChunks(Session s)
    {
        try { return Directory.EnumerateFiles(DirectoryFor(s.Id), "*.chunk").Any(); }
        catch (DirectoryNotFoundException) { return false; } // Expiry cleanup may finish concurrently.
    }
    private void CheckFreeSpace(long required)
    {
        var fullPath = Path.GetFullPath(_root);
        var drive = DriveInfo.GetDrives().Where(d => fullPath.StartsWith(d.Name,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(d => d.Name.Length).First();
        var free = drive.AvailableFreeSpace;
        if (free - required < Options.MinFreeBytes) throw new UploadApiException(507, "Insufficient free disk space.");
    }

    private sealed class ChunkReadStream(IEnumerable<string> paths) : Stream
    {
        private readonly IEnumerator<string> _paths = paths.GetEnumerator();
        private FileStream? _current;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                if (_current is null)
                {
                    if (!_paths.MoveNext()) return 0;
                    _current = new FileStream(_paths.Current, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                var read = await _current.ReadAsync(buffer, ct);
                if (read > 0) return read;
                await _current.DisposeAsync();
                _current = null;
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) { _current?.Dispose(); _paths.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
