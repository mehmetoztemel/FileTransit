using System.Runtime.InteropServices;

namespace FileTransit;

// Windows shell icon and type name per extension. Lookups pass only a synthetic "file.ext" with
// SHGFI_USEFILEATTRIBUTES, so neither local nor remote files are opened. One shell call per extension.
internal sealed class FileTypes(ImageList images, int fallbackImage)
{
    private readonly Dictionary<string, (int Image, string Name)> cache = new(StringComparer.OrdinalIgnoreCase);

    public (int Image, string Name) Lookup(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        if (extension.Length is < 2 or > 16 || !extension.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) extension = "";
        if (cache.TryGetValue(extension, out var known)) return known;
        var result = (Image: fallbackImage, Name: extension.Length == 0 ? "Dosya" : extension[1..].ToUpperInvariant() + " dosyası");
        if (extension.Length > 0)
        {
            var info = new ShellFileInfo();
            if (SHGetFileInfo("file" + extension, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), GetIcon | SmallIcon | GetTypeName | UseFileAttributes) != 0)
            {
                if (!string.IsNullOrWhiteSpace(info.TypeName)) result.Name = info.TypeName;
                if (info.IconHandle != 0)
                {
                    try { using var icon = Icon.FromHandle(info.IconHandle); images.Images.Add(Fit(icon, images.ImageSize)); result.Image = images.Images.Count - 1; }
                    finally { DestroyIcon(info.IconHandle); }
                }
            }
        }
        cache[extension] = result; return result;
    }

    private static Bitmap Fit(Icon icon, Size size)
    {
        using var source = icon.ToBitmap(); var bitmap = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bitmap); g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        int width = Math.Min(source.Width, size.Width), height = Math.Min(source.Height, size.Height);
        g.DrawImage(source, (size.Width - width) / 2, (size.Height - height) / 2, width, height); return bitmap;
    }

    private const uint FileAttributeNormal = 0x80, GetIcon = 0x100, SmallIcon = 0x1, GetTypeName = 0x400, UseFileAttributes = 0x10;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint IconHandle; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
