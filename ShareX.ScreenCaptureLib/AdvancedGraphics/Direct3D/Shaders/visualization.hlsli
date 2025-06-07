//
// Copyright 2024 Andon "Kaldaien" Coleman
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

RWTexture2D <float4> texGamutCoverage : register (u1);

static const uint SKIV_VISUALIZATION_NONE    = 0;
static const uint SKIV_VISUALIZATION_HEATMAP = 1;
static const uint SKIV_VISUALIZATION_GAMUT   = 2;
static const uint SKIV_VISUALIZATION_SDR     = 3;

static const uint SKIV_VIZ_FLAG_SDR_CONSIDER_LUMINANCE  = 0x1;
static const uint SKIV_VIZ_FLAG_SDR_CONSIDER_GAMUT      = 0x2;
static const uint SKIV_VIZ_FLAG_SDR_CONSIDER_OVERBRIGHT = 0x4;

void UpdateCIE1931 (float4 hdr_color)
{
  float3 XYZ = Rec709toXYZ (hdr_color.rgb);
  float  xyz = XYZ.x + XYZ.y + XYZ.z;

  float4 normalized_color =
    float4 (hdr_color.rgb / xyz, 1.0);

  if (all (Rec709toAP1_D65 (hdr_color.rgb) >= FP16_MIN))
  {
    texGamutCoverage [ uint2 (        1024 * XYZ.x / xyz,
                               1024 - 1024 * XYZ.y / xyz ) ].rgba =
      float4 (normalized_color.rgb * 2.0f, 1.0f);
  }
}

// HDR Color Input is Linear Rec 709 (scRGB)
float4 ApplyHDRVisualization (uint type, float4 hdr_color, bool post_tonemap)
{
  // Ideally this remains constant with changes in luminance, but
  //   let's test that theory :)
  if (        post_tonemap  && type != SKIV_VISUALIZATION_GAMUT) UpdateCIE1931 (hdr_color);
  else if ((! post_tonemap) && type == SKIV_VISUALIZATION_GAMUT) UpdateCIE1931 (hdr_color);

  switch (type)
  {
    case SKIV_VISUALIZATION_SDR:
    {
      if (post_tonemap)
          return hdr_color;
      
      float luminance =
        max (Rec709toXYZ (hdr_color.rgb).y, 0.0);

      bool sdr       = true;
      uint viz_flags = hdr_visualization_flags [SKIV_VISUALIZATION_SDR];

      if      ((viz_flags & SKIV_VIZ_FLAG_SDR_CONSIDER_LUMINANCE)  && luminance > 1.0f/*sdr_reference_white*/) sdr = false;
      else if ((viz_flags & SKIV_VIZ_FLAG_SDR_CONSIDER_GAMUT)      && any (hdr_color < 0.0))                   sdr = false;
      else if ((viz_flags & SKIV_VIZ_FLAG_SDR_CONSIDER_OVERBRIGHT) && any (hdr_color > 1.0))                   sdr = false;

      return
        sdr ? float4 (luminance.xxx, 1.0f)
            : float4 (hdr_color.rgb, 1.0f);
    } break;

    case SKIV_VISUALIZATION_HEATMAP:
    {
      if (post_tonemap)
        return hdr_color;

      // Taken from https://github.com/microsoft/Windows-universal-samples/blob/main/Samples/D2DAdvancedColorImages/cpp/D2DAdvancedColorImages/LuminanceHeatmapEffect.hlsl in order to match HDR + WCG Image Viewer's Heatmap
      //
      // Define constants based on above behavior: 9 "stops" for a piecewise linear gradient in scRGB space.
      #define STOP0_NITS 0.00f
      #define STOP1_NITS 3.16f
      #define STOP2_NITS 10.0f
      #define STOP3_NITS 31.6f
      #define STOP4_NITS 100.f
      #define STOP5_NITS 316.f
      #define STOP6_NITS 1000.f
      #define STOP7_NITS 3160.f
      #define STOP8_NITS 10000.f

      #define STOP0_COLOR float4 (0.0f, 0.0f, 0.0f, 1.0f) // Black
      #define STOP1_COLOR float4 (0.0f, 0.0f, 1.0f, 1.0f) // Blue
      #define STOP2_COLOR float4 (0.0f, 1.0f, 1.0f, 1.0f) // Cyan
      #define STOP3_COLOR float4 (0.0f, 1.0f, 0.0f, 1.0f) // Green
      #define STOP4_COLOR float4 (1.0f, 1.0f, 0.0f, 1.0f) // Yellow
      #define STOP5_COLOR float4 (1.0f, 0.2f, 0.0f, 1.0f) // Orange
      // Orange isn't a simple combination of primary colors but allows us to have 8 gradient segments,
      // which gives us cleaner definitions for the nits --> color mappings.
      #define STOP6_COLOR float4 (1.0f, 0.0f, 0.0f, 1.0f) // Red
      #define STOP7_COLOR float4 (1.0f, 0.0f, 1.0f, 1.0f) // Magenta
      #define STOP8_COLOR float4 (1.0f, 1.0f, 1.0f, 1.0f) // White

      // 1: Calculate luminance in nits.
      // Input is in scRGB. First convert to Y from CIEXYZ, then scale by whitepoint of 80 nits.
      float nits =
        max (Rec709toXYZ (hdr_color.rgb).y, 0.0) * 80.0f;

      // 2: Determine which gradient segment will be used.
      // Only one of useSegmentN will be 1 (true) for a given nits value.
      float useSegment0 = sign (nits - STOP0_NITS) - sign (nits - STOP1_NITS);
      float useSegment1 = sign (nits - STOP1_NITS) - sign (nits - STOP2_NITS);
      float useSegment2 = sign (nits - STOP2_NITS) - sign (nits - STOP3_NITS);
      float useSegment3 = sign (nits - STOP3_NITS) - sign (nits - STOP4_NITS);
      float useSegment4 = sign (nits - STOP4_NITS) - sign (nits - STOP5_NITS);
      float useSegment5 = sign (nits - STOP5_NITS) - sign (nits - STOP6_NITS);
      float useSegment6 = sign (nits - STOP6_NITS) - sign (nits - STOP7_NITS);
      float useSegment7 = sign (nits - STOP7_NITS) - sign (nits - STOP8_NITS);

      // 3: Calculate the interpolated color.
      float lerpSegment0 = (nits - STOP0_NITS) / (STOP1_NITS - STOP0_NITS);
      float lerpSegment1 = (nits - STOP1_NITS) / (STOP2_NITS - STOP1_NITS);
      float lerpSegment2 = (nits - STOP2_NITS) / (STOP3_NITS - STOP2_NITS);
      float lerpSegment3 = (nits - STOP3_NITS) / (STOP4_NITS - STOP3_NITS);
      float lerpSegment4 = (nits - STOP4_NITS) / (STOP5_NITS - STOP4_NITS);
      float lerpSegment5 = (nits - STOP5_NITS) / (STOP6_NITS - STOP5_NITS);
      float lerpSegment6 = (nits - STOP6_NITS) / (STOP7_NITS - STOP6_NITS);
      float lerpSegment7 = (nits - STOP7_NITS) / (STOP8_NITS - STOP7_NITS);

      //  Only the "active" gradient segment contributes to the output color.
      hdr_color =
        lerp (STOP0_COLOR, STOP1_COLOR, lerpSegment0) * useSegment0 +
        lerp (STOP1_COLOR, STOP2_COLOR, lerpSegment1) * useSegment1 +
        lerp (STOP2_COLOR, STOP3_COLOR, lerpSegment2) * useSegment2 +
        lerp (STOP3_COLOR, STOP4_COLOR, lerpSegment3) * useSegment3 +
        lerp (STOP4_COLOR, STOP5_COLOR, lerpSegment4) * useSegment4 +
        lerp (STOP5_COLOR, STOP6_COLOR, lerpSegment5) * useSegment5 +
        lerp (STOP6_COLOR, STOP7_COLOR, lerpSegment6) * useSegment6 +
        lerp (STOP7_COLOR, STOP8_COLOR, lerpSegment7) * useSegment7;
      hdr_color.a = 1.0f;
      if (nits > 10000.0)
        hdr_color.rgb = 125.0f;
      if (nits < 0.0)
        hdr_color.rgb = 0.0f;
          return hdr_color;
      } break;

    case SKIV_VISUALIZATION_GAMUT:
    {
      if (post_tonemap)
        return hdr_color;

      #define REC709_HUE  rec709_gamut_hue
      #define DCIP3_HUE   dcip3_gamut_hue
      #define REC2020_HUE rec2020_gamut_hue
      #define AP1_HUE     ap1_gamut_hue
      #define AP0_HUE     ap0_gamut_hue
      #define INVALID_HUE invalid_gamut_hue

      // Display all colors wider than Rec 709 at a minimum of 12 nits
      #define MIN_WIDE_GAMUT_Y 0.15f

      float fLuminance =
        min (125.0f, Rec709toXYZ (hdr_color.rgb).y);

      if (fLuminance < FP16_MIN)
        return (0.0f).xxxx;

      if ((! isnormal (hdr_color.rgb)) || any (Rec709toAP0_D65 (hdr_color.rgb) < 0.0))
      {
        return
          max (MIN_WIDE_GAMUT_Y * 8.0f, fLuminance) * INVALID_HUE;
      }

      if (any (Rec709toAP1_D65 (hdr_color.rgb) < 0.0))
      {
        return
          max (MIN_WIDE_GAMUT_Y * 4.0f, fLuminance) * AP0_HUE;
      }

      if (any (Rec709toRec2020 (hdr_color.rgb) < 0.0))
      {
        return
          max (MIN_WIDE_GAMUT_Y * 3.0f, fLuminance) * AP1_HUE;
      }

      if (any (Rec709toDCIP3 (hdr_color.rgb) < 0.0))
      {
        return
          max (MIN_WIDE_GAMUT_Y * 2.0f, fLuminance) * REC2020_HUE;
      }

      if (any (hdr_color.rgb < 0.0))
      {
        return
          max (MIN_WIDE_GAMUT_Y, fLuminance) * DCIP3_HUE;
      }

      return
        fLuminance * REC709_HUE;
    } break;

    case SKIV_VISUALIZATION_NONE:
    default:
      return hdr_color;
      break;
  }
}