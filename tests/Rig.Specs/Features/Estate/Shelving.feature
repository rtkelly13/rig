@estate
Feature: Shelving uncommitted work
  A worktree about to be removed is set aside first: its working tree becomes a TEMP-SHELVED: commit on
  origin, and estate unshelve reads it back. The round trip gives back exactly what was taken.

  Scenario: Uncommitted work survives a shelve and unshelve round trip
    Given a worktree with a tracked edit and an untracked scratch file
    And an ignored build output that regenerates
    When the worktree is shelved
    Then the worktree is gone
    And the shelf holds its work on origin
    And the ignored build output can be regenerated
    When the shelf is restored
    Then the tracked edit is back
    And the untracked scratch file is back
    And the ignored build output was not carried

  @real @regression
  Scenario: Uncommitted work survives a real shelve and unshelve round trip
    Given a real worktree with a tracked edit and an untracked scratch file
    And an ignored build output that regenerates
    When the worktree is shelved
    Then the worktree is gone
    And the shelf holds its work on origin
    And the ignored build output can be regenerated
    When the shelf is restored
    Then the tracked edit is back
    And the untracked scratch file is back
    And the ignored build output was not carried

  Scenario: A shelf name and ref come from the worktree path
    When a shelf is named for "temp/spike" in the clone
    Then its ref is "refs/heads/shelved/t/temp/spike"

  Scenario: The marker commit tells unshelve where the work belongs
    When a shelf marker is written for branch "spike" at "temp/spike" on base "0123456789abcdef"
    Then the marker names branch "spike", path "temp/spike" and base "0123456789abcdef"
