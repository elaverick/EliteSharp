using System.Text.RegularExpressions;

namespace EliteSharp.Data;

/// <summary>
/// The rule for the language identifiers that name the game's text files
/// (such as "en" in en-strings.yml): a language code of two or three letters,
/// optionally followed by subtags of two to eight letters or digits, each
/// after a hyphen (such as "en-GB"). So an identifier can't contain a path
/// separator, a full stop or anything else that would make its file name
/// refer to some other file.
/// </summary>
public static class LanguageIdentifier
{
    private static readonly Regex Pattern = new(@"\A[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*\z", RegexOptions.CultureInvariant);

    /// <summary>Whether the text is a valid language identifier.</summary>
    public static bool IsValid(string language) => Pattern.IsMatch(language);

    /// <summary>Check that the text is a valid language identifier.</summary>
    /// <exception cref="ArgumentException">It isn't (the message says why).</exception>
    public static void Check(string language)
    {
        if (!IsValid(language))
        {
            throw new ArgumentException($"'{language}' isn't a language identifier (such as 'en' or 'en-GB')", nameof(language));
        }
    }
}
