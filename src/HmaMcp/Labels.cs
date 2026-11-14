namespace HmaMcp;

/// <summary>
/// A 3x5 pixel font for hex digits, so sheets of blocks or sprites can carry their ids in the image
/// (the assistant reads the number under each item instead of counting rows and columns).
/// </summary>
public static class Labels {
   // each glyph: 5 rows of 3 bits, top row first, high bit = left pixel
   static readonly Dictionary<char, int[]> Glyphs = new() {
      ['0'] = new[] { 7, 5, 5, 5, 7 }, ['1'] = new[] { 2, 6, 2, 2, 7 }, ['2'] = new[] { 7, 1, 7, 4, 7 }, ['3'] = new[] { 7, 1, 7, 1, 7 },
      ['4'] = new[] { 5, 5, 7, 1, 1 }, ['5'] = new[] { 7, 4, 7, 1, 7 }, ['6'] = new[] { 7, 4, 7, 5, 7 }, ['7'] = new[] { 7, 1, 1, 2, 2 },
      ['8'] = new[] { 7, 5, 7, 5, 7 }, ['9'] = new[] { 7, 5, 7, 1, 7 }, ['A'] = new[] { 2, 5, 7, 5, 5 }, ['B'] = new[] { 6, 5, 6, 5, 6 },
      ['C'] = new[] { 3, 4, 4, 4, 3 }, ['D'] = new[] { 6, 5, 5, 5, 6 }, ['E'] = new[] { 7, 4, 6, 4, 7 }, ['F'] = new[] { 7, 4, 6, 4, 4 },
   };

   public const int Height = 7; // 5 px glyphs plus a 1 px margin above and below
   public static int Width(string text) => text.Length * 4 + 1;

   /// <summary>Draw text (hex digits) at (x, y) on a pixel buffer: a dark box with light digits. HMA color order.</summary>
   public static void Draw(short[] pixels, int bufferWidth, int x, int y, string text) {
      short bg = Png.FromRgb(16, 16, 24), fg = Png.FromRgb(248, 248, 248);
      int w = Width(text), height = pixels.Length / bufferWidth;
      for (int yy = 0; yy < Height; yy++)
         for (int xx = 0; xx < w; xx++)
            Put(pixels, bufferWidth, height, x + xx, y + yy, bg);
      for (int c = 0; c < text.Length; c++) {
         if (!Glyphs.TryGetValue(char.ToUpperInvariant(text[c]), out var rows)) continue;
         for (int r = 0; r < 5; r++)
            for (int b = 0; b < 3; b++)
               if ((rows[r] >> (2 - b) & 1) != 0) Put(pixels, bufferWidth, height, x + 1 + c * 4 + b, y + 1 + r, fg);
      }
   }

   static void Put(short[] pixels, int width, int height, int x, int y, short color) {
      if (x >= 0 && y >= 0 && x < width && y < height) pixels[y * width + x] = color;
   }
}
