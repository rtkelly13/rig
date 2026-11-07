from collections.abc import Sequence

from comtypes import CoClass, IUnknown

class tagRECT:
    left: int
    top: int
    right: int
    bottom: int

class IUIAutomationCondition(IUnknown): ...

class IUIAutomationElementArray(IUnknown):
    Length: int
    def GetElement(self, index: int, /) -> IUIAutomationElement: ...

class IUIAutomationElement(IUnknown):
    CurrentName: str | None
    CurrentClassName: str | None
    CurrentControlType: int
    CurrentBoundingRectangle: tagRECT
    CurrentIsOffscreen: int
    CurrentHasKeyboardFocus: int
    def GetRuntimeId(self) -> Sequence[int]: ...
    def FindFirst(self, scope: int, condition: IUIAutomationCondition, /) -> IUIAutomationElement: ...
    def FindAll(self, scope: int, condition: IUIAutomationCondition, /) -> IUIAutomationElementArray: ...
    def GetCurrentPattern(self, patternId: int, /) -> IUnknown: ...
    def SetFocus(self) -> None: ...

class IUIAutomationElement3(IUIAutomationElement):
    def ShowContextMenu(self) -> None: ...

class IUIAutomationTreeWalker(IUnknown):
    def GetParentElement(self, element: IUIAutomationElement, /) -> IUIAutomationElement: ...
    def GetFirstChildElement(self, element: IUIAutomationElement, /) -> IUIAutomationElement: ...
    def GetNextSiblingElement(self, element: IUIAutomationElement, /) -> IUIAutomationElement: ...

class IUIAutomation(IUnknown):
    ControlViewWalker: IUIAutomationTreeWalker
    RawViewWalker: IUIAutomationTreeWalker
    def ElementFromHandle(self, hwnd: int, /) -> IUIAutomationElement: ...
    def CompareElements(self, el1: IUIAutomationElement, el2: IUIAutomationElement, /) -> int: ...
    def CreatePropertyCondition(self, propertyId: int, value: int | str, /) -> IUIAutomationCondition: ...
    def CreateAndCondition(
        self, condition1: IUIAutomationCondition, condition2: IUIAutomationCondition, /
    ) -> IUIAutomationCondition: ...

class IUIAutomationSelectionItemPattern(IUnknown):
    CurrentIsSelected: int
    def Select(self) -> None: ...

class IUIAutomationInvokePattern(IUnknown):
    def Invoke(self) -> None: ...

class IUIAutomationScrollItemPattern(IUnknown):
    def ScrollIntoView(self) -> None: ...

class IUIAutomationExpandCollapsePattern(IUnknown):
    def Expand(self) -> None: ...

class IUIAutomationTextRange(IUnknown):
    def GetText(self, maxLength: int, /) -> str: ...

class IUIAutomationTextPattern(IUnknown):
    DocumentRange: IUIAutomationTextRange

class CUIAutomation(CoClass): ...

TreeScope_Children: int
TreeScope_Descendants: int
TreeScope_Subtree: int

UIA_AutomationIdPropertyId: int
UIA_ClassNamePropertyId: int
UIA_ControlTypePropertyId: int
UIA_NamePropertyId: int

UIA_ButtonControlTypeId: int
UIA_DocumentControlTypeId: int
UIA_EditControlTypeId: int
UIA_MenuItemControlTypeId: int
UIA_TabItemControlTypeId: int
UIA_WindowControlTypeId: int

UIA_ExpandCollapsePatternId: int
UIA_InvokePatternId: int
UIA_ScrollItemPatternId: int
UIA_SelectionItemPatternId: int
UIA_TextPatternId: int
