from typing import final

from _win32typing import PyIID, PyPROPERTYKEY

@final
class PropVariant:
    def GetValue(self) -> object: ...

@final
class PropertyStore:
    def GetValue(self, Key: PyPROPERTYKEY, /) -> PropVariant: ...

IID_IPropertyStore: PyIID

def SHGetPropertyStoreForWindow(hwnd: int, riid: PyIID, /) -> PropertyStore: ...
