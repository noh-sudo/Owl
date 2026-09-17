using System.Drawing;
using System.Drawing.Imaging;

namespace Owl1DummyServer;

/// <summary>테스트용 합성 영상 프레임(움직이는 사각형 + 시계)을 JPEG bytes로 생성한다.</summary>
public static class FrameGenerator
{
    private static readonly ImageCodecInfo JpegEncoder =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] CreateFrame(int frameIndex)
    {
        const int width = 640;
        const int height = 480;

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(35, 42, 25));

            using var gridPen = new Pen(Color.FromArgb(60, 90, 40), 1);
            for (var x = 0; x < width; x += 40) g.DrawLine(gridPen, x, 0, x, height);
            for (var y = 0; y < height; y += 40) g.DrawLine(gridPen, 0, y, width, y);

            // 화면을 가로지르며 움직이는 표적(사각형)
            var t = (frameIndex % 200) / 200.0;
            var x0 = (int)(t * (width - 60));
            var y0 = 180 + (int)(60 * Math.Sin(frameIndex * 0.05));
            using var targetBrush = new SolidBrush(Color.FromArgb(200, 60, 40));
            g.FillRectangle(targetBrush, x0, y0, 60, 60);
            using var targetPen = new Pen(Color.White, 2);
            g.DrawRectangle(targetPen, x0, y0, 60, 60);

            using var font = new Font("Consolas", 16);
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString("OWL-1 DUMMY FEED", font, textBrush, 12, 12);
            g.DrawString($"frame #{frameIndex}", font, textBrush, 12, 40);
        }

        using var ms = new MemoryStream();
        using var qualityParams = new EncoderParameters(1);
        qualityParams.Param[0] = new EncoderParameter(Encoder.Quality, 75L);
        bitmap.Save(ms, JpegEncoder, qualityParams);
        return ms.ToArray();
    }
}
