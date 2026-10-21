namespace IPhoneRemote.Core.Wda;

/// <summary>
/// Reads JPEG frames from WDA's MJPEG server (multipart/x-mixed-replace).
/// Rather than trusting multipart headers, it scans for JPEG SOI (FF D8) / EOI (FF D9)
/// markers, which is robust and cheap: entropy-coded JPEG data never contains a bare FF D9.
/// Scanning uses vectorized IndexOf and copies whole runs, not one byte at a time.
/// </summary>
public static class MjpegReader
{
    private const byte Marker = 0xFF, Soi = 0xD8, Eoi = 0xD9;

    /// <summary>
    /// Yields each JPEG frame. The memory is reused for the next frame, so it is only valid
    /// until the loop asks for the next one: decode or copy it before continuing.
    /// (Frames are ~200 KB, so a fresh array per frame would land on the large-object heap
    /// at 30 fps and force frequent full garbage collections.)
    /// </summary>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(
        Uri streamUri, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var res = await http.GetAsync(streamUri, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[1 << 16];
        var frame = new byte[512 * 1024];
        var frameLength = 0;
        var inFrame = false;
        byte prev = 0; // last byte of the previous chunk, for markers split across reads

        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) yield break;
            var pos = 0;
            while (pos < n)
            {
                if (!inFrame)
                {
                    var soi = FindMarkerEnd(buffer.AsSpan(0, n), pos, prev, Soi);
                    if (soi < 0) break;
                    inFrame = true;
                    frameLength = 0;
                    Append(ref frame, ref frameLength, [Marker, Soi]);
                    pos = soi + 1;
                    prev = Soi;
                }
                else
                {
                    var eoi = FindMarkerEnd(buffer.AsSpan(0, n), pos, prev, Eoi);
                    if (eoi < 0)
                    {
                        Append(ref frame, ref frameLength, buffer.AsSpan(pos, n - pos));
                        break;
                    }
                    Append(ref frame, ref frameLength, buffer.AsSpan(pos, eoi + 1 - pos));
                    inFrame = false;
                    pos = eoi + 1;
                    prev = Eoi;
                    yield return frame.AsMemory(0, frameLength);
                }
            }
            prev = buffer[n - 1];
        }
    }

    /// <summary>Index of the second byte of FF <paramref name="second"/> at or after <paramref name="from"/>, or -1.</summary>
    private static int FindMarkerEnd(ReadOnlySpan<byte> data, int from, byte prev, byte second)
    {
        // Marker split across chunks: FF ended the previous read.
        var before = from == 0 ? prev : data[from - 1];
        if (from < data.Length && before == Marker && data[from] == second) return from;

        var i = from;
        while (i < data.Length)
        {
            var k = data[i..].IndexOf(Marker);
            if (k < 0) return -1;
            i += k;
            if (i + 1 >= data.Length) return -1; // FF is the last byte; the next read checks via prev
            if (data[i + 1] == second) return i + 1;
            i++;
        }
        return -1;
    }

    private static void Append(ref byte[] frame, ref int length, ReadOnlySpan<byte> data)
    {
        if (length + data.Length > frame.Length)
            Array.Resize(ref frame, Math.Max(frame.Length * 2, length + data.Length));
        data.CopyTo(frame.AsSpan(length));
        length += data.Length;
    }
}
