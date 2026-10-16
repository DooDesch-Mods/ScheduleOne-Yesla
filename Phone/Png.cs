using System.IO;
using System.IO.Compression;

namespace Yesla.Phone
{
    /// <summary>
    /// PNG for 8-bit RGBA pictures, in plain C# so it runs on a worker thread: Unity's encoder and anything else that
    /// crosses into the IL2CPP runtime must stay on the main thread. Writes one IDAT chunk, which Sideload needs;
    /// reads any number of them, every row filter and nothing but non-interlaced 8-bit RGBA.
    /// </summary>
    internal static class Png
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] Crc = MakeCrc();

        /// <summary>
        /// Encodes <paramref name="rgba"/> (<paramref name="width"/> x <paramref name="height"/>, 4 bytes a pixel).
        /// <paramref name="bottomUp"/>: the first row in the buffer is the picture's bottom row, as Unity reads pixels.
        /// Each row takes the filter with the smallest sum, the usual rule for photos; <paramref name="quick"/> puts every
        /// row through Paeth instead, a third of the time for a slightly larger file.
        /// </summary>
        internal static byte[] Encode(byte[] rgba, int width, int height, bool bottomUp, System.IO.Compression.CompressionLevel level, bool quick = false)
        {
            int stride = width * 4;
            var raw = new byte[height * (stride + 1)];
            var best = new byte[stride];
            var trial = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                int row = (bottomUp ? height - 1 - y : y) * stride;
                int prev = y == 0 ? -1 : (bottomUp ? height - y : y - 1) * stride;
                long bestSum = long.MaxValue;
                byte bestType = 0;
                for (byte type = quick ? (byte)4 : (byte)0; type < 5; type++)
                {
                    long sum = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= 4 ? rgba[row + i - 4] : 0;
                        int b = prev >= 0 ? rgba[prev + i] : 0;
                        int c = i >= 4 && prev >= 0 ? rgba[prev + i - 4] : 0;
                        int x = rgba[row + i];
                        int f = type switch { 0 => x, 1 => x - a, 2 => x - b, 3 => x - ((a + b) >> 1), _ => x - Paeth(a, b, c) };
                        byte v = (byte)f;
                        trial[i] = v;
                        sum += v < 128 ? v : 256 - v;
                        if (sum >= bestSum) break;
                    }
                    if (sum < bestSum) { bestSum = sum; bestType = type; System.Buffer.BlockCopy(trial, 0, best, 0, stride); }
                }
                int o = y * (stride + 1);
                raw[o] = bestType;
                System.Buffer.BlockCopy(best, 0, raw, o + 1, stride);
            }

            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, level, true)) z.Write(raw, 0, raw.Length);
                packed = ms.ToArray();
            }

            using var png = new MemoryStream(packed.Length + 64);
            png.Write(Signature, 0, Signature.Length);
            var ihdr = new byte[13];
            Put(ihdr, 0, (uint)width);
            Put(ihdr, 4, (uint)height);
            ihdr[8] = 8;   // bits per channel
            ihdr[9] = 6;   // RGBA
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", packed);
            Chunk(png, "IEND", System.Array.Empty<byte>());
            return png.ToArray();
        }

        /// <summary>Decodes an 8-bit RGBA PNG to top-down RGBA bytes; throws on anything else.</summary>
        internal static byte[] Decode(byte[] png, out int width, out int height)
        {
            for (int i = 0; i < Signature.Length; i++)
                if (png.Length < 8 || png[i] != Signature[i]) throw new InvalidDataException("not a PNG");
            width = height = 0;
            using var idat = new MemoryStream();
            int p = 8;
            while (p + 8 <= png.Length)
            {
                int len = (int)Get(png, p);
                string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
                int data = p + 8;
                if (type == "IHDR")
                {
                    width = (int)Get(png, data);
                    height = (int)Get(png, data + 4);
                    if (png[data + 8] != 8 || png[data + 9] != 6 || png[data + 12] != 0)
                        throw new InvalidDataException("only non-interlaced 8-bit RGBA is read (depth " + png[data + 8] + ", colour type " + png[data + 9] + ")");
                }
                else if (type == "IDAT") idat.Write(png, data, len);
                else if (type == "IEND") break;
                p = data + len + 4;
            }
            if (width <= 0 || height <= 0) throw new InvalidDataException("no IHDR");

            int stride = width * 4;
            var raw = new byte[height * (stride + 1)];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < raw.Length)
                {
                    int n = z.Read(raw, read, raw.Length - read);
                    if (n <= 0) throw new InvalidDataException("image data ends after " + read + " of " + raw.Length + " bytes");
                    read += n;
                }
            }

            var rgba = new byte[height * stride];
            for (int y = 0; y < height; y++)
            {
                int src = y * (stride + 1) + 1, row = y * stride, prev = row - stride;
                byte type = raw[src - 1];
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= 4 ? rgba[row + i - 4] : 0;
                    int b = y > 0 ? rgba[prev + i] : 0;
                    int c = i >= 4 && y > 0 ? rgba[prev + i - 4] : 0;
                    int x = raw[src + i];
                    rgba[row + i] = (byte)(type switch { 0 => x, 1 => x + a, 2 => x + b, 3 => x + ((a + b) >> 1), 4 => x + Paeth(a, b, c), _ => throw new InvalidDataException("row filter " + type) });
                }
            }
            return rgba;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = p > a ? p - a : a - p, pb = p > b ? p - b : b - p, pc = p > c ? p - c : c - p;
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            Put(head, 0, (uint)data.Length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head, 0, 8);
            s.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFFu;
            for (int i = 4; i < 8; i++) crc = Crc[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
            foreach (byte b in data) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
            var tail = new byte[4];
            Put(tail, 0, crc ^ 0xFFFFFFFFu);
            s.Write(tail, 0, 4);
        }

        private static void Put(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }

        private static uint Get(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        private static uint[] MakeCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
    }
}
