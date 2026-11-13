"""Inspection adapter for legacy Python registrations, not a second ownership policy.

AST discovery finds modules, including unmounted apps and __main__ blocks. Runtime
Click/Typer traversal expands dynamic registrations. Import/discovery errors fail closed.
The C# gate consumes this JSON and owns validation. Never execute command callbacks here.
"""

from __future__ import annotations

import ast
import importlib
import inspect
import json
import sys
import textwrap
import tomllib
from pathlib import Path

import click
import typer
import typer.main


def callback_id(callback):
    original = inspect.unwrap(callback)
    if original.__closure__:
        raise RuntimeError(f"Unsupported stateful CLI closure: {original.__module__}:{original.__qualname__}")
    return f"{original.__module__}:{original.__qualname__}"


def thin_alias(callback):
    """Accept exactly a whole-line delegation, with no policy, branching or fallback."""
    # Typer adapts even a plain callback through this exact framework code object.
    # Strip that one adapter, not arbitrary decorators that happen to use wraps().
    framework_codes = {
        code
        for code in typer.main.get_callback.__code__.co_consts
        if inspect.iscode(code) and code.co_name == "wrapper"
    }
    registered = callback.__wrapped__ if callback.__code__ in framework_codes else callback
    original = inspect.unwrap(registered)
    if original is not registered:
        return None
    from rig import csharp_cli

    if original.__globals__.get("csharp_cli") is not csharp_cli or original.__globals__.get("typer") is not typer:
        return None
    tree = ast.parse(textwrap.dedent(inspect.getsource(original)))
    function = tree.body[0]
    body = function.body
    if isinstance(body[0], ast.Expr) and isinstance(body[0].value, ast.Constant):
        body = body[1:]
    if len(body) != 1 or not isinstance(body[0], ast.Raise):
        return None
    raised = body[0].exc
    if not isinstance(raised, ast.Call) or ast.unparse(raised.func) != "typer.Exit" or raised.args:
        return None
    if len(raised.keywords) != 1 or raised.keywords[0].arg != "code":
        return None
    call = raised.keywords[0].value
    if (
        not isinstance(call, ast.Call)
        or ast.unparse(call.func) != "csharp_cli.run"
        or len(call.args) != 1
        or call.keywords
    ):
        return None
    argument = call.args[0]
    if not isinstance(argument, ast.List) or not argument.elts:
        return None
    *prefix, tail = argument.elts
    if not isinstance(tail, ast.Starred) or ast.unparse(tail.value) != "ctx.args":
        return None
    if not all(isinstance(item, ast.Constant) and isinstance(item.value, str) for item in prefix):
        return None
    return " ".join(item.value for item in prefix)


def imported_names(tree):
    imports = {}
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            imports.update((a.asname or a.name, a.name) for a in node.names)
        if isinstance(node, ast.ImportFrom):
            imports.update((a.asname or a.name, f"{node.module}.{a.name}") for a in node.names)
    return imports


def cli_factories(tree, imports):
    candidates = [node for node in ast.walk(tree) if isinstance(node, ast.Call)]
    candidates.extend(
        decorator
        for function in ast.walk(tree)
        if isinstance(function, (ast.FunctionDef, ast.AsyncFunctionDef))
        for decorator in function.decorator_list
        if not isinstance(decorator, ast.Call)
    )
    found = []
    for node in candidates:
        expression = node.func if isinstance(node, ast.Call) else node
        first, dot, rest = ast.unparse(expression).partition(".")
        resolved = imports.get(first, "") + (dot + rest if dot else "")
        if resolved in {"typer.Typer", "click.Command", "click.Group", "click.group", "click.command"}:
            found.append(node)
    return found


def walk(command, path):
    if command.callback is not None:
        callback = command.callback
        from rig.platforms import supported_platforms

        platforms = supported_platforms(callback)
        yield {
            "Id": f"py:{path}|{callback_id(callback)}",
            "Path": path,
            "Owner": f"py:{callback_id(callback)}",
            "Platforms": sorted(p.name.lower() for p in platforms) if platforms else ["linux", "macos", "windows"],
            "Alias": thin_alias(callback),
        }
    if isinstance(command, click.Group):
        with click.Context(command) as context:
            for name in command.list_commands(context):
                child = command.get_command(context, name)
                if child is None:
                    raise RuntimeError(f"Unresolved dynamic registration: {path} {name}")
                yield from walk(child, f"{path} {name}")


def discover(root):
    source = root / "src"
    sys.path.insert(0, str(source))
    modules = {}
    surfaces = []
    for file in sorted(source.rglob("*.py")):
        relative = file.relative_to(source)
        module_name = ".".join(relative.with_suffix("").parts)
        tree = ast.parse(file.read_text(encoding="utf-8"), filename=str(file))
        imports = imported_names(tree)
        factories = cli_factories(tree, imports)
        for node in ast.walk(tree):
            if isinstance(node, ast.If) and ast.unparse(node.test) in {
                "__name__ == '__main__'",
                "'__main__' == __name__",
            }:
                surfaces.append(f"module:{module_name}")
            if isinstance(node, ast.Call):
                function = ast.unparse(node.func)
                if function.endswith((".add_subparsers", ".add_parser")):
                    raise RuntimeError(f"Unsupported argparse command tree in {file}; add runtime discovery")
        for function in (n for n in ast.walk(tree) if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef))):
            if any(factory in ast.walk(statement) for statement in function.body for factory in factories):
                raise RuntimeError(
                    f"Unsupported local command factory in {file}:{function.name}; expose an inspectable tree"
                )
        if file.name == "__main__.py" and f"module:{module_name}" not in surfaces:
            surfaces.append(f"module:{module_name}")
        uses_framework = any(
            name == "typer" or name.startswith("typer.") or name == "click" or name.startswith("click.")
            for name in imports.values()
        )
        if factories or uses_framework:
            module = importlib.import_module(module_name)
            apps = {
                id(value): value for value in vars(module).values() if isinstance(value, (typer.Typer, click.Command))
            }
            if not apps and factories:
                raise RuntimeError(f"Unsupported CLI discovery in {file}: factories without inspectable global apps")
            if apps:
                modules[module_name] = (module, apps)

    apps = {key: value for _, entries in modules.values() for key, value in entries.items()}
    mounted = {
        id(group.typer_instance)
        for app in apps.values()
        if isinstance(app, typer.Typer)
        for group in app.registered_groups
    }
    roots = {key: value for key, value in apps.items() if key not in mounted}
    commands = []
    for key, app in roots.items():
        definitions = [
            (name, var)
            for name, (module, _) in modules.items()
            for var, value in vars(module).items()
            if id(value) == key
        ]
        # Prefer the defining app module, rather than imports in the composition root.
        module_name, variable = min(definitions, key=lambda row: (len(row[0]), row))
        command = typer.main.get_command(app) if isinstance(app, typer.Typer) else app
        path = command.name or f"{module_name}:{variable}"
        if module_name == "rig.triage.cli":
            path = "triage"
        commands.extend(walk(command, path))

    project = tomllib.loads((root / "pyproject.toml").read_text(encoding="utf-8"))
    if project["project"].get("gui-scripts") or project["project"].get("entry-points"):
        raise RuntimeError("Unsupported GUI/plugin entry points; add a runtime discovery adapter")
    for name, target in project["project"]["scripts"].items():
        if target not in {"rig.csharp_cli:entry", "desktop_manager.cli:main"}:
            raise RuntimeError(f"Unsupported console entry point: {name}={target}; add a runtime discovery adapter")
        surfaces.append(f"script:{name}={target}")
        module_name, _, function = target.partition(":")
        entry = vars(importlib.import_module(module_name))[function]
        if not callable(entry):
            raise RuntimeError(f"Unresolvable entry point: {name}={target}")

    # Development file entry points are executable surfaces too. These are not imported;
    # a new unsupported CLI factory must acquire a runtime adapter rather than be ignored.
    for directory in (root / "scripts", root / ".github" / "scripts", root / "tools" / "command-ownership"):
        for file in sorted(directory.rglob("*.py")):
            tree = ast.parse(file.read_text(encoding="utf-8"))
            surfaces.append(f"python-file:{file.relative_to(root).as_posix()}")
            if cli_factories(tree, imported_names(tree)):
                raise RuntimeError(f"Unsupported development CLI factory: {file}; expose its runtime tree")

    # Inspect the actual MCP registration loop (including dynamically registered tools).
    import asyncio

    from desktop_manager.web_mcp import create_server

    tools = asyncio.run(create_server().list_tools())
    surfaces.extend(f"mcp:{tool.name}" for tool in tools)
    # Mount the real child-route factories without serving requests or executing handlers.
    # Their implementation digests make changes to opaque legacy dispatch explicit in review.
    import hashlib

    from desktop_manager.jobs.registry import Environment, default_jobs
    from desktop_manager.web_cli import mounted_routes

    for route in mounted_routes():
        implementation = route if inspect.isfunction(route) else type(route)
        digest = hashlib.sha256(inspect.getsource(implementation).encode("utf-8")).hexdigest()
        surfaces.append(f"web-child:{implementation.__module__}:{implementation.__qualname__}:{digest}")
    environment = Environment(
        scripts=Path("/scripts"),
        plan=Path("/plan"),
        workspace_root=Path("/workspace"),
        personal_root=Path("/code/personal"),
        work_roots=(),
        decisions_feed=Path("/feed"),
        orca_user_data=Path("/orca"),
    )
    surfaces.extend(f"python-job:{job.name}={' '.join(job.argv)}" for job in default_jobs(environment))
    return {"Commands": sorted(commands, key=lambda c: c["Id"]), "Surfaces": sorted(surfaces)}


if __name__ == "__main__":
    sys.stdout.write(json.dumps(discover(Path(sys.argv[1]).resolve()), ensure_ascii=False) + "\n")
