"""Read GitHub Actions workflow files into typed records.

`on` is a YAML 1.1 boolean, so `yaml.safe_load` parses the key `on:` as `True` rather than the
string `"on"`. `workflow_keys` folds that key back into `on` (a literal `"on"` key wins if both are
present) and drops every other non-string key, so a workflow decodes like any other mapping.
"""

from __future__ import annotations

from typing import Annotated

from pydantic import BeforeValidator

from rig.typed_json import is_list, is_object


def workflow_keys(value: object) -> object:
    """A parsed workflow with its keys as GitHub reads them; anything but a mapping passes through."""
    if not is_object(value):
        return value
    folded = {key: item for key, item in value.items() if isinstance(key, str)}
    for key, item in value.items():
        if key is True:
            _ = folded.setdefault("on", item)
    return folded


def _trigger_names(value: object) -> object:
    if value is None:
        return []
    if is_object(value):
        return [str(key) for key in value]
    if is_list(value):
        return [str(item) for item in value]
    return [str(value)]


#: A workflow's `on`: the event names, whether it is written as a mapping, a list or one name.
type Triggers = Annotated[tuple[str, ...], BeforeValidator(_trigger_names)]

#: A mapping key read as text, as GitHub reads a job id.
type KeyText = Annotated[str, BeforeValidator(str)]
