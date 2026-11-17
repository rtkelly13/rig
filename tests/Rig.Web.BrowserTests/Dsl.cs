using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Rig.Web.BrowserTests;

/// <summary>Short forms of Playwright's role queries, which the pages' tests make hundreds of times.</summary>
internal static class Dsl
{
    /// <summary>The themes the app ships and the tests walk through.</summary>
    public static IReadOnlyList<string> Themes { get; } = ["midnight", "sketch"];

    public static ILocator Button(this IPage scope, string name, bool exact = true) => scope.GetByRole(AriaRole.Button, new() { Name = name, Exact = exact });

    public static ILocator Button(this ILocator scope, string name, bool exact = true) => scope.GetByRole(AriaRole.Button, new() { Name = name, Exact = exact });

    public static ILocator Button(this IPage scope, Regex name) => scope.GetByRole(AriaRole.Button, new() { NameRegex = name });

    public static ILocator Link(this IPage scope, string name, bool exact = true) => scope.GetByRole(AriaRole.Link, new() { Name = name, Exact = exact });

    public static ILocator Link(this ILocator scope, string name, bool exact = true) => scope.GetByRole(AriaRole.Link, new() { Name = name, Exact = exact });

    public static ILocator Link(this IPage scope, Regex name) => scope.GetByRole(AriaRole.Link, new() { NameRegex = name });

    public static ILocator Heading(this IPage scope, string name, bool exact = true) => scope.GetByRole(AriaRole.Heading, new() { Name = name, Exact = exact });

    public static ILocator Modal(this IPage scope, string name, bool exact = false) => scope.GetByRole(AriaRole.Dialog, new() { Name = name, Exact = exact });

    public static ILocator Combobox(this IPage scope, string name) => scope.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });

    public static ILocator Combobox(this ILocator scope, string name) => scope.GetByRole(AriaRole.Combobox, new() { Name = name, Exact = true });

    public static ILocator Option(this IPage scope, string name) => scope.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });

    public static ILocator Textbox(this IPage scope, string name, bool exact = false) => scope.GetByRole(AriaRole.Textbox, new() { Name = name, Exact = exact });

    public static ILocator Text(this IPage scope, string text, bool exact = false) => scope.GetByText(text, new() { Exact = exact });

    public static ILocator Text(this ILocator scope, string text, bool exact = true) => scope.GetByText(text, new() { Exact = exact });

    /// <summary>Whether the page has no horizontal scroll: nothing wider than the viewport.</summary>
    public static async Task<bool> FitsWidthAsync(this IPage page) => await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth");

    public static Task ViewportAsync(this IPage page, int width, int height) => page.SetViewportSizeAsync(width, height);

    /// <summary>The element's box, which a test that measures it has already waited for.</summary>
    public static async Task<LocatorBoundingBoxResult> BoxAsync(this ILocator locator) => await locator.BoundingBoxAsync() ?? throw new InvalidOperationException($"{locator} has no box.");

    public static Task UseThemeAsync(this IPage page, string theme) => page.EvaluateAsync("theme => document.documentElement.dataset.theme = theme", theme);
}
