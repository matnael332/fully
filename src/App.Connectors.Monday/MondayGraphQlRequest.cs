using System.Text.Json;

namespace WorkContinuity.Connectors.Monday;

/// <summary>
/// One GraphQL request body: a fixed document and its variables. Provider values travel only as variables, so no
/// provider text can change the document.
/// </summary>
internal sealed class MondayGraphQlRequest
{
    private readonly List<KeyValuePair<string, object>> _variables = [];

    internal MondayGraphQlRequest(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        Document = document;
    }

    internal string Document { get; }

    internal MondayGraphQlRequest With(string name, string value)
    {
        _variables.Add(new(name, value));
        return this;
    }

    internal MondayGraphQlRequest With(string name, int value)
    {
        _variables.Add(new(name, value));
        return this;
    }

    internal MondayGraphQlRequest With(string name, IReadOnlyList<string> values)
    {
        _variables.Add(new(name, values));
        return this;
    }

    /// <summary>Writes <c>{"query": …, "variables": {…}}</c>; no operation name, since a document holds one operation.</summary>
    internal byte[] ToUtf8Json()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("query", Document);
            writer.WriteStartObject("variables");
            foreach (var (name, value) in _variables)
            {
                switch (value)
                {
                    case string text:
                        writer.WriteString(name, text);
                        break;
                    case int number:
                        writer.WriteNumber(name, number);
                        break;
                    case IReadOnlyList<string> list:
                        writer.WriteStartArray(name);
                        foreach (var item in list)
                        {
                            writer.WriteStringValue(item);
                        }

                        writer.WriteEndArray();
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported GraphQL variable type.");
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
