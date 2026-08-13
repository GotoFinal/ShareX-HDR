using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ShareX.ScreenCaptureLib.ObsHookHarness;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool useRgb10A2 = args.Contains("--rgb10a2", StringComparer.OrdinalIgnoreCase);
        ApplicationConfiguration.Initialize();
        using var form = new HarnessForm(useRgb10A2);
        Application.Run(form);
    }
}

internal sealed class HarnessForm : Form
{
    private const int RenderWidth = 640;
    private const int RenderHeight = 360;

    private readonly System.Windows.Forms.Timer renderTimer;
    private readonly bool useRgb10A2;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDXGISwapChain1? swapChain;
    private ID3D11Texture2D? backBuffer;
    private ID3D11RenderTargetView? renderTarget;
    private int frameNumber;

    public HarnessForm(bool useRgb10A2)
    {
        Text = "ShareX OBS Hook HDR Test Harness";
        ClientSize = new System.Drawing.Size(RenderWidth, RenderHeight);
        this.useRgb10A2 = useRgb10A2;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        renderTimer = new System.Windows.Forms.Timer { Interval = 16 };
        renderTimer.Tick += (_, _) => RenderFrame();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CreateDeviceAndSwapChain();
        RenderFrame();
        Console.WriteLine($"READY {Environment.ProcessId} {GetCurrentThreadId()} {Handle.ToInt64()}");
        Console.Out.Flush();
        renderTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            renderTimer.Stop();
            renderTimer.Dispose();
            context?.ClearState();
            renderTarget?.Dispose();
            backBuffer?.Dispose();
            swapChain?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CreateDeviceAndSwapChain()
    {
        D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out device,
            out _,
            out context).CheckError();

        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = RenderWidth,
            Height = RenderHeight,
            Format = useRgb10A2 ? Format.R10G10B10A2_UNorm : Format.R16G16B16A16_Float,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore
        };

        swapChain = factory.CreateSwapChainForHwnd(device, Handle, description);
        factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAltEnter);
        backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        renderTarget = device.CreateRenderTargetView(backBuffer);

        using IDXGISwapChain3? swapChain3 = swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();
        swapChain3?.SetColorSpace1(useRgb10A2
            ? ColorSpaceType.RgbFullG2084NoneP2020
            : ColorSpaceType.RgbFullG10NoneP709);
    }

    private void RenderFrame()
    {
        if (context == null || renderTarget == null || swapChain == null)
        {
            return;
        }

        float phase = (frameNumber++ % 120) / 119f;
        float red = 2f + phase * 2f;
        float green = 0.25f + phase * 0.5f;
        float blue = 0.125f;

        if (useRgb10A2)
        {
            ConvertRec709ScRgbToRec2020(red, green, blue, out red, out green, out blue);
            red = EncodePqNits(red * 80f);
            green = EncodePqNits(green * 80f);
            blue = EncodePqNits(blue * 80f);
        }

        var color = new Color4(red, green, blue, 1f);
        context.ClearRenderTargetView(renderTarget, color);
        swapChain.Present(1, PresentFlags.None).CheckError();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static void ConvertRec709ScRgbToRec2020(
        float red709,
        float green709,
        float blue709,
        out float red2020,
        out float green2020,
        out float blue2020)
    {
        red2020 = 0.627404f * red709 + 0.329283f * green709 + 0.043313f * blue709;
        green2020 = 0.069097f * red709 + 0.919540f * green709 + 0.011362f * blue709;
        blue2020 = 0.016391f * red709 + 0.088013f * green709 + 0.895595f * blue709;
    }

    private static float EncodePqNits(float nits)
    {
        const float m1 = 2610f / 16384f;
        const float m2 = 2523f / 32f;
        const float c1 = 3424f / 4096f;
        const float c2 = 2413f / 128f;
        const float c3 = 2392f / 128f;

        float linear = Math.Clamp(nits / 10_000f, 0f, 1f);
        float power = MathF.Pow(linear, m1);
        return MathF.Pow((c1 + c2 * power) / (1f + c3 * power), m2);
    }
}
