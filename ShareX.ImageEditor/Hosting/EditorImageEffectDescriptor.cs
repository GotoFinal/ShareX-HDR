#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System.Collections.ObjectModel;
using System.Globalization;

namespace ShareX.ImageEditor.Hosting;

public enum EditorImageEffectParameterKind
{
    Number,
    Decimal,
    Boolean,
    Color,
    Text,
    Enum,
    FilePath
}

public enum EditorImageEffectReplayDeterminism
{
    Unspecified,
    Deterministic,
    Seeded
}

public enum EditorImageEffectHdrCapability
{
    SdrOnly,
    NativeFp16
}

/// <summary>
/// Immutable, transport-safe parameter value for a destructive editor image effect.
/// The separate value slots avoid culture-sensitive object/string serialization.
/// </summary>
public sealed class EditorImageEffectParameter
{
    public string Key { get; }
    public EditorImageEffectParameterKind Kind { get; }
    public double NumberValue { get; }
    public decimal DecimalValue { get; }
    public bool BooleanValue { get; }
    public byte Red { get; }
    public byte Green { get; }
    public byte Blue { get; }
    public byte Alpha { get; }
    public string? StringValue { get; }
    public string? ValueType { get; }

    private EditorImageEffectParameter(
        string key,
        EditorImageEffectParameterKind kind,
        double numberValue = 0,
        decimal decimalValue = 0,
        bool booleanValue = false,
        byte red = 0,
        byte green = 0,
        byte blue = 0,
        byte alpha = 0,
        string? stringValue = null,
        string? valueType = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("An image-effect parameter key is required.", nameof(key));
        }

        Key = key;
        Kind = kind;
        NumberValue = numberValue;
        DecimalValue = decimalValue;
        BooleanValue = booleanValue;
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        StringValue = stringValue;
        ValueType = valueType;
    }

    internal static EditorImageEffectParameter Number(string key, double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Image-effect numeric parameters must be finite.");
        }

        return new EditorImageEffectParameter(key, EditorImageEffectParameterKind.Number, numberValue: value);
    }

    internal static EditorImageEffectParameter Decimal(string key, decimal value) =>
        new(key, EditorImageEffectParameterKind.Decimal, decimalValue: value);

    internal static EditorImageEffectParameter Boolean(string key, bool value) =>
        new(key, EditorImageEffectParameterKind.Boolean, booleanValue: value);

    internal static EditorImageEffectParameter Color(string key, byte red, byte green, byte blue, byte alpha) =>
        new(key, EditorImageEffectParameterKind.Color, red: red, green: green, blue: blue, alpha: alpha);

    internal static EditorImageEffectParameter Text(string key, string value) =>
        new(key, EditorImageEffectParameterKind.Text, stringValue: value ?? string.Empty);

    internal static EditorImageEffectParameter FilePath(string key, string value) =>
        new(key, EditorImageEffectParameterKind.FilePath, stringValue: value ?? string.Empty);

    internal static EditorImageEffectParameter Enum(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Type type = value.GetType();
        string invariantValue = value is System.Enum
            ? value.ToString()!
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        return new EditorImageEffectParameter(
            key,
            EditorImageEffectParameterKind.Enum,
            stringValue: invariantValue,
            valueType: type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
    }
}

/// <summary>
/// Versioned semantic identity for an image effect committed by the modern editor.
/// It is retained through undo/redo even when HDR replay is not implemented yet.
/// </summary>
public sealed class EditorImageEffectDescriptor
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; }
    public string EffectId { get; }
    public IReadOnlyList<EditorImageEffectParameter> Parameters { get; }
    public EditorImageEffectReplayDeterminism ReplayDeterminism { get; }
    public int? ReplaySeed { get; }

    internal EditorImageEffectDescriptor(
        string effectId,
        IEnumerable<EditorImageEffectParameter> parameters,
        EditorImageEffectReplayDeterminism replayDeterminism = EditorImageEffectReplayDeterminism.Unspecified,
        int? replaySeed = null,
        int schemaVersion = CurrentSchemaVersion)
    {
        if (string.IsNullOrWhiteSpace(effectId))
        {
            throw new ArgumentException("An image-effect id is required.", nameof(effectId));
        }

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        if (replayDeterminism == EditorImageEffectReplayDeterminism.Seeded && replaySeed == null)
        {
            throw new ArgumentException("A seeded image effect must carry its replay seed.", nameof(replaySeed));
        }

        if (replayDeterminism != EditorImageEffectReplayDeterminism.Seeded && replaySeed != null)
        {
            throw new ArgumentException("A replay seed is valid only for a seeded image effect.", nameof(replaySeed));
        }

        EditorImageEffectParameter[] ownedParameters = parameters?.ToArray() ??
            throw new ArgumentNullException(nameof(parameters));
        if (ownedParameters.Any(parameter => parameter == null))
        {
            throw new ArgumentException("Image-effect parameters cannot contain null entries.", nameof(parameters));
        }

        if (ownedParameters
            .GroupBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Image-effect parameter keys must be unique.", nameof(parameters));
        }

        SchemaVersion = schemaVersion;
        EffectId = effectId;
        Parameters = new ReadOnlyCollection<EditorImageEffectParameter>(ownedParameters);
        ReplayDeterminism = replayDeterminism;
        ReplaySeed = replaySeed;
    }

    public bool TryGetNumber(string key, out double value)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            foreach (EditorImageEffectParameter parameter in Parameters)
            {
                if (!string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (parameter.Kind == EditorImageEffectParameterKind.Number)
                {
                    value = parameter.NumberValue;
                    return true;
                }

                if (parameter.Kind == EditorImageEffectParameterKind.Decimal)
                {
                    value = (double)parameter.DecimalValue;
                    return true;
                }

                break;
            }
        }

        value = 0;
        return false;
    }
}

/// <summary>
/// Conservative registry boundary for native HDR image-effect replay. New and
/// unclassified effects are SDR-only; an effect may be promoted only alongside
/// a version-aware FP16 implementation and deterministic replay contract.
/// </summary>
public static class EditorImageEffectHdrCapabilities
{
    private static readonly IReadOnlyDictionary<string, EditorImageEffectHdrCapability> Capabilities =
        new ReadOnlyDictionary<string, EditorImageEffectHdrCapability>(
            new Dictionary<string, EditorImageEffectHdrCapability>(StringComparer.OrdinalIgnoreCase)
            {
                ["alpha"] = EditorImageEffectHdrCapability.NativeFp16,
                ["black_and_white"] = EditorImageEffectHdrCapability.NativeFp16,
                ["blur"] = EditorImageEffectHdrCapability.NativeFp16,
                ["brightness"] = EditorImageEffectHdrCapability.NativeFp16,
                ["contrast"] = EditorImageEffectHdrCapability.NativeFp16,
                ["exposure"] = EditorImageEffectHdrCapability.NativeFp16,
                ["gamma"] = EditorImageEffectHdrCapability.NativeFp16,
                ["gaussian_blur"] = EditorImageEffectHdrCapability.NativeFp16,
                ["grayscale"] = EditorImageEffectHdrCapability.NativeFp16,
                ["hue"] = EditorImageEffectHdrCapability.NativeFp16,
                ["invert"] = EditorImageEffectHdrCapability.NativeFp16,
                ["pixelate"] = EditorImageEffectHdrCapability.NativeFp16,
                ["saturation"] = EditorImageEffectHdrCapability.NativeFp16,
                ["sepia"] = EditorImageEffectHdrCapability.NativeFp16,
                ["temperature_tint"] = EditorImageEffectHdrCapability.NativeFp16,
                ["threshold"] = EditorImageEffectHdrCapability.NativeFp16,
                ["vibrance"] = EditorImageEffectHdrCapability.NativeFp16
            });

    public static EditorImageEffectHdrCapability GetCapability(string effectId)
    {
        if (string.IsNullOrWhiteSpace(effectId))
        {
            return EditorImageEffectHdrCapability.SdrOnly;
        }

        return Capabilities.TryGetValue(effectId, out EditorImageEffectHdrCapability capability)
            ? capability
            : EditorImageEffectHdrCapability.SdrOnly;
    }
}
