namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// Decides whether a GraphQL document is a single read query, the only kind of document Gate 3 may send to monday.
/// </summary>
/// <remarks>
/// <para>
/// Gate 3 reads and ingests only (Premium architecture §20 constraint 5). Every operation of this connector is a
/// declared read, but the client checks each document again before sending it, as defence in depth: a document is
/// sent only when it holds exactly one operation definition and that definition is a <c>query</c>. A GraphQL document
/// may hold several operations and let the request choose one, so a document that starts with a query but also defines
/// a mutation or a subscription is refused, and so is anything else at the top level, such as a fragment.
/// </para>
/// <para>
/// Only the top level is inspected, with comments and string literals skipped, because that is where GraphQL declares
/// an operation's type; field names inside a selection set cannot change it.
/// </para>
/// </remarks>
public static class MondayQueryGuard
{
    /// <summary>The longest document the connector sends.</summary>
    public const int MaxDocumentLength = 10_000;

    /// <summary>Whether the document holds exactly one operation definition, and it is a query.</summary>
    public static bool IsSingleReadQuery(string? document)
    {
        if (string.IsNullOrWhiteSpace(document) || document.Length > MaxDocumentLength)
        {
            return false;
        }

        var braceDepth = 0;
        var parenDepth = 0;
        var operations = 0;
        var topLevelWords = new List<string>();

        for (var index = 0; index < document.Length; index++)
        {
            var character = document[index];

            if (character == '#')
            {
                while (index < document.Length && document[index] is not '\n' and not '\r')
                {
                    index++;
                }

                continue;
            }

            if (character == '"')
            {
                if (!TrySkipString(document, ref index))
                {
                    return false;
                }

                continue;
            }

            switch (character)
            {
                case '{':
                    if (braceDepth == 0 && parenDepth == 0)
                    {
                        operations++;
                    }

                    braceDepth++;
                    continue;
                case '}':
                    braceDepth--;
                    if (braceDepth < 0)
                    {
                        return false;
                    }

                    continue;
                case '(' when braceDepth == 0:
                    parenDepth++;
                    continue;
                case ')' when braceDepth == 0:
                    parenDepth--;
                    if (parenDepth < 0)
                    {
                        return false;
                    }

                    continue;
            }

            if (braceDepth == 0 && parenDepth == 0 && (char.IsAsciiLetter(character) || character == '_'))
            {
                var start = index;
                while (index + 1 < document.Length && (char.IsAsciiLetterOrDigit(document[index + 1]) || document[index + 1] == '_'))
                {
                    index++;
                }

                topLevelWords.Add(document[start..(index + 1)]);
            }
            else if (braceDepth == 0 && parenDepth == 0 && !char.IsWhiteSpace(character) && character != ',')
            {
                // A directive, a fragment spread or any other punctuation at the top level is not part of a plain query.
                return false;
            }
        }

        return braceDepth == 0
            && parenDepth == 0
            && operations == 1
            && topLevelWords.Count is 1 or 2
            && topLevelWords[0] == "query";
    }

    private static bool TrySkipString(string document, ref int index)
    {
        var block = index + 2 < document.Length && document[index + 1] == '"' && document[index + 2] == '"';
        if (block)
        {
            var end = document.IndexOf("\"\"\"", index + 3, StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            index = end + 2;
            return true;
        }

        for (index++; index < document.Length; index++)
        {
            switch (document[index])
            {
                case '\\':
                    index++;
                    break;
                case '"':
                    return true;
                case '\n' or '\r':
                    return false;
            }
        }

        return false;
    }
}
