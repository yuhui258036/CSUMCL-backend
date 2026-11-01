namespace PclCmu.Admin;

internal static class DurableFile
{
    // Flush data before the atomic rename. Temp names are unique, so a failed
    // write cannot corrupt another writer's temporary file.
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    // Cleanup failures must not hide the original error or strand worker slots.
    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"[cleanup] Could not remove {Path.GetFileName(path)}: {ex.GetType().Name}"); }
    }
}
