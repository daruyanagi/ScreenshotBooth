using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

namespace ScreenshotBooth.Services;

/// <summary>
/// Opens the Windows Share pane for a captured PNG. Desktop apps have no CoreWindow, so the
/// DataTransferManager has to be obtained for an HWND through IDataTransferManagerInterop.
/// The capture is offered both as a bitmap and as a file (a temp PNG), because share targets
/// differ in what they accept (mail wants a file, chat apps take the bitmap).
/// </summary>
public static class ShareService
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    // Kept alive while the pane is open; DataRequested fires after ShowShareUIForWindow returns.
    private static DataTransferManager? _manager;
    private static byte[]? _pendingPng;
    private static string? _pendingTitle;

    /// <summary>Shows the Share pane for <paramref name="pngBytes"/>. Must be called on the UI thread of <paramref name="hwnd"/>.</summary>
    public static async Task ShareAsync(nint hwnd, byte[] pngBytes, string title)
    {
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        var abi = interop.GetForWindow(hwnd, ref iid);
        var manager = MarshalInterface<DataTransferManager>.FromAbi(abi);

        if (!ReferenceEquals(manager, _manager))
        {
            manager.DataRequested += OnDataRequested;
            _manager = manager;
        }

        _pendingPng = pngBytes;
        _pendingTitle = title;

        // The file is written up front: the DataRequested handler has only a short deferral window.
        var dir = Path.Combine(Path.GetTempPath(), "ScreenshotBooth", "Share");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.png"))
        {
            try { File.Delete(old); } catch (IOException) { /* still open in a share target */ }
        }
        var path = Path.Combine(dir, $"{title}.png");
        await File.WriteAllBytesAsync(path, pngBytes);
        _pendingFile = await StorageFile.GetFileFromPathAsync(path);

        interop.ShowShareUIForWindow(hwnd);
    }

    private static StorageFile? _pendingFile;

    private static void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_pendingPng is null || _pendingFile is null)
        {
            args.Request.FailWithDisplayText(string.Empty);
            return;
        }

        var data = args.Request.Data;
        data.Properties.Title = _pendingTitle ?? "ScreenshotBooth";
        data.Properties.ApplicationName = "ScreenshotBooth";

        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(_pendingPng);
        writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        writer.DetachStream();
        stream.Seek(0);

        var reference = RandomAccessStreamReference.CreateFromStream(stream);
        data.Properties.Thumbnail = reference;
        data.SetBitmap(reference);
        data.SetStorageItems(new[] { _pendingFile });
    }
}
