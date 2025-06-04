using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D.Shaders;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class ModernCapture : IDisposable
{
    private ModernCaptureItemDescription description;

    private DeviceCache deviceCache;
    private IDXGIFactory1 idxgiFactory1;

    private InputElementDescription[] shaderInputElements =
    [
        new("POSITION", 0, Format.R32G32B32_Float, 0),
        new("TEXCOORD", 0, Format.R32G32_Float, 0)
    ];

    private byte[] vxShader;
    private byte[] psShader;
    private Blob inputSignatureBlob;

    public ModernCapture()
    {
#if DEBUG
            // Check memory leaks in debug config
            Configuration.EnableObjectTracking = true;
#endif

        deviceCache = new DeviceCache(InitializeDevice);
        idxgiFactory1 = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        InitializeShaders();
        deviceCache.Init(idxgiFactory1);
    }

    private readonly Dictionary<IntPtr /*hmon*/, DuplicationState> _duplications = new();
    private readonly Lock _lock = new(); // makes first-time creation threadsafe

    private sealed record DuplicationState(
        IDXGIOutputDuplication Dup,
        ID3D11Texture2D Staging, // TODO: option to create new one each time
        bool IsHdr); // remember if this output is in HDR

    private DuplicationState GetOrCreateDup(IntPtr hmon, bool forceRecreate = false)
    {
        lock (_lock)
        {
            if (_duplications.TryGetValue(hmon, out var state))
            {
                if (!forceRecreate) return state;
                state.Dup.Dispose();
                state.Staging.Dispose();
            }

            // your helper:
            var screen = deviceCache.GetOutputForScreen(idxgiFactory1, hmon);

            // Ask for native format first, SDR fallback second
            var fmts = new[] { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm };

            using IDXGIOutput5 output5 = screen.Output.QueryInterface<IDXGIOutput5>();
            var dup = output5.DuplicateOutput1(screen.Device, fmts);

            // inspect the duplication-descriptor to know what we actually got
            var desc = dup.Description;
            bool isHdr = desc.ModeDescription.Format == Format.R16G16B16A16_Float;

            // create a per-output staging texture we’ll copy into every frame
            var texDesc = new Texture2DDescription
            {
                Width = desc.ModeDescription.Width,
                Height = desc.ModeDescription.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.ModeDescription.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Read
            };
            var staging = screen.Device.CreateTexture2D(texDesc);

            state = new DuplicationState(dup, staging, isHdr);
            _duplications[hmon] = state;
            return state;
        }
    }


    /// Temporary struct to carry each region’s state
    private class RegionTempState
    {
        public ModernCaptureMonitorDescription Region;
        public DeviceAccess DeviceAccess;
        public ID3D11Device Device;
        public ID3D11DeviceContext Context;
        public Rectangle SrcRect;
        public ShaderHdrMetadata HdrMetadata;
    }

    public Bitmap CaptureAndProcess(ModernCaptureItemDescription item)
    {
        bool forceCpuTonemap = false;

        // (A) First pass: discover if all Regions live on the *same* ID3D11Device, and gather per-region state:
        ID3D11Device commonDevice = null;
        ID3D11DeviceContext commonCtx = null;
        bool hasCommonDevice = true;
        var perRegionState = new List<RegionTempState>();

        foreach (var r in item.Regions)
        {
            // 2) Grab the D3D11Device + Context for this monitor from your cache:
            var screenAccess = deviceCache.GetOutputForScreen(idxgiFactory1, r.MonitorInfo.Hmon);
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
                HdrMetadata = r.HdrMetadata
            });
        }

        // If we discovered multi‐GPU, we can no longer do GPU‐side composition in one canvas.
        bool gpuComposeAllowed = true; //hasCommonDevice; TODO

        // (B) If GPU composition is allowed, create one big GPU canvas now:
        ID3D11Texture2D canvasGpu = null;
        ID3D11DeviceContext canvasContext = null;
        if (gpuComposeAllowed)
        {
            int W = item.CanvasRect.Width;
            int H = item.CanvasRect.Height;

            canvasGpu = Direct3DUtils.CreateCanvasTexture((uint)W, (uint)H, commonDevice);
            canvasContext = commonCtx;
        }

        // (C) Allocate a CPU‐side buffer only if we’re going to do multi‐GPU (or CPU fallback):
        int fullW = item.CanvasRect.Width;
        int fullH = item.CanvasRect.Height;
        int fullPitch = fullW * 4; // 4 bytes per pixel (B8G8R8A8)
        byte[] cpuCanvasBytes = gpuComposeAllowed
            ? null
            : new byte[fullPitch * fullH];

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
                // sometimes this closes the device??? ?? ?? ? ? ???? TODO
                acquireNextFrame = dupState.Dup.AcquireNextFrame(10, out outduplFrameInfo, out resourcee);
                if (acquireNextFrame.Failure) // TODO: only recreate on some errors?
                {
                    Console.WriteLine("acquireNextFrame.Failure: " + acquireNextFrame.Description + ", " + acquireNextFrame.ApiCode); // TODO: remove
                    dupState = GetOrCreateDup(state.Region.MonitorInfo.Hmon, true);
                }
            } while (!acquireNextFrame.Success);

            using var resource = resourcee;
            using var frameTex = resource.QueryInterface<ID3D11Texture2D>();
            Console.WriteLine("outduplFrameInfo: " + outduplFrameInfo);

            // 2) Copy GPU→staging (float or unorm, depending on format):
            ctx.CopyResource(dupState.Staging, frameTex);
            dupState.Dup.ReleaseFrame();

            // 3) Choose tonemap path:
            ID3D11Texture2D ldrSource = dupState.Staging;
            if (dupState.IsHdr)
            {
                if (gpuComposeAllowed && !forceCpuTonemap)
                {
                    // GPU path: convert HDR staging → B8G8R8A8_UNORM GPU texture
                    ldrSource = Tonemapping.TonemapOnGpu(state.Region, state.DeviceAccess, dupState.Staging, device, ctx, state.HdrMetadata);
                }
                else
                {
                    // CPU path: convert HDR staging → B8G8R8A8_UNORM STAGING
                    ldrSource = Tonemapping.TonemapOnCpu(state.Region, state.DeviceAccess, dupState.Staging, device, ctx, state.HdrMetadata);
                }
            }
            // If not HDR, then dupState.Staging is already B8G8R8A8_UNorm or B8G8R8A8_UNorm fallback.

            // 4) Extract pixel‐rectangle and place into either the GPU canvas or CPU canvas:
            if (gpuComposeAllowed)
            {
                // GPU→GPU CopySubresource:
                //   destBox is where to place it in the big canvas
                var destBox = new Box
                {
                    Left = r.DestGdiRect.X,
                    Top = r.DestGdiRect.Y,
                    Front = 0,
                    Back = 1,
                    Right = r.DestGdiRect.Right,
                    Bottom = r.DestGdiRect.Bottom
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
            else
            {
                // We’re in multi‐GPU or forced CPU fallback mode.
                // Map the LDR source (which is a staging in UNORM8) and copy its bytes into our CPU canvas.
                var descLdr = ldrSource.Description;
                var mapped = ctx.Map(ldrSource, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                unsafe
                {
                    byte* srcBase = (byte*)mapped.DataPointer;
                    for (int yy = 0; yy < srcRect.Height; yy++)
                    {
                        // Source row is at (srcRect.Y + yy) in the staging
                        byte* rowPtr = srcBase + (yy + srcRect.Y) * mapped.RowPitch
                                               + srcRect.X * 4; // 4 bytes per pixel

                        // Dest row in the CPU canvas is (DestGdiRect.Y + yy)
                        int destY = r.DestGdiRect.Y + yy;
                        int destOffset = destY * fullPitch + (r.DestGdiRect.X * 4);

                        Marshal.Copy(
                            new IntPtr(rowPtr),
                            cpuCanvasBytes,
                            destOffset,
                            srcRect.Width * 4
                        );
                    }
                }

                ctx.Unmap(ldrSource, 0);
            }

            // If we created a temporary TonemapOnGpu or TonemapOnCpu texture, dispose it now:
            if (ReferenceEquals(ldrSource, dupState.Staging) == false)
            {
                ldrSource.Dispose();
            }
        } // end per‐region loop

        // (E) Build a System.Drawing.Bitmap and return it
        if (gpuComposeAllowed)
        {
            // 1) Copy GPU canvas → staging
            using var stagingCanvas = Direct3DUtils.CreateStagingFor(canvasGpu);
            canvasContext.CopyResource(stagingCanvas, canvasGpu);

            // 2) Map once, then build a Bitmap from that pointer
            var descSt = stagingCanvas.Description;
            var mapped = canvasContext.Map(stagingCanvas, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            // TODO: test
            // SDRHelper.SKIV_Image_SaveToDisk_SDR(frame, d3dDevice, "C:\\NoBackup\\test.png", false);
            Bitmap finalBitmap = BitmapUtils.BuildBitmapFromMappedPointer(
                mapped.DataPointer,
                (int)mapped.RowPitch,
                (int)descSt.Width,
                (int)descSt.Height
            );
            canvasContext.Unmap(stagingCanvas, 0);

            canvasGpu.Dispose();
            return finalBitmap;
        }
        else
        {
            // CPU path; we already have byte[] cpuCanvasBytes in BGRA8 format row‐major:
            return BitmapUtils.BuildBitmapFromByteArray(cpuCanvasBytes, fullW, fullH);
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
        using (var vxShaderStream = assembly.GetManifestResourceStream($"{ShaderConstant.ResourcePrefix}.PostProcessingQuad.cso"))
        {
            vxShader = new byte[vxShaderStream.Length];
            vxShaderStream.ReadExactly(vxShader);
            inputSignatureBlob = Vortice.D3DCompiler.Compiler.GetInputSignatureBlob(vxShader);
        }

        using (var psShaderStream = assembly.GetManifestResourceStream($"{ShaderConstant.ResourcePrefix}.PostProcessingColor.cso"))
        {
            psShader = new byte[psShaderStream.Length];
            psShaderStream.ReadExactly(psShader);
        }
    }

    public void Dispose()
    {
        deviceCache?.Dispose();
    }
}