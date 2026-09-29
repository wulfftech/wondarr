namespace Wondarr.Core.Tagging;

/// <summary>
/// Prepares cover images for embedding.
/// </summary>
public static class CoverImage
{
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Validates that the image is a JPEG or a PNG and returns it unchanged.
    /// </summary>
    /// <param name="image">The raw image bytes.</param>
    /// <param name="maxEdge">The maximum edge in pixels; not applied yet (see remarks).</param>
    /// <returns>The image bytes, unchanged.</returns>
    /// <exception cref="ArgumentException">
    /// The image is empty or is neither a JPEG nor a PNG — MP4 accepts only those two.
    /// </exception>
    /// <remarks>
    /// // TODO(phase3): bound to maxEdge. Resizing needs an imaging package, which this task does not
    /// add; until then the caller's own bound (the source we fetched from) is what keeps covers small.
    /// </remarks>
    public static byte[] PrepareFrontCover(byte[] image, int maxEdge = 1400)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEdge);

        if (image.Length == 0 || (!StartsWith(image, JpegMagic) && !StartsWith(image, PngMagic)))
        {
            throw new ArgumentException(
                "The cover image must be a JPEG or a PNG; MP4 accepts no other format.",
                nameof(image));
        }

        return image;
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
