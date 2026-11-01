using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PclCmu.Admin;

internal static class InstanceIcons
{
    internal const int MaxBytes = 2 * 1024 * 1024;

    internal static async Task<string?> ResolveAsync(IFormCollection form, string wwwroot,
        string publicBaseUrl, CancellationToken cancellationToken)
    {
        var file = form.Files.GetFile("icon_file");
        if (file is null || file.Length == 0)
        {
            var value = form["icon_url"].ToString().Trim();
            if (value.Length == 0) return null;
            if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0)
                throw new InvalidDataException("请填写有效的 HTTP 或 HTTPS 图标地址。");
            // Keep remote URLs as references; do not fetch arbitrary URLs on the server.
            return uri.AbsoluteUri;
        }
        if (file.Length > MaxBytes) throw new InvalidDataException("图标不能超过 2 MiB。");
        await using var input = file.OpenReadStream();
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaxBytes) throw new InvalidDataException("图标不能超过 2 MiB。");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        var extension = ImageExtension(bytes);
        var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;
        // Content-addressed immutable names avoid overwriting cached or shared icons.
        DurableFile.Write(Path.Combine(wwwroot, "icons", name), bytes);
        return publicBaseUrl.TrimEnd('/') + "/icons/" + name;
    }

    // Restrict uploads to raster formats supported by the Windows launcher.
    // Validate format headers and dimensions without platform-specific GDI dependencies.
    private static string ImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 45 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadUInt32BigEndian(data[8..]) == 13 && data.Slice(12, 4).SequenceEqual("IHDR"u8)
            && data.Slice(data.Length - 12, 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }))
        {
            CheckDimensions(BinaryPrimitives.ReadUInt32BigEndian(data[16..]), BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
            return ".png";
        }
        if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8 && data[^2] == 0xff && data[^1] == 0xd9)
        {
            var pos = 2;
            while (pos + 4 <= data.Length)
            {
                if (data[pos++] != 0xff) break;
                while (pos < data.Length && data[pos] == 0xff) pos++;
                if (pos + 3 > data.Length) break;
                var marker = data[pos++];
                var length = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                if (length < 2 || pos + length > data.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 && length >= 8)
                {
                    CheckDimensions(BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 3)..]));
                    return ".jpg";
                }
                if (marker == 0xda) break;
                pos += length;
            }
        }
        throw new InvalidDataException("请选择有效的 PNG 或 JPG 图片。");
    }

    private static void CheckDimensions(uint width, uint height)
    {
        if (width is 0 or > 4096 || height is 0 or > 4096)
            throw new InvalidDataException("图标尺寸不能超过 4096 × 4096。");
    }
}
