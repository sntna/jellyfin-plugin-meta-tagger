using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.MetaTagger;

public static class TagNormalizer
{
    public static string NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var folded = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);
        var pendingSeparator = false;

        foreach (var character in folded)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);
            if (char.IsLetterOrDigit(lower))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(lower);
                pendingSeparator = false;
                continue;
            }

            if (lower == '+')
            {
                AppendWord(builder, ref pendingSeparator, "plus");
                continue;
            }

            if (lower is '&')
            {
                AppendWord(builder, ref pendingSeparator, "and");
                continue;
            }

            if (char.IsWhiteSpace(lower) || lower is '-' or '_' or '/' or ':' or '.')
            {
                pendingSeparator = builder.Length > 0;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static void AppendWord(StringBuilder builder, ref bool pendingSeparator, string word)
    {
        if (builder.Length > 0)
        {
            builder.Append('-');
        }

        builder.Append(word);
        pendingSeparator = true;
    }
}
