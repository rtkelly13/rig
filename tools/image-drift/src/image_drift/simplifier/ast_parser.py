"""SVG Path AST Parser, Tokenizer, and Subpath Classifier."""

from __future__ import annotations

import re
from dataclasses import dataclass

COMMAND_ARITY = {
    "M": 2,
    "m": 2,
    "L": 2,
    "l": 2,
    "H": 1,
    "h": 1,
    "V": 1,
    "v": 1,
    "C": 6,
    "c": 6,
    "S": 4,
    "s": 4,
    "Q": 4,
    "q": 4,
    "T": 2,
    "t": 2,
    "A": 7,
    "a": 7,
    "Z": 0,
    "z": 0,
}

# Regex to extract SVG path tokens: commands vs numbers
# Note: 'e' or 'E' is part of scientific notation, so command chars exclude e/E
PATH_TOKEN_RE = re.compile(
    r"""
    (?P<cmd>[MmLlHhVvCcSsQqTtAaZz])
    |
    (?P<num>[-+]?(?:[0-9]*\.[0-9]+|[0-9]+)(?:[eE][-+]?[0-9]+)?)
    """,
    re.VERBOSE,
)


@dataclass
class PathCommand:
    """A single SVG path command with its numeric arguments."""

    command: str
    args: list[float]

    def serialize(self, precision: int = 2) -> str:
        """Serialize command to clean SVG path syntax with rounded floats."""
        if not self.args:
            return self.command

        formatted_args: list[str] = []
        is_arc = self.command in ("A", "a")

        for idx, val in enumerate(self.args):
            # Arc flags (indices 3 and 4) must be strict 0 or 1 integers
            if is_arc and idx in (3, 4):
                formatted_args.append(str(int(val)))
            else:
                formatted = f"{val:.{precision}f}".rstrip("0").rstrip(".")
                if formatted == "-0":
                    formatted = "0"
                formatted_args.append(formatted)

        return f"{self.command} {' '.join(formatted_args)}"


@dataclass
class SubPath:
    """A contiguous subpath beginning with M/m and ending with Z/z or end of contour."""

    commands: list[PathCommand]
    is_closed: bool

    def serialize(self, precision: int = 2) -> str:
        return " ".join(c.serialize(precision=precision) for c in self.commands)


class SVGPathTokenizer:
    """Tokenizer and parser for SVG path data `d` strings."""

    @classmethod
    def parse(cls, d_string: str) -> list[PathCommand]:
        """Parse raw SVG path `d` attribute string into structured PathCommand list."""
        if not d_string or not d_string.strip():
            return []

        tokens = list(PATH_TOKEN_RE.finditer(d_string))
        commands: list[PathCommand] = []

        current_cmd: str | None = None
        current_args: list[float] = []

        for match in tokens:
            cmd = match.group("cmd")
            num_str = match.group("num")

            if cmd:
                # Flush previous command if any args collected
                if current_cmd:
                    commands.extend(cls._split_command_args(current_cmd, current_args))
                current_cmd = cmd
                current_args = []

                if current_cmd in ("Z", "z"):
                    commands.append(PathCommand(current_cmd, []))
                    current_cmd = None
            elif num_str:
                if not current_cmd:
                    # Malformed path, assume M if starts with number
                    current_cmd = "M"
                current_args.append(float(num_str))

        if current_cmd:
            commands.extend(cls._split_command_args(current_cmd, current_args))

        return commands

    @classmethod
    def _split_command_args(cls, cmd: str, args: list[float]) -> list[PathCommand]:
        """Split coordinate stream according to arity, treating repeated M args as L."""
        arity = COMMAND_ARITY.get(cmd, 2)
        if arity == 0:
            return [PathCommand(cmd, [])]

        if not args:
            return []

        results: list[PathCommand] = []
        for i in range(0, len(args), arity):
            chunk = args[i : i + arity]
            if len(chunk) < arity:
                continue

            # Per SVG spec, first pair of M/m is moveto; subsequent pairs become implicit L/l
            if i > 0 and cmd == "M":
                op = "L"
            elif i > 0 and cmd == "m":
                op = "l"
            else:
                op = cmd

            results.append(PathCommand(op, chunk))

        return results

    @classmethod
    def extract_subpaths(cls, d_string: str) -> list[SubPath]:
        """Decompose path string into independent SubPath objects."""
        all_commands = cls.parse(d_string)
        if not all_commands:
            return []

        subpaths: list[SubPath] = []
        current_sub_cmds: list[PathCommand] = []

        for cmd in all_commands:
            if cmd.command in ("M", "m") and current_sub_cmds:
                # Check if the previous subpath terminated with Z/z
                is_closed = current_sub_cmds[-1].command in ("Z", "z")
                subpaths.append(SubPath(commands=current_sub_cmds, is_closed=is_closed))
                current_sub_cmds = [cmd]
            else:
                current_sub_cmds.append(cmd)

        if current_sub_cmds:
            is_closed = current_sub_cmds[-1].command in ("Z", "z")
            subpaths.append(SubPath(commands=current_sub_cmds, is_closed=is_closed))

        return subpaths


def parse_path_d(d_string: str) -> list[PathCommand]:
    return SVGPathTokenizer.parse(d_string)


def serialize_path_d(commands: list[PathCommand], precision: int = 2) -> str:
    return " ".join(cmd.serialize(precision=precision) for cmd in commands)
