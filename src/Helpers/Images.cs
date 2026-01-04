

#:package SixLabors.ImageSharp@3.1.12
#:package SixLabors.ImageSharp.Drawing@2.1.7


using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.PixelFormats;


namespace Helpers;

public static partial class Helper
{
    public static void VerticalMergeImage(string imageName, params byte[][] bitmaps)
    {
        var images = bitmaps.ToList().Select(image => Image.Load(new MemoryStream(image))).ToList();
        var height = images.Sum(image => image.Height);
        var width = images.Max(image => image.Width);
        using (var mergeImage = new Image<Rgba32>(width, height))
        {
            int y = 0;//y坐标
            foreach (var image in images)
            {
                mergeImage.Mutate(o => o.DrawImage(image, new Point(0, y), 1));
                y += image.Height;
            }
            mergeImage.SaveAsPng(imageName);
        }
    }

    public static string VerticalMergeImageStream(params Stream[] bitmaps)
    {
        var images = bitmaps.ToList().Select(Image.Load).ToList();
        var height = images.Select(image => image.Height).Sum();
        var width = images.Select(image => image.Width).Max();
        using (var mergeImage = new Image<Rgba32>(width, height))
        {
            int y = 0;//y坐标
            foreach (var image in images)
            {
                mergeImage.Mutate(o => o.DrawImage(image, new Point(0, y), 1));
                y += image.Height;
            }
            return mergeImage.ToBase64String(PngFormat.Instance);
        }
    }
}

