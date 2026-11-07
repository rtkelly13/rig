"""Type-soup gate: code smells that must not appear in a repo's checked paths."""

from type_ratchet.config import ConfigError, RatchetConfig, load_config, parse_config
from type_ratchet.gate import FileCounts, RuleCounts, Violation, measure, tracked_files, violations
from type_ratchet.rules import BOUNDARY_RULES, Rule, count_source

__all__ = [
    "BOUNDARY_RULES",
    "ConfigError",
    "FileCounts",
    "RatchetConfig",
    "Rule",
    "RuleCounts",
    "Violation",
    "count_source",
    "load_config",
    "measure",
    "parse_config",
    "tracked_files",
    "violations",
]
