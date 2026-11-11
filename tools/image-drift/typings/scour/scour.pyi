from typing import final

@final
class ScourOptions:
    strip_comments: bool
    remove_metadata: bool
    remove_descriptive_elements: bool
    strip_xml_prolog: bool
    remove_titles: bool
    shorten_ids: bool
    indent_type: str

def sanitizeOptions(options: ScourOptions | None = None) -> ScourOptions: ...
def scourString(in_string: str, options: ScourOptions | None = None) -> str: ...
