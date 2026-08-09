# ⌨️ Typewriter CLI

Generate TypeScript from C# projects with Typewriter `.tst` templates, from your terminal, editor task, or CI pipeline.

## ✨ Why developers use it

- 🔄 **Keep contracts in sync**: regenerate TypeScript DTOs, API clients, SignalR contracts, and other template-driven output from real Roslyn metadata.
- 🚦 **CI-friendly by design**: deterministic exit codes, JSON output, dry runs, and warning-as-failure support.
- 👀 **Fast local feedback**: `watch` mode tracks C#, project, solution, configuration, and `.tst` changes.
- 🧩 **Flexible workspaces**: point it at a folder, project, or solution, then generate one project or every project.

## 🚀 Install

```bash
dotnet tool install --global AdaskoTheBeAsT.Typewriter.Cli
```

Then run:

```bash
typewriter --help
```

The official Typewriter plugins for Visual Studio, VS Code, and JetBrains Rider include this CLI automatically. Install it globally when you want direct terminal, CI, or custom tooling access.

## ⚡ Quick start

```bash
typewriter init --workspace .
typewriter generate --workspace .
typewriter watch --workspace .
```

## 🛠️ Commands

| Command | What it does |
| --- | --- |
| `typewriter` | Runs `generate` with the provided options. |
| `typewriter init` | Creates a default `typewriter.json`. |
| `typewriter generate` | Generates TypeScript files from discovered or selected `.tst` templates. |
| `typewriter validate` | Loads project metadata and validates templates without writing files. |
| `typewriter watch` | Watches inputs and regenerates after changes. |
| `typewriter list-templates` | Lists discovered templates for the workspace. |

## 🎛️ Switches

| Switch | Commands | Description |
| --- | --- | --- |
| `--workspace <path>` | all | Solution, project, or folder to operate on. |
| `--project <path>` | generate, validate, watch, list-templates | C# project to read metadata from. |
| `--template <path>` | generate, validate, watch, list-templates | Single `.tst` template file or template directory to use. |
| `--template-search-path <path>` | generate, validate, watch, list-templates | Restrict template discovery without changing workspace or project context. |
| `--framework <tfm>` | generate, validate, watch, list-templates | Target framework to use when loading project metadata, for example `net10.0`. |
| `--all-projects` | generate, validate, watch, list-templates | Process every project in a multi-project workspace. |
| `--output text\|json` | generate, validate, watch, list-templates | Choose human-readable text or machine-readable JSON output. |
| `--dry-run` | generate, validate, watch, list-templates | Render and validate without writing generated files. |
| `--diff` | generate, validate, watch | Include unified diffs for changed files, including line-ending changes. |
| `--fail-on-warning` | generate, validate, watch, list-templates | Return a non-zero exit code when warnings are emitted. |
| `--force` | init | Overwrite an existing `typewriter.json`. |

## 💡 Examples

```bash
# Generate for a solution
typewriter generate --workspace ./MyApp.sln

# Validate in CI and fail on warnings
typewriter validate --workspace . --output json --fail-on-warning

# Generate from one project and one template
typewriter generate --project ./src/MyApi/MyApi.csproj --template ./templates/contracts.tst

# Watch every project in a workspace
typewriter watch --workspace . --all-projects
```

## ⚡ Speeding up large workspaces

Metadata loading dominates generation time on multi-project solutions. If your templates do not
reference types produced by source generators, you can skip generator execution in
`typewriter.json`:

```json
{
  "generation": {
    "runSourceGenerators": false
  }
}
```

This is a workspace setting, so it applies everywhere Typewriter generates — the CLI,
`typewriter watch`, and the Visual Studio extension's **Render Template** and
**Render All Templates** commands alike.

For one-off runs, or for workspaces without a `typewriter.json`, the same behaviour is available
as a CLI switch:

```bash
typewriter generate --workspace . --project Web/Web.csproj --template Templates --no-source-generators
```

The switch only ever disables generators: it turns them off when configuration leaves them
enabled, and never re-enables them when `runSourceGenerators` is `false`. There is deliberately no
opposite switch — to re-enable generators for a single run, set `"runSourceGenerators": true` in
`typewriter.json` (or remove the setting, since `true` is the default) and omit
`--no-source-generators`.

On a 9-project reference workspace this reduced end-to-end generation from ~19.3 s to ~15.0 s
(-22%) with byte-identical output.

**When this is safe.** Generated types are never template entities: Typewriter enumerates only the
syntax trees parsed from disk, so a source-generated class never becomes a template `Class`.
Disabling generators is therefore safe when your hand-written, template-visible code does not
*reference* generated symbols.

**When it is not.** Generators still affect semantic resolution of hand-written code. If a property
you emit is typed by a generated type, or a partial method/regex/DTO generator contributes members
your templates read, disabling generators can silently change or drop output rather than fail
loudly. The default stays `true` for this reason — treat this as an opt-in you verify by diffing
generated output before and after.

## ✅ Best fit

Use the CLI when you want repeatable TypeScript generation in local scripts, build steps, pull request checks, or any environment where an IDE should not be required.
