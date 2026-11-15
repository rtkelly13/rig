@estate
Feature: Worktree removal safety
  Every command that removes a worktree asks the same judge. A worktree holding ignored notes, a live
  session, a lock or another clone's registration is kept, and a canary planted in one that is kept is
  still there afterwards.
  The judge has no flag for uncommitted tracked or untracked files: git's own `worktree remove` refuses
  those without `--force`, so the dirty scenarios below expect the worktree to stay.

  Scenario Outline: The facts decide whether a worktree may be removed
    Given a worktree that is <shape>
    And the removal policy is <policy>
    When the estate asks whether the worktree may be removed
    Then the decision is <decision>
    And the refusal reasons are <reasons>
    And the decision is reached in under 10 milliseconds

    Examples:
      | shape         | policy          | decision | reasons      |
      | clean         | strict          | allowed  | none         |
      | finished      | strict          | allowed  | none         |
      | dirty         | strict          | allowed  | none         |
      | ignored notes | strict          | guarded  | IgnoredNotes |
      | ignored notes | discard-ignored | allowed  | none         |
      | in use        | strict          | guarded  | InUse        |
      | locked        | strict          | guarded  | Locked       |
      | foreign clone | strict          | guarded  | ForeignClone |

  Scenario Outline: A guarded worktree is never removed
    Given a worktree that is <shape>
    And the removal policy is strict
    When the estate removes the worktree
    Then the worktree is kept
    And the canary survives

    Examples:
      | shape         |
      | ignored notes |
      | in use        |
      | locked        |
      | foreign clone |

  Scenario: A dirty worktree keeps its uncommitted work
    Given a worktree that is dirty
    And the removal policy is strict
    When the estate removes the worktree
    Then the worktree is kept
    And the canary survives

  Scenario: A finished worktree is removed and its work is on origin
    Given a worktree that is finished
    And the removal policy is strict
    When the estate removes the worktree
    Then the worktree is removed
    And the canary survives

  @real @regression
  Scenario Outline: The same decisions hold against real git worktrees
    Given a real worktree that is <shape>
    And the removal policy is strict
    When the estate removes the worktree
    Then the worktree is <outcome>
    And the canary survives

    Examples:
      | shape         | outcome |
      | finished      | removed |
      | dirty         | kept    |
      | ignored notes | kept    |
      | in use        | kept    |
      | locked        | kept    |
      | foreign clone | kept    |
