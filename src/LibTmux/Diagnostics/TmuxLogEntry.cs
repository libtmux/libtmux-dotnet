using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;

namespace LibTmux;

/// <summary>One diagnostic the library reports about how it drove tmux.</summary>
/// <remarks>
/// The library references no logging package. A caller who wants these records
/// sets <see cref="ServerConnectionOptions.LogSink" />, and a logging framework
/// bridges by forwarding them: <see cref="Template" /> and <see cref="Fields" />
/// keep the structure that a log aggregator groups and filters on.
/// </remarks>
public sealed class TmuxLogEntry
{
    internal TmuxLogEntry(
        EventLevel level,
        int eventId,
        string template,
        IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        Level = level;
        EventId = eventId;
        Template = template;
        Fields = fields;
    }

    /// <summary>Gets how serious the record is.</summary>
    public EventLevel Level { get; }

    /// <summary>Gets the stable number that identifies the kind of record.</summary>
    public int EventId { get; }

    /// <summary>Gets the message with its <c>{Name}</c> placeholders unexpanded.</summary>
    public string Template { get; }

    /// <summary>Gets the named values the placeholders of <see cref="Template" /> stand for.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Fields { get; }

    /// <summary>Gets <see cref="Template" /> with each placeholder replaced by its field's value.</summary>
    public string Message
    {
        get
        {
            StringBuilder text = new(Template.Length + 32);
            int index = 0;
            while (index < Template.Length)
            {
                int open = Template.IndexOf('{', index);
                int close = open < 0 ? -1 : Template.IndexOf('}', open + 1);
                if (close < 0)
                {
                    text.Append(Template, index, Template.Length - index);
                    break;
                }

                text.Append(Template, index, open - index);
                text.Append(ValueOf(Template.Substring(open + 1, close - open - 1)));
                index = close + 1;
            }

            return text.ToString();
        }
    }

    private string ValueOf(string name)
    {
        foreach (KeyValuePair<string, object?> field in Fields)
        {
            if (string.Equals(field.Key, name, StringComparison.Ordinal))
            {
                return Convert.ToString(field.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return "{" + name + "}";
    }
}
