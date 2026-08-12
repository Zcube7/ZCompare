using System.Globalization;
using System.Numerics;

namespace ZCompare.Core;

internal readonly record struct ExactNumber(bool IsNegative, BigInteger Digits, int Scale)
{
    public static bool TryParse(string? text, out ExactNumber value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        var negative = false;
        if (span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        var exponentIndex = span.IndexOfAny('e', 'E');
        ReadOnlySpan<char> exponentSpan = default;
        if (exponentIndex >= 0)
        {
            exponentSpan = span[(exponentIndex + 1)..];
            span = span[..exponentIndex];
        }

        var decimalIndex = span.IndexOf('.');
        var fractionalDigits = decimalIndex >= 0 ? span.Length - decimalIndex - 1 : 0;
        var digitsText = decimalIndex >= 0
            ? string.Concat(span[..decimalIndex], span[(decimalIndex + 1)..])
            : span.ToString();

        if (digitsText.Length == 0 || digitsText.Any(static character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        var exponent = 0;
        if (!exponentSpan.IsEmpty &&
            !int.TryParse(exponentSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            return false;
        }

        if (!BigInteger.TryParse(digitsText, NumberStyles.None, CultureInfo.InvariantCulture, out var digits))
        {
            return false;
        }

        int scale;
        try
        {
            scale = checked(fractionalDigits - exponent);
        }
        catch (OverflowException)
        {
            return false;
        }

        value = Normalize(new ExactNumber(negative, digits, scale));
        return true;
    }

    public ExactNumber AddInteger(int amount)
    {
        var multiplier = Scale > 0 ? BigInteger.Pow(10, Scale) : BigInteger.One;
        var signedDigits = IsNegative ? -Digits : Digits;
        if (Scale < 0)
        {
            signedDigits *= BigInteger.Pow(10, -Scale);
        }

        var sum = signedDigits + (new BigInteger(amount) * multiplier);
        return Normalize(new ExactNumber(sum.Sign < 0, BigInteger.Abs(sum), Math.Max(Scale, 0)));
    }

    public string ToCanonicalString() =>
        Digits.IsZero ? "0:0" : $"{(IsNegative ? '-' : '+')}:{Digits}:{Scale}";

    public bool TryToDouble(out double value)
    {
        var text = ToPlainString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public string ToPlainString()
    {
        if (Digits.IsZero)
        {
            return "0";
        }

        var digits = Digits.ToString(CultureInfo.InvariantCulture);
        string result;
        if (Scale <= 0)
        {
            result = digits + new string('0', -Scale);
        }
        else if (Scale >= digits.Length)
        {
            result = "0." + new string('0', Scale - digits.Length) + digits;
        }
        else
        {
            result = digits.Insert(digits.Length - Scale, ".");
        }

        return IsNegative ? "-" + result : result;
    }

    private static ExactNumber Normalize(ExactNumber number)
    {
        if (number.Digits.IsZero)
        {
            return new ExactNumber(false, BigInteger.Zero, 0);
        }

        var digits = number.Digits;
        var scale = number.Scale;
        while (digits % 10 == 0)
        {
            digits /= 10;
            scale--;
        }

        return new ExactNumber(number.IsNegative, digits, scale);
    }
}
