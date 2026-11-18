using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace FsmEditor.Core
{
    public static class Num
    {
        /// <summary>Rounds half up (towards +∞), like the rounding used for saved coordinates.</summary>
        public static double Round(double n) => Math.Floor(n + 0.5);

        /// <summary>Rounds to 0.1.</summary>
        public static double R1(double n) => Math.Floor(n * 10 + 0.5) / 10;

        /// <summary>
        /// JavaScript's <c>Math.hypot(a, b)</c> as V8 computes it (scaled by the larger value, with Kahan summation),
        /// which differs from <c>Math.Sqrt(a * a + b * b)</c> in the last bit for many values.
        /// </summary>
        public static double Hypot(double a, double b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            if (double.IsInfinity(a) || double.IsInfinity(b)) return double.PositiveInfinity;
            if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
            var max = Math.Max(a, b);
            if (max == 0) return 0;
            double sum = 0, compensation = 0;
            foreach (var x in new[] { a, b })
            {
                var r = x / max;
                var summand = r * r - compensation;
                var preliminary = sum + summand;
                compensation = preliminary - sum - summand;
                sum = preliminary;
            }
            return Math.Sqrt(sum) * max;
        }

        /// <summary>
        /// The text JavaScript's <c>String(n)</c> gives: the shortest digits that read back
        /// as <paramref name="n"/>, <c>60</c>, <c>12.5</c>, <c>1e-7</c>, <c>1.5e+21</c>; <c>-0</c> gives <c>0</c>.
        /// NaN and infinities give <c>0</c>. Computed exactly, so it doesn't depend on the
        /// runtime's own formatting ("R" can print 17 digits on .NET Framework).
        /// </summary>
        public static string Format(double n)
        {
            if (double.IsNaN(n) || double.IsInfinity(n)) return "0";
            if (n == 0) return "0";
            if (n == Math.Floor(n) && Math.Abs(n) < 1e15) return ((long)n).ToString(CultureInfo.InvariantCulture);
            if (n < 0) return "-" + Format(-n);
            ShortestDigits(n, out var digits, out var point);
            return Layout(digits, point);
        }

        /// <summary>JavaScript's <c>String(n)</c>, <c>NaN</c> and <c>Infinity</c> included.</summary>
        public static string Js(double n) =>
            double.IsNaN(n) ? "NaN" : double.IsPositiveInfinity(n) ? "Infinity" : double.IsNegativeInfinity(n) ? "-Infinity" : Format(n);

        /// <summary>
        /// Shortest digits <paramref name="digits"/> (no trailing zero) such that
        /// 0.<paramref name="digits"/> × 10^<paramref name="point"/> reads back as <paramref name="n"/> (&gt; 0),
        /// the closest to <paramref name="n"/> when several do (ECMAScript Number::toString).
        /// </summary>
        private static void ShortestDigits(double n, out string digits, out int point)
        {
            long bits = BitConverter.DoubleToInt64Bits(n);
            int biased = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            long f = biased == 0 ? fraction : fraction | (1L << 52);
            int e = biased == 0 ? -1074 : biased - 1075;
            // n = f × 2^e. In units of 2^(e-2): n is 4f, and the numbers that read back as n lie
            // between 4f-2 (4f-1 just above a power of two) and 4f+2, the bounds included when f is even.
            var v = new BigInteger(f) * 4;
            var hi = v + 2;
            var lo = v - (fraction == 0 && biased > 1 ? 1 : 2);
            bool inclusive = (f & 1) == 0;
            var twoUp = e - 2 >= 0 ? BigInteger.Pow(2, e - 2) : BigInteger.One;
            var twoDown = e - 2 < 0 ? BigInteger.Pow(2, 2 - e) : BigInteger.One;

            // Decimal exponent k with 10^k <= n < 10^(k+1).
            int k = (int)Math.Floor(Math.Log10(n));
            bool AtLeastPow10(int x) =>
                v * twoUp * (x < 0 ? BigInteger.Pow(10, -x) : BigInteger.One) >= twoDown * (x > 0 ? BigInteger.Pow(10, x) : BigInteger.One);
            while (!AtLeastPow10(k)) k--;
            while (AtLeastPow10(k + 1)) k++;

            for (int p = 1; p <= 17; p++)
            {
                // Candidates with p digits are N × 10^s; n / 10^s = num / den.
                int s = k - p + 1;
                var scale = twoUp * (s < 0 ? BigInteger.Pow(10, -s) : BigInteger.One);
                var den = twoDown * (s > 0 ? BigInteger.Pow(10, s) : BigInteger.One);
                var num = v * scale;
                var numLo = lo * scale;
                var numHi = hi * scale;
                var down = BigInteger.DivRem(num, den, out var rem);
                var up = down + 1;
                bool Reads(BigInteger c)
                {
                    var x = c * den;
                    return inclusive ? x >= numLo && x <= numHi : x > numLo && x < numHi;
                }
                bool downOk = Reads(down), upOk = Reads(up);
                if (!downOk && !upOk) continue;
                BigInteger chosen;
                if (downOk && upOk)
                {
                    int c = (rem * 2).CompareTo(den);
                    chosen = c < 0 ? down : c > 0 ? up : (down.IsEven ? down : up);
                }
                else
                {
                    chosen = downOk ? down : up;
                }
                var text = chosen.ToString(CultureInfo.InvariantCulture);
                point = s + text.Length;
                digits = text.TrimEnd('0');
                return;
            }
            // 17 digits always identify a double.
            throw new InvalidOperationException("No round-trip digits for " + n.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>Lays out 0.<paramref name="d"/> × 10^<paramref name="n"/> as ECMAScript Number::toString does.</summary>
        private static string Layout(string d, int n)
        {
            int k = d.Length;
            if (k <= n && n <= 21) return d + new string('0', n - k);
            if (0 < n && n <= 21) return d.Substring(0, n) + "." + d.Substring(n);
            if (-6 < n && n <= 0) return "0." + new string('0', -n) + d;
            int exp = n - 1;
            var tail = "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
            return k == 1 ? d + tail : d.Substring(0, 1) + "." + d.Substring(1) + tail;
        }

        /// <summary>
        /// Parses a number like JavaScript's <c>+s || 0</c> does for XMI attributes (missing or invalid gives 0), infinite
        /// ones giving 0 too, as they do once the other editions' JSON has made them null.
        /// </summary>
        public static double Parse(string s)
        {
            var d = ParseJs(s);
            return double.IsNaN(d) || double.IsInfinity(d) ? 0 : d;
        }

        /// <summary>
        /// JavaScript's unary plus (<c>+s</c>): the number in <paramref name="s"/> once trimmed, 0 when that is empty,
        /// NaN when <paramref name="s"/> is null (a missing attribute) or isn't a number.
        /// </summary>
        public static double ParseJs(string s)
        {
            if (s == null) return double.NaN;
            var t = JsWhitespace.Trim(s);
            if (t.Length == 0) return 0;
            if (t.Length > 2 && t[0] == '0')
            {
                var radix = t[1] == 'x' || t[1] == 'X' ? 16 : t[1] == 'o' || t[1] == 'O' ? 8 : t[1] == 'b' || t[1] == 'B' ? 2 : 0;
                if (radix > 0)
                {
                    double n = 0;
                    for (var i = 2; i < t.Length; i++)
                    {
                        var c = t[i];
                        var digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 99;
                        if (digit >= radix) return double.NaN;
                        n = n * radix + digit;
                    }
                    return n;
                }
            }
            var body = t[0] == '+' || t[0] == '-' ? t.Substring(1) : t;
            if (body == "Infinity") return t[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            // A decimal literal: digits with an optional point and exponent (not the other forms .NET would accept).
            if (!Regex.IsMatch(body, @"^(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$")) return double.NaN;
            // .NET Framework refuses numbers too large for a double, which JavaScript reads as infinite.
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
                : t[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }
    }
}
