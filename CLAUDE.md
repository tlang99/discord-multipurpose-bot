# Working on ModBot

Guidance for AI assistants (and anyone else) changing this repo. README.md describes the features, setup and the
project layout; this file is how to change the code.

## Always apply DRY, and keep it modular

The top rule. The goal is a lean repo with as little bloat as possible, where each piece does one job and is reused.

- **Before writing anything, look for what already exists.** Search `Common/` (shared helpers: text, links, colors,
  permissions, locks, background work, message chores) and the `Services/` of similar features. Reuse it rather than
  writing a second version, even a small one.
- **Second copy → shared helper.** If the same logic would appear twice, move it to one place (usually `Common/`, or
  the feature's service) and call it from both. That includes small things: a link check, an escaping rule, a color,
  an embed layout, a permission check.
- **Reuse whole building blocks, not just helpers.** A new feature that looks like an existing one should use that
  feature's code (e.g. web search answers are built with `NewsEmbedFactory`, the same as news posts).
- **When touching code, clean up repeats you find nearby** and say so in the commit or PR.
- **Don't force it.** Code that only looks alike but would change for different reasons stays separate.
- **Modular:** slash commands in `Modules/<Feature>/`, logic in `Services/<Feature>/` behind interfaces and free of
  Discord types where possible, gateway handlers in `EventHandlers/`. Small classes with one job; no feature reaching
  into another's internals.

## Other rules

- **Every feature works three ways:** its slash command, by asking the chatbot ("@ModBot …"), and in voice where that
  makes sense. They share one code path, so the same rules apply to each.
- **Settings and secrets stay out of the repo.** `appsettings.json` is local only (the template is
  `appsettings.example.json`); keys and tokens go in user-secrets or environment variables, never in committed files
  or logs. The repo is public.
- **Privacy:** log that something happened, never what people said or searched for. Say in the README what leaves
  the machine for each feature.
- **Third-party text** (web pages, feeds, song titles, the model's output) is untrusted: escape it, check links are
  http(s), run it through the server's word filter, and never let it ping anyone.
- **GitHub text:** never write a bare `@word` in PRs, issues, commits or release notes (it pings a real account). Put
  Discord examples like `@ModBot listen` in backticks.
- **Small fixes go together** in one fix branch and PR rather than one PR each.
- **Tests:** add or update tests in `tests/ModBot.Tests` with each change, and keep them all passing.
