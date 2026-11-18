using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Rig.Web.BrowserTests;

/// <summary>The estate graph's lines: they turn in the gutters between boxes and never cross one.</summary>
public sealed class EstateLinesBrowserTests(BrowserFixture fixture) : BrowserTest(fixture)
{
    // What each drawn line measures about itself, in screen pixels.
    private const string Measure = """
        () => {
            const boxes = new Map([...document.querySelectorAll('.estate-node')].map(
                (node) => [node.dataset.node, node.getBoundingClientRect()]));
            return [...document.querySelectorAll('.estate-edges path[data-from]')].map((path) => {
                const matrix = path.getScreenCTM();
                const screen = (x, y) => new DOMPoint(x, y).matrixTransform(matrix);
                const corners = [...path.getAttribute('d').matchAll(/[ML] ([-\d.]+) ([-\d.]+)/g)]
                    .map(([, x, y]) => screen(Number(x), Number(y)));
                const samples = [];
                for (let at = 0; at <= path.getTotalLength(); at += 3) {
                    const point = path.getPointAtLength(at);
                    samples.push(screen(point.x, point.y));
                }
                const through = [...boxes].filter(([id]) => id !== path.dataset.from && id !== path.dataset.to)
                    .filter(([, box]) => samples.some((point) =>
                        point.x > box.left + 1 && point.x < box.right - 1 &&
                        point.y > box.top + 1 && point.y < box.bottom - 1))
                    .map(([id]) => id);
                const source = boxes.get(path.dataset.from), target = boxes.get(path.dataset.to);
                const start = corners[0], end = corners.at(-1);
                return {
                    id: `${path.dataset.from}->${path.dataset.to}`,
                    from: path.dataset.from,
                    to: path.dataset.to,
                    through,
                    startY: start.y,
                    endY: end.y,
                    startGap: Math.abs(start.x - source.right),
                    endGap: Math.abs(end.x - target.left),
                    square: corners.slice(1).every((point, at) =>
                        Math.abs(point.x - corners[at].x) < 0.5 || Math.abs(point.y - corners[at].y) < 0.5),
                    vectorEffect: getComputedStyle(path).vectorEffect,
                };
            });
        }
        """;

    internal override IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Estate => Estates.Lines;

    [Fact]
    public async Task Estate_lines_turn_in_gutters_and_never_cross_a_box()
    {
        string[] ids = [.. "abcde".Select(node => $"owner/{node}")];
        await Page.GotoAsync(Origin + "/estate");
        await Expect(Page.Locator(".estate-edges path[data-from]")).ToHaveCountAsync(Estates.LinePairs.Count);

        foreach (var zoomOut in new[] { 0, 3 })
        {
            for (var step = 0; step < zoomOut; step++)
            {
                await Page.Button("Zoom graph out").ClickAsync();
            }

            var lines = (await Page.EvaluateAsync<JsonElement>(Measure)).EnumerateArray().Select(Drawn.Read).ToList();
            foreach (var line in lines)
            {
                Assert.True(line.Through.Count == 0, $"{line.Id} runs through {string.Join(", ", line.Through)}");
                Assert.True(line.Square, $"{line.Id} has a segment that is neither level nor upright");
                // Two units of slack for the border; the arrow's tip is at the path's end.
                Assert.True(line.StartGap < 2.5 && line.EndGap < 2.5, $"{line.Id} starts {line.StartGap} and ends {line.EndGap} from its boxes");
                Assert.Equal("non-scaling-stroke", line.VectorEffect);
            }

            // No two lines share a port: five consumers of one box are five lines, not one.
            foreach (var node in ids)
            {
                AssertSeparate(node, "out", lines.Where(line => line.From == node).Select(line => line.StartY));
                AssertSeparate(node, "in", lines.Where(line => line.To == node).Select(line => line.EndY));
            }
        }
    }

    private static void AssertSeparate(string node, string side, IEnumerable<double> ys)
    {
        var sorted = ys.Order().ToList();
        for (var at = 1; at < sorted.Count; at++)
        {
            Assert.True(sorted[at] - sorted[at - 1] > 3, $"{node} {side} ports are too close: {string.Join(", ", sorted)}");
        }
    }

    private sealed record Drawn(string Id, string From, string To, IReadOnlyList<string> Through, double StartY, double EndY, double StartGap, double EndGap, bool Square, string VectorEffect)
    {
        public static Drawn Read(JsonElement line) => new(
            line.GetProperty("id").GetString()!,
            line.GetProperty("from").GetString()!,
            line.GetProperty("to").GetString()!,
            [.. line.GetProperty("through").EnumerateArray().Select(id => id.GetString()!)],
            line.GetProperty("startY").GetDouble(),
            line.GetProperty("endY").GetDouble(),
            line.GetProperty("startGap").GetDouble(),
            line.GetProperty("endGap").GetDouble(),
            line.GetProperty("square").GetBoolean(),
            line.GetProperty("vectorEffect").GetString()!);
    }
}
