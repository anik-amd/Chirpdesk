# ADR 0017 - The installer ships no content plugins

**Status:** accepted (2026-11-20); amends the "minimal bundled catalogue" decision in `PLAN.md` 18 and ADR 0011

## Context

Until 0.3.6 the installer carried a small catalogue beside the executable: WhatsApp, Telegram,
Facebook, Messenger, Catppuccin, page-dark, the core filter pack and the disk-cleaner tool. Bundled
plugins were trusted by install location and could not be updated from a repository, because a
repository install writes into the user folder and would create a second copy that shadows the
bundled one.

The product owner wanted one model instead: install the app, open Browse, install the chats and
themes you want. Every plugin then has a source record, so updates, rollback and uninstall are the
same operation for everything.

## Decision

1. **No content ships in the installer.** Services, themes and filter packs are installed from a
   signed repository into `%APPDATA%\Chirpdesk\plugins`. The bundled scan root stays in the loader
   for tests and for a developer who drops a folder beside the exe, but the build output no longer
   contains one.
2. **No default service.** The phase-0 WhatsApp seed is gone. A fresh profile has no instances until
   the user installs a service and adds an account.
3. **First run lands on Browse.** The onboarding explains the model and its primary action opens
   the plugins page on the Browse tab.
4. **The disk cleaner is a shell feature.** It was the only `tool` plugin. Tool permissions
   (`tools`, `storage`, `trim-cache`) remain bundled-only, so the panel is now defined by the host
   (`PluginCatalog`) and renders the same closed primitive set. It is not installable or removable.
5. **Ad blocking is opt-in content.** `filters-core` is a normal filter plugin. A fresh install
   blocks nothing until it is installed; the settings log reports `packs=0`, and the app fails open.

## Consequences

- Updating any plugin, including the chats, is a signed repository update; there is no second update
  path and no bundled/user shadowing.
- A fresh install has an empty rail until the user installs something. The onboarding and Browse
  carry that weight, and the installer is smaller.
- Ad blocking is no longer on out of the box. The protection pack is one Browse click, and the
  capability is never silently absent: the filter pack count is visible in logs and settings.
- Existing profiles keep their accounts and profiles. If a service's definition is not installed
  after an upgrade, that service stays unavailable until the plugin is installed again; the profile
  and its login are untouched.
- The bundled scan root and `Bundled` flag remain in the loader so tests and manual side-loading
  still work; they are compatibility, not a shipping path.

## Revisit trigger

If first-run drop-off shows that an empty rail is too confusing, the answer is a guided
install/recommended strip in Browse, not re-bundling content behind the user's back.
