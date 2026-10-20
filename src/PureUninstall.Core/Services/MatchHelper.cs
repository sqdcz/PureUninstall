using System.Text.RegularExpressions;

namespace PureUninstall.Core.Services;

/// <summary>
/// Name matching for residual scanning. Short or generic tokens (e.g. "QQ")
/// only match exactly; longer tokens match by containment.
/// </summary>
internal static class MatchHelper
{
    public static List<string> Tokens(string? displayName)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(displayName)) return tokens;

        var name = displayName.Trim();
        // strip trailing "(x64)" / "【中文】" style suffixes and version numbers
        var cleaned = Regex.Replace(name, @"\s*[\(\[（【][^\)\]）】]*[\)\]）】]\s*$", "");
        cleaned = Regex.Replace(cleaned, @"\s+[vV]?\d+(\.\d+)+\s*$", "");
        cleaned = cleaned.Trim();

        Add(cleaned);
        var first = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is not null) Add(first);

        return tokens;

        void Add(string t)
        {
            t = t.Trim();
            if (t.Length >= 2 && !tokens.Contains(t, StringComparer.OrdinalIgnoreCase))
                tokens.Add(t);
        }
    }

    public static bool IsPotentialMatch(string? candidate, List<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(candidate) || tokens.Count == 0) return false;
        foreach (var token in tokens)
        {
            if (candidate.Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
            if (token.Length >= 4 && candidate.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
