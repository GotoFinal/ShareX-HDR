using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShareX.ScreenCaptureLib.AdvancedGraphics.Direct3D;

public class ColorspaceUtils
{
    static readonly Matrix4x4 Rec709toICtCpConvMat = new Matrix4x4
    (
        0.5000f, 1.6137f, 4.3780f, 0.0f,
        0.5000f, -3.3234f, -4.2455f, 0.0f,
        0.0000f, 1.7097f, -0.1325f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    );


    private static readonly Matrix4x4 ICtCpToRec709ConvMat = new(
        1.0f, 1.0f, 1.0f, 0.0f,
        0.0086051457f, -0.0086051457f, 0.5600488596f, 0.0f,
        0.1110356045f, -0.1110356045f, -0.3206374702f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f);


    public static readonly Matrix4x4 from709ToXYZ = new
    (
        0.4123907983303070068359375f, 0.2126390039920806884765625f, 0.0193308182060718536376953125f, 0.0f,
        0.3575843274593353271484375f, 0.715168654918670654296875f, 0.119194783270359039306640625f, 0.0f,
        0.18048079311847686767578125f, 0.072192318737506866455078125f, 0.950532138347625732421875f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    );

    static readonly Matrix4x4 fromXYZtoLMS = new
    (
        0.3592f, -0.1922f, 0.0070f, 0.0f,
        0.6976f, 1.1004f, 0.0749f, 0.0f,
        -0.0358f, 0.0755f, 0.8434f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    );

    static readonly Matrix4x4 fromLMStoXYZ = new
    (
        2.070180056695613509600f, 0.364988250032657479740f, -0.049595542238932107896f, 0.0f,
        -1.326456876103021025500f, 0.680467362852235141020f, -0.049421161186757487412f, 0.0f,
        0.206616006847855170810f, -0.045421753075853231409f, 1.187995941732803439400f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    );

    static readonly Matrix4x4 fromXYZto709 = new
    (3.2409698963165283203125f, -0.96924364566802978515625f, 0.055630080401897430419921875f, 0.0f,
        -1.53738319873809814453125f, 1.875967502593994140625f, -0.2039769589900970458984375f, 0.0f,
        -0.4986107647418975830078125f, 0.0415550582110881805419921875f, 1.05697154998779296875f, 0.0f,
        0.0f, 0.0f, 0.0f, 1.0f
    );

    static readonly Vector4 PQ_N = new(2610.0f / 4096.0f / 4.0f);
    static readonly Vector4 PQ_M = new(2523.0f / 4096.0f * 128.0f);
    static readonly Vector4 PQ_C1 = new(3424.0f / 4096.0f);
    static readonly Vector4 PQ_C2 = new(2413.0f / 4096.0f * 32.0f);
    static readonly Vector4 PQ_C3 = new(2392.0f / 4096.0f * 32.0f);
    static readonly Vector4 PQ_MaxPQ = new(125.0f);
    static readonly Vector4 RcpM = new(2610.0f / 4096.0f / 4.0f);
    static readonly Vector4 RcpN = new(2523.0f / 4096.0f * 128.0f);


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Rec709toICtCp(Vector4 N)
    {
        Vector4 ret = N;

        ret = Vector4.Transform(ret.AsVector3(), from709ToXYZ);
        ret = Vector4.Transform(ret.AsVector3(), fromXYZtoLMS);

        ret = LinearToPQ(Vector4.Max(ret, Vector4.Zero), PQ_MaxPQ);


        return Vector4.Transform(ret, Rec709toICtCpConvMat);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 PQToLinear(Vector4 pq, Vector4 maxPQ)
    {
        // ret = (max(pq, 0))^(1/m)

        var ret = VectorPow(Vector4.Max(pq, Vector4.Zero), RcpM);

        // nd  = max(ret - C1, 0) / (C2 - C3·ret)
        var numerator = Vector4.Max(ret - PQ_C1, Vector4.Zero);
        var denominator = PQ_C2 - PQ_C3 * ret;
        var nd = numerator / denominator;

        // ret = nd^(1/n) · maxPQ
        ret = VectorPow(nd, RcpN) * maxPQ;
        return ret;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 ICtCpToRec709(Vector4 ictcp)
    {
        var v = Vector4.Transform(ictcp.AsVector3(), ICtCpToRec709ConvMat);

        v = PQToLinear(v, PQ_MaxPQ);

        v = Vector4.Transform(v.AsVector3(), fromLMStoXYZ);
        return Vector4.Transform(v.AsVector3(), fromXYZto709);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector4 LinearToPQ(Vector4 N, Vector4 maxPQValue)
    {
        Vector4 ret = VectorPow(Vector4.Max(N, Vector4.Zero) / maxPQValue, PQ_N);
        Vector4 nd = (PQ_C1 + (PQ_C2 * ret)) / (Vector4.One + (PQ_C3 * ret));

        return VectorPow(nd, PQ_M);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LinearToPQY(float N) // 1.5
    {
        float fScaledN = Math.Abs(N * 0.008f); // 0.008 = 1/125.0

        float ret = MathF.Pow(fScaledN, 0.1593017578125f);

        float nd = Math.Abs((0.8359375f + (18.8515625f * ret)) /
                            (1.0f + (18.6875f * ret)));

        return MathF.Pow(nd, 78.84375f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float HdrTonemap(float maxYInPQ, float Y_out, float Y_in)
    {
        float a = (Y_out / MathF.Pow(maxYInPQ, 2.0f));
        float b = (1.0f / Y_out);
        Y_out = (Y_in * (1 + a * Y_in)) / (1 + b * Y_in);
        return Y_out;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 VectorPow(Vector4 v, Vector4 p)
    {
        return new Vector4(
            (float)MathF.Pow(v.X, p.X),
            (float)MathF.Pow(v.Y, p.Y),
            (float)MathF.Pow(v.Z, p.Z),
            (float)MathF.Pow(v.W, p.W)
        );
    }
}