using System.Threading;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class ModernCaptureSignletonManager // TODO: remove/simplify?
{
    private static ModernCaptureSignletonManager _instance = new ModernCaptureSignletonManager();
    public static ModernCaptureSignletonManager Instance => _instance;

    private SemaphoreSlim _sharedResourceSemaphore;
    private ModernCapture _captureInstance;

    public ModernCaptureSignletonManager()
    {
        _sharedResourceSemaphore = new SemaphoreSlim(1);
    }

    public bool IsAvailable
    {
        get { return true; } // TODO
    }

    public ModernCapture Take()
    {
        _sharedResourceSemaphore.Wait();
        if (_captureInstance == null)
        {
            _captureInstance = new ModernCapture();
        }

        return _captureInstance;
    }

    public void Release()
    {
        _sharedResourceSemaphore.Release();
    }
}