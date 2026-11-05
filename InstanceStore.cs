using System.Text;
using System.Text.Json;

namespace PclCmu.Admin;

/// <summary>
/// Single-writer, file-backed store for instances. The in-memory list is the
/// source of truth; every mutation serializes <c>data/instances.json</c> and
/// regenerates <c>wwwroot/manifest.json</c> atomically (write-to-temp + rename)
/// under a lock.
/// </summary>
public sealed record InstanceDeleteResult(bool Removed, string? FileCleanupError);

public sealed class InstanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private readonly string _dataDir;
    private readonly string _wwwrootDir;
    private readonly string _publicBaseUrl;
    private readonly ManifestSigner _signer;
    private readonly object _gate = new();
    private List<Instance> _instances;
    private List<Instance> _committed = [];
    public bool RecoveryRequired { get; private set; }
    internal Action<string>? Checkpoint { get; set; }
    internal string StagingDirectory => Path.Combine(_dataDir, "publishing");
    internal string PublicBaseUrl => _publicBaseUrl;

    private sealed class Publication
    {
        public byte[] Instances { get; set; } = [];
        public byte[] Manifest { get; set; } = [];
        public byte[] Signature { get; set; } = [];
        public string? StagedPack { get; set; }
        public string? InstanceId { get; set; }
        public int Revision { get; set; }
        public byte[]? Index { get; set; }
    }

    private string JournalPath => Path.Combine(_dataDir, "publication.pending.json");

    /// <summary>
    /// Creates the store and regenerates the signed manifest. Throws a clear
    /// startup error if the signing key is missing or invalid (fail closed).
    /// </summary>
    public InstanceStore(string dataDir, string wwwrootDir, string publicBaseUrl, string manifestSigningKeyPath)
    {
        _dataDir = dataDir;
        _wwwrootDir = wwwrootDir;
        _publicBaseUrl = publicBaseUrl.TrimEnd('/');
        _signer = new ManifestSigner(manifestSigningKeyPath);

        if (File.Exists(JournalPath))
        {
            var pending = JsonSerializer.Deserialize<Publication>(File.ReadAllBytes(JournalPath))
                ?? throw new InvalidDataException("Invalid publication journal; refusing to discard it.");
            ApplyPublication(pending);
        }

        _instances = LoadInstances(Path.Combine(_dataDir, "instances.json"));
        _committed = _instances.Select(Clone).ToList();

        // Correct stale Minecraft metadata from each published pack's dependency
        // section. Only rewrites instances.json when something actually changed;
        // missing/corrupt packs are skipped and logged, never fatal.
        var backfilled = BackfillMinecraftMetadata();

        // Ensure the public manifest always matches the on-disk data and the
        // current PublicBaseUrl (also produces the seed manifest on first run).
        lock (_gate)
        {
            if (backfilled)
                SaveLocked();
            else
                RegenerateManifestLocked();
        }
        // The process-wide writer lease is held before construction. Journal
        // recovery above owns any committed staging file; remaining GUID packs
        // are pre-commit remnants from interrupted validation and can be reclaimed.
        if (Directory.Exists(StagingDirectory))
            foreach (var path in Directory.EnumerateFiles(StagingDirectory, "*.mrpack"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                    DurableFile.TryDelete(path);
    }

    /// <summary>Ordered snapshot of all instances (sort ascending, then id).</summary>
    public List<Instance> List()
    {
        lock (_gate)
        {
            return _instances
                .Select(Clone)
                .OrderBy(i => i.Sort ?? 0)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public Instance? Get(string id)
    {
        lock (_gate)
        {
            var found = Find(id);
            return found is null ? null : Clone(found);
        }
    }

    public bool Exists(string id)
    {
        lock (_gate) return Find(id) is not null;
    }

    public void Create(Instance instance, bool append = false)
    {
        lock (_gate)
        {
            EnsureWritable();
            if (Find(instance.Id) is not null)
                throw new InvalidOperationException("Instance already exists.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(instance.Id, "^[a-z0-9_-]{1,64}$"))
                throw new ArgumentException("Invalid instance id.");
            if (append)
            {
                var ordered = List();
                for (var n = 0; n < ordered.Count; n++) Find(ordered[n].Id)!.Sort = n;
                instance.Sort = ordered.Count;
            }
            _instances.Add(Clone(instance));
            SaveLocked();
        }
    }

    /// <summary>Replaces metadata for an instance while preserving its pack and revision.</summary>
    public bool UpdateMetadata(Instance instance)
    {
        lock (_gate)
        {
            EnsureWritable();
            var index = IndexOf(instance.Id);
            if (index < 0)
                return false;

            var existing = _instances[index];
            instance.Revision = existing.Revision;
            instance.Pack = existing.Pack;
            instance.Sort = existing.Sort; // Editing metadata cannot undo a concurrent reorder.
            _instances[index] = Clone(instance);
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Removes an instance from the list, regenerates the signed manifest, then
    /// deletes <c>wwwroot/packs/&lt;id&gt;/</c>. The metadata commit happens first so
    /// the public manifest never advertises a pack whose files are being removed.
    /// A file cleanup failure is reported separately; the instance stays removed.
    /// </summary>
    public InstanceDeleteResult Delete(string id)
    {
        lock (_gate)
        {
            EnsureWritable();
            var index = IndexOf(id);
            if (index < 0)
                return new InstanceDeleteResult(false, null);

            _instances.RemoveAt(index);
            SaveLocked();
            return new InstanceDeleteResult(true, DeletePackDirectory(id));
        }
    }

    private string? DeletePackDirectory(string id)
    {
        try
        {
            var packsRoot = Path.GetFullPath(Path.Combine(_wwwrootDir, "packs"));
            var target = Path.GetFullPath(Path.Combine(packsRoot, id));
            var relative = Path.GetRelativePath(packsRoot, target);
            if (relative is "." or "" || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return "拒绝删除 packs 根目录之外的路径。";
            if (!Directory.Exists(target))
                return null;
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                return "目标为链接目录，已拒绝删除。";
            Directory.Delete(target, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[delete] instance \"{id}\" metadata removed but pack cleanup failed: {ex.Message}");
            return ex.Message;
        }
    }

    public void Reorder(string[] ids, string[] expected)
    {
        lock (_gate)
        {
            EnsureWritable();
            var current = List().Select(i => i.Id).ToArray();
            if (!current.SequenceEqual(expected, StringComparer.Ordinal))
                throw new InvalidOperationException("整合包列表已变化，请刷新后重新排序。");
            if (ids.Length != current.Length || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
                || !ids.ToHashSet(StringComparer.Ordinal).SetEquals(current))
                throw new ArgumentException("排序列表无效，请刷新后重试。");
            for (var n = 0; n < ids.Length; n++) Find(ids[n])!.Sort = n;
            SaveLocked();
        }
    }

    /// <summary>
    /// Records a successful upload: bumps the revision by one and stores the
    /// pack hashes/size plus the incremental file index URL/hash. Returns the new
    /// revision, or -1 if the instance is gone.
    /// </summary>
    /// <param name="expectedRevision">
    /// The revision the caller predicted (previous + 1). Used to detect a
    /// concurrent upload and fail closed instead of publishing a manifest whose
    /// files_sha256 was computed for a different revision.
    /// </param>
    public int RecordUpload(
        string id,
        string sha1,
        string sha256,
        string sha512,
        long size,
        string filesSha256,
        int expectedRevision,
        MinecraftInfo? minecraft = null)
    {
        lock (_gate)
        {
            EnsureWritable();
            var index = IndexOf(id);
            if (index < 0)
                return -1;

            var existing = _instances[index];
            var revision = existing.Revision + 1;
            if (revision != expectedRevision)
                throw new InvalidOperationException(
                    $"Upload raced: instance \"{id}\" revision is now {revision}, expected {expectedRevision}.");

            existing.Revision = revision;
            existing.Pack = new PackInfo
            {
                Kind = "mrpack",
                Url = PackUrl(id, revision),
                Sha1 = sha1,
                Sha256 = sha256,
                Sha512 = sha512,
                Size = size,
                FilesUrl = FilesUrl(id, revision),
                FilesSha256 = filesSha256,
            };
            // Correct Minecraft metadata from the pack's dependency section in the
            // same atomic write as the revision/pack bump.
            existing.ApplyMinecraft(minecraft);
            _instances[index] = existing;

            SaveLocked();
            return revision;
        }
    }

    public string GetManifestJson()
    {
        lock (_gate)
        {
            return BuildManifestJsonLocked();
        }
    }

    private Instance? Find(string id)
        => _instances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    private int IndexOf(string id)
        => _instances.FindIndex(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    private string PackUrl(string id, int revision)
        => $"{_publicBaseUrl}/packs/{id}/{revision}.mrpack";

    private string FilesUrl(string id, int revision)
        => $"{_publicBaseUrl}/packs/{id}/{revision}.files.json";

    /// <summary>
    /// Backfills stale <see cref="Instance.Minecraft"/> metadata for every
    /// published instance by reading the dependency section of the on-disk pack
    /// at <c>wwwroot/packs/&lt;id&gt;/&lt;revision&gt;.mrpack</c>. Returns true if
    /// anything changed. Missing or corrupt packs are skipped and logged.
    /// </summary>
    private bool BackfillMinecraftMetadata()
    {
        var changed = false;

        foreach (var instance in _instances)
        {
            if (instance.Revision <= 0 || instance.Pack is null)
                continue;

            var packPath = Path.Combine(_wwwrootDir, "packs", instance.Id, $"{instance.Revision}.mrpack");
            if (!File.Exists(packPath))
                continue;

            try
            {
                if (!FileIndexBuilder.TryReadMinecraftMetadata(packPath, out var parsed) || parsed is null)
                    continue;

                if (instance.ApplyMinecraft(parsed))
                {
                    changed = true;
                    Console.Error.WriteLine(
                        $"[startup] backfilled minecraft metadata for \"{instance.Id}\" from {packPath}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[startup] skipping minecraft backfill for \"{instance.Id}\" ({packPath}): {ex.Message}");
            }
        }

        return changed;
    }

    private void SaveLocked()
    {
        CommitLocked(null);
    }

    private void EnsureWritable()
    {
        if (RecoveryRequired)
            throw new PublicationPendingException("A durable publication needs recovery. Restart after resolving the disk error.");
    }

    // Called only after hashing/ZIP validation. The revision check, artifact
    // ownership and durable commit share the same lock as metadata changes.
    internal int PublishPack(string id, int baseRevision, string stagedPack,
        string sha1, string sha256, string sha512, long size, byte[] indexBytes,
        MinecraftInfo? minecraft)
    {
        lock (_gate)
        {
            EnsureWritable();
            var instance = Find(id) ?? throw new KeyNotFoundException("Instance not found.");
            if (instance.Revision != baseRevision)
                throw new InvalidOperationException("The instance changed since this upload started. Refresh before publishing.");
            var revision = checked(baseRevision + 1);
            var packDir = Path.Combine(_wwwrootDir, "packs", id);
            if (File.Exists(Path.Combine(packDir, $"{revision}.mrpack")) ||
                File.Exists(Path.Combine(packDir, $"{revision}.files.json")))
                throw new IOException("Revision artifacts already exist; refusing to overwrite them.");

            instance.Revision = revision;
            instance.Pack = new PackInfo
            {
                Url = PackUrl(id, revision), Sha1 = sha1, Sha256 = sha256,
                Sha512 = sha512, Size = size, FilesUrl = FilesUrl(id, revision),
                FilesSha256 = FileIndexBuilder.ComputeSha256(indexBytes),
            };
            instance.ApplyMinecraft(minecraft);
            CommitLocked(new Publication
            {
                StagedPack = Path.GetFileName(stagedPack), InstanceId = id,
                Revision = revision, Index = indexBytes,
            });
            return revision;
        }
    }

    private void CommitLocked(Publication? publication)
    {
        EnsureWritable();
        var committed = false;
        try
        {
        // Keep the stored pack.url (and files_url) in sync with PublicBaseUrl so
        // instances.json is always self-consistent.
        foreach (var i in _instances)
        {
            if (i.Pack is not null && i.Revision > 0)
            {
                i.Pack.Url = PackUrl(i.Id, i.Revision);
                if (i.Pack.FilesSha256 is not null)
                    i.Pack.FilesUrl = FilesUrl(i.Id, i.Revision);
            }
        }

            publication ??= new Publication();
            publication.Instances = SerializeUtf8(_instances);
            publication.Manifest = SerializeUtf8(BuildManifestLocked());
            // Sign before changing any published file.
            publication.Signature = Encoding.UTF8.GetBytes(_signer.Sign(publication.Manifest));
            Checkpoint?.Invoke("before-journal");
            DurableFile.Write(JournalPath, JsonSerializer.SerializeToUtf8Bytes(publication));
            committed = true;
            _committed = _instances.Select(Clone).ToList();
            Checkpoint?.Invoke("after-journal");
            ApplyPublication(publication);
        }
        catch (Exception ex)
        {
            if (!committed)
            {
                _instances = _committed.Select(Clone).ToList();
                throw;
            }
            RecoveryRequired = true;
            throw new PublicationPendingException("Publication is durable but needs disk recovery; its resources have been retained.", ex);
        }
    }

    private void ApplyPublication(Publication publication)
    {
        if (publication.StagedPack is not null)
        {
            if (Path.GetFileName(publication.StagedPack) != publication.StagedPack ||
                !System.Text.RegularExpressions.Regex.IsMatch(publication.InstanceId ?? "", "^[a-z0-9_-]{1,64}$") ||
                publication.Revision < 1 || publication.Index is null)
                throw new InvalidDataException("Invalid publication artifact metadata.");
            var directory = Path.Combine(_wwwrootDir, "packs", publication.InstanceId!);
            Directory.CreateDirectory(directory);
            var source = Path.Combine(StagingDirectory, publication.StagedPack);
            var target = Path.Combine(directory, $"{publication.Revision}.mrpack");
            // If DataDir and WwwRoot use different mounts, File.Move may copy.
            // Copy only into a non-served temporary extension, then rename on
            // the destination filesystem; never expose partially copied packs.
            var transfer = Path.Combine(directory, $".{publication.Revision}-{publication.StagedPack}.publishing");
            var expected = JsonSerializer.Deserialize<List<Instance>>(publication.Instances, JsonOptions)!
                .Single(i => i.Id == publication.InstanceId).Pack!;
            if (!File.Exists(target))
            {
                var transferred = false;
                if (File.Exists(source))
                {
                    if (File.Exists(transfer)) File.Delete(transfer); // Owned, interrupted transfer.
                    File.Move(source, transfer, overwrite: false);
                    transferred = true;
                    Checkpoint?.Invoke("after-transfer");
                }
                if (!transferred) VerifyPack(transfer, expected);
                File.Move(transfer, target, overwrite: false);
            }
            else VerifyPack(target, expected);
            Checkpoint?.Invoke("after-pack");
            var indexPath = Path.Combine(directory, $"{publication.Revision}.files.json");
            if (File.Exists(indexPath))
            {
                if (!File.ReadAllBytes(indexPath).SequenceEqual(publication.Index))
                    throw new IOException("Publication index conflicts with existing bytes.");
            }
            else DurableFile.Write(indexPath, publication.Index);
            Checkpoint?.Invoke("after-index");
        }
        DurableFile.Write(Path.Combine(_dataDir, "instances.json"), publication.Instances);
        Checkpoint?.Invoke("after-instances");
        // Two URL responses cannot be atomic. Clients must verify and retry a
        // mismatched pair; the journal guarantees recovery after interruption.
        DurableFile.Write(Path.Combine(_wwwrootDir, "manifest.json.sig"), publication.Signature);
        Checkpoint?.Invoke("after-signature");
        DurableFile.Write(Path.Combine(_wwwrootDir, "manifest.json"), publication.Manifest);
        Checkpoint?.Invoke("after-manifest");
        File.Delete(JournalPath);
    }

    private static void VerifyPack(string path, PackInfo expected)
    {
        if (!File.Exists(path)) throw new IOException("Publication pack is missing.");
        using var existing = File.OpenRead(path);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(existing));
        if (existing.Length != expected.Size || !digest.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Publication pack changed; refusing to advertise corrupted data.");
    }

    private Manifest BuildManifestLocked()
    {
        var manifest = new Manifest { FormatVersion = 1 };
        foreach (var i in _instances
                     .Where(i => i.Revision > 0 && i.Pack is not null)
                     .OrderBy(i => i.Sort ?? 0)
                     .ThenBy(i => i.Id, StringComparer.Ordinal))
        {
            var clone = Clone(i);
            clone.Pack!.Kind = "mrpack";
            clone.Pack.Url = PackUrl(i.Id, i.Revision);
            if (clone.Pack.FilesSha256 is not null)
                clone.Pack.FilesUrl = FilesUrl(i.Id, i.Revision);
            manifest.Instances.Add(clone);
        }

        return manifest;
    }

    private string BuildManifestJsonLocked()
        => JsonSerializer.Serialize(BuildManifestLocked(), JsonOptions);

    private void RegenerateManifestLocked()
    {
        Directory.CreateDirectory(_wwwrootDir);

        // Build the exact bytes once, write manifest.json, then sign those same
        // bytes and write the detached signature. Both writes are atomic.
        var bytes = SerializeUtf8(BuildManifestLocked());
        var sigJson = _signer.Sign(bytes);
        AtomicWriteBytes(Path.Combine(_wwwrootDir, "manifest.json.sig"), Encoding.UTF8.GetBytes(sigJson));
        AtomicWriteBytes(Path.Combine(_wwwrootDir, "manifest.json"), bytes);
    }

    private static byte[] SerializeUtf8<T>(T value)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));

    private static void AtomicWriteBytes(string path, byte[] bytes)
    {
        DurableFile.Write(path, bytes);
    }

    private static List<Instance> LoadInstances(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Instance>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("instances.json is corrupt; refusing to overwrite existing data.", ex);
        }
    }

    private static Instance Clone(Instance i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Description = i.Description,
        IconUrl = i.IconUrl,
        Sort = i.Sort,
        Revision = i.Revision,
        Required = i.Required,
        Minecraft = i.Minecraft is null
            ? null
            : new MinecraftInfo
            {
                GameVersion = i.Minecraft.GameVersion,
                Loader = i.Minecraft.Loader,
                LoaderVersion = i.Minecraft.LoaderVersion,
            },
        Pack = i.Pack is null
            ? null
            : new PackInfo
            {
                Kind = i.Pack.Kind,
                Url = i.Pack.Url,
                Sha1 = i.Pack.Sha1,
                Sha256 = i.Pack.Sha256,
                Sha512 = i.Pack.Sha512,
                Size = i.Pack.Size,
                FilesUrl = i.Pack.FilesUrl,
                FilesSha256 = i.Pack.FilesSha256,
            },
        Server = i.Server is null
            ? null
            : new ServerInfo { Address = i.Server.Address, Port = i.Server.Port },
    };
}
