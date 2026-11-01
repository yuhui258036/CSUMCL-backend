using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PclCmu.Admin;

// ---------------------------------------------------------------------------
// Incremental (.mrpack) file index.
//
// When an operator uploads a pack, the server emits a per-revision
// `<rev>.files.json` next to `<rev>.mrpack` so the client can download only the
// changed files instead of the whole pack. The index is a small, public,
// non-secret document served from the immutable-cached `/packs/**` tree.
//
// Field names follow the Modrinth/manifest camelCase convention:
//   formatVersion, revision, packSha256, overridesSha256, files[].{path,
//   downloads[], hashes{sha1,sha512}, fileSize}.
// ---------------------------------------------------------------------------

public sealed class FileIndex
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("revision")] public int Revision { get; set; }

    [JsonPropertyName("packSha256")] public string PackSha256 { get; set; } = "";

    [JsonPropertyName("overridesSha256")] public string OverridesSha256 { get; set; } = "";

    [JsonPropertyName("files")] public List<FileIndexEntry> Files { get; set; } = [];
}

public sealed class FileIndexEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = [];

    [JsonPropertyName("hashes")] public FileIndexHashes Hashes { get; set; } = new();

    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
}

public sealed class FileIndexHashes
{
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }

    [JsonPropertyName("sha512")] public string? Sha512 { get; set; }
}

/// <summary>Result of building an index, including the files that were dropped.</summary>
public sealed class FileIndexBuildResult
{
    public FileIndex Index { get; init; } = new();

    /// <summary>
    /// Minecraft metadata parsed from the pack's <c>dependencies</c> section, or
    /// null when the index has no <c>dependencies</c> object. Not serialized into
    /// the on-disk file index; used to keep <see cref="Instance.Minecraft"/> in
    /// sync with the published pack.
    /// </summary>
    public MinecraftInfo? Minecraft { get; init; }

    /// <summary>Human-readable reasons for files that were not included.</summary>
    public List<string> SkippedFiles { get; init; } = [];
}

public static class FileIndexBuilder
{
    public const string IndexEntryName = "modrinth.index.json";

    private const string OverridesPrefix = "overrides/";
    private const string ClientOverridesPrefix = "client-overrides/";

    private static readonly byte[] NulByte = [0x00];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
    };

    /// <summary>
    /// Builds the file index for the pack at <paramref name="zipPath"/>. Throws
    /// <see cref="InvalidDataException"/> if the archive is missing or has a
    /// corrupt <c>modrinth.index.json</c>.
    /// </summary>
    public static FileIndexBuildResult Build(string zipPath, int revision, string packSha256, CancellationToken cancellationToken = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        ValidateArchive(zip);

        var overridesSha256 = ComputeOverridesSha256(zip, cancellationToken);
        var (files, skipped, minecraft) = BuildFiles(zip);

        var index = new FileIndex
        {
            FormatVersion = 1,
            Revision = revision,
            PackSha256 = packSha256,
            OverridesSha256 = overridesSha256,
            Files = files,
        };

        return new FileIndexBuildResult { Index = index, Minecraft = minecraft, SkippedFiles = skipped };
    }

    /// <summary>
    /// Reads only the <c>modrinth.index.json</c> entry (no override bytes) and
    /// parses its <c>dependencies</c> into Minecraft metadata. Returns false when
    /// the archive is missing/corrupt, the index is missing/invalid, or the entry
    /// cannot be read; never throws for malformed packs. Used by the startup
    /// backfill to correct stale instance metadata.
    /// </summary>
    public static bool TryReadMinecraftMetadata(string zipPath, out MinecraftInfo? minecraft)
    {
        minecraft = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, IndexEntryName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return false;
            if (entry.Length > 16 * 1024 * 1024)
                return false;

            using var stream = entry.Open();
            var dto = JsonSerializer.Deserialize<ModrinthIndexDto>(stream, JsonOptions);
            if (dto is null)
                return false;

            minecraft = ParseMinecraft(dto.Dependencies);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            minecraft = null;
            return false;
        }
    }

    /// <summary>Serializes the index to compact JSON (UTF-8, no BOM).</summary>
    public static byte[] Serialize(FileIndex index)
        => JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);

    /// <summary>Lowercase hex SHA-256 of the exact bytes (the manifest's files_sha256).</summary>
    public static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Atomic write (temp + rename) so a reader never sees a partial file.</summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Rejects empty, rooted (leading <c>/</c> or <c>\</c>), Windows drive-letter,
    /// or traversal (<c>..</c>, <c>.</c>, empty segment) relative paths. Modrinth
    /// index paths are forward-slash relative paths into the instance directory.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // Rooted (POSIX or Windows).
        if (path[0] == '/' || path[0] == '\\')
            return false;

        // Windows drive-letter absolute path, e.g. "C:" or "c:foo".
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return false;

        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ files

    private static (List<FileIndexEntry> Files, List<string> Skipped, MinecraftInfo? Minecraft) BuildFiles(ZipArchive zip)
    {
        var indexEntry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, IndexEntryName, StringComparison.OrdinalIgnoreCase));

        if (indexEntry is null)
            throw new InvalidDataException("The ZIP must contain a modrinth.index.json entry.");
        if (indexEntry.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("modrinth.index.json exceeds 16 MiB.");

        ModrinthIndexDto dto;
        try
        {
            using var indexStream = indexEntry.Open();
            dto = JsonSerializer.Deserialize<ModrinthIndexDto>(indexStream, JsonOptions)
                  ?? throw new InvalidDataException("modrinth.index.json is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"modrinth.index.json is not valid JSON: {ex.Message}");
        }

        var files = new List<FileIndexEntry>();
        var skipped = new List<string>();

        foreach (var f in dto.Files ?? [])
        {
            if (f is null) throw new InvalidDataException("modrinth.index.json contains a null file entry.");
            var path = f.Path ?? "";

            if (!IsSafeRelativePath(path))
            {
                skipped.Add($"{(path.Length == 0 ? "<empty>" : path)} (unsafe path)");
                continue;
            }

            var downloads = (f.Downloads ?? [])
                .Where(IsHttpsUrl)
                .ToList();

            if (downloads.Count == 0)
            {
                skipped.Add($"{path} (no HTTPS download URL)");
                continue;
            }

            files.Add(new FileIndexEntry
            {
                Path = path,
                Downloads = downloads,
                Hashes = new FileIndexHashes
                {
                    Sha1 = NormalizeHex(f.Hashes?.Sha1),
                    Sha512 = NormalizeHex(f.Hashes?.Sha512),
                },
                FileSize = f.FileSize,
            });
        }

        return (files, skipped, ParseMinecraft(dto.Dependencies));
    }

    private static bool IsHttpsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps;
    }

    // ------------------------------------------------------------- dependencies

    /// <summary>
    /// Maps the Modrinth <c>dependencies</c> object to Minecraft metadata:
    ///   minecraft     -> GameVersion
    ///   fabric-loader -> Loader "fabric" + LoaderVersion
    ///   forge         -> Loader "forge"
    ///   neoforge      -> Loader "neoforge"
    ///   quilt-loader  -> Loader "quilt"
    /// Returns null when there is no <c>dependencies</c> object or it names
    /// neither a Minecraft nor a loader dependency. A missing game version is
    /// represented as an empty GameVersion (callers keep the existing game);
    /// a missing loader is represented as an empty Loader (vanilla).
    /// </summary>
    private static MinecraftInfo? ParseMinecraft(Dictionary<string, JsonElement>? dependencies)
    {
        var game = DependencyString(dependencies, "minecraft");

        string? loader = null;
        string? loaderVersion = null;

        var fabric = DependencyString(dependencies, "fabric-loader");
        if (fabric is not null)
        {
            loader = "fabric";
            loaderVersion = fabric;
        }
        else if (DependencyString(dependencies, "forge") is string forge)
        {
            loader = "forge";
            loaderVersion = forge;
        }
        else if (DependencyString(dependencies, "neoforge") is string neoforge)
        {
            loader = "neoforge";
            loaderVersion = neoforge;
        }
        else if (DependencyString(dependencies, "quilt-loader") is string quilt)
        {
            loader = "quilt";
            loaderVersion = quilt;
        }

        if (game is null && loader is null)
            return null;

        return new MinecraftInfo
        {
            GameVersion = game ?? "",
            Loader = loader ?? "",
            LoaderVersion = loaderVersion ?? "",
        };
    }

    /// <summary>
    /// Reads a dependency value that may be a JSON string or (for legacy packs) an
    /// array of strings, returning the first non-empty string or null.
    /// </summary>
    private static string? DependencyString(Dictionary<string, JsonElement>? dependencies, string key)
    {
        if (dependencies is null || !dependencies.TryGetValue(key, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    return s;
            }
        }

        return null;
    }

    private static string? NormalizeHex(string? hex)
        => string.IsNullOrWhiteSpace(hex) ? null : hex.ToLowerInvariant();

    // ---------------------------------------------------------------- overrides

    /// <summary>
    /// Deterministic SHA-256 over the pack's override entries. The client computes
    /// the same value so it can detect config/override changes and fall back to a
    /// full-pack download when they differ.
    ///
    /// Algorithm (MUST match the client):
    ///   1. Collect every non-directory ZIP entry whose full name starts with
    ///      "overrides/" or "client-overrides/" (ordinal, case-sensitive).
    ///   2. Sort those entries by full name using ordinal comparison.
    ///   3. Feed into one SHA-256: UTF-8(full name) || 0x00 || entry bytes, for
    ///      each entry in sorted order.
    ///   4. Return the lowercase hex digest.
    /// </summary>
    private static void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > 100000) throw new InvalidDataException("Archive has too many entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Archive expanded size exceeds 8 GiB.");
            if (!IsSafeRelativePath(entry.FullName.TrimEnd('/')) || !names.Add(entry.FullName))
                throw new InvalidDataException("Unsafe or duplicate archive entry.");
        }
    }

    private static string ComputeOverridesSha256(ZipArchive zip, CancellationToken cancellationToken)
    {
        var entries = zip.Entries
            .Where(e => !e.FullName.EndsWith('/'))
            .Where(e => e.FullName.StartsWith(OverridesPrefix, StringComparison.Ordinal)
                        || e.FullName.StartsWith(ClientOverridesPrefix, StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long expanded = 0;

        foreach (var entry in entries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.FullName));
            hash.AppendData(NulByte);

            using var stream = entry.Open();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expanded += read;
                if (expanded > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded overrides exceed 8 GiB.");
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // --------------------------------------------------------------------- DTOs

    private sealed class ModrinthIndexDto
    {
        [JsonPropertyName("files")] public List<ModrinthFileDto>? Files { get; set; }

        [JsonPropertyName("dependencies")] public Dictionary<string, JsonElement>? Dependencies { get; set; }
    }

    private sealed class ModrinthFileDto
    {
        [JsonPropertyName("path")] public string? Path { get; set; }

        [JsonPropertyName("downloads")] public List<string>? Downloads { get; set; }

        [JsonPropertyName("hashes")] public ModrinthHashesDto? Hashes { get; set; }

        [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    }

    private sealed class ModrinthHashesDto
    {
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }

        [JsonPropertyName("sha512")] public string? Sha512 { get; set; }
    }
}
