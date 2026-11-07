"""Module entrypoint for python -m rig.agent_launcher."""

from __future__ import annotations

import sys

from rig.agent_launcher.cli import main

if __name__ == "__main__":
    sys.exit(main())
