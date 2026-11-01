using System.Security.Cryptography;

namespace PclCmu.Admin;

public sealed class PublicationPendingException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>Shared, bounded preparation for both multipart and chunk uploads.</summary>
public sealed class PackPublishService(InstanceStore store, long uploadLimitBytes)
{
    // One expensive validation job at a time; metadata commits remain short.
    private readonly SemaphoreSlim _validation = new(1, 1);

    public async Task<int> PublishAsync(string instanceId, int baseRevision, Stream input,
        string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        await _validation.WaitAsync(cancellationToken);
        string? staged = null;
        var keep = false;
        try
        {
            if (store.Get(instanceId)?.Revision != baseRevision)
                throw new InvalidOperationException("Instance revision changed or instance is missing.");
            Directory.CreateDirectory(store.StagingDirectory);
            staged = Path.Combine(store.StagingDirectory, Guid.NewGuid().ToString("N") + ".mrpack");
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            long size = 0;
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[131072];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size = checked(size + read);
                    if (size > uploadLimitBytes) throw new InvalidDataException("File exceeds the upload limit.");
                    sha1.AppendData(buffer, 0, read);
                    sha256.AppendData(buffer, 0, read);
                    sha512.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                output.Flush(flushToDisk: true);
            }
            var digest = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 is not null && !string.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Full file SHA-256 does not match.");
            cancellationToken.ThrowIfCancellationRequested();
            var build = FileIndexBuilder.Build(staged, checked(baseRevision + 1), digest, cancellationToken);
            if (build.Index.Files.Count == 0)
                throw new InvalidDataException("No safe HTTPS download entries in the pack.");
            // Do not silently advertise a partial incremental index.
            if (build.SkippedFiles.Count != 0)
                throw new InvalidDataException("Unsafe or unsupported pack entries: " + string.Join("; ", build.SkippedFiles.Take(10)));
            cancellationToken.ThrowIfCancellationRequested();
            return store.PublishPack(instanceId, baseRevision, staged,
                Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(), digest,
                Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant(), size,
                FileIndexBuilder.Serialize(build.Index), build.Minecraft);
        }
        catch (PublicationPendingException) { keep = true; throw; }
        finally
        {
            try { if (!keep && staged is not null) DurableFile.TryDelete(staged); }
            finally { _validation.Release(); }
        }
    }
}
