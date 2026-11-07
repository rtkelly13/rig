from types import ModuleType

from comtypes import CoClass, IUnknown

def GetModule(tlib: str, /) -> ModuleType: ...
def CreateObject[Interface: IUnknown](
    progid: type[CoClass],
    clsctx: int | None = None,
    machine: str | None = None,
    *,
    interface: type[Interface],
) -> Interface: ...
