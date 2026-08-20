#region License Information (GPL v3)

/*
    ShareX - A program for capturing and sharing images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;

namespace ShareX.ScreenCaptureLib
{
    internal static class HdrEncodingPerformance
    {
        internal static Action<string> LogSink { get; set; }

        internal static void Log(string message)
        {
            DebugHelper.WriteLine(message);
            LogSink?.Invoke(message);

            if (string.Equals(
                Environment.GetEnvironmentVariable("SHAREX_RUN_HDR_OUTPUT_PERFORMANCE_TESTS"),
                "1",
                StringComparison.Ordinal))
            {
                Console.WriteLine(message);
            }
        }
    }
}
