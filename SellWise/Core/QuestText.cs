using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SellWise.Core;

/// <summary>
/// Reads how many of an item a quest wants, and whether they must be high quality, from the quest's journal and
/// objective text ("procure ten bone chips", "Deliver a high-quality holy cedar composite bow"). The game data lists
/// which items a quest takes but not how many, so this is the best source there is. English text only; anything it
/// can't read comes back as "not stated".
/// </summary>
public static partial class QuestText
{
    private static readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["single"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
        ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["dozen"] = 12, ["pair"] = 1,
    };

    private static readonly string[] RequestVerbs = ["deliver", "procure", "bring", "gather", "mine", "harvest", "catch", "fish", "prepare", "craft", "fashion", "make", "present", "obtain"];

    /// <summary>
    /// How many of the item the text asks for (null when it doesn't say) and whether it asks for high quality.
    /// <paramref name="onlyItem"/>: this is the quest's only required item, so a bare "deliver twelve lengths"
    /// can be taken to mean it.
    /// </summary>
    public static (int? Count, bool Hq) Requirement(IEnumerable<string> texts, string name, string singular, string plural, bool onlyItem)
    {
        var forms = Forms(name, singular, plural);
        int? count = null;
        var hq = false;
        int? looseCount = null;
        var namedInSingular = false;
        var pluralForm = Normalize(plural);
        var singularDiffers = pluralForm != Normalize(singular);

        foreach (var sentence in texts.SelectMany(Sentences))
        {
            var lower = Normalize(sentence);
            var at = forms.Select(f => (Form: f, Index: IndexOfWord(lower, f))).Where(x => x.Index >= 0).OrderBy(x => x.Index).FirstOrDefault();
            if (at.Form != null)
            {
                var before = lower[..at.Index];
                if (Regex.IsMatch(before, @"high[- ]quality\s*$|high[- ]quality\s+[\w'-]+\s*$|\bhq\s*$")) hq = true;
                if (NumberBefore(before) is { } n && (count == null || n > count)) count = n;
                if (singularDiffers && at.Form != pluralForm && !lower[at.Index..].StartsWith(pluralForm)) namedInSingular = true;
            }
            else if (onlyItem && RequestVerbs.Any(v => Regex.IsMatch(lower, $@"\b{v}\b")))
            {
                // "Deliver twelve lengths to him": the item's measure word, or a bare number after the verb.
                var m = Regex.Match(lower, $@"\b(?:{string.Join("|", RequestVerbs)})\b\s+(?:(?:him|her|them|you)\s+)?([\w-]+)\s+([\w'-]+)");
                if (m.Success && Numbers.TryGetValue(m.Groups[1].Value, out var n2) && n2 > 1) looseCount = Math.Max(looseCount ?? 0, n2);
                else if (m.Success && int.TryParse(m.Groups[1].Value, out var d) && d > 1) looseCount = Math.Max(looseCount ?? 0, d);
                if (Regex.IsMatch(lower, @"high[- ]quality")) hq = true;
            }
        }

        // "his desired bow": named in the singular with no number means one.
        return (count > 1 ? count : looseCount ?? count ?? (namedInSingular ? 1 : null), hq);
    }

    /// <summary>The ways the text may name the item: full name, singular, plural, and the measure word ("lengths").</summary>
    private static List<string> Forms(string name, string singular, string plural)
    {
        var forms = new List<string>();
        foreach (var f in new[] { plural, singular, name })
        {
            var n = Normalize(f);
            if (n.Length > 0 && !forms.Contains(n)) forms.Add(n);
        }
        // "lengths of ash lumber" → also "ash lumber" (the text often drops the measure word)
        foreach (var f in forms.ToList())
        {
            var of = f.IndexOf(" of ", StringComparison.Ordinal);
            if (of > 0 && !forms.Contains(f[(of + 4)..])) forms.Add(f[(of + 4)..]);
        }
        return forms.OrderByDescending(f => f.Length).ToList();
    }

    private static int? NumberBefore(string before)
    {
        var words = Regex.Matches(before, @"[\w'-]+").Select(m => m.Value).ToList();
        // Look back a few words: "ten bone chips", "a pair of boarskin gloves", "a single materia-enhanced iron lance".
        for (var i = words.Count - 1; i >= Math.Max(0, words.Count - 4); i--)
        {
            if (int.TryParse(words[i], out var d)) return d;
            if (Numbers.TryGetValue(words[i], out var n))
            {
                if (words[i] == "dozen" && i > 0 && words[i - 1] is "a" or "one") return 12;
                return n;
            }
        }
        return null;
    }

    private static int IndexOfWord(string text, string phrase)
    {
        var m = Regex.Match(text, $@"(?<![\w]){Regex.Escape(phrase)}(?![\w])");
        return m.Success ? m.Index : -1;
    }

    private static IEnumerable<string> Sentences(string text)
        => SentenceSplit().Split(text).Where(s => s.Trim().Length > 0);

    private static string Normalize(string s) => Spaces().Replace(s.Replace('’', '\'').ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"(?<=[.!?;])\s+|\n+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
