// Ported from V8 13.6.233.17 (the V8 in Node 24.12.0), src/base/ieee754.cc, which is adapted from fdlibm:
//
// ====================================================
// Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//
// Developed at SunSoft, a Sun Microsystems, Inc. business.
// Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice
// is preserved.
// ====================================================
//
// The original source code covered by the above license above has been
// modified significantly by Google Inc.
// Copyright 2016 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file (https://github.com/v8/v8/blob/13.6.233.17/LICENSE).
//
// Node's build does not define V8_USE_LIBM_TRIG_FUNCTIONS (`node --v8-options` has no --use-libm-trig-functions), so
// Math.sin/cos are fdlibm's, as are acos, atan2 and log10. Math.pow / `**` go to v8::internal::math::pow
// (src/numbers/ieee754.cc), which with the default --use-std-math-pow calls the C library's std::pow after a few
// JS special cases; see Pow below. The C is ported line for line: hex constants, 32-bit word access, int32 wraparound.
namespace Archify.Net;

internal static class JsMath
{
    // ---- word access (EXTRACT_WORDS, GET_HIGH_WORD, GET_LOW_WORD, INSERT_WORDS, SET_HIGH_WORD, SET_LOW_WORD) ----
    static double W(uint hi, uint lo) => BitConverter.UInt64BitsToDouble(((ulong)hi << 32) | lo);
    static int Hi(double d) => (int)(BitConverter.DoubleToUInt64Bits(d) >> 32);
    static uint Lo(double d) => (uint)BitConverter.DoubleToUInt64Bits(d);
    static double SetHi(double d, int v) => BitConverter.UInt64BitsToDouble((BitConverter.DoubleToUInt64Bits(d) & 0x0000_0000_FFFF_FFFFUL) | ((ulong)(uint)v << 32));
    static double SetLo(double d, uint v) => BitConverter.UInt64BitsToDouble((BitConverter.DoubleToUInt64Bits(d) & 0xFFFF_FFFF_0000_0000UL) | v);

    // ---- __ieee754_rem_pio2 ----
    static readonly int[] TwoOverPi =
    [
        0xA2F983, 0x6E4E44, 0x1529FC, 0x2757D1, 0xF534DD, 0xC0DB62, 0x95993C,
        0x439041, 0xFE5163, 0xABDEBB, 0xC561B7, 0x246E3A, 0x424DD2, 0xE00649,
        0x2EEA09, 0xD1921C, 0xFE1DEB, 0x1CB129, 0xA73EE8, 0x8235F5, 0x2EBB44,
        0x84E99C, 0x7026B4, 0x5F7E41, 0x3991D6, 0x398353, 0x39F49C, 0x845F8B,
        0xBDF928, 0x3B1FF8, 0x97FFDE, 0x05980F, 0xEF2F11, 0x8B5A0A, 0x6D1F6D,
        0x367ECF, 0x27CB09, 0xB74F46, 0x3F669E, 0x5FEA2D, 0x7527BA, 0xC7EBE5,
        0xF17B3D, 0x0739F7, 0x8A5292, 0xEA6BFB, 0x5FB11F, 0x8D5D08, 0x560330,
        0x46FC7B, 0x6BABF0, 0xCFBC20, 0x9AF436, 0x1DA9E3, 0x91615E, 0xE61B08,
        0x659985, 0x5F14A0, 0x68408D, 0xFFD880, 0x4D7327, 0x310606, 0x1556CA,
        0x73A8C9, 0x60E27B, 0xC08C6B,
    ];
    static readonly int[] Npio2Hw =
    [
        0x3FF921FB, 0x400921FB, 0x4012D97C, 0x401921FB, 0x401F6A7A, 0x4022D97C,
        0x4025FDBB, 0x402921FB, 0x402C463A, 0x402F6A7A, 0x4031475C, 0x4032D97C,
        0x40346B9C, 0x4035FDBB, 0x40378FDB, 0x403921FB, 0x403AB41B, 0x403C463A,
        0x403DD85A, 0x403F6A7A, 0x40407E4C, 0x4041475C, 0x4042106C, 0x4042D97C,
        0x4043A28C, 0x40446B9C, 0x404534AC, 0x4045FDBB, 0x4046C6CB, 0x40478FDB,
        0x404858EB, 0x404921FB,
    ];
    static readonly double
        Invpio2 = W(0x3FE45F30, 0x6DC9C883),
        Pio2_1 = W(0x3FF921FB, 0x54400000),
        Pio2_1t = W(0x3DD0B461, 0x1A626331),
        Pio2_2 = W(0x3DD0B461, 0x1A600000),
        Pio2_2t = W(0x3BA3198A, 0x2E037073),
        Pio2_3 = W(0x3BA3198A, 0x2E000000),
        Pio2_3t = W(0x397B839A, 0x252049C1);
    const double Zero = 0.0, Half = 0.5, One = 1.0, Two24 = 16777216.0, Twon24 = 5.96046447753906250000e-08; // 0x3E700000 00000000

    static int RemPio2(double x, Span<double> y)
    {
        double z, w, t, r, fn;
        Span<double> tx = stackalloc double[3];
        int e0, i, j, nx, n, ix, hx;
        uint low;

        z = 0;
        hx = Hi(x);
        ix = hx & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) { y[0] = x; y[1] = 0; return 0; } /* |x| ~<= pi/4 */
        if (ix < 0x4002D97C)
        { /* |x| < 3pi/4, special case with n=+-1 */
            if (hx > 0)
            {
                z = x - Pio2_1;
                if (ix != 0x3FF921FB) { y[0] = z - Pio2_1t; y[1] = (z - y[0]) - Pio2_1t; }
                else { z -= Pio2_2; y[0] = z - Pio2_2t; y[1] = (z - y[0]) - Pio2_2t; }
                return 1;
            }
            else
            {
                z = x + Pio2_1;
                if (ix != 0x3FF921FB) { y[0] = z + Pio2_1t; y[1] = (z - y[0]) + Pio2_1t; }
                else { z += Pio2_2; y[0] = z + Pio2_2t; y[1] = (z - y[0]) + Pio2_2t; }
                return -1;
            }
        }
        if (ix <= 0x413921FB)
        { /* |x| ~<= 2^19*(pi/2), medium size */
            t = Math.Abs(x);
            n = (int)(t * Invpio2 + Half);
            fn = n;
            r = t - fn * Pio2_1;
            w = fn * Pio2_1t; /* 1st round good to 85 bit */
            if (n < 32 && ix != Npio2Hw[n - 1])
            {
                y[0] = r - w; /* quick check no cancellation */
            }
            else
            {
                uint high;
                j = ix >> 20;
                y[0] = r - w;
                high = (uint)Hi(y[0]);
                i = j - (int)((high >> 20) & 0x7FF);
                if (i > 16)
                { /* 2nd iteration needed, good to 118 */
                    t = r;
                    w = fn * Pio2_2;
                    r = t - w;
                    w = fn * Pio2_2t - ((t - r) - w);
                    y[0] = r - w;
                    high = (uint)Hi(y[0]);
                    i = j - (int)((high >> 20) & 0x7FF);
                    if (i > 49)
                    { /* 3rd iteration need, 151 bits acc */
                        t = r;
                        w = fn * Pio2_3;
                        r = t - w;
                        w = fn * Pio2_3t - ((t - r) - w);
                        y[0] = r - w;
                    }
                }
            }
            y[1] = (r - y[0]) - w;
            if (hx < 0) { y[0] = -y[0]; y[1] = -y[1]; return -n; }
            return n;
        }
        /* all other (large) arguments */
        if (ix >= 0x7FF00000) { y[0] = y[1] = x - x; return 0; } /* x is inf or NaN */
        /* set z = scalbn(|x|,ilogb(x)-23) */
        low = Lo(x);
        z = SetLo(z, low);
        e0 = (ix >> 20) - 1046; /* e0 = ilogb(z)-23; */
        z = SetHi(z, ix - (int)((uint)e0 << 20));
        for (i = 0; i < 2; i++)
        {
            tx[i] = (int)z;
            z = (z - tx[i]) * Two24;
        }
        tx[2] = z;
        nx = 3;
        while (tx[nx - 1] == Zero) nx--; /* skip zero term */
        n = KernelRemPio2(tx, y, e0, nx, 2, TwoOverPi);
        if (hx < 0) { y[0] = -y[0]; y[1] = -y[1]; return -n; }
        return n;
    }

    // ---- __kernel_cos ----
    static readonly double
        C1 = W(0x3FA55555, 0x5555554C),
        C2 = W(0xBF56C16C, 0x16C15177),
        C3 = W(0x3EFA01A0, 0x19CB1590),
        C4 = W(0xBE927E4F, 0x809C52AD),
        C5 = W(0x3E21EE9E, 0xBDB4B1C4),
        C6 = W(0xBDA8FAE9, 0xBE8838D4);

    static double KernelCos(double x, double y)
    {
        double a, iz, z, r, qx;
        int ix = Hi(x);
        ix &= 0x7FFFFFFF; /* ix = |x|'s high word*/
        if (ix < 0x3E400000)
        { /* if x < 2**27 */
            if ((int)x == 0) return One; /* generate inexact */
        }
        z = x * x;
        r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
        if (ix < 0x3FD33333) return One - (0.5 * z - (z * r - x * y)); /* if |x| < 0.3 */
        if (ix > 0x3FE90000) qx = 0.28125; /* x > 0.78125 */
        else qx = W((uint)(ix - 0x00200000), 0); /* x/4 */
        iz = 0.5 * z - qx;
        a = One - qx;
        return a - (iz - (z * r - x * y));
    }

    // ---- __kernel_rem_pio2 ----
    static readonly int[] InitJk = [2, 3, 4, 6];
    static readonly double[] PIo2 =
    [
        W(0x3FF921FB, 0x40000000), W(0x3E74442D, 0x00000000), W(0x3CF84698, 0x80000000), W(0x3B78CC51, 0x60000000),
        W(0x39F01B83, 0x80000000), W(0x387A2520, 0x40000000), W(0x36E38222, 0x80000000), W(0x3569F31D, 0x00000000),
    ];

    static int KernelRemPio2(Span<double> x, Span<double> y, int e0, int nx, int prec, int[] ipio2)
    {
        int jz, jx, jv, jp, jk, carry, n, i, j, k, m, q0, ih;
        Span<int> iq = stackalloc int[20];
        double z, fw;
        Span<double> f = stackalloc double[20], fq = stackalloc double[20], q = stackalloc double[20];

        /* initialize jk*/
        jk = InitJk[prec];
        jp = jk;

        /* determine jx,jv,q0, note that 3>q0 */
        jx = nx - 1;
        jv = (e0 - 3) / 24;
        if (jv < 0) jv = 0;
        q0 = e0 - 24 * (jv + 1);

        /* set up f[0] to f[jx+jk] where f[jx+jk] = ipio2[jv+jk] */
        j = jv - jx;
        m = jx + jk;
        for (i = 0; i <= m; i++, j++) f[i] = (j < 0) ? Zero : ipio2[j];

        /* compute q[0],q[1],...q[jk] */
        for (i = 0; i <= jk; i++)
        {
            for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
            q[i] = fw;
        }

        jz = jk;
    recompute:
        /* distill q[] into iq[] reversingly */
        for (i = 0, j = jz, z = q[jz]; j > 0; i++, j--)
        {
            fw = (int)(Twon24 * z);
            iq[i] = (int)(z - Two24 * fw);
            z = q[j - 1] + fw;
        }

        /* compute n */
        z = Math.ScaleB(z, q0);             /* actual value of z */
        z -= 8.0 * Math.Floor(z * 0.125);   /* trim off integer >= 8 */
        n = (int)z;
        z -= n;
        ih = 0;
        if (q0 > 0)
        { /* need iq[jz-1] to determine n */
            i = iq[jz - 1] >> (24 - q0);
            n += i;
            iq[jz - 1] -= i << (24 - q0);
            ih = iq[jz - 1] >> (23 - q0);
        }
        else if (q0 == 0)
        {
            ih = iq[jz - 1] >> 23;
        }
        else if (z >= 0.5)
        {
            ih = 2;
        }

        if (ih > 0)
        { /* q > 0.5 */
            n += 1;
            carry = 0;
            for (i = 0; i < jz; i++)
            { /* compute 1-q */
                j = iq[i];
                if (carry == 0)
                {
                    if (j != 0) { carry = 1; iq[i] = 0x1000000 - j; }
                }
                else
                {
                    iq[i] = 0xFFFFFF - j;
                }
            }
            if (q0 > 0)
            { /* rare case: chance is 1 in 12 */
                switch (q0)
                {
                    case 1: iq[jz - 1] &= 0x7FFFFF; break;
                    case 2: iq[jz - 1] &= 0x3FFFFF; break;
                }
            }
            if (ih == 2)
            {
                z = One - z;
                if (carry != 0) z -= Math.ScaleB(One, q0);
            }
        }

        /* check if recomputation is needed */
        if (z == Zero)
        {
            j = 0;
            for (i = jz - 1; i >= jk; i--) j |= iq[i];
            if (j == 0)
            { /* need recomputation */
                for (k = 1; jk >= k && iq[jk - k] == 0; k++) { /* k = no. of terms needed */ }
                for (i = jz + 1; i <= jz + k; i++)
                { /* add q[jz+1] to q[jz+k] */
                    f[jx + i] = ipio2[jv + i];
                    for (j = 0, fw = 0.0; j <= jx; j++) fw += x[j] * f[jx + i - j];
                    q[i] = fw;
                }
                jz += k;
                goto recompute;
            }
        }

        /* chop off zero terms */
        if (z == 0.0)
        {
            jz -= 1;
            q0 -= 24;
            while (iq[jz] == 0) { jz--; q0 -= 24; }
        }
        else
        { /* break z into 24-bit if necessary */
            z = Math.ScaleB(z, -q0);
            if (z >= Two24)
            {
                fw = (int)(Twon24 * z);
                iq[jz] = (int)(z - Two24 * fw);
                jz += 1;
                q0 += 24;
                iq[jz] = (int)fw;
            }
            else
            {
                iq[jz] = (int)z;
            }
        }

        /* convert integer "bit" chunk to floating-point value */
        fw = Math.ScaleB(One, q0);
        for (i = jz; i >= 0; i--) { q[i] = fw * iq[i]; fw *= Twon24; }

        /* compute PIo2[0,...,jp]*q[jz,...,0] */
        for (i = jz; i >= 0; i--)
        {
            for (fw = 0.0, k = 0; k <= jp && k <= jz - i; k++) fw += PIo2[k] * q[i + k];
            fq[jz - i] = fw;
        }

        /* compress fq[] into y[]; rem_pio2 always passes prec 2 (cases 1 and 2 of the C switch) */
        fw = 0.0;
        for (i = jz; i >= 0; i--) fw += fq[i];
        y[0] = (ih == 0) ? fw : -fw;
        fw = fq[0] - fw;
        for (i = 1; i <= jz; i++) fw += fq[i];
        y[1] = (ih == 0) ? fw : -fw;
        return n & 7;
    }

    // ---- __kernel_sin ----
    static readonly double
        S1 = W(0xBFC55555, 0x55555549),
        S2 = W(0x3F811111, 0x1110F8A6),
        S3 = W(0xBF2A01A0, 0x19C161D5),
        S4 = W(0x3EC71DE3, 0x57B1FE7D),
        S5 = W(0xBE5AE5E6, 0x8A2B9CEB),
        S6 = W(0x3DE5D93A, 0x5ACFD57C);

    static double KernelSin(double x, double y, int iy)
    {
        double z, r, v;
        int ix = Hi(x);
        ix &= 0x7FFFFFFF; /* high word of x */
        if (ix < 0x3E400000)
        { /* |x| < 2**-27 */
            if ((int)x == 0) return x; /* generate inexact */
        }
        z = x * x;
        v = z * x;
        r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
        if (iy == 0) return x + v * (S1 + z * r);
        return x - ((z * (Half * y - v * r) - y) - v * S1);
    }

    // ---- acos ----
    static readonly double
        Pi = W(0x400921FB, 0x54442D18),
        Pio2Hi = W(0x3FF921FB, 0x54442D18),
        Pio2Lo = W(0x3C91A626, 0x33145C07),
        PS0 = W(0x3FC55555, 0x55555555),
        PS1 = W(0xBFD4D612, 0x03EB6F7D),
        PS2 = W(0x3FC9C155, 0x0E884455),
        PS3 = W(0xBFA48228, 0xB5688F3B),
        PS4 = W(0x3F49EFE0, 0x7501B288),
        PS5 = W(0x3F023DE1, 0x0DFDF709),
        QS1 = W(0xC0033A27, 0x1C8A2D4B),
        QS2 = W(0x40002AE5, 0x9C598AC8),
        QS3 = W(0xBFE6066C, 0x1B8D0159),
        QS4 = W(0x3FB3B8C5, 0xB12E9282);

    public static double Acos(double x)
    {
        double z, p, q, r, w, s, c, df;
        int hx = Hi(x), ix = hx & 0x7FFFFFFF;
        if (ix >= 0x3FF00000)
        { /* |x| >= 1 */
            uint lx = Lo(x);
            if (((uint)(ix - 0x3FF00000) | lx) == 0)
            { /* |x|==1 */
                if (hx > 0) return 0.0;      /* acos(1) = 0  */
                return Pi + 2.0 * Pio2Lo;    /* acos(-1)= pi */
            }
            return double.NaN; // acos(|x|>1) is NaN
        }
        if (ix < 0x3FE00000)
        { /* |x| < 0.5 */
            if (ix <= 0x3C600000) return Pio2Hi + Pio2Lo; /*if|x|<2**-57*/
            z = x * x;
            p = z * (PS0 + z * (PS1 + z * (PS2 + z * (PS3 + z * (PS4 + z * PS5)))));
            q = One + z * (QS1 + z * (QS2 + z * (QS3 + z * QS4)));
            r = p / q;
            return Pio2Hi - (x - (Pio2Lo - x * r));
        }
        else if (hx < 0)
        { /* x < -0.5 */
            z = (One + x) * 0.5;
            p = z * (PS0 + z * (PS1 + z * (PS2 + z * (PS3 + z * (PS4 + z * PS5)))));
            q = One + z * (QS1 + z * (QS2 + z * (QS3 + z * QS4)));
            s = Math.Sqrt(z);
            r = p / q;
            w = r * s - Pio2Lo;
            return Pi - 2.0 * (s + w);
        }
        else
        { /* x > 0.5 */
            z = (One - x) * 0.5;
            s = Math.Sqrt(z);
            df = s;
            df = SetLo(df, 0);
            c = (z - df * df) / (s + df);
            p = z * (PS0 + z * (PS1 + z * (PS2 + z * (PS3 + z * (PS4 + z * PS5)))));
            q = One + z * (QS1 + z * (QS2 + z * (QS3 + z * QS4)));
            r = p / q;
            w = r * s + c;
            return 2.0 * (df + w);
        }
    }

    // ---- atan (used by atan2) ----
    static readonly double[] AtanHi = [W(0x3FDDAC67, 0x0561BB4F), W(0x3FE921FB, 0x54442D18), W(0x3FEF730B, 0xD281F69B), W(0x3FF921FB, 0x54442D18)];
    static readonly double[] AtanLo = [W(0x3C7A2B7F, 0x222F65E2), W(0x3C81A626, 0x33145C07), W(0x3C700788, 0x7AF0CBBD), W(0x3C91A626, 0x33145C07)];
    static readonly double[] AT =
    [
        W(0x3FD55555, 0x5555550D), W(0xBFC99999, 0x9998EBC4), W(0x3FC24924, 0x920083FF), W(0xBFBC71C6, 0xFE231671),
        W(0x3FB745CD, 0xC54C206E), W(0xBFB3B0F2, 0xAF749A6D), W(0x3FB10D66, 0xA0D03D51), W(0xBFADDE2D, 0x52DEFD9A),
        W(0x3FA97B4B, 0x24760DEB), W(0xBFA2B444, 0x2C6A6C2F), W(0x3F90AD3A, 0xE322DA11),
    ];
    static readonly double Huge = W(0x7E37E43C, 0x8800759C); // 1.0e300

    static double Atan(double x)
    {
        double w, s1, s2, z;
        int ix, hx, id;

        hx = Hi(x);
        ix = hx & 0x7FFFFFFF;
        if (ix >= 0x44100000)
        { /* if |x| >= 2^66 */
            uint low = Lo(x);
            if (ix > 0x7FF00000 || (ix == 0x7FF00000 && (low != 0))) return x + x; /* NaN */
            if (hx > 0) return AtanHi[3] + AtanLo[3];
            return -AtanHi[3] - AtanLo[3];
        }
        if (ix < 0x3FDC0000)
        { /* |x| < 0.4375 */
            if (ix < 0x3E400000)
            { /* |x| < 2^-27 */
                if (Huge + x > One) return x; /* raise inexact */
            }
            id = -1;
        }
        else
        {
            x = Math.Abs(x);
            if (ix < 0x3FF30000)
            { /* |x| < 1.1875 */
                if (ix < 0x3FE60000) { id = 0; x = (2.0 * x - One) / (2.0 + x); } /* 7/16 <=|x|<11/16 */
                else { id = 1; x = (x - One) / (x + One); }                        /* 11/16<=|x|< 19/16 */
            }
            else
            {
                if (ix < 0x40038000) { id = 2; x = (x - 1.5) / (One + 1.5 * x); } /* |x| < 2.4375 */
                else { id = 3; x = -1.0 / x; }                                   /* 2.4375 <= |x| < 2^66 */
            }
        }
        /* end of argument reduction */
        z = x * x;
        w = z * z;
        /* break sum from i=0 to 10 aT[i]z**(i+1) into odd and even poly */
        s1 = z * (AT[0] + w * (AT[2] + w * (AT[4] + w * (AT[6] + w * (AT[8] + w * AT[10])))));
        s2 = w * (AT[1] + w * (AT[3] + w * (AT[5] + w * (AT[7] + w * AT[9]))));
        if (id < 0) return x - x * (s1 + s2);
        z = AtanHi[id] - ((x * (s1 + s2) - AtanLo[id]) - x);
        return (hx < 0) ? -z : z;
    }

    // ---- atan2 ----
    static readonly double
        Tiny = W(0x01A56E1F, 0xC2F8F359), // 1.0e-300
        PiO4 = W(0x3FE921FB, 0x54442D18),
        PiO2 = W(0x3FF921FB, 0x54442D18),
        PiLo = W(0x3CA1A626, 0x33145C07);

    public static double Atan2(double y, double x)
    {
        double z;
        int k, m, hx, hy, ix, iy;
        uint lx, ly;

        hx = Hi(x); lx = Lo(x);
        ix = hx & 0x7FFFFFFF;
        hy = Hi(y); ly = Lo(y);
        iy = hy & 0x7FFFFFFF;
        if ((((uint)ix | ((lx | (uint)(-(int)lx)) >> 31)) > 0x7FF00000u) ||
            (((uint)iy | ((ly | (uint)(-(int)ly)) >> 31)) > 0x7FF00000u))
            return x + y; /* x or y is NaN */
        if (((uint)(hx - 0x3FF00000) | lx) == 0) return Atan(y); /* x=1.0 */
        m = ((hy >> 31) & 1) | ((hx >> 30) & 2); /* 2*sign(x)+sign(y) */

        /* when y = 0 */
        if (((uint)iy | ly) == 0)
        {
            switch (m)
            {
                case 0:
                case 1: return y;           /* atan(+-0,+anything)=+-0 */
                case 2: return Pi + Tiny;   /* atan(+0,-anything) = pi */
                case 3: return -Pi - Tiny;  /* atan(-0,-anything) =-pi */
            }
        }
        /* when x = 0 */
        if (((uint)ix | lx) == 0) return (hy < 0) ? -PiO2 - Tiny : PiO2 + Tiny;

        /* when x is INF */
        if (ix == 0x7FF00000)
        {
            if (iy == 0x7FF00000)
            {
                switch (m)
                {
                    case 0: return PiO4 + Tiny;         /* atan(+INF,+INF) */
                    case 1: return -PiO4 - Tiny;        /* atan(-INF,+INF) */
                    case 2: return 3.0 * PiO4 + Tiny;   /*atan(+INF,-INF)*/
                    case 3: return -3.0 * PiO4 - Tiny;  /*atan(-INF,-INF)*/
                }
            }
            else
            {
                switch (m)
                {
                    case 0: return Zero;        /* atan(+...,+INF) */
                    case 1: return -Zero;       /* atan(-...,+INF) */
                    case 2: return Pi + Tiny;   /* atan(+...,-INF) */
                    case 3: return -Pi - Tiny;  /* atan(-...,-INF) */
                }
            }
        }
        /* when y is INF */
        if (iy == 0x7FF00000) return (hy < 0) ? -PiO2 - Tiny : PiO2 + Tiny;

        /* compute y/x */
        k = (iy - ix) >> 20;
        if (k > 60)
        { /* |y/x| >  2**60 */
            z = PiO2 + 0.5 * PiLo;
            m &= 1;
        }
        else if (hx < 0 && k < -60)
        {
            z = 0.0; /* 0 > |y|/x > -2**-60 */
        }
        else
        {
            z = Atan(Math.Abs(y / x)); /* safe to do y/x */
        }
        return m switch
        {
            0 => z,                     /* atan(+,+) */
            1 => -z,                    /* atan(-,+) */
            2 => Pi - (z - PiLo),       /* atan(+,-) */
            _ => (z - PiLo) - Pi,       /* atan(-,-) */
        };
    }

    // ---- cos, sin ----
    public static double Cos(double x)
    {
        Span<double> y = stackalloc double[2];
        double z = 0.0;
        int ix = Hi(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return KernelCos(x, z); /* |x| ~< pi/4 */
        if (ix >= 0x7FF00000) return x - x;          /* cos(Inf or NaN) is NaN */
        int n = RemPio2(x, y);                       /* argument reduction needed */
        return (n & 3) switch
        {
            0 => KernelCos(y[0], y[1]),
            1 => -KernelSin(y[0], y[1], 1),
            2 => -KernelCos(y[0], y[1]),
            _ => KernelSin(y[0], y[1], 1),
        };
    }

    public static double Sin(double x)
    {
        Span<double> y = stackalloc double[2];
        double z = 0.0;
        int ix = Hi(x) & 0x7FFFFFFF;
        if (ix <= 0x3FE921FB) return KernelSin(x, z, 0); /* |x| ~< pi/4 */
        if (ix >= 0x7FF00000) return x - x;             /* sin(Inf or NaN) is NaN */
        int n = RemPio2(x, y);                          /* argument reduction needed */
        return (n & 3) switch
        {
            0 => KernelSin(y[0], y[1], 1),
            1 => KernelCos(y[0], y[1]),
            2 => -KernelSin(y[0], y[1], 1),
            _ => -KernelCos(y[0], y[1]),
        };
    }

    // ---- log (used by log10) ----
    static readonly double
        Ln2Hi = W(0x3FE62E42, 0xFEE00000),
        Ln2Lo = W(0x3DEA39EF, 0x35793C76),
        Two54 = W(0x43500000, 0x00000000),
        Lg1 = W(0x3FE55555, 0x55555593),
        Lg2 = W(0x3FD99999, 0x9997FA04),
        Lg3 = W(0x3FD24924, 0x94229359),
        Lg4 = W(0x3FCC71C5, 0x1D8E78AF),
        Lg5 = W(0x3FC74664, 0x96CB03DE),
        Lg6 = W(0x3FC39A09, 0xD078C69F),
        Lg7 = W(0x3FC2F112, 0xDF3E5244),
        Third = W(0x3FD55555, 0x55555555); // the C literal 0.33333333333333333

    static double Log(double x)
    {
        double hfsq, f, s, z, R, w, t1, t2, dk;
        int k, hx, i, j;
        uint lx;

        hx = Hi(x); lx = Lo(x);
        k = 0;
        if (hx < 0x00100000)
        { /* x < 2**-1022  */
            if (((uint)(hx & 0x7FFFFFFF) | lx) == 0) return double.NegativeInfinity; /* log(+-0)=-inf */
            if (hx < 0) return double.NaN; /* log(-#) = NaN */
            k -= 54;
            x *= Two54; /* subnormal number, scale up x */
            hx = Hi(x);
        }
        if (hx >= 0x7FF00000) return x + x;
        k += (hx >> 20) - 1023;
        hx &= 0x000FFFFF;
        i = (hx + 0x95F64) & 0x100000;
        x = SetHi(x, hx | (i ^ 0x3FF00000)); /* normalize x or x/2 */
        k += (i >> 20);
        f = x - 1.0;
        if ((0x000FFFFF & (2 + hx)) < 3)
        { /* -2**-20 <= f < 2**-20 */
            if (f == Zero)
            {
                if (k == 0) return Zero;
                dk = k;
                return dk * Ln2Hi + dk * Ln2Lo;
            }
            R = f * f * (0.5 - Third * f);
            if (k == 0) return f - R;
            dk = k;
            return dk * Ln2Hi - ((R - dk * Ln2Lo) - f);
        }
        s = f / (2.0 + f);
        dk = k;
        z = s * s;
        i = hx - 0x6147A;
        w = z * z;
        j = 0x6B851 - hx;
        t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
        t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
        i |= j;
        R = t2 + t1;
        if (i > 0)
        {
            hfsq = 0.5 * f * f;
            if (k == 0) return f - (hfsq - s * (hfsq + R));
            return dk * Ln2Hi - ((hfsq - (s * (hfsq + R) + dk * Ln2Lo)) - f);
        }
        if (k == 0) return f - s * (f - R);
        return dk * Ln2Hi - ((s * (f - R) - dk * Ln2Lo) - f);
    }

    // ---- log10 ----
    static readonly double
        Ivln10 = W(0x3FDBCB7B, 0x1526E50E),
        Log10_2hi = W(0x3FD34413, 0x509F6000),
        Log10_2lo = W(0x3D59FEF3, 0x11F12B36);

    public static double Log10(double x)
    {
        double y;
        int i, k, hx;
        uint lx;

        hx = Hi(x); lx = Lo(x);
        k = 0;
        if (hx < 0x00100000)
        { /* x < 2**-1022  */
            if (((uint)(hx & 0x7FFFFFFF) | lx) == 0) return double.NegativeInfinity; /* log(+-0)=-inf */
            if (hx < 0) return double.NaN; /* log(-#) = NaN */
            k -= 54;
            x *= Two54; /* subnormal number, scale up x */
            hx = Hi(x);
            lx = Lo(x);
        }
        if (hx >= 0x7FF00000) return x + x;
        if (hx == 0x3FF00000 && lx == 0) return 0.0; /* log(1) = +0 */
        k += (hx >> 20) - 1023;

        i = (int)(((uint)k & 0x80000000u) >> 31);
        hx = (hx & 0x000FFFFF) | ((0x3FF - i) << 20);
        y = k + i;
        x = SetHi(x, hx);
        x = SetLo(x, lx);

        double z = y * Log10_2lo + Ivln10 * Log(x);
        return z + y * Log10_2hi;
    }

    // ---- pow: v8::internal::math::pow (src/numbers/ieee754.cc) with --use-std-math-pow (the default) ----
    // V8's JS special cases, then std::pow: the C library's, so Node itself differs between Windows (static UCRT) and
    // Linux (glibc), e.g. 10 ** 210. The port follows Windows Node, where the goldens are minted: 10 ** p for integer p
    // comes from Pow10, captured there; Math.Pow (the OS ucrtbase pow) matches it on every other power Pele reaches
    // (0.99 ** k, 0.5 ** k, 2 ** k; JsMathTests). Random non-integer exponents differ in ~0.03% of cases (tools/v8-math).
    public static double Pow(double x, double y)
    {
        if (double.IsNaN(y)) return double.NaN;                                  // 1. exponent NaN
        if (double.IsInfinity(y) && (x == 1 || x == -1)) return double.NaN;      // 9b/10b. |base| = 1, exponent ±∞
        if (y == 2) return x * x;                                                // x ** 2 ==> x * x
        if (y == 0.5) return double.IsInfinity(x) ? double.PositiveInfinity : Math.Sqrt(x + 0);
        if (x == 10 && y >= -330 && y <= 310 && y == Math.Floor(y)) return BitConverter.UInt64BitsToDouble(Pow10[(int)y + 330]);
        return Math.Pow(x, y);
    }

    // Windows Node 24.12.0's 10 ** p for p = -330..310. Regenerate: node tools/v8-math/gen.mjs --cs-table
    static readonly ulong[] Pow10 =
    [
        0x0000000000000000UL, 0x0000000000000000UL, 0x0000000000000000UL, 0x0000000000000000UL, 0x0000000000000000UL, 0x0000000000000000UL,
        0x0000000000000000UL, 0x0000000000000002UL, 0x0000000000000014UL, 0x00000000000000CAUL, 0x00000000000007E8UL, 0x0000000000004F10UL,
        0x00000000000316A2UL, 0x00000000001EE257UL, 0x000000000134D761UL, 0x000000000C1069CDUL, 0x0000000078A42205UL, 0x00000004B6695433UL,
        0x0000002F201D49FBUL, 0x000001D74124E3D1UL, 0x000012688B70E62BUL, 0x0000B8157268FDAFUL, 0x000730D67819E8D2UL, 0x0031FA182C40C60DUL,
        0x0066789E3750F791UL, 0x009C16C5C5253575UL, 0x00D18E3B9B374169UL, 0x0105F1CA820511C3UL, 0x013B6E3D22865634UL, 0x017124E63593F5E1UL,
        0x01A56E1FC2F8F359UL, 0x01DAC9A7B3B7302FUL, 0x0210BE08D0527E1DUL, 0x0244ED8B04671DA5UL, 0x027A28EDC580E50EUL, 0x02B059949B708F29UL,
        0x02E46FF9C24CB2F3UL, 0x03198BF832DFDFB0UL, 0x034FEEF63F97D79CUL, 0x0383F559E7BEE6C1UL, 0x03B8F2B061AEA072UL, 0x03EF2F5C7A1A488EUL,
        0x04237D99CC506D59UL, 0x04585D003F6488AFUL, 0x048E74404F3DAADBUL, 0x04C308A831868AC9UL, 0x04F7CAD23DE82D7BUL, 0x052DBD86CD6238D9UL,
        0x05629674405D6388UL, 0x05973C115074BC6AUL, 0x05CD0B15A491EB84UL, 0x060226ED86DB3333UL, 0x0636B0A8E891FFFFUL, 0x066C5CD322B67FFFUL,
        0x06A1BA03F5B21000UL, 0x06D62884F31E93FFUL, 0x070BB2A62FE638FFUL, 0x07414FA7DDEFE3A0UL, 0x0775A391D56BDC87UL, 0x07AB0C764AC6D3A9UL,
        0x07E0E7C9EEBC444AUL, 0x081521BC6A6B555CUL, 0x084A6A2B85062AB3UL, 0x0880825B3323DAB0UL, 0x08B4A2F1FFECD15CUL, 0x08E9CBAE7FE805B3UL,
        0x09201F4D0FF10390UL, 0x0954272053ED4474UL, 0x098930E868E89591UL, 0x09BF7D228322BAF5UL, 0x09F3AE3591F5B4D9UL, 0x0A2899C2F6732210UL,
        0x0A5EC033B40FEA93UL, 0x0A9338205089F29CUL, 0x0AC8062864AC6F43UL, 0x0AFE07B27DD78B14UL, 0x0B32C4CF8EA6B6ECUL, 0x0B677603725064A8UL,
        0x0B9D53844EE47DD1UL, 0x0BD25432B14ECEA3UL, 0x0C06E93F5DA2824CUL, 0x0C3CA38F350B22DFUL, 0x0C71E6398126F5CBUL, 0x0CA65FC7E170B33EUL,
        0x0CDBF7B9D9CCE00DUL, 0x0D117AD428200C08UL, 0x0D45D98932280F0AUL, 0x0D7B4FEB7EB212CDUL, 0x0DB111F32F2F4BC0UL, 0x0DE5566FFAFB1EB0UL,
        0x0E1AAC0BF9B9E65CUL, 0x0E50AB877C142FFAUL, 0x0E84D6695B193BF8UL, 0x0EBA0C03B1DF8AF6UL, 0x0EF047824F2BB6DAUL, 0x0F245962E2F6A490UL,
        0x0F596FBB9BB44DB4UL, 0x0F8FCBAA82A16121UL, 0x0FC3DF4A91A4DCB5UL, 0x0FF8D71D360E13E2UL, 0x102F0CE4839198DBUL, 0x1063680ED23AFF89UL,
        0x1098421286C9BF6BUL, 0x10CE5297287C2F45UL, 0x1102F39E794D9D8BUL, 0x1137B08617A104EEUL, 0x116D9CA79D89462AUL, 0x11A281E8C275CBDAUL,
        0x11D72262F3133ED1UL, 0x120CEAFBAFD80E85UL, 0x124212DD4DE70913UL, 0x12769794A160CB58UL, 0x12AC3D79C9B8FE2EUL, 0x12E1A66C1E139EDDUL,
        0x1316100725988694UL, 0x134B9408EEFEA839UL, 0x13813C85955F2923UL, 0x13B58BA6FAB6F36CUL, 0x13EAEE90B964B047UL, 0x1420D51A73DEEE2DUL,
        0x14550A6110D6A9B8UL, 0x148A4CF9550C5426UL, 0x14C0701BD527B498UL, 0x14F48C22CA71A1BDUL, 0x1529AF2B7D0E0A2DUL, 0x15600D7B2E28C65CUL,
        0x159410D9F9B2F7F3UL, 0x15C91510781FB5F0UL, 0x15FF5A549627A36CUL, 0x16339874DDD8C623UL, 0x16687E92154EF7ACUL, 0x169E9E369AA2B597UL,
        0x16D322E220A5B17EUL, 0x1707EB9AA8CF1DDEUL, 0x173DE6815302E556UL, 0x1772B010D3E1CF56UL, 0x17A75C1508DA432BUL, 0x17DD331A4B10D3F6UL,
        0x18123FF06EEA847AUL, 0x1846CFEC8AA52598UL, 0x187C83E7AD4E6EFEUL, 0x18B1D270CC51055FUL, 0x18E6470CFF6546B6UL, 0x191BD8D03F3E9864UL,
        0x1951678227871F3EUL, 0x1985C162B168E70EUL, 0x19BB31BB5DC320D2UL, 0x19F0FF151A99F483UL, 0x1A253EDA614071A4UL, 0x1A5A8E90F9908E0DUL,
        0x1A90991A9BFA58C8UL, 0x1AC4BF6142F8EEFAUL, 0x1AF9EF3993B72AB8UL, 0x1B303583FC527AB3UL, 0x1B6442E4FB671960UL, 0x1B99539E3A40DFB8UL,
        0x1BCFA885C8D117A6UL, 0x1C03C9539D82AEC8UL, 0x1C38BBA884E35A7AUL, 0x1C6EEA92A61C3118UL, 0x1CA3529BA7D19EAFUL, 0x1CD8274291C6065BUL,
        0x1D0E3113363787F2UL, 0x1D42DEAC01E2B4F7UL, 0x1D779657025B6235UL, 0x1DAD7BECC2F23AC2UL, 0x1DE26D73F9D764B9UL, 0x1E1708D0F84D3DE7UL,
        0x1E4CCB0536608D61UL, 0x1E81FEE341FC585DUL, 0x1EB67E9C127B6E74UL, 0x1EEC1E43171A4A11UL, 0x1F2192E9EE706E4BUL, 0x1F55F7A46A0C89DDUL,
        0x1F8B758D848FAC55UL, 0x1FC1297872D9CBB5UL, 0x1FF573D68F903EA2UL, 0x202AD0CC33744E4BUL, 0x2060C27FA028B0EFUL, 0x2094F31F8832DD2AUL,
        0x20CA2FE76A3F9475UL, 0x21005DF0A267BCC9UL, 0x2134756CCB01ABFBUL, 0x216992C7FDC216FAUL, 0x219FF779FD329CB9UL, 0x21D3FAAC3E3FA1F3UL,
        0x2208F9574DCF8A70UL, 0x223F37AD21436D0CUL, 0x227382CC34CA2428UL, 0x22A8637F41FCAD32UL, 0x22DE7C5F127BD87EUL, 0x23130DBB6B8D674FUL,
        0x2347D12A4670C123UL, 0x237DC574D80CF16BUL, 0x23B29B69070816E3UL, 0x23E7424348CA1C9CUL, 0x241D12D41AFCA3C3UL, 0x24522BC490DDE65AUL,
        0x2486B6B5B5155FF0UL, 0x24BC6463225AB7ECUL, 0x24F1BEBDF578B2F4UL, 0x25262E6D72D6DFB0UL, 0x255BBA08CF8C979DUL, 0x2591544581B7DEC2UL,
        0x25C5A956E225D672UL, 0x25FB13AC9AAF4C0FUL, 0x2630EC4BE0AD8F89UL, 0x2665275ED8D8F36CUL, 0x269A71368F0F3047UL, 0x26D086C219697E2CUL,
        0x2704A8729FC3DDB7UL, 0x2739D28F47B4D525UL, 0x277023998CD10537UL, 0x27A42C7FF0054685UL, 0x27D9379FEC069826UL, 0x280F8587E7083E30UL,
        0x2843B374F06526DEUL, 0x2878A0522C7E7095UL, 0x28AEC866B79E0CBAUL, 0x28E33D4032C2C7F5UL, 0x29180C903F7379F2UL, 0x294E0FB44F50586EUL,
        0x2982C9D0B1923745UL, 0x29B77C44DDF6C516UL, 0x29ED5B561574765BUL, 0x2A225915CD68C9F9UL, 0x2A56EF5B40C2FC77UL, 0x2A8CAB3210F3BB95UL,
        0x2AC1EAFF4A98553DUL, 0x2AF665BF1D3E6A8DUL, 0x2B2BFF2EE48E0530UL, 0x2B617F7D4ED8C33EUL, 0x2B95DF5CA28EF40DUL, 0x2BCB5733CB32B111UL,
        0x2C0116805EFFAEAAUL, 0x2C355C2076BF9A55UL, 0x2C6AB328946F80EAUL, 0x2CA0AFF95CC5B092UL, 0x2CD4DBF7B3F71CB7UL, 0x2D0A12F5A0F4E3E5UL,
        0x2D404BD984990E6FUL, 0x2D745ECFE5BF520BUL, 0x2DA97683DF2F268DUL, 0x2DDFD424D6FAF031UL, 0x2E13E497065CD61FUL, 0x2E48DDBCC7F40BA6UL,
        0x2E7F152BF9F10E90UL, 0x2EB36D3B7C36A91AUL, 0x2EE8488A5B445360UL, 0x2F1E5AACF2156838UL, 0x2F52F8AC174D6123UL, 0x2F87B6D71D20B96CUL,
        0x2FBDA48CE468E7C7UL, 0x2FF286D80EC190DCUL, 0x3027288E1271F513UL, 0x305CF2B1970E7258UL, 0x309217AEFE690777UL, 0x30C69D9ABE034955UL,
        0x30FC45016D841BAAUL, 0x3131AB20E472914AUL, 0x316615E91D8F359DUL, 0x319B9B6364F30304UL, 0x31D1411E1F17E1E3UL, 0x32059165A6DDDA5BUL,
        0x323AF5BF109550F2UL, 0x3270D9976A5D5297UL, 0x32A50FFD44F4A73DUL, 0x32DA53FC9631D10DUL, 0x3310747DDDDF22A8UL, 0x3344919D5556EB52UL,
        0x3379B604AAACA626UL, 0x33B011C2EAABE7D8UL, 0x33E41633A556E1CEUL, 0x34191BC08EAC9A41UL, 0x344F62B0B257C0D2UL, 0x34839DAE6F76D883UL,
        0x34B8851A0B548EA4UL, 0x34EEA6608E29B24DUL, 0x352327FC58DA0F70UL, 0x3557F1FB6F10934CUL, 0x358DEE7A4AD4B81FUL, 0x35C2B50C6EC4F313UL,
        0x35F7624F8A762FD8UL, 0x362D3AE36D13BBCEUL, 0x366244CE242C5561UL, 0x3696D601AD376AB9UL, 0x36CC8B8218854567UL, 0x3701D7314F534B61UL,
        0x37364CFDA3281E39UL, 0x376BE03D0BF225C7UL, 0x37A16C262777579CUL, 0x37D5C72FB1552D83UL, 0x380B38FB9DAA78E4UL, 0x3841039D428A8B8FUL,
        0x38754484932D2E72UL, 0x38AA95A5B7F87A0FUL, 0x38E09D8792FB4C49UL, 0x3914C4E977BA1F5CUL, 0x3949F623D5A8A733UL, 0x398039D665896880UL,
        0x39B4484BFEEBC2A0UL, 0x39E95A5EFEA6B347UL, 0x3A1FB0F6BE506019UL, 0x3A53CE9A36F23C10UL, 0x3A88C240C4AECB14UL, 0x3ABEF2D0F5DA7DD9UL,
        0x3AF357C299A88EA7UL, 0x3B282DB34012B251UL, 0x3B5E392010175EE6UL, 0x3B92E3B40A0E9B4FUL, 0x3BC79CA10C924223UL, 0x3BFD83C94FB6D2ACUL,
        0x3C32725DD1D243ACUL, 0x3C670EF54646D497UL, 0x3C9CD2B297D889BCUL, 0x3CD203AF9EE75616UL, 0x3D06849B86A12B9BUL, 0x3D3C25C268497682UL,
        0x3D719799812DEA11UL, 0x3DA5FD7FE1796495UL, 0x3DDB7CDFD9D7BDBBUL, 0x3E112E0BE826D695UL, 0x3E45798EE2308C3AUL, 0x3E7AD7F29ABCAF48UL,
        0x3EB0C6F7A0B5ED8DUL, 0x3EE4F8B588E368F1UL, 0x3F1A36E2EB1C432DUL, 0x3F50624DD2F1A9FCUL, 0x3F847AE147AE147BUL, 0x3FB999999999999AUL,
        0x3FF0000000000000UL, 0x4024000000000000UL, 0x4059000000000000UL, 0x408F400000000000UL, 0x40C3880000000000UL, 0x40F86A0000000000UL,
        0x412E848000000000UL, 0x416312D000000000UL, 0x4197D78400000000UL, 0x41CDCD6500000000UL, 0x4202A05F20000000UL, 0x42374876E8000000UL,
        0x426D1A94A2000000UL, 0x42A2309CE5400000UL, 0x42D6BCC41E900000UL, 0x430C6BF526340000UL, 0x4341C37937E08000UL, 0x4376345785D8A000UL,
        0x43ABC16D674EC800UL, 0x43E158E460913D00UL, 0x4415AF1D78B58C40UL, 0x444B1AE4D6E2EF50UL, 0x4480F0CF064DD592UL, 0x44B52D02C7E14AF7UL,
        0x44EA784379D99DB4UL, 0x45208B2A2C280291UL, 0x4554ADF4B7320335UL, 0x4589D971E4FE8402UL, 0x45C027E72F1F1281UL, 0x45F431E0FAE6D721UL,
        0x46293E5939A08CEAUL, 0x465F8DEF8808B024UL, 0x4693B8B5B5056E17UL, 0x46C8A6E32246C99CUL, 0x46FED09BEAD87C03UL, 0x4733426172C74D82UL,
        0x476812F9CF7920E3UL, 0x479E17B84357691BUL, 0x47D2CED32A16A1B1UL, 0x48078287F49C4A1DUL, 0x483D6329F1C35CA5UL, 0x48725DFA371A19E7UL,
        0x48A6F578C4E0A061UL, 0x48DCB2D6F618C879UL, 0x4911EFC659CF7D4CUL, 0x49466BB7F0435C9EUL, 0x497C06A5EC5433C6UL, 0x49B18427B3B4A05CUL,
        0x49E5E531A0A1C873UL, 0x4A1B5E7E08CA3A8FUL, 0x4A511B0EC57E649AUL, 0x4A8561D276DDFDC0UL, 0x4ABABA4714957D30UL, 0x4AF0B46C6CDD6E3EUL,
        0x4B24E1878814C9CEUL, 0x4B5A19E96A19FC41UL, 0x4B905031E2503DA9UL, 0x4BC4643E5AE44D13UL, 0x4BF97D4DF19D6057UL, 0x4C2FDCA16E04B86DUL,
        0x4C63E9E4E4C2F344UL, 0x4C98E45E1DF3B015UL, 0x4CCF1D75A5709C1BUL, 0x4D03726987666191UL, 0x4D384F03E93FF9F5UL, 0x4D6E62C4E38FF872UL,
        0x4DA2FDBB0E39FB47UL, 0x4DD7BD29D1C87A19UL, 0x4E0DAC74463A989FUL, 0x4E428BC8ABE49F64UL, 0x4E772EBAD6DDC73DUL, 0x4EACFA698C95390CUL,
        0x4EE21C81F7DD43A7UL, 0x4F16A3A275D49491UL, 0x4F4C4C8B1349B9B5UL, 0x4F81AFD6EC0E1411UL, 0x4FB61BCCA7119916UL, 0x4FEBA2BFD0D5FF5BUL,
        0x502145B7E285BF99UL, 0x50559725DB272F7FUL, 0x508AFCEF51F0FB5FUL, 0x50C0DE1593369D1BUL, 0x50F5159AF8044462UL, 0x512A5B01B605557BUL,
        0x516078E111C3556DUL, 0x5194971956342AC8UL, 0x51C9BCDFABC1357AUL, 0x5200160BCB58C16CUL, 0x52341B8EBE2EF1C7UL, 0x526922726DBAAE39UL,
        0x529F6B0F092959C7UL, 0x52D3A2E965B9D81DUL, 0x53088BA3BF284E24UL, 0x533EAE8CAEF261ADUL, 0x53732D17ED577D0CUL, 0x53A7F85DE8AD5C4FUL,
        0x53DDF67562D8B363UL, 0x5412BA095DC7701EUL, 0x5447688BB5394C25UL, 0x547D42AEA2879F2EUL, 0x54B249AD2594C37DUL, 0x54E6DC186EF9F45CUL,
        0x551C931E8AB87173UL, 0x5551DBF316B346E8UL, 0x558652EFDC6018A2UL, 0x55BBE7ABD3781ECAUL, 0x55F170CB642B133FUL, 0x5625CCFE3D35D80EUL,
        0x565B403DCC834E12UL, 0x569108269FD210CBUL, 0x56C54A3047C694FEUL, 0x56FA9CBC59B83A3DUL, 0x5730A1F5B8132466UL, 0x5764CA732617ED80UL,
        0x5799FD0FEF9DE8E0UL, 0x57D03E29F5C2B18CUL, 0x58044DB473335DEFUL, 0x583961219000356BUL, 0x586FB969F40042C5UL, 0x58A3D3E2388029BBUL,
        0x58D8C8DAC6A0342AUL, 0x590EFB1178484135UL, 0x59435CEAEB2D28C1UL, 0x59783425A5F872F1UL, 0x59AE412F0F768FADUL, 0x59E2E8BD69AA19CCUL,
        0x5A17A2ECC414A03FUL, 0x5A4D8BA7F519C84FUL, 0x5A827748F9301D32UL, 0x5AB7151B377C247EUL, 0x5AECDA62055B2D9EUL, 0x5B22087D4358FC82UL,
        0x5B568A9C942F3BA3UL, 0x5B8C2D43B93B0A8CUL, 0x5BC19C4A53C4E697UL, 0x5BF6035CE8B6203DUL, 0x5C2B843422E3A84DUL, 0x5C6132A095CE4930UL,
        0x5C957F48BB41DB7CUL, 0x5CCADF1AEA12525BUL, 0x5D00CB70D24B7379UL, 0x5D34FE4D06DE5057UL, 0x5D6A3DE04895E46DUL, 0x5DA066AC2D5DAEC4UL,
        0x5DD4805738B51A75UL, 0x5E09A06D06E26112UL, 0x5E400444244D7CABUL, 0x5E7405552D60DBD6UL, 0x5EA906AA78B912CCUL, 0x5EDF485516E7577FUL,
        0x5F138D352E5096AFUL, 0x5F48708279E4BC5BUL, 0x5F7E8CA3185DEB72UL, 0x5FB317E5EF3AB327UL, 0x5FE7DDDF6B095FF1UL, 0x601DD55745CBB7EDUL,
        0x6052A5568B9F52F4UL, 0x60874EAC2E8727B1UL, 0x60BD22573A28F19DUL, 0x60F2357684599702UL, 0x6126C2D4256FFCC3UL, 0x615C73892ECBFBF4UL,
        0x6191C835BD3F7D78UL, 0x61C63A432C8F5CD6UL, 0x61FBC8D3F7B3340CUL, 0x62315D847AD00087UL, 0x6265B4E5998400A9UL, 0x629B221EFFE500D4UL,
        0x62D0F5535FEF2084UL, 0x630532A837EAE8A5UL, 0x633A7F5245E5A2CFUL, 0x63708F936BAF85C1UL, 0x63A4B378469B6732UL, 0x63D9E056584240FEUL,
        0x64102C35F729689FUL, 0x6444374374F3C2C6UL, 0x647945145230B378UL, 0x64AF965966BCE056UL, 0x64E3BDF7E0360C36UL, 0x6518AD75D8438F43UL,
        0x654ED8D34E547314UL, 0x6583478410F4C7ECUL, 0x65B819651531F9E8UL, 0x65EE1FBE5A7E7861UL, 0x6622D3D6F88F0B3DUL, 0x665788CCB6B2CE0CUL,
        0x668D6AFFE45F818FUL, 0x66C262DFEEBBB0F9UL, 0x66F6FB97EA6A9D38UL, 0x672CBA7DE5054486UL, 0x6761F48EAF234AD4UL, 0x679671B25AEC1D89UL,
        0x67CC0E1EF1A724EBUL, 0x680188D357087713UL, 0x6835EB082CCA94D7UL, 0x686B65CA37FD3A0DUL, 0x68A11F9E62FE4448UL, 0x68D56785FBBDD55AUL,
        0x690AC1677AAD4AB1UL, 0x6940B8E0ACAC4EAFUL, 0x6974E718D7D7625AUL, 0x69AA20DF0DCD3AF1UL, 0x69E0548B68A044D6UL, 0x6A1469AE42C8560CUL,
        0x6A498419D37A6B8FUL, 0x6A7FE52048590673UL, 0x6AB3EF342D37A408UL, 0x6AE8EB0138858D0AUL, 0x6B1F25C186A6F04CUL, 0x6B537798F4285630UL,
        0x6B88557F31326BBBUL, 0x6BBE6ADEFD7F06AAUL, 0x6BF302CB5E6F642AUL, 0x6C27C37E360B3D35UL, 0x6C5DB45DC38E0C82UL, 0x6C9290BA9A38C7D1UL,
        0x6CC734E940C6F9C6UL, 0x6CFD022390F8B837UL, 0x6D3221563A9B7323UL, 0x6D66A9ABC9424FEBUL, 0x6D9C5416BB92E3E6UL, 0x6DD1B48E353BCE70UL,
        0x6E0621B1C28AC20CUL, 0x6E3BAA1E332D728FUL, 0x6E714A52DFFC6799UL, 0x6EA59CE797FB817FUL, 0x6EDB04217DFA61DFUL, 0x6F10E294EEBC7D2CUL,
        0x6F451B3A2A6B9C76UL, 0x6F7A6208B5068394UL, 0x6FB07D457124123DUL, 0x6FE49C96CD6D16CCUL, 0x7019C3BC80C85C7FUL, 0x70501A55D07D39CFUL,
        0x708420EB449C8843UL, 0x70B9292615C3AA54UL, 0x70EF736F9B3494E9UL, 0x7123A825C100DD11UL, 0x7158922F31411456UL, 0x718EB6BAFD91596BUL,
        0x71C33234DE7AD7E3UL, 0x71F7FEC216198DDCUL, 0x722DFE729B9FF153UL, 0x7262BF07A143F6D4UL, 0x72976EC98994F489UL, 0x72CD4A7BEBFA31ABUL,
        0x73024E8D737C5F0BUL, 0x7336E230D05B76CDUL, 0x736C9ABD04725481UL, 0x73A1E0B622C774D0UL, 0x73D658E3AB795204UL, 0x740BEF1C9657A686UL,
        0x74417571DDF6C814UL, 0x7475D2CE55747A18UL, 0x74AB4781EAD1989EUL, 0x74E10CB132C2FF63UL, 0x75154FDD7F73BF3CUL, 0x754AA3D4DF50AF0BUL,
        0x7580A6650B926D67UL, 0x75B4CFFE4E7708C0UL, 0x75EA03FDE214CAF1UL, 0x7620427EAD4CFED6UL, 0x7654531E58A03E8CUL, 0x768967E5EEC84E2FUL,
        0x76BFC1DF6A7A61BBUL, 0x76F3D92BA28C7D15UL, 0x7728CF768B2F9C5AUL, 0x775F03542DFB8370UL, 0x779362149CBD3226UL, 0x77C83A99C3EC7EB0UL,
        0x77FE494034E79E5CUL, 0x7832EDC82110C2F9UL, 0x7867A93A2954F3B8UL, 0x789D9388B3AA30A5UL, 0x78D27C35704A5E67UL, 0x79071B42CC5CF601UL,
        0x793CE2137F743382UL, 0x79720D4C2FA8A031UL, 0x79A6909F3B92C83DUL, 0x79DC34C70A777A4DUL, 0x7A11A0FC668AAC70UL, 0x7A46093B802D578CUL,
        0x7A7B8B8A6038AD6FUL, 0x7AB137367C236C65UL, 0x7AE585041B2C477FUL, 0x7B1AE64521F7595EUL, 0x7B50CFEB353A97DBUL, 0x7B8503E602893DD2UL,
        0x7BBA44DF832B8D46UL, 0x7BF06B0BB1FB384CUL, 0x7C2485CE9E7A065FUL, 0x7C59A742461887F6UL, 0x7C9008896BCF54FAUL, 0x7CC40AABC6C32A38UL,
        0x7CF90D56B873F4C7UL, 0x7D2F50AC6690F1F8UL, 0x7D63926BC01A973BUL, 0x7D987706B0213D0AUL, 0x7DCE94C85C298C4CUL, 0x7E031CFD3999F7B0UL,
        0x7E37E43C8800759CUL, 0x7E6DDD4BAA009303UL, 0x7EA2AA4F4A405BE2UL, 0x7ED754E31CD072DAUL, 0x7F0D2A1BE4048F90UL, 0x7F423A516E82D9BAUL,
        0x7F76C8E5CA239029UL, 0x7FAC7B1F3CAC7433UL, 0x7FE1CCF385EBC8A0UL, 0x7FF0000000000000UL, 0x7FF0000000000000UL,
    ];
}
