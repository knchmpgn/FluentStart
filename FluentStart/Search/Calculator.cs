using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FluentStart;

/// <summary>Tiny arithmetic evaluator for the search box ("12*7", "(3+4)^2", "100/8"). Needs at least one binary operator.</summary>
public static class Calculator
{
    private static readonly Regex Allowed = new(@"^[\d\s+\-*/().,%^x×÷]+$", RegexOptions.Compiled);

    public static bool TryEvaluate(string input, out string result)
    {
        result = "";
        string s = (input ?? "").Trim().TrimEnd('=').Trim();
        if (s.Length < 3 || !Allowed.IsMatch(s)) return false;

        s = s.Replace('×', '*').Replace('÷', '/').Replace('x', '*').Replace(" ", "").Replace(',', '.');
        var p = new Parser(s);
        if (!p.TryParse(out double value) || !p.UsedBinaryOperator || double.IsNaN(value) || double.IsInfinity(value)) return false;

        result = value.ToString("G12", CultureInfo.CurrentCulture);
        return true;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        public bool UsedBinaryOperator { get; private set; }

        public Parser(string s) { _s = s; }

        public bool TryParse(out double value)
        {
            try
            {
                value = Expr();
                return _i == _s.Length;
            }
            catch { value = 0; return false; }
        }

        private double Expr()
        {
            double v = Term();
            while (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-'))
            {
                char op = _s[_i++];
                UsedBinaryOperator = true;
                double r = Term();
                v = op == '+' ? v + r : v - r;
            }
            return v;
        }

        private double Term()
        {
            double v = Power();
            while (_i < _s.Length && (_s[_i] == '*' || _s[_i] == '/' || _s[_i] == '%'))
            {
                char op = _s[_i++];
                UsedBinaryOperator = true;
                double r = Power();
                if ((op == '/' || op == '%') && r == 0) throw new DivideByZeroException();
                v = op == '*' ? v * r : op == '/' ? v / r : v % r;
            }
            return v;
        }

        private double Power()
        {
            double b = Unary();
            if (_i < _s.Length && _s[_i] == '^')
            {
                _i++;
                UsedBinaryOperator = true;
                return Math.Pow(b, Power());
            }
            return b;
        }

        private double Unary()
        {
            if (_i < _s.Length && _s[_i] == '-') { _i++; return -Unary(); }
            if (_i < _s.Length && _s[_i] == '+') { _i++; return Unary(); }
            return Primary();
        }

        private double Primary()
        {
            if (_i < _s.Length && _s[_i] == '(')
            {
                _i++;
                double v = Expr();
                if (_i >= _s.Length || _s[_i] != ')') throw new FormatException();
                _i++;
                return v;
            }
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (start == _i) throw new FormatException();
            return double.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
