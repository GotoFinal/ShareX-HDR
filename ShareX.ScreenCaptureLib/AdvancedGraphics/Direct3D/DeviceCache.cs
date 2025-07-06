using System;
using System.Collections.Generic;
using System.Linq;
using ShareX.ScreenCaptureLib.AdvancedGraphics.GDI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

class DeviceCache : IDisposable, DisposableCache
{
    private Action<DeviceAccess> deviceInit;
    private Dictionary<long, CachedOutput> adapters = new();
    private Dictionary<uint, DeviceAccess> devices = new();

    public DeviceCache(Action<DeviceAccess> deviceInit)
    {
        this.deviceInit = deviceInit;
    }

    // TODO: validate how to handle multi gpu setups with screens connected to more than 1 gpu
    public DeviceAccess GetMainDevice(IDXGIFactory1 factory)
    {
        if (devices.Values.Count == 0)
        {
            Init(factory);
            if (devices.Values.Count == 0) throw new InvalidOperationException("No devices available for screen capture?");
        }

        return devices.Values.FirstOrDefault();
    }


    // private ID3D11Device tempForcedDeviceCache;

    public ScreenDeviceAccess GetOutputForScreen(IDXGIFactory1 factory, IntPtr hMon, bool initDevice  = true) // TODO: separate function for dontInit/
    {
        var output = GetOrCreateOutput(factory, hMon);
        devices.TryGetValue(output.Adapter.Description.DeviceId, out DeviceAccess deviceAccess);
        if (initDevice && (deviceAccess == null || deviceAccess.Device.DeviceRemovedReason.Failure))
        {
            if (deviceAccess?.Device != null)
            {
                deviceAccess.Dispose();
                devices.Remove(output.Adapter.Description.DeviceId);
                return GetOutputForScreen(factory, hMon);
            }

            // if (tempForcedDeviceCache == null)
            // {
                D3D11.D3D11CreateDevice(
                    output.Adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport, //| DeviceCreationFlags.Debug, // | DeviceCreationFlags.Debuggable,
                    null,
                    out var newDevice).CheckError();
                if (newDevice == null) throw new ApplicationException("Could not create device for screen capture.");
                deviceAccess = new DeviceAccess(newDevice);
            // }
            // else
            // {
            //     deviceAccess = new DeviceAccess(tempForcedDeviceCache);
            // }

            deviceInit(deviceAccess);
            devices[output.Adapter.Description.DeviceId] = deviceAccess;
        }

        if (initDevice && (deviceAccess == null || deviceAccess.Device == null)) throw new ApplicationException("Could not create device for screen capture.");

        return new ScreenDeviceAccess
        {
            Adapter = output.Adapter,
            Device = deviceAccess?.Device,
            Context = deviceAccess,
            Output = output.Output,
        };
    }

    public void Init(IDXGIFactory1 factory)
    {
        foreach (var monitorInfo in MonitorEnumerationHelper.GetMonitors())
        {
            GetOutputForScreen(factory, monitorInfo.Hmon);
        }
    }

    private CachedOutput GetOrCreateOutput(IDXGIFactory1 factory, IntPtr hMon)
    {
        adapters.TryGetValue(hMon.ToInt64(), out CachedOutput cachedOutput);
        if (cachedOutput is not null)
        {
            return cachedOutput;
        }

        for (uint ai = 0; factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Success; ++ai)
        {
            for (uint oi = 0; adapter.EnumOutputs(oi, out IDXGIOutput output).Success; ++oi)
            {
                var desc = output.Description;
                if (desc.Monitor == hMon)
                {
                    cachedOutput = new CachedOutput(adapter, output.QueryInterface<IDXGIOutput1>());
                    break;
                }

                output.Dispose();
            }

            if (cachedOutput is not null) break;
            adapter.Dispose();
        }

        if (cachedOutput is null)
            throw new InvalidOperationException("Monitor not found");

        adapters[hMon.ToInt64()] = cachedOutput;
        return cachedOutput;
    }

    private class CachedOutput(IDXGIAdapter1 adapter, IDXGIOutput1 output) : IDisposable
    {
        internal IDXGIAdapter1 Adapter { get; } = adapter;
        internal IDXGIOutput1 Output { get; } = output;

        public void Dispose()
        {
            Adapter?.Dispose();
            Output?.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var deviceAccess in devices.Values)
        {
            deviceAccess.Dispose();
        }

        devices.Clear();
        foreach (var adaptersValue in adapters.Values)
        {
            adaptersValue.Dispose();
        }

        adapters.Clear();
    }

    public void ReleaseCachedValues(HdrSettings settings)
    {
        if (!settings.SaveDevices)
        {
            Dispose();
        }
    }
}

public struct ScreenDeviceAccess
{
    public IDXGIAdapter1 Adapter;
    public IDXGIOutput1 Output;
    public ID3D11Device Device;
    public DeviceAccess Context;
}

public class DeviceAccess : IDisposable
{
    public ID3D11PixelShader pxShader;
    public ID3D11VertexShader vxShader;
    public ID3D11InputLayout inputLayout;
    public ID3D11SamplerState samplerState;

    public DeviceAccess(ID3D11Device device)
    {
        Device = device;
    }

    public ID3D11Device Device { get; }


    public void Dispose()
    {
        samplerState?.Dispose();
        inputLayout?.Dispose();
        pxShader?.Dispose();
        vxShader?.Dispose();
        Device?.Dispose();
    }
}