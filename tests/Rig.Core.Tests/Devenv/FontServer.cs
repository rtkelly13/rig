using System.IO.Compression;
using System.Net;
using Rig.Core.Devenv;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Devenv;

/// <summary>Serves Nerd Font release zips from memory, so no test reaches GitHub.</summary>
internal sealed class FontServer : IDisposable
{
    private readonly StubHandler _handler;
    private readonly HttpClient _http;

    public FontServer()
    {
        _handler = new StubHandler(request => Task.FromResult(Answer(request)));
        _http = new HttpClient(_handler, disposeHandler: false);
    }

    /// <summary>Zip bodies by URL; a URL not here answers 404.</summary>
    public Dictionary<string, byte[]> Zips { get; } = new(StringComparer.Ordinal);

    /// <summary>Every URL asked for, in order.</summary>
    public List<string> Asked { get; } = [];

    public NerdFonts Fonts => new(_http);

    /// <summary>Serves <paramref name="font"/>'s zip holding an empty file at each of <paramref name="entries"/>.</summary>
    public FontServer With(NerdFont font, params string[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(entry);
            }
        }

        Zips[NerdFonts.Url(font).ToString()] = buffer.ToArray();
        return this;
    }

    /// <summary>Serves every family with one regular <c>.ttf</c> named after it.</summary>
    public FontServer WithAll()
    {
        foreach (var font in NerdFonts.All)
        {
            _ = With(font, $"{font.Name.Replace(" ", string.Empty, StringComparison.Ordinal)}-Regular.ttf");
        }

        return this;
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var url = request.RequestUri!.ToString();
        Asked.Add(url);
        Assert.Equal(NerdFonts.UserAgent, request.Headers.UserAgent.ToString());
        return Zips.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
