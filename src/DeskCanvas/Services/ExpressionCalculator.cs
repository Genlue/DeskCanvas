using System;
using System.Globalization;
using System.Text;

namespace DeskCanvas.Services;

/// <summary>
/// The launcher's inline calculator: evaluates the text in the search field as an arithmetic
/// expression and formats the answer for the field's result pill.
/// <para>
/// <b>Why a hand-written parser instead of a scripting engine or <c>DataTable.Compute</c>:</b> the
/// launcher evaluates <i>every keystroke</i> of every search. It must be allocation-light, must
/// never throw, and must answer "is this an expression at all?" without treating a normal app
/// search (<c>chrome</c>, <c>vs code</c>) as a broken formula. A two-function recursive-descent
/// parser over the string does exactly that: no reflection, no compilation, no exceptions on the
/// hot path.
/// </para>
/// <para>
/// <b>Grammar</b> (standard precedence, <c>^</c> is right-associative):
/// </para>
/// <code>
/// expr    := term (('+' | '-') term)*
/// term    := unary (('*' | '/' | '%' | '×' | '÷') unary)*
/// unary   := ('+' | '-') unary | power
/// power   := primary ('^' primary)*
/// primary := number | '(' expr ')' | name ['(' expr ')'] | name
/// </code>
/// <para>
/// Supported names are the constants <c>pi</c>/<c>π</c>/<c>e</c> and the unary functions
/// <c>sqrt abs round floor ceil ln log log2 exp sin cos tan asin acos atan</c>. Trigonometry is in
/// radians, like every other calculator's "advanced" row.
/// </para>
/// <para>
/// The result is deliberately formatted the way a person writes a number — integers without a
/// decimal point, at most ten decimals otherwise, scientific notation only at the extremes — because
/// the pill sits next to the query and must not read as <c>42.00000000000001</c>.
/// </para>
/// </summary>
public static class ExpressionCalculator
{
    /// <summary>Longest text accepted. A search field never holds more; this caps the parser's work.</summary>
    private const int MaxLength = 240;

    /// <summary>Nesting guard for parentheses and function arguments (a typed-in "(((((" must not recurse).</summary>
    private const int MaxDepth = 24;

    /// <summary>
    /// Evaluate <paramref name="text"/> as arithmetic.
    /// <para>
    /// Returns <c>false</c> for anything that is not a self-contained expression: blank text, a
    /// single bare number (that is a search, not a calculation), a syntax error, a division by
    /// zero, or a non-finite result. Callers use the boolean as "show the calculator".
    /// </para>
    /// </summary>
    public static bool TryEvaluate(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var input = text.Trim();
        if (input.Length > MaxLength) return false;
        // A leading digit-less token is a search ("chrome", "vs code"); only the math alphabet is
        // allowed, and at least one operator has to be present for this to be a calculation.
        if (!LooksLikeMath(input)) return false;

        try
        {
            var parser = new Parser(input);
            var result = parser.ParseExpression(0);
            parser.SkipSpaces();
            if (!parser.AtEnd) return false;
            if (!double.IsFinite(result)) return false;
            value = result;
            return true;
        }
        catch
        {
            // Malformed input is the normal case while typing: "12*" is not an error to report.
            return false;
        }
    }

    /// <summary>
    /// The pill's text: the formatted answer, or <c>null</c> when the text is not an expression.
    /// </summary>
    public static string? TryFormat(string? text)
        => TryEvaluate(text, out var value) ? Format(value) : null;

    /// <summary>
    /// True when every character belongs to the math alphabet and at least one operator is present.
    /// This is what keeps ordinary searches out of the calculator.
    /// </summary>
    private static bool LooksLikeMath(string text)
    {
        var operators = 0;
        var digits = 0;
        var parens = 0;
        var letters = false;
        foreach (var c in text)
        {
            if (c is >= '0' and <= '9')
            {
                digits++;
                continue;
            }

            if (c is '+' or '-' or '*' or '/' or '%' or '^' or '×' or '÷' or '−' or '(' or ')' or '.')
            {
                if (c is '(' or ')') parens++;
                else if (c != '.') operators++;
                continue;
            }

            if (char.IsWhiteSpace(c)) continue;

            // Letters are only legal as a function/constant name; everything else (CJK, '?', '@')
            // disqualifies the text immediately.
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                letters = true;
                continue;
            }

            if (c is 'π')
            {
                letters = true;
                continue;
            }

            return false;
        }

        // A bare number is a search ("2024"), a lone word is a search ("chrome"). A calculation is
        // "digits + an operator", or "digits + a function call" ("sqrt(16)").
        return digits > 0 && (operators > 0 || (letters && parens > 0));
    }

    /// <summary>
    /// Format an answer for display: integers plainly, decimals without trailing zeros, and
    /// scientific notation only outside the range a person reads as a number.
    /// </summary>
    public static string Format(double value)
    {
        if (!double.IsFinite(value)) return string.Empty;

        var magnitude = Math.Abs(value);
        if (magnitude >= 1e16 || (magnitude > 0 && magnitude < 1e-9))
            return value.ToString("0.##########E+0", CultureInfo.InvariantCulture);

        // Ten decimals kills the 0.30000000000000004 class of artefact without hiding real digits.
        var rounded = Math.Round(value, 10);
        if (Math.Abs(rounded) < 1e16 && rounded == Math.Truncate(rounded))
            return ((long)rounded).ToString(CultureInfo.InvariantCulture);

        return rounded.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>Recursive-descent parser over one string. Each instance parses exactly one text.</summary>
    private sealed class Parser
    {
        private readonly string text;
        private int position;

        public Parser(string text) => this.text = text;

        public bool AtEnd => position >= text.Length;

        public void SkipSpaces()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }

        public double ParseExpression(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("too deep");

            var left = ParseTerm(depth);
            while (true)
            {
                SkipSpaces();
                if (AtEnd) return left;
                var op = text[position];
                if (op is not ('+' or '-' or '−')) return left;
                position++;
                var right = ParseTerm(depth);
                left = op == '+' ? left + right : left - right;
            }
        }

        private double ParseTerm(int depth)
        {
            var left = ParseUnary(depth);
            while (true)
            {
                SkipSpaces();
                if (AtEnd) return left;
                var op = text[position];
                if (op is not ('*' or '/' or '%' or '×' or '÷')) return left;
                position++;
                var right = ParseUnary(depth);
                left = op switch
                {
                    '×' => left * right,
                    '÷' => Divide(left, right),
                    '*' => left * right,
                    '/' => Divide(left, right),
                    _ => Remainder(left, right),
                };
            }
        }

        private double ParseUnary(int depth)
        {
            SkipSpaces();
            if (AtEnd) throw new FormatException("unexpected end");

            if (text[position] is '+' or '-' or '−')
            {
                var negative = text[position] != '+';
                position++;
                var operand = ParseUnary(depth + 1);
                return negative ? -operand : operand;
            }

            return ParsePower(depth);
        }

        private double ParsePower(int depth)
        {
            var left = ParsePrimary(depth);
            SkipSpaces();
            if (AtEnd || text[position] != '^') return left;

            position++;
            // Right-associative: 2^3^2 = 2^(3^2), which is what a calculator shows.
            var right = ParseUnary(depth + 1);
            return Math.Pow(left, right);
        }

        private double ParsePrimary(int depth)
        {
            SkipSpaces();
            if (AtEnd) throw new FormatException("unexpected end");

            var c = text[position];
            if (c == '(')
            {
                position++;
                var inner = ParseExpression(depth + 1);
                SkipSpaces();
                if (AtEnd || text[position] != ')') throw new FormatException("missing )");
                position++;
                return inner;
            }

            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (char.IsLetter(c) || c == 'π') return ParseName(depth);

            throw new FormatException($"unexpected '{c}'");
        }

        private double ParseNumber()
        {
            var start = position;
            var seenDot = false;
            while (position < text.Length)
            {
                var c = text[position];
                if (char.IsDigit(c))
                {
                    position++;
                    continue;
                }

                if (c == '.' && !seenDot)
                {
                    seenDot = true;
                    position++;
                    continue;
                }

                break;
            }

            var slice = text[start..position];
            if (slice == ".") throw new FormatException("bare dot");
            return double.Parse(slice, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private double ParseName(int depth)
        {
            var start = position;
            while (position < text.Length && (char.IsLetter(text[position]) || text[position] == 'π'))
                position++;

            var name = text[start..position].ToLowerInvariant();
            SkipSpaces();
            var hasArgument = !AtEnd && text[position] == '(';

            if (!hasArgument)
            {
                return name switch
                {
                    "pi" or "π" => Math.PI,
                    "e" => Math.E,
                    _ => throw new FormatException($"unknown constant '{name}'"),
                };
            }

            position++; // '('
            var argument = ParseExpression(depth + 1);
            SkipSpaces();
            if (AtEnd || text[position] != ')') throw new FormatException("missing )");
            position++;

            return name switch
            {
                "sqrt" => Math.Sqrt(argument),
                "cbrt" => Math.Cbrt(argument),
                "abs" => Math.Abs(argument),
                "round" => Math.Round(argument, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(argument),
                "ceil" => Math.Ceiling(argument),
                "ln" => Math.Log(argument),
                "log" => Math.Log10(argument),
                "log2" => Math.Log2(argument),
                "exp" => Math.Exp(argument),
                "sin" => Math.Sin(argument),
                "cos" => Math.Cos(argument),
                "tan" => Math.Tan(argument),
                "asin" => Math.Asin(argument),
                "acos" => Math.Acos(argument),
                "atan" => Math.Atan(argument),
                _ => throw new FormatException($"unknown function '{name}'"),
            };
        }

        private static double Divide(double left, double right)
            => right == 0 ? throw new DivideByZeroException() : left / right;

        private static double Remainder(double left, double right)
            => right == 0 ? throw new DivideByZeroException() : left % right;
    }
}
