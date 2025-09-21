using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Chat.Shared.Net
{
    /// <summary>
    /// Framing length-prefixed: 4 bytes (Int32 little-endian) + payload.
    /// </summary>
    public static class Framing
    {
        public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct = default)
        {
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, payload.Length);
            await stream.WriteAsync(lenBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
        {
            var lenBytes = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
            if (lenBytes is null) return null;

            int len = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
            if (len < 0) return null; // inválido

            return await ReadExactAsync(stream, len, ct).ConfigureAwait(false);
        }

        private static async Task<byte[]?> ReadExactAsync(Stream stream, int len, CancellationToken ct)
        {
            var buffer = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, len - read), ct).ConfigureAwait(false);
                if (n == 0) return null; // desconexão
                read += n;
            }
            return buffer;
        }
    }
}
