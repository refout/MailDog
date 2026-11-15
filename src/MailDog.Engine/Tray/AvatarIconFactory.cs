using System.Runtime.InteropServices;

namespace MailDog.Engine.Tray;

/// <summary>
/// 用 GDI 画出“首字母 + 圆底”的发件人头像，并转换成通知栏可用的 HICON。
/// 颜色与 UI 列表头像使用同一组色板，保持视觉一致。
/// </summary>
internal sealed class AvatarIconFactory : IDisposable
{
    private const int IconSize = 48;
    private const int FontWeightSemiBold = 600;
    private const int DefaultCharset = 1;
    private const int AntialiasedQuality = 4;

    private static readonly uint[] Palette =
    [
        0x00BD6C0F, // #0F6CBD
        0x00B86487, // #8764B8
        0x005F7A0E, // #0E7A5F
        0x004547B2, // #B24745
        0x002E568E, // #8E562E
        0x005C5C5C  // #5C5C5C
    ];

    private readonly Dictionary<string, IntPtr> _cache = new(StringComparer.Ordinal);
    private bool _disposed;

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
            icon = Create(initial, color);
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

    private static string GetInitial(string? name, string? address)
    {
        var source = string.IsNullOrWhiteSpace(name) ? address : name;
        return string.IsNullOrWhiteSpace(source) ? "?" : source[..1].ToUpperInvariant();
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

    private static IntPtr Create(string initial, uint circleColor)
    {
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
                Width = IconSize,
                Height = -IconSize,
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
                Width = IconSize,
                Height = -IconSize,
                Planes = 1,
                BitCount = 1,
                Compression = 0,
                ColorsUsed = 2
            },
            Color0 = default,
            Color1 = new RgbQuad { Blue = 0xFF, Green = 0xFF, Red = 0xFF }
        };

        var maskBitmap = Gdi.CreateDib1(hdc, ref maskInfo, 0, out _, IntPtr.Zero, 0);

        if (colorBitmap == IntPtr.Zero || maskBitmap == IntPtr.Zero)
        {
            Gdi.DeleteObject(colorBitmap);
            Gdi.DeleteObject(maskBitmap);
            Gdi.DeleteDC(hdc);
            return IntPtr.Zero;
        }

        var previous = Gdi.SelectObject(hdc, colorBitmap);
        var magenta = Gdi.CreateSolidBrush(0x00FF00FF);
        var circle = Gdi.CreateSolidBrush(circleColor);
        var nullPen = Gdi.GetStockObject(8);
        var font = Gdi.CreateFontW(
            -28,
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

        try
        {
            var full = new Rect { Left = 0, Top = 0, Right = IconSize, Bottom = IconSize };
            Gdi.FillRect(hdc, ref full, magenta);

            var oldBrush = Gdi.SelectObject(hdc, circle);
            var oldPen = Gdi.SelectObject(hdc, nullPen);
            Gdi.Ellipse(hdc, 1, 1, IconSize - 1, IconSize - 1);
            Gdi.SelectObject(hdc, oldPen);
            Gdi.SelectObject(hdc, oldBrush);

            var oldFont = font != IntPtr.Zero ? Gdi.SelectObject(hdc, font) : IntPtr.Zero;

            if (font != IntPtr.Zero)
            {
                Gdi.SetBkMode(hdc, 1);
                Gdi.SetTextColor(hdc, 0x00FFFFFF);
                Gdi.GetTextExtentPoint32W(hdc, initial, initial.Length, out var extent);
                Gdi.TextOutW(
                    hdc,
                    (IconSize - extent.Cx) / 2,
                    (IconSize - extent.Cy) / 2,
                    initial,
                    initial.Length);
                Gdi.SelectObject(hdc, oldFont);
            }

            ApplyAlpha(bits);
        }
        finally
        {
            Gdi.SelectObject(hdc, previous);
            Gdi.DeleteObject(font);
            Gdi.DeleteObject(magenta);
            Gdi.DeleteObject(circle);
            Gdi.DeleteDC(hdc);
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

    private static void ApplyAlpha(IntPtr bits)
    {
        if (bits == IntPtr.Zero)
        {
            return;
        }

        var pixels = IconSize * IconSize;

        for (var index = 0; index < pixels; index++)
        {
            var offset = IntPtr.Add(bits, index * 4);
            var blue = Marshal.ReadByte(offset);
            var green = Marshal.ReadByte(offset, 1);
            var red = Marshal.ReadByte(offset, 2);

            // 洋红是填充用的透明色标，其余像素全部不透明。
            if (blue == 0xFF && green == 0x00 && red == 0xFF)
            {
                Marshal.WriteByte(offset, 0);
                Marshal.WriteByte(offset, 1, 0);
                Marshal.WriteByte(offset, 2, 0);
                Marshal.WriteByte(offset, 3, 0);
            }
            else
            {
                Marshal.WriteByte(offset, 3, 0xFF);
            }
        }
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
    }
}
