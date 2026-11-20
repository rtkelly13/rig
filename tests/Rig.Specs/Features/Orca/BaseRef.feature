@orca
Feature: Orca base refs
  Orca creates new worktrees from the repo's configured `base_ref`. The doctor compares the local
  branch with that remote-tracking ref in one `for-each-ref` call, and spends one `rev-list`
  fallback only when the branch tracks a different ref.

  Scenario Outline: The doctor reads one repo's local base
    Given an Orca repo whose local branch is <state>
    When the doctor reads the local base
    Then the local base is <status>
    And the local base detail is <detail>
    And the read makes <calls> git calls

    Examples:
      | state          | status  | detail                                | calls |
      | level          | current | main is level with origin/main         | 1     |
      | behind 3       | behind  | main is 3 commits behind origin/main   | 1     |
      | ahead 2        | ahead   | main is 2 commits ahead of origin/main | 1     |
      | other upstream | behind  | main is 2 commits behind origin/main   | 2     |
      | missing branch | absent  | none                                   | 1     |
      | missing remote | absent  | none                                   | 2     |

  @real @regression
  Scenario Outline: The same reads hold against real git
    Given a real Orca repo whose local branch is <state>
    When the doctor reads the local base
    Then the local base is <status>
    And the local base detail is <detail>
    And the read makes <calls> git calls
    And the read finishes within 30 seconds

    Examples:
      | state          | status  | detail                                | calls |
      | level          | current | main is level with origin/main         | 1     |
      | behind 3       | behind  | main is 3 commits behind origin/main   | 1     |
      | ahead 2        | ahead   | main is 2 commits ahead of origin/main | 1     |
      | other upstream | behind  | main is 2 commits behind origin/main   | 2     |
      | missing branch | absent  | none                                   | 1     |
      | missing remote | absent  | none                                   | 2     |

  @real @regression
  Scenario: The read costs one git call per repo
    Given 6 real Orca repos that are current
    When the doctor reads every local base
    Then every local base is current
    And the read makes one git call per repo
    And the read finishes within 30 seconds
