using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MailDog.Engine.Tray;

/// <summary>
/// 用 GDI 画出“首字母 + 圆底”的发件人头像。圆形与文字分两次画成灰度蒙版，
/// 在 4x 画布上合成后缩小，得到真正的逐像素 alpha，避免老做法里洋红抠图留下的硬边锯齿。
/// 同一张位图既能换成 HICON 给托盘气泡，也能编码成 PNG 给通知中心的正规 toast。
/// </summary>
internal sealed class AvatarIconFactory : IDisposable
{
    private const int IconSize = 48;
    private const int AvatarIconSize = 96;
    private const int ToastAvatarSize = 192;
    private const int UserIconSize = 48;
    private const int SuperSample = 4;
    private const int FontWeightSemiBold = 600;
    private const int DefaultCharset = 1;
    private const int TransparentBkMode = 1;
    private const int AntialiasedQuality = 4;
    private const uint BadgeColor = 0x003834D1;

    private static readonly uint[] Palette =
    [
        0x00BD6C0F, // #0F6CBD
        0x00B86487, // #8764B8
        0x005F7A0E, // #0E7A5F
        0x004547B2, // #B24745
        0x002E568E, // #8E562E
        0x005C5C5C  // #5C5C5C
    ];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly Dictionary<string, IntPtr> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pngCache = new(StringComparer.Ordinal);
    private readonly string _avatarDirectory;
    private IntPtr _unreadDotIcon;
    private bool _unreadDotCreated;
    private bool _disposed;

    public AvatarIconFactory()
    {
        _avatarDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailDog",
            "avatars");
    }

    public IntPtr GetUnreadDotIcon(IntPtr baseIcon, int size = IconSize)
    {
        if (_disposed || baseIcon == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (_unreadDotCreated)
        {
            return _unreadDotIcon;
        }

        try
        {
            _unreadDotIcon = CreateWithUnreadDot(baseIcon, Math.Clamp(size, 16, 256));
        }
        catch
        {
            _unreadDotIcon = IntPtr.Zero;
        }

        _unreadDotCreated = true;
        return _unreadDotIcon;
    }

    public IntPtr GetIcon(string? displayName, string? address)
    {
        if (_disposed)
        {
            return IntPtr.Zero;
        }

        var initial = GetInitial(displayName, address);
        var color = GetColor(address, initial);
        var key = $"{initial}:{color:X8}";

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        IntPtr icon;

        try
        {
            icon = CreateIcon(initial, color);
        }
        catch
        {
            return IntPtr.Zero;
        }

        if (icon != IntPtr.Zero)
        {
            _cache[key] = icon;
        }

        return icon;
    }

    /// <summary>给 toast 用的圆形头像 PNG，返回绝对路径；生成失败返回 null。</summary>
    public string? GetAvatarPng(string? displayName, string? address)
    {
        if (_disposed)
        {
            return null;
        }

        var initial = GetInitial(displayName, address);
        var color = GetColor(address, initial);
        var key = $"{initial}:{color:X8}";

        if (_pngCache.TryGetValue(key, out var cachedPath))
        {
            return cachedPath;
        }

        try
        {
            Directory.CreateDirectory(_avatarDirectory);

            // 文件名只用 ASCII：toast 的 src 是 URI，中文名要额外的百分号编码，
            // 有的系统组件处理不好，干脆避开。
            var path = Path.Combine(
                _avatarDirectory,
                $"avatar-{color:X8}-{(int)initial[0]:X4}.png");
            var rgba = RenderRgba(initial, color, ToastAvatarSize);

            if (rgba is null)
            {
                return null;
            }

            WritePng(path, rgba, ToastAvatarSize, ToastAvatarSize);
            _pngCache[key] = path;
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string GetInitial(string? name, string? address)
    {
        var source = string.IsNullOrWhiteSpace(name) ? address : name;

        if (string.IsNullOrWhiteSpace(source))
        {
            return "?";
        }

        // 跳过引号和括号这类装饰字符，直接取第一个有意义的字形。
        foreach (var character in source)
        {
            if (char.IsLetterOrDigit(character))
            {
                return char.ToUpperInvariant(character).ToString();
            }
        }

        return source[..1].ToUpperInvariant();
    }

    private static uint GetColor(string? address, string initial)
    {
        var hash = 0;
        var source = string.IsNullOrWhiteSpace(address) ? initial : address;

        foreach (var character in source)
        {
            hash = unchecked((hash * 31) + character);
        }

        var index = (int)((uint)hash % (uint)Palette.Length);
        return Palette[index];
    }

    private static IntPtr CreateWithUnreadDot(IntPtr baseIcon, int size)
    {
        // 直接在图标自带的 32bpp 像素上叠红点：先 DrawIconEx 合成到白底再强行
        // alpha=255，会把圆角抗锯齿抹平并留下白边，小尺寸下一眼就能看出锯齿。
        var iconInfoSource = new IconInfo();
        if (Gdi.GetIconInfo(baseIcon, ref iconInfoSource) && iconInfoSource.ColorBitmap != IntPtr.Zero)
        {
            try
            {
                var composed = TryComposeOnAlphaChannel(iconInfoSource.ColorBitmap, size);
                if (composed != IntPtr.Zero)
                {
                    return composed;
                }
            }
            finally
            {
                Gdi.DeleteObject(iconInfoSource.ColorBitmap);
                if (iconInfoSource.MaskBitmap != IntPtr.Zero)
                {
                    Gdi.DeleteObject(iconInfoSource.MaskBitmap);
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 读出 HICON 的颜色位图（含逐像素 alpha），在其上以 4x4 超采样画一个抗锯齿红点，
    /// 再直接作为图标返回，整个过程不经过白底合成。
    /// </summary>
    private static IntPtr TryComposeOnAlphaChannel(IntPtr colorBitmap, int size)
    {
        var source = new Bitmap();
        if (Gdi.GetObject(colorBitmap, Marshal.SizeOf<Bitmap>(), ref source) == 0
            || source.Width <= 0
            || source.Height <= 0)
        {
            return IntPtr.Zero;
        }

        // GetDIBits 只填自下而上的行序，所以位图按正高度创建，绘制时再翻行号。
        var hdc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var colorInfo = new BitmapInfo32
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = source.Width,
                Height = source.Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            }
        };

        var colorDib = Gdi.CreateDib32(hdc, ref colorInfo, 0, out var bits, IntPtr.Zero, 0);
        if (colorDib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Gdi.DeleteDC(hdc);
            return IntPtr.Zero;
        }

        var lines = Gdi.GetDIBits(hdc, colorBitmap, 0, (uint)source.Height, bits, ref colorInfo, 0);
        if (lines != source.Height)
        {
            Gdi.DeleteObject(colorDib);
            Gdi.DeleteDC(hdc);
            return IntPtr.Zero;
        }

        DrawUnreadDot(bits, source.Width, source.Height, size);

        var maskInfo = new BitmapInfo1
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = source.Width,
                Height = source.Height,
                Planes = 1,
                BitCount = 1,
                Compression = 0,
                ColorsUsed = 2
            },
            Color0 = default,
            Color1 = new RgbQuad { Blue = 0xFF, Green = 0xFF, Red = 0xFF }
        };

        // CreateDIBSection 的内容已清零，即蒙版全 0（不透明），透明交由 alpha 通道决定。
        var maskBitmap = Gdi.CreateDib1(hdc, ref maskInfo, 0, out _, IntPtr.Zero, 0);
        Gdi.DeleteDC(hdc);

        if (maskBitmap == IntPtr.Zero)
        {
            Gdi.DeleteObject(colorDib);
            return IntPtr.Zero;
        }

        var iconInfo = new IconInfo
        {
            IsIcon = 1,
            MaskBitmap = maskBitmap,
            ColorBitmap = colorDib
        };

        var icon = Gdi.CreateIconIndirect(ref iconInfo);

        Gdi.DeleteObject(colorDib);
        Gdi.DeleteObject(maskBitmap);

        return icon;
    }

    private static void DrawUnreadDot(IntPtr bits, int width, int height, int iconSize)
    {
        // 红点几何按 48px 设计稿等比缩放。
        var scale = iconSize / (double)IconSize;
        var diameter = 18 * scale;
        var margin = 2 * scale;
        var cx = iconSize - margin - diameter / 2.0;
        var cy = cx;
        var radius = diameter / 2.0;

        var red = (byte)((BadgeColor >> 0) & 0xFF);
        var green = (byte)((BadgeColor >> 8) & 0xFF);
        var blue = (byte)((BadgeColor >> 16) & 0xFF);

        const int sub = 4;
        var minX = Math.Max(0, (int)Math.Floor(cx - radius - 1));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(cx + radius + 1));
        var minY = Math.Max(0, (int)Math.Floor(cy - radius - 1));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(cy + radius + 1));

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var covered = 0;
                for (var sy = 0; sy < sub; sy++)
                {
                    var py = y + (sy + 0.5) / sub;
                    for (var sx = 0; sx < sub; sx++)
                    {
                        var px = x + (sx + 0.5) / sub;
                        var dx = px - cx;
                        var dy = py - cy;
                        if (dx * dx + dy * dy <= radius * radius)
                        {
                            covered++;
                        }
                    }
                }

                if (covered == 0)
                {
                    continue;
                }

                var coverage = covered / (sub * sub);

                // GetDIBits 给的是自下而上的行序，这里把 y 翻过来。
                var offset = IntPtr.Add(bits, ((height - 1 - y) * width + x) * 4);
                var oldAlpha = Marshal.ReadByte(offset, 3) / 255.0;
                var outAlpha = oldAlpha + coverage * (1 - oldAlpha);

                if (outAlpha <= 0)
                {
                    continue;
                }

                var oldBlue = Marshal.ReadByte(offset, 0);
                var oldGreen = Marshal.ReadByte(offset, 1);
                var oldRed = Marshal.ReadByte(offset, 2);

                var newBlue = (int)Math.Round((blue * coverage + oldBlue * oldAlpha * (1 - coverage)) / outAlpha);
                var newGreen = (int)Math.Round((green * coverage + oldGreen * oldAlpha * (1 - coverage)) / outAlpha);
                var newRed = (int)Math.Round((red * coverage + oldRed * oldAlpha * (1 - coverage)) / outAlpha);

                Marshal.WriteByte(offset, 0, (byte)Math.Clamp(newBlue, 0, 255));
                Marshal.WriteByte(offset, 1, (byte)Math.Clamp(newGreen, 0, 255));
                Marshal.WriteByte(offset, 2, (byte)Math.Clamp(newRed, 0, 255));
                Marshal.WriteByte(offset, 3, (byte)Math.Round(outAlpha * 255));
            }
        }
    }

    private static IntPtr CreateIcon(string initial, uint color)
    {
        var rgba = RenderRgba(initial, color, AvatarIconSize);

        if (rgba is null)
        {
            return IntPtr.Zero;
        }

        var hdc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // HICON 的颜色位图是自下而上的，DIB 用正高度创建再逐行翻转写入。
        var colorInfo = new BitmapInfo32
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = AvatarIconSize,
                Height = AvatarIconSize,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            }
        };

        var colorBitmap = Gdi.CreateDib32(hdc, ref colorInfo, 0, out var bits, IntPtr.Zero, 0);

        var maskInfo = new BitmapInfo1
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = AvatarIconSize,
                Height = AvatarIconSize,
                Planes = 1,
                BitCount = 1,
                Compression = 0,
                ColorsUsed = 2
            },
            Color0 = default,
            Color1 = new RgbQuad { Blue = 0xFF, Green = 0xFF, Red = 0xFF }
        };

        var maskBitmap = Gdi.CreateDib1(hdc, ref maskInfo, 0, out _, IntPtr.Zero, 0);
        Gdi.DeleteDC(hdc);

        if (colorBitmap == IntPtr.Zero || maskBitmap == IntPtr.Zero)
        {
            if (colorBitmap != IntPtr.Zero)
            {
                Gdi.DeleteObject(colorBitmap);
            }

            if (maskBitmap != IntPtr.Zero)
            {
                Gdi.DeleteObject(maskBitmap);
            }

            return IntPtr.Zero;
        }

        for (var y = 0; y < AvatarIconSize; y++)
        {
            var sourceRow = y * AvatarIconSize * 4;
            var targetRow = ((AvatarIconSize - 1 - y) * AvatarIconSize * 4);

            for (var x = 0; x < AvatarIconSize * 4; x++)
            {
                Marshal.WriteByte(bits, targetRow + x, rgba[sourceRow + x]);
            }
        }

        var iconInfo = new IconInfo
        {
            IsIcon = 1,
            MaskBitmap = maskBitmap,
            ColorBitmap = colorBitmap
        };

        var icon = Gdi.CreateIconIndirect(ref iconInfo);

        Gdi.DeleteObject(colorBitmap);
        Gdi.DeleteObject(maskBitmap);

        return icon;
    }

    /// <summary>
    /// 圆形蒙版 + 文字蒙版分别在 4x 画布上用 GDI 画成灰度图（黑底白笔），
    /// 再按覆盖率合成、缩小，得到边缘柔和的 RGBA。
    /// </summary>
    private static byte[]? RenderRgba(string initial, uint color, int size)
    {
        var canvas = size * SuperSample;
        var inset = SuperSample;      // 最终图上留 1px 边，圆形不会贴死边缘
        var circle = MaskCanvas.Create(canvas);
        var glyph = MaskCanvas.Create(canvas);

        if (circle is null || glyph is null)
        {
            circle?.Dispose();
            glyph?.Dispose();
            return null;
        }

        using (circle)
        using (glyph)
        {
            var radius = (canvas - inset * 2) / 2.0;
            var center = canvas / 2.0;

            circle.FillEllipse(inset, inset, canvas - inset, canvas - inset);

            // 字号按画布高度取，字宽超出 78% 时按比例回缩，保证长字母（W/M）不被裁掉。
            var fontSize = (int)Math.Round(canvas * 0.52);
            int minX = 0, minY = 0, maxX = 0, maxY = 0;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                glyph.Clear();
                glyph.DrawText(initial, fontSize, 8, 8);

                if (!glyph.TryGetInkBounds(out minX, out minY, out maxX, out maxY))
                {
                    return null;
                }

                var inkWidth = maxX - minX + 1;
                var inkHeight = maxY - minY + 1;

                if (inkWidth <= radius * 1.56 && inkHeight <= radius * 1.56)
                {
                    break;
                }

                var ratio = Math.Min(radius * 1.5 / inkWidth, radius * 1.5 / inkHeight);
                fontSize = Math.Max(8, (int)Math.Round(fontSize * ratio * 0.98));
            }

            var offsetX = (int)Math.Round(center - (minX + maxX) / 2.0);
            var offsetY = (int)Math.Round(center - (minY + maxY) / 2.0);

            var red = (byte)((color >> 0) & 0xFF);
            var green = (byte)((color >> 8) & 0xFF);
            var blue = (byte)((color >> 16) & 0xFF);

            // 缩小时先算 alpha 加权的颜色，避免透明边缘发灰（也就是常说的“脏边”）。
            var pixels = size * size;
            var alphaSum = new double[pixels];
            var redSum = new double[pixels];
            var greenSum = new double[pixels];
            var blueSum = new double[pixels];

            for (var y = 0; y < canvas; y++)
            {
                for (var x = 0; x < canvas; x++)
                {
                    var coverage = circle.Coverage(x, y);

                    if (coverage <= 0)
                    {
                        continue;
                    }

                    var glyphCoverage = glyph.Coverage(x - offsetX, y - offsetY);
                    if (glyphCoverage > coverage)
                    {
                        glyphCoverage = coverage;
                    }

                    var cr = red + (255 - red) * glyphCoverage;
                    var cg = green + (255 - green) * glyphCoverage;
                    var cb = blue + (255 - blue) * glyphCoverage;

                    var index = (y / SuperSample) * size + (x / SuperSample);
                    alphaSum[index] += coverage;
                    redSum[index] += cr * coverage;
                    greenSum[index] += cg * coverage;
                    blueSum[index] += cb * coverage;
                }
            }

            var rgba = new byte[pixels * 4];
            var sampleCount = SuperSample * SuperSample;

            for (var index = 0; index < pixels; index++)
            {
                var alpha = alphaSum[index] / sampleCount;
                var offset = index * 4;

                if (alpha <= 0.0001)
                {
                    continue;
                }

                rgba[offset] = (byte)Math.Clamp((int)Math.Round(redSum[index] / alphaSum[index]), 0, 255);
                rgba[offset + 1] = (byte)Math.Clamp((int)Math.Round(greenSum[index] / alphaSum[index]), 0, 255);
                rgba[offset + 2] = (byte)Math.Clamp((int)Math.Round(blueSum[index] / alphaSum[index]), 0, 255);
                rgba[offset + 3] = (byte)Math.Clamp((int)Math.Round(alpha * 255), 0, 255);
            }

            return rgba;
        }
    }

    private static void WritePng(string path, byte[] rgba, int width, int height)
    {
        using var stream = File.Create(path);
        stream.Write(PngSignature);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // color type: RGBA
        WriteChunk(stream, "IHDR", header);

        var rowLength = width * 4;
        var raw = new byte[(rowLength + 1) * height];

        for (var y = 0; y < height; y++)
        {
            raw[y * (rowLength + 1)] = 0; // filter: none
            Buffer.BlockCopy(rgba, y * rowLength, raw, y * (rowLength + 1) + 1, rowLength);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);

        var typeBytes = new byte[4];
        for (var index = 0; index < 4; index++)
        {
            typeBytes[index] = (byte)type[index];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in first)
        {
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in second)
        {
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            var value = index;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var icon in _cache.Values)
        {
            if (icon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(icon);
            }
        }

        _cache.Clear();
        _pngCache.Clear();

        if (_unreadDotIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_unreadDotIcon);
            _unreadDotIcon = IntPtr.Zero;
        }
    }

    /// <summary>黑底白笔的灰度蒙版画布，只关心单通道覆盖率。</summary>
    private sealed class MaskCanvas : IDisposable
    {
        private readonly IntPtr _dc;
        private readonly IntPtr _bitmap;
        private readonly IntPtr _bits;
        private readonly int _size;
        private bool _disposed;

        private MaskCanvas(IntPtr dc, IntPtr bitmap, IntPtr bits, int size)
        {
            _dc = dc;
            _bitmap = bitmap;
            _bits = bits;
            _size = size;
        }

        public static MaskCanvas? Create(int size)
        {
            var dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                return null;
            }

            var info = new BitmapInfo32
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = size,
                    Height = -size, // 负高度：内存按自上而下排列，读像素时不用翻行
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };

            var bitmap = Gdi.CreateDib32(dc, ref info, 0, out var bits, IntPtr.Zero, 0);

            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                if (bitmap != IntPtr.Zero)
                {
                    Gdi.DeleteObject(bitmap);
                }

                Gdi.DeleteDC(dc);
                return null;
            }

            Gdi.SelectObject(dc, bitmap);

            var canvas = new MaskCanvas(dc, bitmap, bits, size);
            canvas.Clear();
            return canvas;
        }

        public void Clear()
        {
            var black = Gdi.CreateSolidBrush(0x00000000);
            var full = new Rect { Left = 0, Top = 0, Right = _size, Bottom = _size };
            Gdi.FillRect(_dc, ref full, black);
            Gdi.DeleteObject(black);
        }

        public void FillEllipse(int left, int top, int right, int bottom)
        {
            var brush = Gdi.CreateSolidBrush(0x00FFFFFF);
            var nullPen = Gdi.GetStockObject(8);
            var oldBrush = Gdi.SelectObject(_dc, brush);
            var oldPen = Gdi.SelectObject(_dc, nullPen);
            Gdi.Ellipse(_dc, left, top, right, bottom);
            Gdi.SelectObject(_dc, oldPen);
            Gdi.SelectObject(_dc, oldBrush);
            Gdi.DeleteObject(brush);
        }

        public void DrawText(string text, int fontSize, int x, int y)
        {
            var font = Gdi.CreateFontW(
                -fontSize,
                0,
                0,
                0,
                FontWeightSemiBold,
                0,
                0,
                0,
                DefaultCharset,
                0,
                0,
                AntialiasedQuality,
                0,
                "Segoe UI");

            if (font == IntPtr.Zero)
            {
                return;
            }

            var oldFont = Gdi.SelectObject(_dc, font);
            Gdi.SetBkMode(_dc, TransparentBkMode);
            Gdi.SetTextColor(_dc, 0x00FFFFFF);
            Gdi.TextOutW(_dc, x, y, text, text.Length);
            Gdi.SelectObject(_dc, oldFont);
            Gdi.DeleteObject(font);
        }

        /// <summary>单像素覆盖率；32bpp DIB 内存布局是 BGRA，取红色通道即可。</summary>
        public double Coverage(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _size || y >= _size)
            {
                return 0;
            }

            return Marshal.ReadByte(_bits, ((y * _size) + x) * 4 + 2) / 255.0;
        }

        /// <summary>找出笔迹的外接矩形，用来把字真正放到圆心，而不是靠字体行高估算。</summary>
        public bool TryGetInkBounds(out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = _size;
            minY = _size;
            maxX = -1;
            maxY = -1;

            for (var y = 0; y < _size; y++)
            {
                for (var x = 0; x < _size; x++)
                {
                    if (Marshal.ReadByte(_bits, ((y * _size) + x) * 4 + 2) < 6)
                    {
                        continue;
                    }

                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }

            return maxX >= 0 && maxY >= 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Gdi.DeleteObject(_bitmap);
            Gdi.DeleteDC(_dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo32
    {
        public BitmapInfoHeader Header;
        public RgbQuad Color0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo1
    {
        public BitmapInfoHeader Header;
        public RgbQuad Color0;
        public RgbQuad Color1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    private static class Gdi
    {
        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern int GetObject(IntPtr handle, int size, ref Bitmap bitmap);

        [DllImport("gdi32.dll")]
        internal static extern int GetDIBits(
            IntPtr hdc,
            IntPtr bitmap,
            uint start,
            uint lines,
            IntPtr bits,
            ref BitmapInfo32 info,
            uint usage);

        [DllImport("user32.dll")]
        internal static extern bool GetIconInfo(IntPtr icon, ref IconInfo info);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
        internal static extern IntPtr CreateDib32(
            IntPtr hdc,
            ref BitmapInfo32 info,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
        internal static extern IntPtr CreateDib1(
            IntPtr hdc,
            ref BitmapInfo1 info,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr GetStockObject(int index);

        [DllImport("user32.dll")]
        internal static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

        [DllImport("gdi32.dll")]
        internal static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        internal static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        internal static extern uint SetTextColor(IntPtr hdc, uint color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateFontW(
            int height,
            int width,
            int escapement,
            int orientation,
            int weight,
            uint italic,
            uint underline,
            uint strikeOut,
            uint charSet,
            uint outPrecision,
            uint clipPrecision,
            uint quality,
            uint pitchAndFamily,
            string faceName);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out Size size);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int length);

        [DllImport("user32.dll")]
        internal static extern IntPtr CreateIconIndirect(ref IconInfo info);

        [DllImport("user32.dll")]
        internal static extern bool DrawIconEx(
            IntPtr hdc,
            int x,
            int y,
            IntPtr icon,
            int width,
            int height,
            uint step,
            IntPtr flickerFreeBrush,
            uint flags);
    }
}
