"""Tests for step runner and execution pipeline."""

import pytest

from rig.devenv.engine.context import StepContext
from rig.devenv.engine.runner import execute_step, run_steps, should_execute_step
from rig.devenv.engine.step import Step


def _noop(_: StepContext) -> None:
    return None


@pytest.mark.ci
def test_should_execute_step():
    ctx_express = StepContext(mode="express")
    s_default_true = Step(name="S1", description="desc", func=_noop, default_confirm=True)
    s_default_false = Step(name="S2", description="desc", func=_noop, default_confirm=False)

    assert should_execute_step(s_default_true, ctx_express)
    assert not should_execute_step(s_default_false, ctx_express)

    ctx_agent = StepContext(mode="agent")
    assert should_execute_step(s_default_false, ctx_agent)


@pytest.mark.ci
def test_execute_step_success():
    called: list[str] = []
    step = Step(name="Test Step", description="Test", func=lambda ctx: called.append(ctx.mode))
    ctx = StepContext(mode="agent")
    assert execute_step(step, ctx, number=1, total_steps=1)
    assert called == ["agent"]


@pytest.mark.ci
def test_execute_step_skipped_in_express_mode_does_not_run():
    called: list[str] = []
    step = Step(name="Skip Step", description="Test", func=lambda ctx: called.append(ctx.mode), default_confirm=False)
    assert execute_step(step, StepContext(mode="express"), number=1, total_steps=1)
    assert called == []


@pytest.mark.ci
def test_run_steps_starts_at_the_requested_step():
    called: list[str] = []
    steps = [
        Step(name=name, description=name, func=lambda _ctx, name=name: called.append(name)) for name in ("A", "B", "C")
    ]
    run_steps(StepContext(mode="agent"), steps, start_step=2)
    assert called == ["B", "C"]


def _never_prompt(*_args: object, **_kwargs: object) -> bool:
    raise AssertionError("agent mode must not prompt")


@pytest.mark.ci
def test_failing_step_in_agent_mode_continues_without_prompting(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setattr("rig.devenv.engine.runner.Confirm.ask", _never_prompt)

    def boom(_ctx: StepContext) -> None:
        raise RuntimeError("step broke")

    step = Step(name="Broken", description="Test", func=boom)

    assert execute_step(step, StepContext(mode="agent"), number=1, total_steps=1) is False


@pytest.mark.ci
def test_failing_step_in_full_mode_asks_whether_to_continue(monkeypatch: pytest.MonkeyPatch):
    asked: list[str] = []

    def answer(prompt: str, **_kwargs: object) -> bool:
        asked.append(prompt)
        return True

    monkeypatch.setattr("rig.devenv.engine.runner.Confirm.ask", answer)

    def boom(_ctx: StepContext) -> None:
        raise RuntimeError("step broke")

    step = Step(name="Broken", description="Test", func=boom)

    assert execute_step(step, StepContext(mode="full"), number=1, total_steps=1) is False
    # Full mode asks once to run the step, then once whether to continue after it fails.
    assert len(asked) == 2
    assert "Continue" in asked[1]
