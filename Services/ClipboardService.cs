using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace ScreenshotBooth.Services;

/// <summary>Copies a captured screenshot to the system clipboard as a bitmap.</summary>
public static class ClipboardService
{
    public static async Task CopyPngAsync(byte[] pngBytes)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);

        var dataPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));

        Clipboard.SetContent(dataPackage);
    }
}
