using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Cairn.App.Views;
using Cairn.App.ViewModels;
using Xunit;

namespace Cairn.App.Tests;

/// <summary>
/// The Windows .ico is assembled by hand in make-icons.sh — there is no ImageMagick on
/// the machine that builds it — so these check the container is genuinely decodable
/// rather than merely present. A malformed icon is easy to ship and hard to notice.
/// </summary>
public class IconTests
{
    private static Stream OpenIcon() =>
        AssetLoader.Open(new Uri("avares://cairn-launcher/Assets/cairn.ico"));

    [AvaloniaFact]
    public void The_icon_is_a_multi_size_ico_with_the_sizes_windows_asks_for()
    {
        using var stream = OpenIcon();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();

        // ICONDIR: reserved 0, type 1 (icon), then the entry count.
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            // A width byte of 0 means 256 — the format has only one byte for it.
            sizes.Add(bytes[entry] == 0 ? 256 : bytes[entry]);

            var length = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            Assert.InRange(offset + length, 0, bytes.Length);

            // Entries are PNG-compressed, which Windows has accepted since Vista.
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' },
                bytes.Skip(offset).Take(4).ToArray());
        }

        // 16 and 32 are the taskbar and title bar; 256 is what Explorer's largest view wants.
        Assert.Contains(16, sizes);
        Assert.Contains(32, sizes);
        Assert.Contains(48, sizes);
        Assert.Contains(256, sizes);
    }

    [AvaloniaFact]
    public void Every_entry_decodes_to_an_image_of_the_size_it_claims()
    {
        using var stream = OpenIcon();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();

        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(count > 0);

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var declared = bytes[entry] == 0 ? 256 : bytes[entry];
            var length = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);

            // Decoded for real, through Skia. The directory could claim sizes the embedded
            // images do not have, and Windows would just render the mismatch.
            using var one = new MemoryStream(bytes, offset, length);
            var bitmap = new Avalonia.Media.Imaging.Bitmap(one);

            Assert.Equal(declared, bitmap.PixelSize.Width);
            Assert.Equal(declared, bitmap.PixelSize.Height);
        }
    }

    [AvaloniaFact]
    public void The_window_carries_it()
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();

        // The default Avalonia logo used to ship here.
        Assert.NotNull(window.Icon);
    }
}
