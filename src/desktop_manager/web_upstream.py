"""The Python routes the C# web host still forwards to.

The C# host owns the browser's port and serves everything it has ported. It starts this module on a free loopback
port for the jobs coordinator, the C4 viewer and the estate graph, forwards each path it does not own, and closes this
process's standard input to stop it. It is an internal child, not a CLI command.
"""

from __future__ import annotations

import sys
import threading
from collections.abc import Callable
from typing import BinaryIO, Protocol, TextIO

from rig.web_routes import Route


class _Server(Protocol):
    def serve_forever(self) -> None: ...
    def shutdown(self) -> None: ...
    def server_close(self) -> None: ...


def mounted_routes() -> tuple[Route, ...]:
    """The JSON routes features still contribute to `rig web`.

    No routes remain here.
    """
    return ()


def serve(
    port: int,
    host: str = "127.0.0.1",
    *,
    server_factory: Callable[[str, int, tuple[Route, ...]], _Server] | None = None,
    stdin: BinaryIO | None = None,
) -> None:
    """Serve the remaining routes until standard input closes or the process is interrupted."""
    if server_factory is None:
        from rig.web_host.server import StudioServer

        server: _Server = StudioServer(host=host, port=port, routes=mounted_routes())
    else:
        server = server_factory(host, port, mounted_routes())

    stream = sys.stdin.buffer if stdin is None else stdin

    def stop_at_eof() -> None:
        _ = stream.read()
        server.shutdown()

    threading.Thread(target=stop_at_eof, name="rig-web-upstream-stdin", daemon=True).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


def main(
    argv: list[str] | None = None,
    *,
    runner: Callable[[int], None] = serve,
    stderr: TextIO | None = None,
) -> None:
    """Read ``--port`` and serve until standard input closes."""
    arguments = sys.argv[1:] if argv is None else argv
    err = sys.stderr if stderr is None else stderr
    try:
        port = int(arguments[arguments.index("--port") + 1])
    except (ValueError, IndexError):
        _ = err.write("usage: python -m desktop_manager.web_upstream --port <port>\n")
        raise SystemExit(2) from None
    runner(port)


if __name__ == "__main__":
    main()
