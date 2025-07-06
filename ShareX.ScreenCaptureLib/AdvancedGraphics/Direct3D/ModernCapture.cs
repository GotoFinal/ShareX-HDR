using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using ShareX.ScreenCaptureLib.AdvancedGraphics.GDI;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class ModernCapture : IDisposable, DisposableCache
{
#if DEBUG
    private IDXGIDebug1 debug;
#endif
    private DeviceCache deviceCache;
    private IDXGIFactory1 idxgiFactory1;
    private HdrSettings Settings;

    private InputElementDescription[] shaderInputElements =
    [
        new("POSITION", 0, Format.R32G32_Float, 0),
        new("TEXCOORD", 0, Format.R32G32_Float, 0)
    ];

    private byte[] vxShader;
    private byte[] psShader;
    private Blob inputSignatureBlob;

    public ModernCapture(HdrSettings settings)
    {
#if DEBUG
        DXGI.DXGIGetDebugInterface1(out debug).CheckError();
#endif

        Settings = settings;
        deviceCache = new DeviceCache(InitializeDevice);
        idxgiFactory1 = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        InitializeShaders();
        if (settings.SaveDevices)
        {
            deviceCache.Init(idxgiFactory1);
        }
    }

    private void ReInit()
    {
        Dispose();
#if DEBUG
        DXGI.DXGIGetDebugInterface1(out debug).CheckError();
#endif
        deviceCache = new DeviceCache(InitializeDevice);
        idxgiFactory1 = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (Settings.SaveDevices)
        {
            deviceCache.Init(idxgiFactory1);
        }
    }

    private void PrintDebug()
    {
#if DEBUG
        debug.ReportLiveObjects(DXGI.DebugAll,ReportLiveObjectFlags.Summary);
        // TODO: how to do this correctly?
        var idxgiInfoQueue = debug.QueryInterface<IDXGIInfoQueue>();
        var infoQueueMessage = idxgiInfoQueue.GetMessage(DXGI.DebugAll, 0);
        Console.WriteLine(infoQueueMessage.Description);
#endif
    }

    private readonly Dictionary<IntPtr /*hmon*/, DuplicationState> _duplications = new();
    private readonly Lock _lock = new(); // makes first-time creation threadsafe

    private sealed class DuplicationState(IDXGIOutputDuplication dup, ID3D11Texture2D staging, bool isHdr, ID3D11Device device) : IDisposable, DisposableCache
    {
        public IDXGIOutputDuplication Dup { get; } = dup;
        public ID3D11Texture2D Staging { get; set; } = staging;
        public bool IsHdr { get; } = isHdr;

        public ID3D11Device Device = device;

        public void ReleaseFrame(bool includeBuffer)
        {
            Dup?.ReleaseFrame();
            if (includeBuffer)
            {
                Staging?.Dispose();
                Staging = null;
            }
        }

        public void Dispose()
        {
            Dup?.Dispose();
            Staging?.Dispose();
        }

        public void ReleaseCachedValues(HdrSettings settings)
        {
            ReleaseFrame(!settings.ReuseBuffers);
        }
    }

    private DeviceCache GetCache()
    {
        // deviceCache.Dispose();
        // deviceCache = new DeviceCache(InitializeDevice);
        // deviceCache.Init(idxgiFactory1);
        return deviceCache;
    }

    private DuplicationState GetOrCreateDup(IntPtr hmon, bool forceRecreate = false)
    {
        lock (_lock)
        {
            if (_duplications.Count > MonitorEnumerationHelper.GetMonitorsCount())
            {
                foreach (var duplicationsValue in _duplications.Values)
                {
                    duplicationsValue.Dispose();
                }

                _duplications.Clear();
            }

            if (_duplications.TryGetValue(hmon, out var state))
            {
                if (!forceRecreate)
                {
                    if (Settings.ReuseBuffers && state.Staging != null) return state;
                    state.Staging?.Dispose();
                    state.Staging = CreateStagingBuffer(state.Device, state.Dup.Description);
                    return state;
                }

                state.Dup.Dispose();
                state.Staging.Dispose();
            }

            // your helper:
            var screen = GetCache().GetOutputForScreen(idxgiFactory1, hmon);

            // Ask for native format first, SDR fallback second
            var fmts = new[] { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm };

            using IDXGIOutput5 output5 = screen.Output.QueryInterface<IDXGIOutput5>();
            var dup = output5.DuplicateOutput1(screen.Device, fmts);

            var desc = dup.Description;
            bool isHdr = desc.ModeDescription.Format == Format.R16G16B16A16_Float;

            state = new DuplicationState(dup, CreateStagingBuffer(screen.Device, desc), isHdr, screen.Device);
            _duplications[hmon] = state;
            return state;
        }
    }

    private ID3D11Texture2D CreateStagingBuffer(ID3D11Device device, OutduplDescription desc)
    {
        var texDesc = new Texture2DDescription
        {
            Width = desc.ModeDescription.Width,
            Height = desc.ModeDescription.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.ModeDescription.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read | CpuAccessFlags.Write
        };
        return device.CreateTexture2D(texDesc);
    }


    /// Temporary struct to carry each region’s state
    private class RegionTempState
    {
        public ModernCaptureMonitorDescription Region;
        public DeviceAccess DeviceAccess;
        public ID3D11Device Device;
        public ID3D11DeviceContext Context;
        public Rectangle SrcRect;
    }

    public Bitmap CaptureAndProcess(HdrSettings hdrSettings, ModernCaptureItemDescription item)
    {
        // TODO: support multi-gpu setups
        item.Regions = CursorFilter.FilterByCursorGpu(deviceCache, idxgiFactory1, item.Regions);
        Settings = hdrSettings;
        List<DisposableCache> disposableCaches = [];
        try
        {
            bool forceCpuTonemap = false;

            // (A) First pass: discover if all Regions live on the *same* ID3D11Device, and gather per-region state:
            ID3D11Device commonDevice = null;
            ID3D11DeviceContext commonCtx = null;
            bool hasCommonDevice = true;
            var perRegionState = new List<RegionTempState>();
            ID3D11Device firstDevice = null;

            foreach (var r in item.Regions)
            {
                // 2) Grab the D3D11Device + Context for this monitor from your cache:
                var screenAccess = GetCache().GetOutputForScreen(idxgiFactory1, r.MonitorInfo.Hmon);
                ID3D11Device device = screenAccess.Device;
                ID3D11DeviceContext ctx = screenAccess.Context.Device.ImmediateContext;

                // 3) If this is the first region, capture its device as "common"; else check equality:
                if (commonDevice == null)
                {
                    commonDevice = device;
                    commonCtx = ctx;
                }
                else if (!ReferenceEquals(commonDevice, device))
                {
                    hasCommonDevice = false;
                    break;
                }

                // 4) Compute this region’s SrcRect (pixel‐coords inside the monitor texture):
                var srcRect = new Rectangle(
                    r.DestGdiRect.X - r.MonitorInfo.MonitorArea.X,
                    r.DestGdiRect.Y - r.MonitorInfo.MonitorArea.Y,
                    r.DestGdiRect.Width,
                    r.DestGdiRect.Height
                );

                perRegionState.Add(new RegionTempState
                {
                    Region = r,
                    Device = device,
                    DeviceAccess = screenAccess.Context,
                    Context = ctx,
                    SrcRect = srcRect,
                });
            }

            if (!hasCommonDevice)
            {
                throw new Exception("💀 We currently don't support screenshots across multiple GPUs");
            }
#if DEBUG
            var loaded = RenderDoc.Load(out var lib);
            if (loaded && lib != null) lib.StartFrameCapture();
#endif

            // (B) If GPU composition is allowed, create one big GPU canvas now:
            ID3D11Texture2D canvasGpu = null;
            ID3D11DeviceContext canvasContext = null;
            int W = item.CanvasRect.Width;
            int H = item.CanvasRect.Height;

            canvasGpu = Direct3DUtils.CreateCanvasTexture((uint)W, (uint)H, commonDevice);
            canvasContext = commonCtx;

            // (D) Now actually do one pass per region:
            foreach (var state in perRegionState)
            {
                var r = state.Region;
                var device = state.Device;
                var ctx = state.Context;
                var srcRect = state.SrcRect;

                // 1) AcquireNextFrame:
                var dupState = GetOrCreateDup(state.Region.MonitorInfo.Hmon);
                IDXGIResource resourcee;
                Result acquireNextFrame;
                OutduplFrameInfo outduplFrameInfo;
                do
                {
                    dupState.Dup.ReleaseFrame();
                    // sometimes this closes the device??? ?? ?? ? ? ???? wheen screen is in the nagtive space??? TODO
                    acquireNextFrame = dupState.Dup.AcquireNextFrame(10, out outduplFrameInfo, out resourcee);
                    if (acquireNextFrame.Failure) // TODO: only recreate on some errors?
                    {
                        if (acquireNextFrame.ApiCode != "WaitTimeout")
                        {
                            dupState.Dup.ReleaseFrame();
                            dupState = GetOrCreateDup(state.Region.MonitorInfo.Hmon, true);
                        }
                    }
                } while (!acquireNextFrame.Success || outduplFrameInfo.LastPresentTime == 0);

                using var resource = resourcee;
                using var frameTex = resource.QueryInterface<ID3D11Texture2D>();

                // 2) Copy GPU→staging (float or unorm, depending on format):
                ctx.CopyResource(dupState.Staging, frameTex);

                ID3D11Texture2D ldrSource = dupState.Staging;


                //   destBox is where to place it in the big canvas
                var destBox = new Box
                {
                    Left = r.DestGdiRect.X - item.CanvasRect.Left,
                    Top = r.DestGdiRect.Y - item.CanvasRect.Top,
                    Front = 0,
                    Back = 1,
                    Right = (r.DestGdiRect.X - item.CanvasRect.Left) + r.DestGdiRect.Width,
                    Bottom = ( r.DestGdiRect.Y - item.CanvasRect.Top) + r.DestGdiRect.Height
                };

                //   srcBox is the sub‐rectangle inside ldrSource
                var srcBox = new Box
                {
                    Left = srcRect.X,
                    Top = srcRect.Y,
                    Front = 0,
                    Back = 1,
                    Right = srcRect.Right,
                    Bottom = srcRect.Bottom
                };

                if (dupState.IsHdr)
                {
                    if (!forceCpuTonemap)
                    {
                        // GPU path: convert HDR staging → B8G8R8A8_UNorm GPU texture
                        ldrSource = Tonemapping.TonemapOnGpu(Settings, state.Region, state.DeviceAccess, dupState.Staging, frameTex, canvasGpu, destBox, srcBox);
                    }
                    else
                    {
                        // CPU path: convert HDR staging → B8G8R8A8_UNorm STAGING
                        ldrSource = Tonemapping.TonemapOnCpu(Settings, state.Region, state.DeviceAccess, frameTex);
                    }
                }
                else
                {
                    canvasContext.CopySubresourceRegion(
                        canvasGpu, // destination (big canvas)
                        0, // dest mip
                        (uint)destBox.Left, // dest X offset in canvas
                        (uint)destBox.Top, // dest Y offset in canvas
                        0, // dest Z
                        ldrSource, // source texture (either GPU‐tonemapped or staging if it was already unorm)
                        0, // source mip
                        srcBox
                    );
                }
                dupState.ReleaseFrame(!Settings.ReuseBuffers);
            } // end per‐region loop

            // 1) Copy GPU canvas → staging
            using var stagingCanvas = Direct3DUtils.CreateStagingFor(canvasGpu);
            canvasContext.CopyResource(stagingCanvas, canvasGpu);

            // 2) Map once, then build a Bitmap from that pointer
            var descSt = stagingCanvas.Description;
            var mapped = canvasContext.Map(stagingCanvas, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            Bitmap finalBitmap = BitmapUtils.BuildBitmapFromMappedPointer(
                mapped.DataPointer,
                (int)mapped.RowPitch,
                (int)descSt.Width,
                (int)descSt.Height
            );
            canvasContext.Unmap(stagingCanvas, 0);

            canvasGpu.Dispose();
            stagingCanvas.Dispose();
#if DEBUG
            if (loaded && lib != null) lib.EndFrameCapture();
#endif
            return finalBitmap;
        }
        catch (Exception e)
        {
            // somethingn went wrong, so lets scram
            foreach (var disposableCache in disposableCaches)
            {
                disposableCache.ReleaseCachedValues(Settings);
            }

            ReInit();

            throw new ApplicationException("HDR screenshot failed", e);
        }
        finally
        {
            foreach (var disposableCache in disposableCaches)
            {
                disposableCache.ReleaseCachedValues(Settings);
            }
            this.ReleaseCachedValues(Settings);
        }
    }

    private void InitializeDevice(DeviceAccess deviceAccess)
    {
        var device = deviceAccess.Device;
        deviceAccess.pxShader = device.CreatePixelShader(psShader);
        deviceAccess.vxShader = device.CreateVertexShader(vxShader);

        deviceAccess.inputLayout = device.CreateInputLayout(shaderInputElements, inputSignatureBlob);

        var samplerDesc = new SamplerDescription
        {
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxLOD = float.MaxValue,
            BorderColor = new Color4(0, 0, 0, 0),
            Filter = Filter.MinMagMipLinear
        };

        deviceAccess.samplerState = device.CreateSamplerState(samplerDesc);
    }

    private void InitializeShaders()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using (var vxShaderStream = assembly.GetManifestResourceStream($"{ShaderConstants.ResourcePrefix}.PostProcessingQuad.cso"))
        {
            vxShader = new byte[vxShaderStream.Length];
            vxShaderStream.ReadExactly(vxShader);
            inputSignatureBlob = Vortice.D3DCompiler.Compiler.GetInputSignatureBlob(vxShader);
        }

        using (var psShaderStream = assembly.GetManifestResourceStream($"{ShaderConstants.ResourcePrefix}.PostProcessingColor.cso"))
        {
            psShader = new byte[psShaderStream.Length];
            psShaderStream.ReadExactly(psShader);
        }
    }

    public void Dispose()
    {
        foreach (var duplicationsValue in _duplications.Values)
        {
            duplicationsValue.Dispose();
        }
        _duplications.Clear();
        deviceCache?.Dispose();
        deviceCache = null;
#if DEBUG
        debug?.Dispose();
#endif
    }

    public void ReleaseCachedValues(HdrSettings settings)
    {
        if (!settings.AvoidBuffering) return;
        foreach (var duplicationsValue in _duplications.Values)
        {
            duplicationsValue.Dispose();
        }
        _duplications.Clear();
        deviceCache?.ReleaseCachedValues(settings);
    }
}