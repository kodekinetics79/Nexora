using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Security
{
    /// <summary>
    /// SEC-11: validates uploaded image files by inspecting their leading "magic bytes"
    /// instead of trusting the client-supplied filename extension or Content-Type header.
    /// Files whose contents do not match a whitelisted image signature are rejected, and the
    /// safe extension is derived from the *detected* type. This prevents a disguised payload
    /// (e.g. an .html/.svg/.js file carrying script, renamed to .png) from being written to a
    /// web-served folder and later served back to a browser (stored XSS / malicious upload).
    /// </summary>
    public static class FileContentValidator
    {
        /// <summary>Outcome of a magic-byte image validation.</summary>
        public sealed record ImageValidationResult(bool IsValid, string? ContentType, string? Extension, string? Error)
        {
            public static ImageValidationResult Ok(string contentType, string extension) =>
                new(true, contentType, extension, null);

            public static ImageValidationResult Fail(string error) =>
                new(false, null, null, error);
        }

        /// <summary>
        /// Canonical content types allowed by default (web avatar / profile image uploads).
        /// </summary>
        public static readonly IReadOnlyCollection<string> DefaultAllowedContentTypes = new[]
        {
            "image/jpeg", "image/png", "image/gif", "image/bmp", "image/webp"
        };

        // Longest signature we inspect (WEBP needs 12 leading bytes: "RIFF"????"WEBP").
        private const int HeaderLength = 12;

        /// <summary>
        /// Validates that <paramref name="file"/> is a real image whose detected type is in
        /// <paramref name="allowedContentTypes"/> (defaults to <see cref="DefaultAllowedContentTypes"/>).
        /// On success the returned result carries the canonical content type and a safe extension.
        /// </summary>
        public static async Task<ImageValidationResult> ValidateImageAsync(
            IFormFile file,
            IReadOnlyCollection<string>? allowedContentTypes = null,
            CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length == 0)
                return ImageValidationResult.Fail("Uploaded file is empty.");

            allowedContentTypes ??= DefaultAllowedContentTypes;

            var header = new byte[HeaderLength];
            int read;
            await using (var stream = file.OpenReadStream())
            {
                read = await ReadAtLeastAsync(stream, header, HeaderLength, cancellationToken);
            }

            if (read < 2)
                return ImageValidationResult.Fail("Uploaded file is too small to be a valid image.");

            var detected = DetectImageType(header, read);
            if (detected == null ||
                !allowedContentTypes.Any(t => string.Equals(t, detected.Value.ContentType, StringComparison.OrdinalIgnoreCase)))
            {
                var allowed = string.Join(", ", allowedContentTypes);
                return ImageValidationResult.Fail(
                    $"Uploaded file is not a recognized/allowed image. Allowed image types: {allowed}.");
            }

            return ImageValidationResult.Ok(detected.Value.ContentType, detected.Value.Extension);
        }

        /// <summary>
        /// Returns the canonical (content type, safe extension) for a recognized image signature,
        /// or null if the leading bytes match no known image format.
        /// </summary>
        public static (string ContentType, string Extension)? DetectImageType(byte[] header, int length)
        {
            if (header == null) return null;

            // JPEG: FF D8 FF
            if (length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return ("image/jpeg", ".jpg");

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (length >= 8 &&
                header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
                return ("image/png", ".png");

            // GIF: "GIF87a" or "GIF89a"
            if (length >= 6 &&
                header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38 &&
                (header[4] == 0x37 || header[4] == 0x39) && header[5] == 0x61)
                return ("image/gif", ".gif");

            // BMP: "BM"
            if (length >= 2 && header[0] == 0x42 && header[1] == 0x4D)
                return ("image/bmp", ".bmp");

            // WEBP: "RIFF" .... "WEBP"
            if (length >= 12 &&
                header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return ("image/webp", ".webp");

            // TIFF: "II*\0" (little-endian) or "MM\0*" (big-endian)
            if (length >= 4 &&
                ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
                 (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A)))
                return ("image/tiff", ".tiff");

            return null;
        }

        private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            int total = 0;
            while (total < count)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);
                if (n == 0) break; // end of stream
                total += n;
            }
            return total;
        }
    }
}
