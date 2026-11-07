# ADR 0007: Pulumi for Declarative Infrastructure as Code

- **Status:** Accepted
- **Applies to:** Vercel project management, custom domain provisioning, Storybook hosting

## Context
Managing cloud resources, frontend hosting targets (Vercel), and Storybook preview deployments manually through web consoles created configuration drift and undocumented DNS/domain routing.

## Decision
Adopt **Pulumi** (Python SDK) to declaratively manage all infrastructure resources:
- Vercel project definitions, environment variables, and production domains.
- Independent, composable Storybook hosting projects.

## Consequences
- Full version-controlled audit trail for all cloud resources.
- Preview and production deployments are automated via CI/CD.

## References
- [Pulumi Python Documentation](https://www.pulumi.com/docs/languages-sdks/python/)
