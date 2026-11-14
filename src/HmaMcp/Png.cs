using System.Buffers.Binary;
using System.IO.Compression;
using ModelContextProtocol;

namespace HmaMcp;

/// <summary>
/// Minimal PNG reader/writer for GBA graphics. Colors are 15-bit shorts in the order HMA's
/// palettes and renderers use (bits 10-14 red, 5-9 green, 0-4 blue: the reverse of the GBA's own layout).
/// </summary>
public static class Png {
   public static int ToRgb(short gba) {
      int c5(int v) => (v << 3) | (v >> 2);
      return (c5((gba >> 10) & 31) << 16) | (c5((gba >> 5) & 31) << 8) | c5(gba & 31);
   }

   public static short FromRgb(int r, int g, int b) => (short)(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));

   public static string ToHex(short gba) => $"#{ToRgb(gba):X6}";

   public static short ParseHex(string text) {
      var t = text.Trim().TrimStart('#');
      if (t.Length == 3) t = string.Concat(t.Select(ch => $"{ch}{ch}"));
      if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
         throw new McpException($"'{text}' isn't a color. Use #RRGGBB, e.g. #F8F8F8.");
      return FromRgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
   }

   /// <summary>Encode GBA pixels as a truecolor PNG, each pixel drawn as scale x scale.</summary>
   public static byte[] Encode(IReadOnlyList<short> pixels, int width, int height, int scale = 1) {
      scale = Math.Max(1, scale);
      int w = width * scale, h = height * scale;
      var raw = new byte[h * (1 + w * 3)];
      for (int y = 0; y < h; y++) {
         int row = y * (1 + w * 3);
         raw[row] = 0; // filter: none
         for (int x = 0; x < w; x++) {
            int i = (y / scale) * width + x / scale;
            int rgb = i < pixels.Count ? ToRgb(pixels[i]) : 0;
            raw[row + 1 + x * 3] = (byte)(rgb >> 16);
            raw[row + 2 + x * 3] = (byte)(rgb >> 8);
            raw[row + 3 + x * 3] = (byte)rgb;
         }
      }
      using var ms = new MemoryStream();
      ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
      var ihdr = new byte[13];
      BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
      BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
      ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
      Chunk(ms, "IHDR", ihdr);
      using (var z = new MemoryStream()) {
         using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
         Chunk(ms, "IDAT", z.ToArray());
      }
      Chunk(ms, "IEND", Array.Empty<byte>());
      return ms.ToArray();
   }

   /// <summary>Encode palette indices as an 8-bit indexed PNG (keeps exact indices for a round trip).</summary>
   public static byte[] EncodeIndexed(int[,] indices, IReadOnlyList<short> palette) {
      int w = indices.GetLength(0), h = indices.GetLength(1);
      var raw = new byte[h * (1 + w)];
      for (int y = 0; y < h; y++)
         for (int x = 0; x < w; x++) raw[y * (1 + w) + 1 + x] = (byte)indices[x, y];
      var plte = new byte[palette.Count * 3];
      for (int i = 0; i < palette.Count; i++) {
         int rgb = ToRgb(palette[i]);
         (plte[i * 3], plte[i * 3 + 1], plte[i * 3 + 2]) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
      }
      using var ms = new MemoryStream();
      ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
      var ihdr = new byte[13];
      BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
      BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
      ihdr[8] = 8; ihdr[9] = 3; // 8-bit indexed
      Chunk(ms, "IHDR", ihdr);
      Chunk(ms, "PLTE", plte);
      using (var z = new MemoryStream()) {
         using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
         Chunk(ms, "IDAT", z.ToArray());
      }
      Chunk(ms, "IEND", Array.Empty<byte>());
      return ms.ToArray();
   }

   /// <summary>A decoded PNG. Indices/Palette are set when the file is an indexed PNG.</summary>
   /// <summary>Transparent is set when the PNG has transparency (alpha below 50%, or a transparent palette entry).</summary>
   public sealed record Image(int Width, int Height, short[] Pixels, int[,]? Indices, short[]? Palette, bool[]? Transparent = null);

   public static Image Decode(byte[] file) {
      if (file.Length < 8 || file[0] != 137 || file[1] != 80 || file[2] != 78 || file[3] != 71)
         throw new McpException("Not a PNG file. Convert the image to PNG first.");
      int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
      byte[]? plte = null, trns = null;
      using var idat = new MemoryStream();
      for (int p = 8; p + 8 <= file.Length;) {
         int len = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(p));
         var type = System.Text.Encoding.ASCII.GetString(file, p + 4, 4);
         var data = file.AsSpan(p + 8, len);
         switch (type) {
            case "IHDR":
               width = BinaryPrimitives.ReadInt32BigEndian(data);
               height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
               (depth, colorType, interlace) = (data[8], data[9], data[12]);
               break;
            case "PLTE": plte = data.ToArray(); break;
            case "tRNS": trns = data.ToArray(); break;
            case "IDAT": idat.Write(data); break;
         }
         if (type == "IEND") break;
         p += 12 + len;
      }
      if (interlace != 0) throw new McpException("Interlaced PNGs aren't supported. Re-save the image without interlacing.");
      int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new McpException($"Unsupported PNG color type {colorType}.") };
      if (depth == 16) throw new McpException("16-bit PNGs aren't supported. Re-save the image as 8-bit.");
      if (colorType != 3 && colorType != 0 && depth != 8) throw new McpException($"Unsupported PNG bit depth {depth}.");
      if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw new McpException($"Image size {width}x{height} isn't supported.");

      idat.Position = 0;
      using var zs = new ZLibStream(idat, CompressionMode.Decompress);
      using var rawStream = new MemoryStream();
      zs.CopyTo(rawStream);
      var raw = rawStream.ToArray();

      int bitsPerPixel = depth * channels, stride = (width * bitsPerPixel + 7) / 8, bpp = Math.Max(1, bitsPerPixel / 8);
      if (raw.Length < height * (stride + 1)) throw new McpException("The PNG's image data is truncated.");
      var prev = new byte[stride];
      var cur = new byte[stride];
      var pixels = new short[width * height];
      var indices = colorType == 3 ? new int[width, height] : null;
      short[]? palette = null;
      bool[]? transparent = null;
      if (colorType == 3) {
         if (plte == null) throw new McpException("Indexed PNG has no palette.");
         palette = new short[plte.Length / 3];
         for (int i = 0; i < palette.Length; i++) palette[i] = FromRgb(plte[i * 3], plte[i * 3 + 1], plte[i * 3 + 2]);
      }
      for (int y = 0; y < height; y++) {
         int filter = raw[y * (stride + 1)];
         Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
         for (int i = 0; i < stride; i++) {
            int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            cur[i] = (byte)(cur[i] + filter switch {
               0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
               4 => Paeth(a, b, c),
               _ => throw new McpException($"Corrupt PNG (filter {filter})."),
            });
         }
         for (int x = 0; x < width; x++) {
            short color;
            if (colorType == 3 || (colorType == 0 && depth < 8)) {
               int bit = x * depth, v = (cur[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
               if (colorType == 3) {
                  indices![x, y] = v;
                  color = v < palette!.Length ? palette[v] : (short)0;
                  if (trns != null && v < trns.Length && trns[v] < 128) (transparent ??= new bool[width * height])[y * width + x] = true;
               } else {
                  int g = v * 255 / ((1 << depth) - 1);
                  color = FromRgb(g, g, g);
               }
            } else {
               int o = x * channels;
               int alpha = colorType == 6 ? cur[o + 3] : colorType == 4 ? cur[o + 1] : 255;
               if (alpha < 128) (transparent ??= new bool[width * height])[y * width + x] = true;
               color = colorType switch {
                  0 or 4 => FromRgb(cur[o], cur[o], cur[o]),
                  _ => FromRgb(cur[o], cur[o + 1], cur[o + 2]),
               };
            }
            pixels[y * width + x] = color;
         }
         (prev, cur) = (cur, prev);
      }
      return new Image(width, height, pixels, indices, palette, transparent);
   }

   static int Paeth(int a, int b, int c) {
      int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
      return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
   }

   static void Chunk(Stream s, string type, byte[] data) {
      Span<byte> len = stackalloc byte[4];
      BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
      s.Write(len);
      var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
      s.Write(typeBytes);
      s.Write(data);
      uint crc = Crc(typeBytes, 0xFFFFFFFF);
      crc = Crc(data, crc) ^ 0xFFFFFFFF;
      BinaryPrimitives.WriteUInt32BigEndian(len, crc);
      s.Write(len);
   }

   static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n => {
      uint c = (uint)n;
      for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
      return c;
   }).ToArray();

   static uint Crc(byte[] data, uint crc) {
      foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
      return crc;
   }
}
