using System;
using System.Drawing;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.GDI
{
    public class MonitorInfo
    {
        public bool IsPrimary { get; set; }
        public Rectangle MonitorArea { get; set; }
        public Rectangle WorkArea { get; set; }
        public string DeviceName { get; set; }
        public IntPtr Hmon { get; set; }

        public void QueryMonitorData(Action<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO?, DISPLAYCONFIG_SDR_WHITE_LEVEL?, IDXGIOutput6> func)
        {
            var err = BetterWin32Errors.Win32Error.ERROR_SUCCESS;
            bool monAdvColorInfoFound = false;
            var monAdvColorInfo = DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO.CreateGet();
            bool monSdrWhiteLevelFound = false;
            var monSdrWhiteLevel = DISPLAYCONFIG_SDR_WHITE_LEVEL.CreateGet();
            uint numPathArrayElements = 0;
            uint numModeInfoArrayElements = 0;

            err = GdiInterop.GetDisplayConfigBufferSizes(QDC_CONSTANT.QDC_ONLY_ACTIVE_PATHS,
                ref numPathArrayElements, ref numModeInfoArrayElements);
            if (err != BetterWin32Errors.Win32Error.ERROR_SUCCESS)
            {
                throw new System.ComponentModel.Win32Exception((int)err);
            }

            var displayPathInfoArray = new DISPLAYCONFIG_PATH_INFO[numPathArrayElements];
            var displayModeInfoArray = new DISPLAYCONFIG_MODE_INFO[numModeInfoArrayElements];

            err = GdiInterop.QueryDisplayConfig(QDC_CONSTANT.QDC_ONLY_ACTIVE_PATHS,
                ref numPathArrayElements, displayPathInfoArray,
                ref numModeInfoArrayElements, displayModeInfoArray, IntPtr.Zero);
            if (err != BetterWin32Errors.Win32Error.ERROR_SUCCESS)
            {
                throw new System.ComponentModel.Win32Exception((int)err);
            }

            for (uint pathIdx = 0; pathIdx < numPathArrayElements; pathIdx++)
            {
                DISPLAYCONFIG_SOURCE_DEVICE_NAME srcName = DISPLAYCONFIG_SOURCE_DEVICE_NAME.CreateGet();
                srcName.header.adapterId.HighPart = displayPathInfoArray[pathIdx].sourceInfo.adapterId.HighPart;
                srcName.header.adapterId.LowPart = displayPathInfoArray[pathIdx].sourceInfo.adapterId.LowPart;
                srcName.header.id = displayPathInfoArray[pathIdx].sourceInfo.id;

                err = GdiInterop.DisplayConfigGetDeviceInfo(ref srcName);
                if (err != BetterWin32Errors.Win32Error.ERROR_SUCCESS)
                {
                    throw new System.ComponentModel.Win32Exception((int)err);
                }

                if (srcName.DeviceName == DeviceName)
                {
                    // If matches, proceed to query color information
                    monAdvColorInfo.header.adapterId.HighPart = displayPathInfoArray[pathIdx].targetInfo.adapterId.HighPart;
                    monAdvColorInfo.header.adapterId.LowPart = displayPathInfoArray[pathIdx].targetInfo.adapterId.LowPart;
                    monAdvColorInfo.header.id = displayPathInfoArray[pathIdx].targetInfo.id;

                    monSdrWhiteLevel.header.adapterId.HighPart = displayPathInfoArray[pathIdx].targetInfo.adapterId.HighPart;
                    monSdrWhiteLevel.header.adapterId.LowPart = displayPathInfoArray[pathIdx].targetInfo.adapterId.LowPart;
                    monSdrWhiteLevel.header.id = displayPathInfoArray[pathIdx].targetInfo.id;

                    err = GdiInterop.DisplayConfigGetDeviceInfo(ref monAdvColorInfo);
                    if (err != BetterWin32Errors.Win32Error.ERROR_SUCCESS)
                    {
                        throw new System.ComponentModel.Win32Exception((int)err);
                    }

                    monAdvColorInfoFound = true;

                    err = GdiInterop.DisplayConfigGetDeviceInfo(ref monSdrWhiteLevel);
                    monSdrWhiteLevelFound = err == BetterWin32Errors.Win32Error.ERROR_SUCCESS;
                    // Should just throw?
                }
            }


            var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            bool done = false;
            for (uint ai = 0; factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Success; ++ai)
            {
                for (uint oi = 0; adapter.EnumOutputs(oi, out IDXGIOutput output).Success; ++oi)
                {
                    if (output.Description.DeviceName != DeviceName)
                    {
                        continue;
                    }

                    done = true;
                    using (var output6 = output.QueryInterface<IDXGIOutput6>())
                    {
                        func(monAdvColorInfoFound ? monAdvColorInfo : null, monSdrWhiteLevelFound ? monSdrWhiteLevel : null, output6);
                    }
                }

                adapter.Dispose();
                if (done) return;
            }

            func(monAdvColorInfoFound ? monAdvColorInfo : null, monSdrWhiteLevelFound ? monSdrWhiteLevel : null, null);
        }
    }
}