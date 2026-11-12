using Rig.Core.Desktop.Windows.Uia;

namespace Rig.Core.Tests.Desktop.Windows;

/// <summary>
/// One element of an in-memory accessibility tree. A <see cref="RawOnly"/> element (Chrome's <c>TabGroupView</c>) shows
/// only in the raw view: the control view skips it and lifts its children to its parent.
/// </summary>
internal sealed class FakeUiaElement(string className, UiaControlType type = UiaControlType.Other, string name = "") : IUiaElement
{
    private static int _nextId = 1;
    private readonly List<FakeUiaElement> _children = [];
    private FakeUiaElement? _parent;

    public string ClassNameValue { get; set; } = className;

    public string NameValue { get; set; } = name;

    public UiaControlType Type { get; set; } = type;

    public IReadOnlyList<int> Id { get; set; } = [42, Interlocked.Increment(ref _nextId)];

    public bool RawOnly { get; init; }

    public bool Selected { get; set; }

    public bool Stale { get; set; }

    /// <summary>Reads of <see cref="Name"/> that throw before one succeeds, as when Chrome rebuilds the view.</summary>
    public int FailingNameReads { get; set; }

    public int Selects { get; private set; }

    public string Name
    {
        get
        {
            Touch();
            if (FailingNameReads > 0)
            {
                FailingNameReads--;
                throw new UiaException("The element is no longer available.");
            }

            return NameValue;
        }
    }

    public string ClassName => Touch(ClassNameValue);

    public UiaControlType ControlType => Touch(Type);

    public IReadOnlyList<int> RuntimeId => Touch(Id);

    public UiaRect BoundingRectangle => Touch(new UiaRect(0, 0, 100, 30));

    public bool IsOffscreen => Touch(false);

    public bool HasKeyboardFocus => Touch(false);

    public bool IsSelected => Touch(Selected);

    public FakeUiaElement Add(params FakeUiaElement[] children)
    {
        foreach (var child in children)
        {
            child._parent = this;
            _children.Add(child);
        }

        return this;
    }

    public IUiaElement? Parent(UiaView view = UiaView.Control)
    {
        Touch();
        var parent = _parent;
        while (view == UiaView.Control && parent is { RawOnly: true })
        {
            parent = parent._parent;
        }

        return parent;
    }

    public IUiaElement? FirstChild(UiaView view = UiaView.Control) => Touch(Children(view).FirstOrDefault());

    public IUiaElement? NextSibling(UiaView view = UiaView.Control)
    {
        Touch();
        if (Parent(view) is not FakeUiaElement parent)
        {
            return null;
        }

        var siblings = parent.Children(view);
        var index = siblings.IndexOf(this);
        return index + 1 < siblings.Count ? siblings[index + 1] : null;
    }

    public bool SameAs(IUiaElement other) => Touch(ReferenceEquals(this, other));

    public IReadOnlyList<IUiaElement> FindAll(UiaScope scope, UiaCondition condition)
    {
        Touch();
        IEnumerable<FakeUiaElement> candidates = scope switch
        {
            UiaScope.Children => Children(UiaView.Control),
            UiaScope.Descendants => Descendants(),
            _ => [this, .. Descendants()],
        };
        return [.. candidates.Where(element => Matches(element, condition))];
    }

    public IUiaElement? FindFirst(UiaScope scope, UiaCondition condition) => FindAll(scope, condition) is [var first, ..] ? first : null;

    public void SelectItem()
    {
        Touch();
        Selects++;
    }

    public void Invoke() => Touch();

    public bool Expand() => Touch(false);

    public bool ScrollIntoView() => Touch(false);

    public void ShowContextMenu() => Touch();

    private List<FakeUiaElement> Children(UiaView view) =>
        view == UiaView.Raw
            ? _children
            : [.. _children.SelectMany(child => child.RawOnly ? child.Children(UiaView.Control) : [child])];

    private IEnumerable<FakeUiaElement> Descendants() =>
        Children(UiaView.Control).SelectMany(child => (IEnumerable<FakeUiaElement>)[child, .. child.Descendants()]);

    private static bool Matches(FakeUiaElement element, UiaCondition condition) =>
        (condition.ControlType is null || condition.ControlType == element.Type)
        && (condition.ClassName is null || condition.ClassName == element.ClassNameValue)
        && (condition.Name is null || condition.Name == element.NameValue);

    private void Touch()
    {
        if (Stale)
        {
            throw new UiaException("The element is no longer available.");
        }
    }

    private T Touch<T>(T value)
    {
        Touch();
        return value;
    }
}

/// <summary>An in-memory UI Automation session: one tree per window handle.</summary>
internal sealed class FakeUiAutomation : IUiAutomation
{
    public Dictionary<long, FakeUiaElement> Roots { get; } = [];

    public IUiaElement? ElementFromHandle(long handle) => Roots.GetValueOrDefault(handle);
}
