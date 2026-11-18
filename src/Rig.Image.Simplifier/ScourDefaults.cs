namespace Rig.Image.Simplifier;

/// <summary>
/// One attribute scour treats as removable when it holds its default (scour's <c>DefaultAttribute</c>). A text value must match
/// exactly; a number is compared as a length (so <c>0px</c> matches 0); no value at all matches anything the condition accepts.
/// </summary>
internal sealed record DefaultAttribute(string Name, string? Text, double? Number, LengthUnit? Unit, IReadOnlyList<string> Elements, Func<DomElement, bool>? Condition)
{
    /// <summary>An attribute whose default is this exact text.</summary>
    public static DefaultAttribute OfText(string name, string text, params string[] elements) => new(name, text, null, null, elements, null);

    /// <summary>An attribute whose default is this number, in any unit when <paramref name="unit"/> is null.</summary>
    public static DefaultAttribute OfNumber(string name, double number, LengthUnit? unit, string[] elements, Func<DomElement, bool>? condition = null) =>
        new(name, null, number, unit, elements, condition);

    /// <summary>An attribute that is removable whenever its condition holds, whatever its value.</summary>
    public static DefaultAttribute When(string name, string[] elements, Func<DomElement, bool> condition) => new(name, null, null, null, elements, condition);
}

/// <summary>Scour's table of attributes that can be dropped at their default value, in scour's order (the order matters: fx and fy go before cx and cy).</summary>
internal static class ScourDefaults
{
    private static readonly string[] ExternalResourcesElements =
    [
        "a", "altGlyph", "animate", "animateColor", "animateMotion", "animateTransform", "circle", "clipPath", "cursor", "defs", "ellipse",
        "feImage", "filter", "font", "foreignObject", "g", "image", "line", "linearGradient", "marker", "mask", "mpath", "path", "pattern",
        "polygon", "polyline", "radialGradient", "rect", "script", "set", "svg", "switch", "symbol", "text", "textPath", "tref", "tspan", "use", "view",
    ];

    private static readonly string[] PositionElements = ["cursor", "fePointLight", "feSpotLight", "foreignObject", "image", "pattern", "rect", "svg", "text", "use"];
    private static readonly string[] FilterAndMask = ["filter", "mask"];
    private static readonly string[] Gradients = ["linearGradient", "radialGradient"];
    private static readonly string[] RadialGradient = ["radialGradient"];
    private static readonly string[] LinearGradient = ["linearGradient"];
    private static readonly string[] TransferFunctions = ["feFuncA", "feFuncB", "feFuncG", "feFuncR"];

    /// <summary>The table.</summary>
    public static IReadOnlyList<DefaultAttribute> All { get; } = Build();

    // scour's lambdas read gradientUnits even on filters and masks; the same quirk is kept so the same attributes go.
    private static bool NotUserSpace(DomElement node) => node.GetAttribute("gradientUnits") != "userSpaceOnUse";

    private static List<DefaultAttribute> Build()
    {
        var table = new List<DefaultAttribute>
        {
            DefaultAttribute.OfText("clipPathUnits", "userSpaceOnUse", "clipPath"),
            DefaultAttribute.OfText("filterUnits", "objectBoundingBox", "filter"),
            DefaultAttribute.OfText("gradientUnits", "objectBoundingBox", Gradients),
            DefaultAttribute.OfText("maskUnits", "objectBoundingBox", "mask"),
            DefaultAttribute.OfText("maskContentUnits", "userSpaceOnUse", "mask"),
            DefaultAttribute.OfText("patternUnits", "objectBoundingBox", "pattern"),
            DefaultAttribute.OfText("patternContentUnits", "userSpaceOnUse", "pattern"),
            DefaultAttribute.OfText("primitiveUnits", "userSpaceOnUse", "filter"),
            DefaultAttribute.OfText("externalResourcesRequired", "false", ExternalResourcesElements),

            DefaultAttribute.OfNumber("width", 100, LengthUnit.Percent, ["svg"]),
            DefaultAttribute.OfNumber("height", 100, LengthUnit.Percent, ["svg"]),
            DefaultAttribute.OfText("baseProfile", "none", "svg"),
            DefaultAttribute.OfText("preserveAspectRatio", "xMidYMid meet", "feImage", "image", "marker", "pattern", "svg", "symbol", "view"),

            DefaultAttribute.OfNumber("x", 0, null, PositionElements),
            DefaultAttribute.OfNumber("y", 0, null, PositionElements),
            DefaultAttribute.OfNumber("z", 0, null, ["fePointLight", "feSpotLight"]),
            DefaultAttribute.OfNumber("x1", 0, null, ["line"]),
            DefaultAttribute.OfNumber("y1", 0, null, ["line"]),
            DefaultAttribute.OfNumber("x2", 0, null, ["line"]),
            DefaultAttribute.OfNumber("y2", 0, null, ["line"]),
            DefaultAttribute.OfNumber("cx", 0, null, ["circle", "ellipse"]),
            DefaultAttribute.OfNumber("cy", 0, null, ["circle", "ellipse"]),

            DefaultAttribute.OfText("markerUnits", "strokeWidth", "marker"),
            DefaultAttribute.OfNumber("refX", 0, null, ["marker"]),
            DefaultAttribute.OfNumber("refY", 0, null, ["marker"]),
            DefaultAttribute.OfNumber("markerHeight", 3, null, ["marker"]),
            DefaultAttribute.OfNumber("markerWidth", 3, null, ["marker"]),
            DefaultAttribute.OfNumber("orient", 0, null, ["marker"]),

            DefaultAttribute.OfText("lengthAdjust", "spacing", "text", "textPath", "tref", "tspan"),
            DefaultAttribute.OfNumber("startOffset", 0, null, ["textPath"]),
            DefaultAttribute.OfText("method", "align", "textPath"),
            DefaultAttribute.OfText("spacing", "exact", "textPath"),

            DefaultAttribute.OfNumber("x", -10, LengthUnit.Percent, FilterAndMask),
            DefaultAttribute.OfNumber("x", -0.1, LengthUnit.None, FilterAndMask, NotUserSpace),
            DefaultAttribute.OfNumber("y", -10, LengthUnit.Percent, FilterAndMask),
            DefaultAttribute.OfNumber("y", -0.1, LengthUnit.None, FilterAndMask, NotUserSpace),
            DefaultAttribute.OfNumber("width", 120, LengthUnit.Percent, FilterAndMask),
            DefaultAttribute.OfNumber("width", 1.2, LengthUnit.None, FilterAndMask, NotUserSpace),
            DefaultAttribute.OfNumber("height", 120, LengthUnit.Percent, FilterAndMask),
            DefaultAttribute.OfNumber("height", 1.2, LengthUnit.None, FilterAndMask, NotUserSpace),

            DefaultAttribute.OfNumber("x1", 0, null, LinearGradient),
            DefaultAttribute.OfNumber("y1", 0, null, LinearGradient),
            DefaultAttribute.OfNumber("y2", 0, null, LinearGradient),
            DefaultAttribute.OfNumber("x2", 100, LengthUnit.Percent, LinearGradient),
            DefaultAttribute.OfNumber("x2", 1, LengthUnit.None, LinearGradient, NotUserSpace),
            DefaultAttribute.When("fx", RadialGradient, node => node.GetAttribute("fx") == node.GetAttribute("cx")),
            DefaultAttribute.When("fy", RadialGradient, node => node.GetAttribute("fy") == node.GetAttribute("cy")),
            DefaultAttribute.OfNumber("r", 50, LengthUnit.Percent, RadialGradient),
            DefaultAttribute.OfNumber("r", 0.5, LengthUnit.None, RadialGradient, NotUserSpace),
            DefaultAttribute.OfNumber("cx", 50, LengthUnit.Percent, RadialGradient),
            DefaultAttribute.OfNumber("cx", 0.5, LengthUnit.None, RadialGradient, NotUserSpace),
            DefaultAttribute.OfNumber("cy", 50, LengthUnit.Percent, RadialGradient),
            DefaultAttribute.OfNumber("cy", 0.5, LengthUnit.None, RadialGradient, NotUserSpace),
            DefaultAttribute.OfText("spreadMethod", "pad", "linearGradient", "radialGradient"),

            DefaultAttribute.OfNumber("amplitude", 1, null, TransferFunctions),
            DefaultAttribute.OfNumber("azimuth", 0, null, ["feDistantLight"]),
            DefaultAttribute.OfText("baseFrequency", "0", TransferFunctions),
            DefaultAttribute.OfNumber("bias", 1, null, ["feConvolveMatrix"]),
            DefaultAttribute.OfNumber("diffuseConstant", 1, null, ["feDiffuseLighting"]),
            DefaultAttribute.OfText("edgeMode", "duplicate", "feConvolveMatrix"),
            DefaultAttribute.OfNumber("elevation", 0, null, ["feDistantLight"]),
            DefaultAttribute.OfNumber("exponent", 1, null, TransferFunctions),
            DefaultAttribute.OfNumber("intercept", 0, null, TransferFunctions),
            DefaultAttribute.OfNumber("k1", 0, null, ["feComposite"]),
            DefaultAttribute.OfNumber("k2", 0, null, ["feComposite"]),
            DefaultAttribute.OfNumber("k3", 0, null, ["feComposite"]),
            DefaultAttribute.OfNumber("k4", 0, null, ["feComposite"]),
            DefaultAttribute.OfText("mode", "normal", "feBlend"),
            DefaultAttribute.OfNumber("numOctaves", 1, null, ["feTurbulence"]),
            DefaultAttribute.OfNumber("offset", 0, null, TransferFunctions),
            DefaultAttribute.OfText("operator", "over", "feComposite"),
            DefaultAttribute.OfText("operator", "erode", "feMorphology"),
            DefaultAttribute.OfText("order", "3", "feConvolveMatrix"),
            DefaultAttribute.OfNumber("pointsAtX", 0, null, ["feSpotLight"]),
            DefaultAttribute.OfNumber("pointsAtY", 0, null, ["feSpotLight"]),
            DefaultAttribute.OfNumber("pointsAtZ", 0, null, ["feSpotLight"]),
            DefaultAttribute.OfText("preserveAlpha", "false", "feConvolveMatrix"),
            DefaultAttribute.OfText("radius", "0", "feMorphology"),
            DefaultAttribute.OfNumber("scale", 0, null, ["feDisplacementMap"]),
            DefaultAttribute.OfNumber("seed", 0, null, ["feTurbulence"]),
            DefaultAttribute.OfNumber("specularConstant", 1, null, ["feSpecularLighting"]),
            DefaultAttribute.OfNumber("specularExponent", 1, null, ["feSpecularLighting", "feSpotLight"]),
            DefaultAttribute.OfText("stdDeviation", "0", "feGaussianBlur"),
            DefaultAttribute.OfText("stitchTiles", "noStitch", "feTurbulence"),
            DefaultAttribute.OfNumber("surfaceScale", 1, null, ["feDiffuseLighting", "feSpecularLighting"]),
            DefaultAttribute.OfText("type", "matrix", "feColorMatrix"),
            DefaultAttribute.OfText("type", "turbulence", "feTurbulence"),
            DefaultAttribute.OfText("xChannelSelector", "A", "feDisplacementMap"),
            DefaultAttribute.OfText("yChannelSelector", "A", "feDisplacementMap"),
        };
        return table;
    }
}
