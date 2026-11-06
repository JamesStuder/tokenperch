using System.Text;

namespace TokenPerch.Core;

/// <summary>Splits a command line into words, honouring '…' and "…" quotes.</summary>
public static class ShellWords
{
    /// <summary>Backslash escapes count on Linux only; on Windows a backslash is a path separator.</summary>
    public static List<string> Split(string line) => Split(line, backslashEscapes: !OperatingSystem.IsWindows());

    public static List<string> Split(string line, bool backslashEscapes)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && backslashEscapes && i + 1 < line.Length) word.Append(line[++i]);
                else word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
            }
            else if (c == '\\' && backslashEscapes && i + 1 < line.Length)
            {
                word.Append(line[++i]);
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }
        if (inWord) words.Add(word.ToString());
        return words;
    }
}
