"""The SendInput batch Chrome tab-group titles and Windows Terminal prompts are typed through."""

from __future__ import annotations

from desktop_manager.win32.send_input import (
    INPUT_KEYBOARD,
    KEYEVENTF_KEYUP,
    KEYEVENTF_UNICODE,
    KeyEvent,
    input_batch,
    key_tap,
    unicode_events,
)

DOWN = KEYEVENTF_UNICODE
UP = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP


def test_a_key_tap_is_a_press_then_a_release() -> None:
    assert key_tap(0x0D) == [KeyEvent(0x0D, 0, 0), KeyEvent(0x0D, 0, KEYEVENTF_KEYUP)]


def test_text_is_typed_one_utf16_unit_at_a_time() -> None:
    assert unicode_events("a✳") == [
        KeyEvent(0, ord("a"), DOWN),
        KeyEvent(0, ord("a"), UP),
        KeyEvent(0, 0x2733, DOWN),
        KeyEvent(0, 0x2733, UP),
    ]


def test_a_character_outside_the_bmp_is_typed_as_its_surrogate_pair() -> None:
    assert [event.scan for event in unicode_events("😀")] == [0xD83D, 0xD83D, 0xDE00, 0xDE00]


def test_the_batch_carries_every_event_as_a_keyboard_input() -> None:
    events = [*key_tap(0x1B), *unicode_events("é")]

    batch = input_batch(events)

    assert len(batch) == 4
    assert all(item.type == INPUT_KEYBOARD for item in batch)
    assert [KeyEvent(item.u.ki.wVk, item.u.ki.wScan, item.u.ki.dwFlags) for item in batch] == events
