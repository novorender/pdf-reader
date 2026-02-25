using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NovoRender.PDFReader;

public static partial class PdfDocumentId
{
    /// <summary>
    /// Extracts a stable document identifier from a PDF file.
    /// Per the PDF spec (§14.4), the trailer may contain an /ID entry - an array of two
    /// byte-strings. The first element is a permanent identifier based on the file's
    /// original contents and is stable across saves; we use that as the document ID.
    /// The /ID value can appear as a hex string (&lt;...&gt;) or a literal string ((...));
    /// both forms are handled and normalized to a lowercase hex string.
    /// If no /ID entry is found, falls back to a SHA-256 hash of the entire file.
    /// </summary>
    public static string GetDocumentId(FileInfo file)
    {
        const int initialWindow = 8192;
        const int maxWindow = 1024 * 1024;

        using var stream = file.OpenRead();
        var length = stream.Length;
        var windowSize = (int)Math.Min(length, initialWindow);

        while (windowSize > 0)
        {
            stream.Seek(-windowSize, SeekOrigin.End);
            var buffer = new byte[windowSize];
            stream.ReadExactly(buffer);
            var tail = Encoding.Latin1.GetString(buffer);

            if (TryExtractId(tail, out var id))
            {
                return id;
            }

            if (windowSize >= length || windowSize >= maxWindow)
            {
                break;
            }

            windowSize = (int)Math.Min(Math.Min(windowSize * 2L, maxWindow), length);
        }

        // Fallback: SHA-256 of the entire file
        stream.Seek(0, SeekOrigin.Begin);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    public static bool TryExtractId(string tail, out string id)
    {
        // Look for /ID [ <hex> ... ] or /ID [ (...) ... ]
        var match = HexStringIdRegex().Match(tail);
        if (match.Success)
        {
            id = WhitespaceRegex().Replace(match.Groups[1].Value, "").ToLowerInvariant();
            return true;
        }

        match = LiteralStringIdRegex().Match(tail);
        if (match.Success)
        {
            id = Convert.ToHexStringLower(DecodePdfLiteralString(match.Groups[1].Value));
            return true;
        }

        id = null!;
        return false;
    }

    public static byte[] DecodePdfLiteralString(string raw)
    {
        var bytes = new List<byte>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                i++;
                switch (raw[i])
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '(': bytes.Add((byte)'('); break;
                    case ')': bytes.Add((byte)')'); break;
                    case '\\': bytes.Add((byte)'\\'); break;
                    default:
                        // Octal escape: up to 3 digits
                        if (raw[i] is >= '0' and <= '7')
                        {
                            var octal = raw[i] - '0';
                            for (var j = 0; j < 2 && i + 1 < raw.Length && raw[i + 1] is >= '0' and <= '7'; j++)
                            {
                                i++;
                                octal = octal * 8 + (raw[i] - '0');
                            }
                            bytes.Add((byte)(octal & 0xFF));
                        }
                        else
                        {
                            // Unknown escape - ignore the backslash per PDF spec
                            bytes.Add((byte)raw[i]);
                        }
                        break;
                }
            }
            else
            {
                bytes.Add((byte)raw[i]);
            }
        }
        return bytes.ToArray();
    }

    [GeneratedRegex(@"/ID\s*\[\s*<([0-9A-Fa-f\s]+)>")]
    private static partial Regex HexStringIdRegex();

    [GeneratedRegex(@"/ID\s*\[\s*\(((?:\\.|[^)\\])*)\)")]
    private static partial Regex LiteralStringIdRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
