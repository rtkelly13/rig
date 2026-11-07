"""Gmail triage rules as code: one YAML file compiled to filter XML and connector sweeps.

Built for a Workspace account where no OAuth app can be approved. Nothing here talks to
Gmail. Persistent rules reach Gmail as filter XML imported by hand (Settings → Filters →
Import), and the triage skill runs the sweep queries through the Gmail connector.
"""
