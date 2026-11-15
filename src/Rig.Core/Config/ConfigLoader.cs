using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Rig.Core.Config;

/// <summary>Why a configuration file could not be loaded.</summary>
public enum ConfigLoadError
{
    /// <summary>The file was absent or could not be read.</summary>
    MissingFile,

    /// <summary>The text is not JSON at all.</summary>
    InvalidJson,

    /// <summary>The JSON is valid but does not match the schema.</summary>
    Schema,
}

/// <summary>
/// A configuration file that could not be loaded. It carries the source and, where the reader knows it, the line and
/// column of the offending token. Being a <see cref="JsonException"/>, callers that already handled malformed JSON
/// keep working.
/// </summary>
public sealed class ConfigLoadException : JsonException
{
    /// <summary>A load failure with no further detail, kept for exception conventions.</summary>
    public ConfigLoadException()
        : this(ConfigLoadError.InvalidJson, "The configuration could not be loaded.")
    {
    }

    /// <summary>A load failure described by <paramref name="message"/>.</summary>
    public ConfigLoadException(string message)
        : this(ConfigLoadError.InvalidJson, message)
    {
    }

    /// <summary>A load failure described by <paramref name="message"/> and caused by <paramref name="innerException"/>.</summary>
    public ConfigLoadException(string message, Exception innerException)
        : this(ConfigLoadError.InvalidJson, message, innerException: innerException)
    {
    }

    /// <summary>A load failure of <paramref name="kind"/> described by <paramref name="message"/>.</summary>
    public ConfigLoadException(
        ConfigLoadError kind,
        string message,
        string? filePath = null,
        long? line = null,
        long? column = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        FilePath = filePath;
        Line = line;
        Column = column;
    }

    /// <summary>What went wrong.</summary>
    public ConfigLoadError Kind { get; }

    /// <summary>The file that was being read, when the failure came from one.</summary>
    public string? FilePath { get; }

    /// <summary>The 0-based line of the offending token, when known.</summary>
    public long? Line { get; }

    /// <summary>The 0-based byte position of the offending token on <see cref="Line"/>, when known.</summary>
    public long? Column { get; }
}

/// <summary>
/// The one way rig reads a declared configuration file: read it, parse it with a source-generated
/// <see cref="JsonTypeInfo{T}"/>, and report a malformed file with its source, line and column instead of a bare
/// serializer message.
/// </summary>
public static class ConfigLoader
{
    /// <summary>
    /// <paramref name="filePath"/> decoded as <typeparamref name="T"/>. Throws <see cref="ConfigLoadException"/> when the
    /// file is missing, is not JSON, or does not match the schema.
    /// </summary>
    public static T Load<T>(string filePath, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(typeInfo);

        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ConfigLoadException(ConfigLoadError.MissingFile, $"{filePath}: the file does not exist.", filePath, innerException: e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigLoadException(ConfigLoadError.MissingFile, $"{filePath}: the file cannot be read: {e.Message}", filePath, innerException: e);
        }

        return Parse(text, filePath, typeInfo);
    }

    /// <summary>
    /// <paramref name="filePath"/> decoded as <typeparamref name="T"/>, or false with the reasons in
    /// <paramref name="errors"/>. Never throws for a malformed or missing file.
    /// </summary>
    public static bool TryLoad<T>(string filePath, JsonTypeInfo<T> typeInfo, out T? result, out IReadOnlyList<string> errors)
        where T : class
    {
        try
        {
            result = Load(filePath, typeInfo);
            errors = [];
            return true;
        }
        catch (ConfigLoadException e)
        {
            result = null;
            errors = [e.Message];
            return false;
        }
    }

    /// <summary>
    /// <paramref name="json"/> decoded as <typeparamref name="T"/>; <paramref name="source"/> names it in errors. Throws
    /// <see cref="ConfigLoadException"/> when the text is not JSON or does not match the schema.
    /// </summary>
    public static T Parse<T>(string json, string source, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentNullException.ThrowIfNull(typeInfo);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw Failure(ConfigLoadError.InvalidJson, source, e);
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Null)
            {
                throw new ConfigLoadException(ConfigLoadError.Schema, $"{source}: the document is empty.", source);
            }

            T? result;
            try
            {
                result = document.RootElement.Deserialize(typeInfo);
            }
            catch (JsonException e)
            {
                throw Failure(ConfigLoadError.Schema, source, e);
            }

            return result ?? throw new ConfigLoadException(ConfigLoadError.Schema, $"{source}: the document is empty.", source);
        }
    }

    private static ConfigLoadException Failure(ConfigLoadError kind, string source, JsonException cause)
    {
        var detail = Detail(cause.Message);
        return cause.LineNumber is { } line
            ? new ConfigLoadException(kind, $"{source}:{line + 1}:{(cause.BytePositionInLine ?? 0) + 1}: {detail}", source, line, cause.BytePositionInLine, cause)
            : new ConfigLoadException(kind, $"{source}: {detail}", source, innerException: cause);
    }

    // The serializer already carries the position in dedicated fields; drop the copy it appends to the message.
    private static string Detail(string message)
    {
        var cut = message.IndexOf(" | LineNumber:", StringComparison.Ordinal);
        if (cut < 0)
        {
            cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        }

        return cut < 0 ? message : message[..cut];
    }
}
