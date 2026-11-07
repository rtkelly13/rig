Feature: Shelving a worktree never loses work
  `estate shelve` clears worktrees that `tidy` refuses: it pushes whatever exists
  only on this machine, proves the remote holds it, and only then removes the
  worktree. Anything a snapshot would silently lose or leak is left for a person.

  Background:
    Given a clone whose main branch is pushed to origin

  Rule: Shelving picks the least action that loses nothing

    Scenario Outline: The plan for a worktree <state>
      Given a worktree <state>
      When I plan shelving it
      Then the plan is to <action>
      And no decision is needed

      Examples:
        | state                                    | action   |
        | that is clean and pushed                 | remove   |
        | with commits that are not on origin      | push     |
        | with uncommitted changes                 | snapshot |

    Scenario: A merged pull request authorises removing commits a squash merge stranded
      Given a worktree whose local commits were squash-merged
      When I plan shelving it
      Then the plan is to remove
      And the plan gives the reason "squash-merged in #7"

  Rule: Anything a snapshot would lose or leak needs a person's decision

    Scenario Outline: A worktree <state> is not shelved
      Given a worktree <state>
      When I plan shelving it
      Then a decision is needed about "<concern>"
      When I apply the plan
      Then the outcome is "needs-decision"
      And the worktree is still on disk

      Examples:
        | state                                          | concern           |
        | holding an ignored secret                      | .env              |
        | touched recently enough to be a live session   | live session      |
        | whose shelf on origin holds other content      | already on origin |

    Scenario: Ignored build output is regenerable, so it is no reason to stop
      Given a worktree holding ignored build output
      When I plan shelving it
      Then no decision is needed

    Scenario: The owner can choose to carry an ignored secret in the snapshot
      Given a worktree holding an ignored secret
      When I plan shelving it with ignored files included
      Then the plan is to snapshot
      And the snapshot carries ".env"

    Scenario: The owner can choose to discard an ignored secret
      Given a worktree holding an ignored secret
      When I plan shelving it with ignored files discarded
      Then no decision is needed

  Rule: Nothing is removed until origin proves it holds the work

    Scenario: A rejected push leaves the worktree exactly as it was
      Given origin rejects every push
      And a worktree with uncommitted changes that was never pushed
      When I plan shelving it
      And I apply the plan
      Then the outcome is "failed"
      And the worktree is still on disk
      And the worktree's own index is untouched

  Rule: A shelf restores exactly what was there

    Scenario: Uncommitted work survives a shelve and unshelve round trip
      Given a worktree with a local commit and uncommitted changes
      When I plan shelving it
      And I apply the plan
      Then the outcome is "shelved"
      And the worktree is gone from disk
      And origin holds a shelf recording the branch and the commit it was taken on
      And the pull request branch on origin did not move
      When I unshelve it
      Then the branch and its commits are back
      And every uncommitted change is back, unstaged
