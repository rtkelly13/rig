using System.Text;

namespace Rig.Core.Text;

/// <summary>Writes JSON two-space indented with non-ASCII kept, in the order members are written.</summary>
internal sealed class IndentedJsonWriter
{
    private readonly StringBuilder text = new();
    private readonly Stack<int> counts = new();
    private bool afterKey;

    /// <summary>Writes an object whose members <paramref name="members"/> writes.</summary>
    public IndentedJsonWriter Object(Action members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return Container('{', '}', members);
    }

    /// <summary>Writes an array of <paramref name="items"/>, each written by <paramref name="write"/>.</summary>
    public IndentedJsonWriter Array<T>(IEnumerable<T> items, Action<T> write)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(write);
        return Container('[', ']', () =>
        {
            foreach (var item in items)
            {
                write(item);
            }
        });
    }

    /// <summary>Writes an object member's key; its value comes next.</summary>
    public IndentedJsonWriter Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Separate();
        Quote(key);
        text.Append(": ");
        afterKey = true;
        return this;
    }

    /// <summary>Writes a string.</summary>
    public IndentedJsonWriter String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Separate();
        Quote(value);
        return this;
    }

    /// <summary>Writes <c>true</c> or <c>false</c>.</summary>
    public IndentedJsonWriter Bool(bool value)
    {
        Separate();
        text.Append(value ? "true" : "false");
        return this;
    }

    /// <summary>Writes <c>null</c>.</summary>
    public IndentedJsonWriter Null()
    {
        Separate();
        text.Append("null");
        return this;
    }

    /// <summary>Writes an integer.</summary>
    public IndentedJsonWriter Number(long value)
    {
        Separate();
        text.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>Writes an unsigned integer (an inode can exceed <see cref="long.MaxValue"/>).</summary>
    public IndentedJsonWriter Number(ulong value)
    {
        Separate();
        text.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return this;
    }

    /// <inheritdoc/>
    public override string ToString() => text.ToString();

    private IndentedJsonWriter Container(char open, char close, Action body)
    {
        Separate();
        text.Append(open);
        counts.Push(0);
        body();
        var count = counts.Pop();
        if (count > 0)
        {
            Newline(counts.Count);
        }

        text.Append(close);
        return this;
    }

    // Before a value: nothing after a key; otherwise a comma after an earlier sibling, then a newline and indent.
    private void Separate()
    {
        if (afterKey)
        {
            afterKey = false;
            return;
        }

        if (counts.Count == 0)
        {
            return;
        }

        var count = counts.Pop();
        if (count > 0)
        {
            text.Append(',');
        }

        counts.Push(count + 1);
        Newline(counts.Count);
    }

    private void Newline(int depth) => text.Append('\n').Append(' ', depth * 2);

    private void Quote(string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case < ' ': text.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: text.Append(c); break;
            }
        }

        text.Append('"');
    }
}
