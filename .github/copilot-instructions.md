# Copilot Instructions

## Project Guidelines
- When building packages in the Typewriter repo, output should go to `artifacts/packages` (the canonical location used by CI, release.yml, README, and docs/packing_plan.md), not the `artifacts` default that the Build-*.ps1 scripts use. Pass `-OutputDirectory artifacts/packages` explicitly when running the packaging scripts.
