namespace BaryoDev.Umbraco.SocialPost.Net;

internal static class BoundedRead
{
    /// <summary>Reads the body by counting bytes as they arrive. Null when it exceeds the cap.</summary>
    public static async Task<byte[]?> ReadAsync(HttpContent content, long max, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long declared && declared > max)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > max)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }
}
