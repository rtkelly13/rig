from __future__ import annotations

from unittest.mock import MagicMock

from desktop_manager.handlers.restore_steps import _diagnostic_callback

# ---------------------------------------------------------------------------
# _diagnostic_callback
# ---------------------------------------------------------------------------


def test_diagnostic_callback_returns_none_when_logger_is_none() -> None:
    assert _diagnostic_callback(None) is None


def test_diagnostic_callback_returns_callable_when_logger_provided() -> None:
    logger = MagicMock()
    result = _diagnostic_callback(logger)
    assert callable(result)


def test_diagnostic_callback_forwards_message_to_logger_log() -> None:
    logger = MagicMock()
    callback = _diagnostic_callback(logger)
    assert callback is not None
    callback("detected window 12345")
    logger.log.assert_called_once_with("detected window 12345")


def test_diagnostic_callback_forwards_any_string_message() -> None:
    logger = MagicMock()
    callback = _diagnostic_callback(logger)
    assert callback is not None
    callback("timeout waiting for hwnd")
    logger.log.assert_called_once_with("timeout waiting for hwnd")


def test_diagnostic_callback_can_be_called_multiple_times() -> None:
    logger = MagicMock()
    callback = _diagnostic_callback(logger)
    assert callback is not None
    callback("first")
    callback("second")
    assert logger.log.call_count == 2
