using System.IO;
using System.Text;

namespace JuChuang.Services;

/// <summary>
/// Bounds locally forwarded toast/second-launch messages. The pipe is also
/// restricted to the current Windows user. This is an input-validation boundary,
/// not a trust boundary against another process already running as that user.
/// </summary>
internal static class ActivationMessagePolicy
{
    internal const int MaximumCharacters = 2048;
    internal const int MaximumPending = 16;

    internal static bool IsSafe(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= MaximumCharacters
           && value.All(character => !char.IsControl(character) && character != '\uFFFD');

    // StreamReader.ReadLineAsync can allocate arbitrarily large strings from a
    // local sender. Read a single character at a time with a strict bound and a
    // linked cancellation deadline enforced by the caller.
    internal static async Task<string?> ReadBoundedLineAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, bufferSize: 256, leaveOpen: true);
        var buffer = new char[1];
        var result = new StringBuilder();
        var sawCarriageReturn = false;
        while (result.Length <= MaximumCharacters)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) return null; // Require a complete newline-terminated frame.
            var character = buffer[0];
            if (character == '\n')
                return IsSafe(result.ToString()) ? result.ToString() : null;
            if (character == '\r')
            {
                if (sawCarriageReturn) return null;
                sawCarriageReturn = true;
                continue;
            }
            if (sawCarriageReturn || char.IsControl(character) || character == '\uFFFD')
                return null;
            result.Append(character);
        }
        return null;
    }
}
