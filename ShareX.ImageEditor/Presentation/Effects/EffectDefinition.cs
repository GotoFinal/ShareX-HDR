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

using ShareX.ImageEditor.Core.ImageEffects;
using ShareX.ImageEditor.Core.ImageEffects.Manipulations;
using ShareX.ImageEditor.Hosting;
using CoreEffectParameter = ShareX.ImageEditor.Core.ImageEffects.Parameters.EffectParameter;

namespace ShareX.ImageEditor.Presentation.Effects;

public sealed class EffectDefinition
{
    public string Id { get; }

    public string Name { get; }

    public string BrowserLabel { get; }

    public string Icon { get; }

    public string Description { get; }

    public ImageEffectCategory Category { get; }

    public Func<ImageEffect> CreateEffect { get; }

    public IReadOnlyList<EffectParameterDefinition> Parameters { get; }

    public IReadOnlyList<CoreEffectParameter> CoreParameters { get; }

    /// <summary>
    /// When true, the effect is applied immediately from the browser without opening a dialog.
    /// Used for parameterless effects like Invert, Black &amp; White, Polaroid, etc.
    /// </summary>
    public bool ApplyImmediately { get; }

    public EffectDefinition(
        string id,
        string browserLabel,
        string icon,
        string description,
        ImageEffectCategory category,
        Func<ImageEffect> createEffect,
        IReadOnlyList<EffectParameterDefinition> parameters,
        IReadOnlyList<CoreEffectParameter>? coreParameters = null,
        string? customEditorKey = null,
        bool applyImmediately = false)
        : this(
            id,
            DeriveName(browserLabel),
            browserLabel,
            icon,
            description,
            category,
            createEffect,
            parameters,
            coreParameters,
            applyImmediately)
    {
    }

    public EffectDefinition(
        string id,
        string name,
        string browserLabel,
        string icon,
        string description,
        ImageEffectCategory category,
        Func<ImageEffect> createEffect,
        IReadOnlyList<EffectParameterDefinition> parameters,
        IReadOnlyList<CoreEffectParameter>? coreParameters = null,
        bool applyImmediately = false)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        BrowserLabel = browserLabel ?? throw new ArgumentNullException(nameof(browserLabel));
        Icon = icon ?? throw new ArgumentNullException(nameof(icon));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Category = category;
        CreateEffect = createEffect ?? throw new ArgumentNullException(nameof(createEffect));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        CoreParameters = coreParameters ?? [];
        ApplyImmediately = applyImmediately;
    }

    public ImageEffect CreateConfiguredEffect(IEnumerable<EffectParameterState> parameterStates)
    {
        ImageEffect effect = CreateEffect();
        bool isResizeImageEffect = effect is ResizeImageEffect;
        NumericParameterState? widthState = null;
        NumericParameterState? heightState = null;

        foreach (EffectParameterState parameterState in parameterStates)
        {
            parameterState.ApplyValue(effect);

            if (!isResizeImageEffect || parameterState is not NumericParameterState numericParameter)
            {
                continue;
            }

            if (string.Equals(parameterState.Key, "width", StringComparison.OrdinalIgnoreCase))
            {
                widthState = numericParameter;
            }
            else if (string.Equals(parameterState.Key, "height", StringComparison.OrdinalIgnoreCase))
            {
                heightState = numericParameter;
            }
        }

        if (effect is ResizeImageEffect resizeImageEffect)
        {
            resizeImageEffect.AspectRatioAnchor = ResolveResizeImageAspectRatioAnchor(widthState, heightState);
        }

        return effect;
    }

    public EditorImageEffectDescriptor CreateDescriptor(IEnumerable<EffectParameterState> parameterStates)
    {
        ArgumentNullException.ThrowIfNull(parameterStates);

        EditorImageEffectParameter[] parameters = parameterStates
            .Select(CreateDescriptorParameter)
            .ToArray();
        EditorImageEffectReplayDeterminism replayDeterminism =
            EditorImageEffectHdrCapabilities.GetCapability(Id) == EditorImageEffectHdrCapability.NativeFp16
                ? EditorImageEffectReplayDeterminism.Deterministic
                : EditorImageEffectReplayDeterminism.Unspecified;
        return new EditorImageEffectDescriptor(Id, parameters, replayDeterminism);
    }

    private static EditorImageEffectParameter CreateDescriptorParameter(EffectParameterState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state switch
        {
            SliderParameterState slider => EditorImageEffectParameter.Number(state.Key, slider.Value),
            CheckboxParameterState checkbox => EditorImageEffectParameter.Boolean(state.Key, checkbox.Value),
            EnumParameterState enumParameter => EditorImageEffectParameter.Enum(
                state.Key,
                enumParameter.SelectedOption.Value),
            ColorParameterState color => EditorImageEffectParameter.Color(
                state.Key,
                color.Value.R,
                color.Value.G,
                color.Value.B,
                color.Value.A),
            NumericParameterState numeric => EditorImageEffectParameter.Decimal(
                state.Key,
                numeric.Value ?? numeric.DefaultValue),
            TextParameterState text => EditorImageEffectParameter.Text(state.Key, text.Value),
            FilePathParameterState filePath => EditorImageEffectParameter.FilePath(state.Key, filePath.Value),
            _ => throw new NotSupportedException($"Unsupported image-effect parameter state '{state.GetType().FullName}'.")
        };
    }

    private static ResizeImageEffectAspectRatioAnchor ResolveResizeImageAspectRatioAnchor(
        NumericParameterState? widthState,
        NumericParameterState? heightState)
    {
        long widthSequence = widthState?.LastChangedSequence ?? 0;
        long heightSequence = heightState?.LastChangedSequence ?? 0;

        if (widthSequence == heightSequence)
        {
            return ResizeImageEffectAspectRatioAnchor.LargestDimension;
        }

        return widthSequence > heightSequence
            ? ResizeImageEffectAspectRatioAnchor.Width
            : ResizeImageEffectAspectRatioAnchor.Height;
    }

    private static string DeriveName(string browserLabel)
    {
        if (browserLabel is null)
        {
            throw new ArgumentNullException(nameof(browserLabel));
        }

        return browserLabel.EndsWith("...", StringComparison.Ordinal)
            ? browserLabel[..^3]
            : browserLabel;
    }
}