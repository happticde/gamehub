namespace GameHub.Core.Services;

/// <summary>Validates retail GTIN input and converts it to a canonical 14-digit string.</summary>
public static class GtinNormalizer
{
    private const int CanonicalLength = 14;

    /// <summary>
    /// Normalizes GTIN-12, GTIN-13, or GTIN-14 input. ASCII spaces and hyphens are accepted only
    /// as single visual separators between digit groups. Eight-digit input is rejected because an
    /// untrusted raw value cannot safely distinguish EAN-8 from compressed UPC-E.
    /// </summary>
    public static GtinNormalizationResult Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return GtinNormalizationResult.Invalid("A GTIN is required.");
        }

        var previousWasSeparator = false;
        var digits = new char[input.Length];
        var digitCount = 0;

        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (character is >= '0' and <= '9')
            {
                digits[digitCount++] = character;
                previousWasSeparator = false;
                continue;
            }

            if (character is ' ' or '-')
            {
                if (digitCount == 0 || previousWasSeparator || index == input.Length - 1)
                {
                    return GtinNormalizationResult.Invalid(
                        "Spaces and hyphens may appear only as single separators between digit groups.");
                }

                previousWasSeparator = true;
                continue;
            }

            return GtinNormalizationResult.Invalid(
                "A GTIN may contain only ASCII digits, spaces, and hyphens.");
        }

        if (digitCount == 8)
        {
            return GtinNormalizationResult.Invalid(
                "Eight-digit input is not accepted because UPC-E cannot be safely interpreted as GTIN-8 without trusted symbology information.");
        }

        if (digitCount is not (12 or 13 or 14))
        {
            return GtinNormalizationResult.Invalid(
                "A GTIN must contain 12, 13, or 14 digits.");
        }

        var value = new string(digits, 0, digitCount);
        if (!HasValidCheckDigit(value))
        {
            return GtinNormalizationResult.Invalid("The GTIN check digit is invalid.");
        }

        return GtinNormalizationResult.Valid(value.PadLeft(CanonicalLength, '0'));
    }

    private static bool HasValidCheckDigit(string value)
    {
        var sum = 0;
        for (var offsetFromRight = 0; offsetFromRight < value.Length; offsetFromRight++)
        {
            var digit = value[value.Length - 1 - offsetFromRight] - '0';
            sum += digit * (offsetFromRight % 2 == 0 ? 1 : 3);
        }

        return sum % 10 == 0;
    }
}

/// <summary>Result of validating and normalizing one GTIN input.</summary>
public sealed record GtinNormalizationResult(bool IsValid, string? NormalizedGtin, string? Error)
{
    /// <summary>Creates a successful normalization result.</summary>
    public static GtinNormalizationResult Valid(string normalizedGtin) =>
        new(true, normalizedGtin, null);

    /// <summary>Creates a safe validation failure that does not echo the submitted identifier.</summary>
    public static GtinNormalizationResult Invalid(string error) =>
        new(false, null, error);
}
