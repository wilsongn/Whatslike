using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Chat.Shared.Net
{
    public static class SocketFraming
    {
        public static async Task SendFrameAsync(Socket socket, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            byte[] len = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
            await SendAllAsync(socket, len, ct).ConfigureAwait(false);
            await SendAllAsync(socket, payload, ct).ConfigureAwait(false);
        }

        public static async Task<byte[]?> ReadFrameAsync(Socket socket, CancellationToken ct = default)
        {
            var lenBuf = new byte[4];
            if (!await ReadExactAsync(socket, lenBuf, ct).ConfigureAwait(false))
                return null;

            int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (len < 0) return null;

            var payload = new byte[len];
            if (!await ReadExactAsync(socket, payload, ct).ConfigureAwait(false))
                return null;

            return payload;
        }

        private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                int n = await socket.SendAsync(data.Slice(sent), SocketFlags.None, ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException("Socket fechado durante envio.");
                sent += n;
            }
        }

        private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await socket.ReceiveAsync(buffer.AsMemory(read, buffer.Length - read), SocketFlags.None, ct).ConfigureAwait(false);
                if (n == 0) return false; // desconectou
                read += n;
            }
            return true;
        }
    }
}
