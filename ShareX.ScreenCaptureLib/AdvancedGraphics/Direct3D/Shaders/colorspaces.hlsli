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

float
ApplySRGBCurve (float x)
{
#ifdef FAST_SRGB
  return x < 0.0031308 ? 12.92 * x : 1.13005 * sqrt(x - 0.00228) - 0.13448 * x + 0.005719;
#else
  // Approximately pow(x, 1.0 / 2.2)
  return (x < 0.0031308 ? 12.92 * x :
                          1.055 * pow(x, 1.0 / 2.4) - 0.055);
#endif
}

float3
ApplySRGBCurve (float3 x)
{
#ifdef FAST_SRGB
  return x < 0.0031308 ? 12.92 * x : 1.13005 * sqrt(x - 0.00228) - 0.13448 * x + 0.005719;
#else
  // Approximately pow(x, 1.0 / 2.2)
  return (x < 0.0031308 ? 12.92 * x :
                          1.055 * pow(x, 1.0 / 2.4) - 0.055);
#endif
}

float
RemoveSRGBCurve (float x)
{
#ifdef FAST_SRGB
  return x < 0.04045 ? x / 12.92 : -7.43605 * x - 31.24297 * sqrt(-0.53792 * x + 1.279924) + 35.34864;
#else
  return sign (x) * (abs (x) < 0.04045 ? abs (x) / 12.92 :
                                   pow( (abs (x) + 0.055) / 1.055, 2.4 ));
#endif
}

float3
RemoveSRGBCurve (float3 x)
{
#ifdef FAST_SRGB
  return x < 0.04045 ? x / 12.92 : -7.43605 * x - 31.24297 * sqrt(-0.53792 * x + 1.279924) + 35.34864;
#else
  // Approximately pow(x, 2.2)
  return (x < 0.04045 ? x / 12.92 :
                  pow( (x + 0.055) / 1.055, 2.4 ));
#endif
}

float3
ApplyGammaExp (float3 x, float exp)
{
  return     sign (x) *
         pow (abs (x), 1.0f/exp);
}

float3
RemoveGammaExp (float3 x, float exp)
{
  return     sign (x) *
         pow (abs (x), exp);
}

float3 ApplyRec709Curve (float3 x)
{
  return x < 0.0181 ? 4.5 * x : 1.0993 * pow(x, 0.45) - 0.0993;
}

float Luma (float3 color)
{
  return
    dot (color, float3 (0.299f, 0.587f, 0.114f));
}

float3 ApplyREC2084Curve (float3 L, float maxLuminance)
{
  float m1 = 2610.0 / 4096.0 / 4;
  float m2 = 2523.0 / 4096.0 * 128;
  float c1 = 3424.0 / 4096.0;
  float c2 = 2413.0 / 4096.0 * 32;
  float c3 = 2392.0 / 4096.0 * 32;

  float maxLuminanceScale = maxLuminance / 10000.0f;
  L *= maxLuminanceScale;

  float3 L_sign = sign (L);
  float3 Lp =
    pow (abs (L), m1);

  return
    L_sign * pow ((c1 + c2 * Lp) / (1 + c3 * Lp), m2);
}

float3 RemoveREC2084Curve (float3 N)
{
  float  m1 = 2610.0 / 4096.0 / 4;
  float  m2 = 2523.0 / 4096.0 * 128;
  float  c1 = 3424.0 / 4096.0;
  float  c2 = 2413.0 / 4096.0 * 32;
  float  c3 = 2392.0 / 4096.0 * 32;
  float3 N_sign =      sign(N);
  float3 Np     = pow (abs (N), 1 / m2);

  return
    N_sign * pow (max (Np - c1, 0) / (c2 - c3 * Np), 1 / m1);
}

float3 Rec709toRec2020 (float3 linearRec709)
{
  static const float3x3 ConvMat =
  {
    0.627403914928436279296875f,      0.3292830288410186767578125f,  0.0433130674064159393310546875f,
    0.069097287952899932861328125f,   0.9195404052734375f,           0.011362315155565738677978515625f,
    0.01639143936336040496826171875f, 0.08801330626010894775390625f, 0.895595252513885498046875f
  };

  return mul (ConvMat, linearRec709);
}

float3 Rec709toDCIP3 (float3 linearRec709)
{
  static const float3x3 ConvMat =
  {
    0.82246196269989013671875f,    0.17753803730010986328125f,   0.f,
    0.03319419920444488525390625f, 0.96680581569671630859375f,   0.f,
    0.017082631587982177734375f,   0.0723974406719207763671875f, 0.91051995754241943359375f
  };

  return mul (ConvMat, linearRec709);
}

float3 Rec709toAP1_D65 (float3 linearRec709)
{
  static const float3x3 ConvMat =
  {
    0.61702883243560791015625f,       0.333867609500885009765625f,    0.04910354316234588623046875f,
    0.069922320544719696044921875f,   0.91734969615936279296875f,     0.012727967463433742523193359375f,
    0.02054978720843791961669921875f, 0.107552029192447662353515625f, 0.871898174285888671875f
  };

  return mul (ConvMat, linearRec709);
}

float3 Rec709toAP0_D65 (float3 linearRec709)
{
  static const float3x3 ConvMat =
  {
    0.4339316189289093017578125f,   0.3762523829936981201171875f,   0.1898159682750701904296875f,
    0.088618390262126922607421875f, 0.809275329113006591796875f,    0.10210628807544708251953125f,
    0.01775003969669342041015625f,  0.109447620809078216552734375f, 0.872802317142486572265625f
  };

  return mul (ConvMat, linearRec709);
}

float3 Rec709toXYZ (float3 linearRec709)
{
  static const float3x3 ConvMat =
  {
    0.4123907983303070068359375f,    0.3575843274593353271484375f,   0.18048079311847686767578125f,
    0.2126390039920806884765625f,    0.715168654918670654296875f,    0.072192318737506866455078125f,
    0.0193308182060718536376953125f, 0.119194783270359039306640625f, 0.950532138347625732421875f
  };

  return
    mul (ConvMat, linearRec709);
}

float3 XYZtoRec709 (float3 linearXYZ)
{
  static const float3x3 ConvMat =
  {
     3.240969896316528320312500000f, -1.5373831987380981445312500f, -0.4986107647418975830078125000f,
    -0.969243645668029785156250000f,  1.8759675025939941406250000f,  0.0415550582110881805419921875f,
     0.055630080401897430419921875f, -0.2039769589900970458984375f,  1.0569715499877929687500000000f
  };

  return
    mul (ConvMat, linearXYZ);
}

bool   IsNan (float  x) { return (asuint (x) & 0x7fffffff)  > 0x7f800000; } // Scalar NaN checker
float2 IsNan (float2 v) { return float2 ( IsNan (v.x), IsNan (v.y) );                           }
float3 IsNan (float3 v) { return float3 ( IsNan (v.x), IsNan (v.y), IsNan (v.z) );              }
float4 IsNan (float4 v) { return float4 ( IsNan (v.x), IsNan (v.y), IsNan (v.z), IsNan (v.w) ); }

bool   IsInf (float  x) { return (asuint (x) & 0x7f8fffff) == 0x7f800000; } // Scalar Infinity checker
float2 IsInf (float2 v) { return float2 ( IsInf (v.x), IsInf (v.y) );                           }
float3 IsInf (float3 v) { return float3 ( IsInf (v.x), IsInf (v.y), IsInf (v.z) );              }
float4 IsInf (float4 v) { return float4 ( IsInf (v.x), IsInf (v.y), IsInf (v.z), IsInf (v.w) ); }

// Vectorized versions
bool AnyIsInf (float  x)    { return        IsInf (x);                                 }
bool AnyIsInf (float2 xy)   { return any ((asuint (xy)   & 0x7f8fffff) == 0x7f800000); }
bool AnyIsInf (float3 xyz)  { return any ((asuint (xyz)  & 0x7f8fffff) == 0x7f800000); }
bool AnyIsInf (float4 xyzw) { return any ((asuint (xyzw) & 0x7f8fffff) == 0x7f800000); }

bool AnyIsNan (float  x)    { return        IsNan (x);                                 }
bool AnyIsNan (float2 xy)   { return any ((asuint (xy)   & 0x7fffffff)  > 0x7f800000); }
bool AnyIsNan (float3 xyz)  { return any ((asuint (xyz)  & 0x7fffffff)  > 0x7f800000); }
bool AnyIsNan (float4 xyzw) { return any ((asuint (xyzw) & 0x7fffffff)  > 0x7f800000); }

// Combined NaN and Infinity check
bool isnormal (float  x)    { return (! (     (asuint (x)    & 0x7fffffff) >= 0x7f800000));  }
bool isnormal (float2 xy)   { return (! (any ((asuint (xy)   & 0x7fffffff) >= 0x7f800000))); }
bool isnormal (float3 xyz)  { return (! (any ((asuint (xyz)  & 0x7fffffff) >= 0x7f800000))); }
bool isnormal (float4 xyzw) { return (! (any ((asuint (xyzw) & 0x7fffffff) >= 0x7f800000))); }

// Remove special floating-point bit patterns, clamping is the
//   final step before output and outputting NaN or Infinity would
//     break color blending!
#define SanitizeFP(c) ((! isnormal ((c))) ? (IsInf ((c)) ? sign ((c)) * float_MAX : (! IsNan ((c))) * (c)) : (c))

#define float_MAX 65504.0 // (2 - 2^-10) * 2^15
#define FP16_MIN  0.0005
//#define FP16_MIN asfloat (0x33C00000)

float3 AP0_D65toRec709 (float3 linearAP0)
{
  static const float3x3 ConvMat =
  {
     2.552483081817626953125f,         -1.12950992584228515625f,       -0.422973215579986572265625f,
    -0.2773441374301910400390625f,      1.3782665729522705078125f,     -0.1009224355220794677734375f,
    -0.01713105104863643646240234375f, -0.14986114203929901123046875f,  1.1669921875f
  };

  return mul (ConvMat, linearAP0);
}

float3 Clamp_scRGB (float3 c)
{
  c = SanitizeFP (c);

  c =
    clamp (c, -float_MAX, float_MAX);

  return c;
}

float Clamp_scRGB (float c, bool strip_nan = false)
{
  // No colorspace clamp here, just keep it away from 0.0
  if (strip_nan)
    c = SanitizeFP (c);

  return clamp (c + sign (c) * FP16_MIN, -float_MAX,
                                          float_MAX);
}




float3 Rec709_to_XYZ (float3 linearRec709)
{
  static const float3x3 ConvMat =
  {
    0.4123907983303070068359375000f, 0.357584327459335327148437500f, 0.180480793118476867675781250f,
    0.2126390039920806884765625000f, 0.715168654918670654296875000f, 0.072192318737506866455078125f,
    0.0193308182060718536376953125f, 0.119194783270359039306640625f, 0.950532138347625732421875000f
  };

  return
    mul (ConvMat, linearRec709);
}

float3 XYZ_to_Rec709 (float3 XYZ)
{
  static const float3x3 ConvMat =
  {
     3.240969896316528320312500000f, -1.5373831987380981445312500f, -0.4986107647418975830078125000f,
    -0.969243645668029785156250000f,  1.8759675025939941406250000f,  0.0415550582110881805419921875f,
     0.055630080401897430419921875f, -0.2039769589900970458984375f,  1.0569715499877929687500000000f
  };

  return
    mul (ConvMat, XYZ);
}

float3 XYZ_to_LMS (float3 XYZ)
{
  static const float3x3 ConvMat =
  {
     0.3592, 0.6976, -0.0358,
    -0.1922, 1.1004,  0.0755,
     0.0070, 0.0749,  0.8434
  };

  return
    mul (ConvMat, XYZ);
}

float3 LMS_to_XYZ (float3 LMS)
{
  static const float3x3 ConvMat =
  {
     2.070180056695613509600, -1.326456876103021025500,  0.206616006847855170810,
     0.364988250032657479740,  0.680467362852235141020, -0.045421753075853231409,
    -0.049595542238932107896, -0.049421161186757487412,  1.187995941732803439400
  };

  return
    mul (ConvMat, LMS);
}

//
// SMPTE ST.2084 (PQ) transfer functions
// Used for HDR Lut storage, max range depends on the maxPQValue parameter
//
struct ParamsPQ
{
  float N, rcpN;
  float M, rcpM;
  float C1, C2, C3;
};

static const ParamsPQ PQ =
{
       2610.0 / 4096.0 / 4.0,    // N
  rcp (2610.0 / 4096.0 / 4.0),   // rcp (N)
       2523.0 / 4096.0 * 128.0,  // M
  rcp (2523.0 / 4096.0 * 128.0), // rcp (M)
       3424.0 / 4096.0,          // C1
       2413.0 / 4096.0 * 32.0,   // C2
       2392.0 / 4096.0 * 32.0,   // C3
};

#define PositivePow(x,y) pow (abs (x), y)
#define DEFAULT_MAX_PQ 125.0f

float3 LinearToPQ (float3 x, float maxPQValue)
{
  float3 sign_bits = sign (x);
  
  x =
    pow ( abs (x) / maxPQValue,
                       PQ.N );
 
  float3 nd =
    (PQ.C1 + PQ.C2 * x) /
      (1.0 + PQ.C3 * x);

  return
    sign_bits * pow (nd, PQ.M);
}

float3 PQToLinear (float3 x, float maxPQValue)
{
  float3 sign_bits = sign (x);
  
  x =
    pow (abs (x), PQ.rcpM);

  float3 nd =
    max (x - PQ.C1, 0.0) /
            (PQ.C2 - (PQ.C3 * x));

  return
    sign_bits * pow (nd, PQ.rcpN) * maxPQValue;
}

float3 Rec709toICtCp (float3 c)
{
  c = Rec709_to_XYZ (c);
  c = XYZ_to_LMS    (c);
  
  c =
    LinearToPQ (max (c, 0.0f), 125.0f);

  static const float3x3 ConvMat =
  {
    0.5000,  0.5000,  0.0000,
    1.6137, -3.3234,  1.7097,
    4.3780, -4.2455, -0.1325
  };

  return
    mul (ConvMat, c);
}

float3 ICtCptoRec709 (float3 c)
{
  static const float3x3 ConvMat =
  {
    1.0,  0.00860514569398152,  0.11103560447547328,
    1.0, -0.00860514569398152, -0.11103560447547328,
    1.0,  0.56004885956263900, -0.32063747023212210
  };
  
  c =
    mul (ConvMat, c);
  
  c = PQToLinear (c, 125.0f);
  c = LMS_to_XYZ (c);
  
  return
    XYZ_to_Rec709 (c);
}

float LinearToPQY (float x, float maxPQValue)
{
  float sign_bit =
    sign (x);
  
  x =
    pow ( abs (x) / maxPQValue,
                       PQ.N );
  
  float nd =
    (PQ.C1 + PQ.C2 * x) /
      (1.0 + PQ.C3 * x);

  return
    sign_bit * Clamp_scRGB (pow (nd, PQ.M).xxx).x;
}

float LinearToPQY (float x)
{
  return
    LinearToPQY (max (x, 0.0f), DEFAULT_MAX_PQ);
}

float4 LinearToPQ4 (float4 x, float maxPQValue)
{
  x =
    PositivePow ( x / maxPQValue,
                         PQ.N );
 
  float4 nd =
    (PQ.C1 + PQ.C2 * x) /
      (1.0 + PQ.C3 * x);

  return
    PositivePow (nd, PQ.M);
}

float4 PQToLinear4 (float4 x, float maxPQValue)
{
  x =
    PositivePow (x, PQ.rcpM);

  float4 nd =
    max (x - PQ.C1, 0.0) /
            (PQ.C2 - (PQ.C3 * x));

  return
    PositivePow (nd, PQ.rcpN) * maxPQValue;
}