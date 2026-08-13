#region License Information (GPL v3)

/*
    ShareX - A program to capture and share images
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShareX.ScreenCaptureLib
{
    public sealed record ObsGraphicsOffsetValues(
        uint D3D8Present,
        uint D3D9Present,
        uint D3D9PresentEx,
        uint D3D9PresentSwap,
        uint D3D9ClassOffset,
        uint IsD3D9ExClassOffset,
        uint DxgiPresent,
        uint DxgiPresent1,
        uint DxgiResize,
        uint DxgiRelease)
    {
        public ObsGraphicsOffsets ToNative()
        {
            return new ObsGraphicsOffsets
            {
                D3D8 = new ObsD3D8Offsets
                {
                    Present = D3D8Present
                },
                D3D9 = new ObsD3D9Offsets
                {
                    Present = D3D9Present,
                    PresentEx = D3D9PresentEx,
                    PresentSwap = D3D9PresentSwap,
                    D3D9ClassOffset = D3D9ClassOffset,
                    IsD3D9ExClassOffset = IsD3D9ExClassOffset
                },
                Dxgi = new ObsDxgiOffsets
                {
                    Present = DxgiPresent,
                    Resize = DxgiResize,
                    Present1 = DxgiPresent1
                },
                Dxgi2 = new ObsDxgiOffsets2
                {
                    Release = DxgiRelease
                }
            };
        }
    }

    public static class ObsGraphicsOffsetParser
    {
        private static readonly string[] D3D8Fields = { "present" };
        private static readonly string[] D3D9Fields =
        {
            "present", "present_ex", "present_swap", "d3d9_clsoff", "is_d3d9ex_clsoff"
        };
        private static readonly string[] DxgiFields = { "present", "present1", "resize", "release" };

        public static ObsGraphicsOffsetValues Parse(string output)
        {
            ArgumentNullException.ThrowIfNull(output);

            string[] tokens = output.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int index = 0;
            Dictionary<string, uint> d3d8 = ParseSection(tokens, ref index, "d3d8", D3D8Fields);
            Dictionary<string, uint> d3d9 = ParseSection(tokens, ref index, "d3d9", D3D9Fields);
            Dictionary<string, uint> dxgi = ParseSection(tokens, ref index, "dxgi", DxgiFields);

            if (index != tokens.Length)
            {
                throw new FormatException("OBS graphics-offset helper returned unexpected trailing fields.");
            }

            return new ObsGraphicsOffsetValues(
                d3d8["present"],
                d3d9["present"],
                d3d9["present_ex"],
                d3d9["present_swap"],
                d3d9["d3d9_clsoff"],
                d3d9["is_d3d9ex_clsoff"],
                dxgi["present"],
                dxgi["present1"],
                dxgi["resize"],
                dxgi["release"]);
        }

        private static Dictionary<string, uint> ParseSection(
            IReadOnlyList<string> tokens,
            ref int index,
            string expectedSection,
            IReadOnlyCollection<string> expectedFields)
        {
            if (index >= tokens.Count ||
                !string.Equals(tokens[index++], $"[{expectedSection}]", StringComparison.Ordinal))
            {
                throw new FormatException($"Missing or reordered [{expectedSection}] graphics-offset section.");
            }

            var values = new Dictionary<string, uint>(StringComparer.Ordinal);

            for (int i = 0; i < expectedFields.Count; i++)
            {
                if (index >= tokens.Count)
                {
                    throw new FormatException($"Missing [{expectedSection}] graphics-offset field.");
                }

                string token = tokens[index++];
                int separator = token.IndexOf('=');

                if (separator <= 0 || separator == token.Length - 1)
                {
                    throw new FormatException($"Invalid [{expectedSection}] key/value token.");
                }

                string key = token[..separator];
                string valueText = token[(separator + 1)..];

                if (!expectedFields.Contains(key, StringComparer.Ordinal) || !values.TryAdd(key, ParseHex(valueText)))
                {
                    throw new FormatException($"Unexpected or duplicate [{expectedSection}] field.");
                }
            }

            if (values.Count != expectedFields.Count)
            {
                throw new FormatException($"Missing [{expectedSection}] graphics-offset field.");
            }

            return values;
        }

        private static uint ParseHex(string text)
        {
            if (!text.StartsWith("0x", StringComparison.Ordinal) || text.Length <= 2 ||
                !uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
            {
                throw new FormatException("Invalid graphics-offset hexadecimal value.");
            }

            return value;
        }
    }
}
