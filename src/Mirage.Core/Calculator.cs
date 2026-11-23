using System.Globalization;

namespace Mirage.Core;

/// <summary>
/// Small, safe expression evaluator: + - * / % ^ ! ( ), functions and constants.
/// No reflection, no code execution.
/// </summary>
public static class Calculator
{
    static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqrt","cbrt","abs","sin","cos","tan","asin","acos","atan","ln","log","log2","exp",
        "round","floor","ceil","min","max","pow","deg","rad"
    };

    public static bool LooksLikeExpression(string input)
    {
        var s = input.Trim();
        if (s.StartsWith('=')) return s.Length > 1;
        if (s.Length < 2) return false;
        bool digit = s.Any(char.IsDigit);
        bool op = s.IndexOfAny(new[] { '+', '-', '*', '/', '^', '%', '(', '!', '×', '÷' }) >= 0;
        bool fn = Functions.Any(f => s.StartsWith(f + "(", StringComparison.OrdinalIgnoreCase));
        if (!(digit && op) && !fn) return false;
        // Avoid matching dates/phone numbers like 2024-01-02 or paths
        if (s.Contains('\\') || s.Contains(':')) return false;
        return TryEvaluate(s, out _, out _);
    }

    public static bool TryEvaluate(string input, out double value, out string? error)
    {
        try
        {
            var p = new Parser(input.Trim().TrimStart('='));
            value = p.ParseExpression();
            p.SkipWs();
            if (!p.AtEnd) throw new FormatException($"Unexpected '{p.Current}'");
            if (double.IsNaN(value)) throw new FormatException("Not a number");
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or DivideByZeroException or OverflowException or ArgumentException)
        {
            value = double.NaN;
            error = ex.Message;
            return false;
        }
    }

    public static string Format(double v, bool grouping = false)
    {
        if (double.IsInfinity(v)) return v > 0 ? "∞" : "-∞";
        if (Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0))
            return v.ToString("G12", CultureInfo.InvariantCulture);
        var rounded = Math.Round(v, 10);
        return rounded.ToString(grouping ? "#,0.##########" : "0.##########", CultureInfo.InvariantCulture);
    }

    sealed class Parser
    {
        readonly string _s;
        int _i;
        public Parser(string s) => _s = s.Replace('×', '*').Replace('÷', '/');
        public bool AtEnd => _i >= _s.Length;
        public char Current => _s[_i];

        public void SkipWs() { while (!AtEnd && char.IsWhiteSpace(Current)) _i++; }

        bool Eat(char c)
        {
            SkipWs();
            if (!AtEnd && Current == c) { _i++; return true; }
            return false;
        }

        public double ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                if (Eat('+')) left += ParseTerm();
                else if (Eat('-')) left -= ParseTerm();
                else return left;
            }
        }

        double ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Eat('*')) left *= ParseUnary();
                else if (Eat('/'))
                {
                    var r = ParseUnary();
                    if (r == 0) throw new DivideByZeroException("Division by zero");
                    left /= r;
                }
                else if (Eat('%'))
                {
                    var r = ParseUnary();
                    if (r == 0) throw new DivideByZeroException("Division by zero");
                    left %= r;
                }
                else
                {
                    // implicit multiplication: 2(3+4), 2pi
                    SkipWs();
                    if (!AtEnd && (Current == '(' || char.IsLetter(Current)))
                        left *= ParseUnary();
                    else return left;
                }
            }
        }

        double ParseUnary()
        {
            if (Eat('-')) return -ParseUnary();
            if (Eat('+')) return ParseUnary();
            return ParsePower();
        }

        double ParsePower()
        {
            var b = ParsePostfix();
            if (Eat('^')) return Math.Pow(b, ParseUnary()); // right associative
            return b;
        }

        double ParsePostfix()
        {
            var v = ParsePrimary();
            while (Eat('!'))
            {
                if (v < 0 || v > 170 || Math.Abs(v - Math.Round(v)) > 1e-9)
                    throw new ArgumentException("Factorial needs a whole number 0-170");
                double f = 1;
                for (int k = 2; k <= (int)Math.Round(v); k++) f *= k;
                v = f;
            }
            return v;
        }

        double ParsePrimary()
        {
            SkipWs();
            if (AtEnd) throw new FormatException("Incomplete expression");
            if (Eat('('))
            {
                var v = ParseExpression();
                if (!Eat(')')) throw new FormatException("Missing )");
                return v;
            }
            if (char.IsDigit(Current) || Current == '.') return ParseNumber();
            if (char.IsLetter(Current))
            {
                int start = _i;
                while (!AtEnd && char.IsLetterOrDigit(Current)) _i++;
                var name = _s[start.._i].ToLowerInvariant();
                switch (name)
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                    case "tau": return Math.Tau;
                }
                if (!Functions.Contains(name)) throw new FormatException($"Unknown name '{name}'");
                if (!Eat('(')) throw new FormatException($"{name} needs (");
                var args = new List<double> { ParseExpression() };
                while (Eat(',')) args.Add(ParseExpression());
                if (!Eat(')')) throw new FormatException("Missing )");
                return Call(name, args);
            }
            throw new FormatException($"Unexpected '{Current}'");
        }

        double ParseNumber()
        {
            int start = _i;
            while (!AtEnd && (char.IsDigit(Current) || Current == '.' || Current == '_')) _i++;
            if (!AtEnd && (Current == 'e' || Current == 'E') && _i + 1 < _s.Length &&
                (char.IsDigit(_s[_i + 1]) || ((_s[_i + 1] == '-' || _s[_i + 1] == '+') && _i + 2 < _s.Length && char.IsDigit(_s[_i + 2]))))
            {
                _i += 2;
                while (!AtEnd && char.IsDigit(Current)) _i++;
            }
            var text = _s[start.._i].Replace("_", "");
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"Bad number '{text}'");
            return v;
        }

        static double Call(string name, List<double> a)
        {
            double One() => a.Count == 1 ? a[0] : throw new ArgumentException($"{name} takes 1 value");
            return name switch
            {
                "sqrt" => One() < 0 ? throw new ArgumentException("sqrt of negative") : Math.Sqrt(a[0]),
                "cbrt" => Math.Cbrt(One()),
                "abs" => Math.Abs(One()),
                "sin" => Math.Sin(One()),
                "cos" => Math.Cos(One()),
                "tan" => Math.Tan(One()),
                "asin" => Math.Asin(One()),
                "acos" => Math.Acos(One()),
                "atan" => Math.Atan(One()),
                "ln" => Math.Log(One()),
                "log" => a.Count == 2 ? Math.Log(a[0], a[1]) : Math.Log10(One()),
                "log2" => Math.Log2(One()),
                "exp" => Math.Exp(One()),
                "round" => a.Count == 2 ? Math.Round(a[0], (int)a[1]) : Math.Round(One()),
                "floor" => Math.Floor(One()),
                "ceil" => Math.Ceiling(One()),
                "min" => a.Count > 0 ? a.Min() : throw new ArgumentException("min needs values"),
                "max" => a.Count > 0 ? a.Max() : throw new ArgumentException("max needs values"),
                "pow" => a.Count == 2 ? Math.Pow(a[0], a[1]) : throw new ArgumentException("pow takes 2 values"),
                "deg" => One() * 180 / Math.PI,
                "rad" => One() * Math.PI / 180,
                _ => throw new FormatException($"Unknown function {name}")
            };
        }
    }
}
