using System.IO;
using System.Windows.Media.Imaging;

namespace Owl1Client.Services;

public static class ImageHelper
{
    /// <summary>
    /// JPEG 바이트 배열을 BitmapImage로 변환한다. OnLoad 캐시 옵션으로 즉시 디코딩한 뒤 Freeze()하여
    /// 백그라운드 스레드에서 생성해도 UI 스레드로 안전하게 넘길 수 있게 한다(스레드 간 공유 가능,
    /// 매 프레임 새로 만들어도 Dispatcher 쪽 작업은 참조 대입뿐이라 성능 부담이 크지 않다).
    /// </summary>
    public static BitmapImage ToBitmapImage(byte[] jpegBytes)
    {
        using var stream = new MemoryStream(jpegBytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
