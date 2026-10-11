using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tools.Services;

public record UnitDefinition(string Code, string Name, string Symbol, double ToBaseFactor, Func<double, double>? CustomToBase = null, Func<double, double>? CustomFromBase = null)
{
    public override string ToString() => $"{Name} ({Symbol})";

    public double ToBase(double val) => CustomToBase != null ? CustomToBase(val) : val * ToBaseFactor;
    public double FromBase(double baseVal) => CustomFromBase != null ? CustomFromBase(baseVal) : baseVal / ToBaseFactor;
}

public record UnitCategory(string Code, string Name, IReadOnlyList<UnitDefinition> Units)
{
    public override string ToString() => Name;
}

public static class CalculatorEngine
{
    public static readonly IReadOnlyList<UnitCategory> UnitCategories =
    [
        new("Data", "数据存储",
        [
            new("B", "字节", "B", 1d),
            new("KB", "千字节", "KB", 1024d),
            new("MB", "兆字节", "MB", 1024d * 1024d),
            new("GB", "吉字节", "GB", 1024d * 1024d * 1024d),
            new("TB", "太字节", "TB", 1024d * 1024d * 1024d * 1024d),
            new("PB", "拍字节", "PB", 1024d * 1024d * 1024d * 1024d * 1024d),
        ]),
        new("Length", "长度",
        [
            new("mm", "毫米", "mm", 0.001d),
            new("cm", "厘米", "cm", 0.01d),
            new("m", "米", "m", 1d),
            new("km", "千米", "km", 1000d),
            new("in", "英寸", "in", 0.0254d),
            new("ft", "英尺", "ft", 0.3048d),
            new("mi", "英里", "mi", 1609.344d),
        ]),
        new("Mass", "重量",
        [
            new("mg", "毫克", "mg", 0.000001d),
            new("g", "克", "g", 0.001d),
            new("kg", "千克", "kg", 1d),
            new("jin", "市斤", "斤", 0.5d),
            new("t", "吨", "t", 1000d),
            new("oz", "盎司", "oz", 0.028349523125d),
            new("lb", "磅", "lb", 0.45359237d),
        ]),
        new("Temp", "温度",
        [
            new("C", "摄氏度", "°C", 1d, c => c, c => c),
            new("F", "华氏度", "°F", 1d, f => (f - 32d) * 5d / 9d, c => c * 9d / 5d + 32d),
            new("K", "开尔文", "K", 1d, k => k - 273.15d, c => c + 273.15d),
        ]),
        new("Area", "面积",
        [
            new("cm2", "平方厘米", "cm²", 0.0001d),
            new("m2", "平方米", "m²", 1d),
            new("mu", "亩", "亩", 666.6666667d),
            new("ha", "公顷", "ha", 10000d),
            new("km2", "平方千米", "km²", 1_000_000d),
        ]),
        new("Time", "时间",
        [
            new("ms", "毫秒", "ms", 0.001d),
            new("s", "秒", "s", 1d),
            new("min", "分钟", "min", 60d),
            new("h", "小时", "h", 3600d),
            new("d", "天", "d", 86400d),
            new("wk", "周", "wk", 604800d),
        ]),
    ];

    public static bool TryEvaluate(string? expression, bool degreesMode, double ans, out double result, out string? error)
    {
        result = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
            return false;

        try
        {
            var normalized = NormalizeExpression(expression);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            var parser = new ExpressionParser(normalized, degreesMode, ans);
            result = parser.Parse();
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                error = "数值溢出或无效";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string FormatNumber(double value, int decimalPlaces = 6, bool thousandsSeparator = true)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "∞";
        if (double.IsNegativeInfinity(value)) return "-∞";

        if (Math.Abs(value) < 1e-12) value = 0;

        int clampedDecimals = Math.Clamp(decimalPlaces, 0, 10);
        double abs = Math.Abs(value);

        if (abs > 0 && (abs >= 1e15 || abs < Math.Pow(10, -Math.Max(clampedDecimals, 6))))
        {
            return value.ToString("0.######e+0", CultureInfo.InvariantCulture);
        }

        double rounded = Math.Round(value, clampedDecimals, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) < 1e-12) rounded = 0;

        string decPattern = clampedDecimals > 0 ? "." + new string('#', clampedDecimals) : string.Empty;
        string intPattern = thousandsSeparator ? "#,##0" : "0";
        return rounded.ToString(intPattern + decPattern, CultureInfo.InvariantCulture);
    }

    public static string FormatRawNumber(double value, int decimalPlaces = 8)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "0";
        if (Math.Abs(value) < 1e-12) return "0";
        int clampedDecimals = Math.Clamp(decimalPlaces, 0, 10);
        double rounded = Math.Round(value, clampedDecimals, MidpointRounding.AwayFromZero);
        string decPattern = clampedDecimals > 0 ? "." + new string('#', clampedDecimals) : string.Empty;
        return rounded.ToString("0" + decPattern, CultureInfo.InvariantCulture);
    }

    private static string NormalizeExpression(string raw)
    {
        var sb = new StringBuilder(raw.Length + 8);
        foreach (char c in raw)
        {
            switch (c)
            {
                case '×':
                case '·':
                    sb.Append('*');
                    break;
                case '÷':
                    sb.Append('/');
                    break;
                case '−':
                case '—':
                    sb.Append('-');
                    break;
                case '（':
                    sb.Append('(');
                    break;
                case '）':
                    sb.Append(')');
                    break;
                case '，':
                case ',':
                    // Ignore thousands commas in copied numbers
                    break;
                case '=':
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    // ---- Programmer Mode 64-bit Evaluator ----

    public static bool TryEvaluateProgrammer(string? expression, int defaultBase, out long result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        try
        {
            var parser = new ProgrammerParser(expression, defaultBase);
            result = parser.Parse();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string FormatBase(long value, int radix)
    {
        return radix switch
        {
            16 => GroupRight(Convert.ToString(value, 16).ToUpperInvariant(), 4, ' '),
            8 => GroupRight(Convert.ToString(value, 8), 3, ' '),
            2 => GroupRight(Convert.ToString(value, 2), 4, ' '),
            _ => value.ToString("#,##0", CultureInfo.InvariantCulture),
        };
    }

    private static string GroupRight(string text, int groupSize, char sep)
    {
        if (text.Length <= groupSize) return text;
        var sb = new StringBuilder(text.Length + text.Length / groupSize);
        int firstGroup = text.Length % groupSize;
        if (firstGroup > 0)
        {
            sb.Append(text, 0, firstGroup);
        }
        for (int i = firstGroup; i < text.Length; i += groupSize)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append(text, i, groupSize);
        }
        return sb.ToString();
    }

    private sealed class ExpressionParser
    {
        private readonly string text;
        private readonly bool degrees;
        private readonly double ans;
        private int pos;

        public ExpressionParser(string text, bool degrees, double ans)
        {
            this.text = text;
            this.degrees = degrees;
            this.ans = ans;
        }

        public double Parse()
        {
            double val = ParseAddSub();
            SkipSpaces();
            if (pos < text.Length)
                throw new FormatException("算式语法不完整");
            return val;
        }

        private void SkipSpaces()
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;
        }

        // Handles contextual percentage like A + B% => A + (A * B / 100)
        private double ParseAddSub()
        {
            double left = ParseMulDiv();
            while (true)
            {
                SkipSpaces();
                if (pos >= text.Length) break;
                char op = text[pos];
                if (op != '+' && op != '-') break;
                pos++;

                double right = ParseMulDiv(out bool rightWasPurePercent);
                if (rightWasPurePercent)
                {
                    // e.g. 100 + 20% = 100 + (100 * 0.20) = 120
                    right = left * right;
                }

                left = op == '+' ? left + right : left - right;
            }
            return left;
        }

        private double ParseMulDiv() => ParseMulDiv(out _);

        private double ParseMulDiv(out bool wasPurePercent)
        {
            double left = ParsePower(out bool firstPercent);
            bool anyMulDiv = false;

            while (true)
            {
                SkipSpaces();
                if (pos >= text.Length) break;

                char c = text[pos];
                if (c == '*' || c == '/')
                {
                    anyMulDiv = true;
                    pos++;
                    double right = ParsePower(out _);
                    if (c == '*') left *= right;
                    else
                    {
                        if (Math.Abs(right) < 1e-15)
                            throw new DivideByZeroException("除数不能为 0");
                        left /= right;
                    }
                }
                else if (StartsImplicitMultiplication())
                {
                    anyMulDiv = true;
                    double right = ParsePower(out _);
                    left *= right;
                }
                else
                {
                    break;
                }
            }

            wasPurePercent = firstPercent && !anyMulDiv;
            return left;
        }

        private bool StartsImplicitMultiplication()
        {
            if (pos >= text.Length) return false;
            char c = text[pos];
            return c == '(' || c == 'π' || c == '√' || char.IsLetter(c);
        }

        private double ParsePower(out bool wasPercent)
        {
            double left = ParseUnary(out wasPercent);
            SkipSpaces();
            if (pos < text.Length && text[pos] == '^')
            {
                pos++;
                wasPercent = false;
                double right = ParsePower(out _);
                return Math.Pow(left, right);
            }
            return left;
        }

        private double ParseUnary(out bool wasPercent)
        {
            SkipSpaces();
            wasPercent = false;
            if (pos < text.Length)
            {
                if (text[pos] == '+')
                {
                    pos++;
                    return ParseUnary(out wasPercent);
                }
                if (text[pos] == '-')
                {
                    pos++;
                    return -ParseUnary(out wasPercent);
                }
                if (text[pos] == '√')
                {
                    pos++;
                    double arg = ParseUnary(out _);
                    if (arg < 0) throw new ArgumentException("负数无法开平方");
                    return Math.Sqrt(arg);
                }
            }
            return ParsePostfix(out wasPercent);
        }

        private double ParsePostfix(out bool wasPercent)
        {
            double val = ParsePrimary();
            wasPercent = false;

            while (true)
            {
                SkipSpaces();
                if (pos >= text.Length) break;
                char c = text[pos];
                if (c == '%')
                {
                    pos++;
                    val /= 100.0;
                    wasPercent = true;
                }
                else if (c == '!')
                {
                    pos++;
                    val = Factorial(val);
                    wasPercent = false;
                }
                else if (c == '²')
                {
                    pos++;
                    val = val * val;
                    wasPercent = false;
                }
                else if (c == '³')
                {
                    pos++;
                    val = val * val * val;
                    wasPercent = false;
                }
                else
                {
                    break;
                }
            }

            return val;
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (pos >= text.Length)
                throw new FormatException("算式末尾缺少操作数");

            char c = text[pos];

            if (c == '(')
            {
                pos++;
                double val = ParseAddSub();
                SkipSpaces();
                if (pos < text.Length && text[pos] == ')')
                    pos++;
                return val;
            }

            if (c == 'π')
            {
                pos++;
                return Math.PI;
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = pos;
                while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.'))
                    pos++;

                // Scientific notation e.g. 1.5e-3
                if (pos + 1 < text.Length && (text[pos] == 'e' || text[pos] == 'E') &&
                    (char.IsDigit(text[pos + 1]) || ((text[pos + 1] == '+' || text[pos + 1] == '-') && pos + 2 < text.Length && char.IsDigit(text[pos + 2]))))
                {
                    pos++;
                    if (text[pos] == '+' || text[pos] == '-') pos++;
                    while (pos < text.Length && char.IsDigit(text[pos])) pos++;
                }

                string numStr = text.Substring(start, pos - start);
                if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    throw new FormatException($"无效数字: {numStr}");
                return num;
            }

            if (char.IsLetter(c))
            {
                int start = pos;
                while (pos < text.Length && char.IsLetter(text[pos]))
                    pos++;
                string ident = text.Substring(start, pos - start).ToLowerInvariant();

                return ident switch
                {
                    "pi" => Math.PI,
                    "e" => Math.E,
                    "ans" => ans,
                    _ => ApplyFunction(ident, ParseFunctionArgument()),
                };
            }

            throw new FormatException($"无法识别的字符: {c}");
        }

        private double ParseFunctionArgument()
        {
            SkipSpaces();
            if (pos < text.Length && text[pos] == '(')
            {
                pos++;
                double v = ParseAddSub();
                SkipSpaces();
                if (pos < text.Length && text[pos] == ')')
                    pos++;
                return v;
            }
            return ParseUnary(out _);
        }

        private double ApplyFunction(string func, double arg)
        {
            double rad = degrees ? arg * Math.PI / 180.0 : arg;
            return func switch
            {
                "sin" => Math.Sin(rad),
                "cos" => Math.Cos(rad),
                "tan" => Math.Tan(rad),
                "asin" => degrees ? Math.Asin(arg) * 180.0 / Math.PI : Math.Asin(arg),
                "acos" => degrees ? Math.Acos(arg) * 180.0 / Math.PI : Math.Acos(arg),
                "atan" => degrees ? Math.Atan(arg) * 180.0 / Math.PI : Math.Atan(arg),
                "sqrt" => arg < 0 ? throw new ArgumentException("负数无法开平方") : Math.Sqrt(arg),
                "cbrt" => Math.Cbrt(arg),
                "ln" => arg <= 0 ? throw new ArgumentException("对数真数需大于 0") : Math.Log(arg),
                "log" => arg <= 0 ? throw new ArgumentException("对数真数需大于 0") : Math.Log10(arg),
                "abs" => Math.Abs(arg),
                "exp" => Math.Exp(arg),
                "fact" => Factorial(arg),
                _ => throw new FormatException($"未知函数: {func}"),
            };
        }

        private static double Factorial(double n)
        {
            if (n < 0 || Math.Abs(n - Math.Round(n)) > 1e-9)
                throw new ArgumentException("阶乘仅支持非负整数");
            int k = (int)Math.Round(n);
            if (k > 170) throw new OverflowException("阶乘超出范围");
            double res = 1;
            for (int i = 2; i <= k; i++) res *= i;
            return res;
        }
    }

    private sealed class ProgrammerParser
    {
        private readonly string text;
        private readonly int radix;
        private int pos;

        public ProgrammerParser(string text, int radix)
        {
            this.text = text.Replace(" ", "").Replace(",", "").Replace("×", "*").Replace("÷", "/").Replace("−", "-");
            this.radix = radix;
        }

        public long Parse()
        {
            long v = ParseBitwiseOr();
            if (pos < text.Length)
                throw new FormatException();
            return v;
        }

        private long ParseBitwiseOr()
        {
            long left = ParseBitwiseXor();
            while (pos < text.Length && text[pos] == '|')
            {
                pos++;
                left |= ParseBitwiseXor();
            }
            return left;
        }

        private long ParseBitwiseXor()
        {
            long left = ParseBitwiseAnd();
            while (pos < text.Length && text[pos] == '^')
            {
                pos++;
                left ^= ParseBitwiseAnd();
            }
            return left;
        }

        private long ParseBitwiseAnd()
        {
            long left = ParseShift();
            while (pos < text.Length && text[pos] == '&')
            {
                pos++;
                left &= ParseShift();
            }
            return left;
        }

        private long ParseShift()
        {
            long left = ParseAddSub();
            while (pos + 1 < text.Length)
            {
                if (text[pos] == '<' && text[pos + 1] == '<')
                {
                    pos += 2;
                    left <<= (int)ParseAddSub();
                }
                else if (text[pos] == '>' && text[pos + 1] == '>')
                {
                    pos += 2;
                    left >>= (int)ParseAddSub();
                }
                else break;
            }
            return left;
        }

        private long ParseAddSub()
        {
            long left = ParseMulDiv();
            while (pos < text.Length && (text[pos] == '+' || text[pos] == '-'))
            {
                char op = text[pos++];
                long right = ParseMulDiv();
                left = op == '+' ? left + right : left - right;
            }
            return left;
        }

        private long ParseMulDiv()
        {
            long left = ParseUnary();
            while (pos < text.Length && (text[pos] == '*' || text[pos] == '/' || text[pos] == '%'))
            {
                char op = text[pos++];
                long right = ParseUnary();
                if (op == '*') left *= right;
                else if (op == '/') left = right == 0 ? 0 : left / right;
                else left = right == 0 ? 0 : left % right;
            }
            return left;
        }

        private long ParseUnary()
        {
            if (pos < text.Length)
            {
                if (text[pos] == '+') { pos++; return ParseUnary(); }
                if (text[pos] == '-') { pos++; return -ParseUnary(); }
                if (text[pos] == '~') { pos++; return ~ParseUnary(); }
            }
            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            if (pos >= text.Length) throw new FormatException();
            if (text[pos] == '(')
            {
                pos++;
                long v = ParseBitwiseOr();
                if (pos < text.Length && text[pos] == ')') pos++;
                return v;
            }

            int start = pos;
            while (pos < text.Length && IsValidDigit(text[pos], radix))
                pos++;

            if (pos == start) throw new FormatException();
            string token = text.Substring(start, pos - start);
            return Convert.ToInt64(token, radix);
        }

        private static bool IsValidDigit(char c, int r)
        {
            if (c >= '0' && c <= '9') return (c - '0') < r;
            char u = char.ToUpperInvariant(c);
            if (u >= 'A' && u <= 'F') return (10 + u - 'A') < r;
            return false;
        }
    }
}
