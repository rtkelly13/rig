import subprocess

import pytest

from rig.subprocess_errors import stderr_text, stdout_text

pytestmark = pytest.mark.ci


@pytest.mark.parametrize(
    ("captured", "expected"),
    [("boom\n", "boom\n"), (b"boom", "boom"), (None, "")],
)
def test_stderr_and_stdout_read_as_text(captured: str | bytes | None, expected: str):
    exc = subprocess.CalledProcessError(1, ["x"], output=captured, stderr=captured)
    assert stderr_text(exc) == expected
    assert stdout_text(exc) == expected
