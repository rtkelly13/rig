@reconciliation
Feature: Reconciliation pipeline
  A desired state is compared with what exists to produce a typed plan. A dry run reports the drift and
  mutates nothing; an apply converges the observed state.

  Scenario: A clean estate plans no drift
    Given the desired state is:
      | path     | content |
      | app.conf | v2      |
    And the observed state is:
      | path     | content |
      | app.conf | v2      |
    When the reconciliation plan is evaluated
    Then the plan is clean
    And the plan reports no drift

  Scenario: Drift becomes typed changes
    Given the desired state is:
      | path       | content |
      | app.conf   | v2      |
      | extra.conf | new     |
    And the observed state is:
      | path       | content |
      | app.conf   | v1      |
      | stale.conf | old     |
    When the reconciliation plan is evaluated
    Then the plan reports 3 changes
    And the plan creates "extra.conf"
    And the plan updates "app.conf"
    And the plan deletes "stale.conf"

  Scenario: A dry run reports drift and mutates nothing
    Given the desired state is:
      | path     | content |
      | app.conf | v2      |
    And the observed state is:
      | path     | content |
      | app.conf | v1      |
    When the reconciliation plan is evaluated
    And the pipeline runs in dry-run mode
    Then no mutation reached the sink
    And the observed state is unchanged
    And the plan reports 1 change

  Scenario: Applying the plan converges the observed state
    Given the desired state is:
      | path       | content |
      | app.conf   | v2      |
      | extra.conf | new     |
    And the observed state is:
      | path       | content |
      | app.conf   | v1      |
      | stale.conf | old     |
    When the reconciliation plan is evaluated
    And the pipeline applies the plan
    Then every mutation went through the sink
    And the observed state matches the desired state

  @real @regression
  Scenario: A real dry run leaves the filesystem byte-identical
    Given the desired state is:
      | path       | content |
      | app.conf   | v2      |
      | extra.conf | new     |
    And the observed state is:
      | path       | content |
      | app.conf   | v1      |
      | stale.conf | old     |
    When the reconciliation plan is evaluated
    And the pipeline runs in dry-run mode
    Then no mutation reached the sink
    And the observed state is unchanged
    When the pipeline applies the plan
    Then every mutation went through the sink
    And the observed state matches the desired state
