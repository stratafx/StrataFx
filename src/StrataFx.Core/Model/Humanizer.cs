using System.Text;

namespace StrataFx.Model;

/// <summary>Turns identifiers into readable captions: "OrderDate" → "Order Date", "Customer" → "Customers".</summary>
public static class Humanizer
{
    public static string SplitWords(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return identifier;
        var sb = new StringBuilder(identifier.Length + 8);
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (c == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c))
            {
                var prev = identifier[i - 1];
                var nextIsLower = i + 1 < identifier.Length && char.IsLower(identifier[i + 1]);
                // "OrderDate" → "Order Date", "VIPLevel" → "VIP Level", "ID" stays "ID".
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Pluralize(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;
        if (word.EndsWith("y", StringComparison.OrdinalIgnoreCase) && word.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(word[^2])))
            return word[..^1] + "ies";
        if (word.EndsWith("s") || word.EndsWith("x") || word.EndsWith("ch") || word.EndsWith("sh"))
            return word + "es";
        return word + "s";
    }
}
