using System.Drawing;
using System.Drawing.Imaging;

namespace GammaBrightnessTool;

/// <summary>
/// Converts PNG image to ICO format for use as application icon.
/// </summary>
public static class PngToIcoConverter
{
    /// <summary>
    /// Converts a PNG file to ICO format with multiple sizes.
    /// </summary>
    public static void Convert(string pngPath, string icoPath)
    {
        using var original = Image.FromFile(pngPath);
        
        // Create multiple sizes for the icon
        int[] sizes = { 256, 128, 64, 48, 32, 16 };
        var images = new List<Bitmap>();
        
        try
        {
            foreach (int size in sizes)
            {
                var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawImage(original, 0, 0, size, size);
                }
                images.Add(bitmap);
            }
            
            // Save as ICO
            SaveAsIco(images, icoPath);
        }
        finally
        {
            foreach (var img in images)
            {
                img.Dispose();
            }
        }
    }
    
    private static void SaveAsIco(List<Bitmap> images, string path)
    {
        using var stream = new FileStream(path, FileMode.Create);
        using var writer = new BinaryWriter(stream);
        
        // ICO Header
        writer.Write((short)0); // Reserved
        writer.Write((short)1); // Type: Icon
        writer.Write((short)images.Count); // Number of images
        
        // Calculate header size
        int headerSize = 6 + images.Count * 16;
        int dataOffset = headerSize;
        
        // Image directory entries
        var imageDataList = new List<byte[]>();
        foreach (var img in images)
        {
            using var ms = new MemoryStream();
            img.Save(ms, ImageFormat.Png);
            var data = ms.ToArray();
            imageDataList.Add(data);
            
            int width = img.Width;
            int height = img.Height;
            
            writer.Write((byte)(width >= 256 ? 0 : width)); // Width
            writer.Write((byte)(height >= 256 ? 0 : height)); // Height
            writer.Write((byte)0); // Colors (0 = >256)
            writer.Write((byte)0); // Reserved
            writer.Write((short)1); // Color planes
            writer.Write((short)32); // Bits per pixel
            writer.Write(data.Length); // Size of image data
            writer.Write(dataOffset); // Offset to image data
            
            dataOffset += data.Length;
        }
        
        // Write image data
        foreach (var data in imageDataList)
        {
            writer.Write(data);
        }
        
        writer.Flush();
    }
}
